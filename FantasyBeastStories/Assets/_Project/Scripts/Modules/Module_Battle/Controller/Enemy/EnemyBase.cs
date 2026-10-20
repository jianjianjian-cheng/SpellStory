using System.Collections;
using System.Collections.Generic;
using Controllers.Character;
using Core;
using Core.SharedModel;
using Core.Channels.Combat;
using Controllers.Player;
using Core.Contracts;
using Core.Network;
using Controllers.Network;
using UnityEngine;
using UnityEngine.AI;
using Controllers.Battle;
using Controllers.Task;

namespace Controllers.Battle
{
  public class EnemyBase : MonoBehaviour
  {
    [Header("敌人配置")]
    [SerializeField]
    protected EnemyConfigSO enemyConfig;

    [Header("纯数据")]
    [SerializeField]
    protected EnemyData enemyData;

    [Header("事件通道")]
    [SerializeField] private DamageEventChannelSO damageEventChannel;

    [SerializeField] protected NetworkIdentityBase _network;

    protected Collider[] colliders;

    [SerializeField]
    protected Animator animator;

    [SerializeField]
    protected Rigidbody rb;

    [SerializeField]
    protected GameObject PlayerTarget;

    /// <summary>敌人模型实例（纯 C#，可单测）</summary>
    public EnemyModel Model { get; protected set; }

    protected virtual void Awake()
    {
      if (enemyConfig == null)
      {
        Debug.LogError($"[EnemyBase] {gameObject.name} 的 EnemyConfigSO 未配置！", this);
        return;
      }
      enemyData = new EnemyData(new AttributeEnemyBase(
          enemyConfig.maxHealth, enemyConfig.maxHealth,
          enemyConfig.attackPower, enemyConfig.moveSpeed));
      colliders = GetComponentsInChildren<Collider>();
      if (_network == null)
        _network = GetComponent<NetworkIdentityBase>();

      if (rb == null)
        rb = GetComponent<Rigidbody>();
      if (animator == null)
        animator = GetComponent<Animator>();

      // 创建 Model 并订阅状态切换
      Model = new EnemyModel(enemyData.attribute);
      Model.OnStateChanged += OnModelStateChanged;
    }

    protected virtual void Start()
    {
      InitializeEnemy();
    }

    // 初始化方法，从池中取出时也会调用
    protected virtual void InitializeEnemy()
    {
      Model.Initialize();
    }

    protected virtual void Update()
    {
      if (GamePauseManager.isPaused)
        return;
      if (Model.CurrentState == EnemyState.Die)
      {
        return;
      }

      if (!EventChannelLocator.MainContainer.gameSettings.IsTest && !_network.IsMine)
      {
        return;
      }

      switch (Model.CurrentState)
      {
        case EnemyState.Idle:
          UpdateIdle();
          break;
        case EnemyState.Run:
          UpdateRun();
          break;
        case EnemyState.Attack:
          UpdateAttack();
          break;
        case EnemyState.Die:
          UpdateDie();
          break;
      }
    }

    //追踪最近的玩家
    protected virtual void TrackPlayer()
    {
      if (Model.GetIsDie() || Model.CurrentState == EnemyState.Die)
      {
        if (rb != null)
        {
          rb.velocity = Vector3.zero;
          rb.angularVelocity = Vector3.zero;
        }
        return;
      }

      IReadOnlyList<GameObject> players =
          ServiceLocator.Get<PlayerManager>() != null ? ServiceLocator.Get<PlayerManager>().ActivePlayerObjects : null;

      if (players == null || players.Count == 0)
      {
        PlayerTarget = null;
        return;
      }

      PlayerTarget = players[0];
      for (int i = 1; i < players.Count; i++)
      {
        if (players[i] == null)
          continue;
        if (
            PlayerTarget == null
            || Vector3.Distance(transform.position, players[i].transform.position)
                < Vector3.Distance(transform.position, PlayerTarget.transform.position)
        )
        {
          PlayerTarget = players[i];
        }
      }
    }

    #region 状态机相关代码

    /// <summary>
    /// 状态切换入口 — 委托给 Model，Model 通过 OnStateChanged 回调触发物理操作。
    /// 子类仍可 override 各 Enter/Exit 方法。
    /// </summary>
    protected virtual void TransitionToState(EnemyState newState)
    {
      Model.SetState(newState);
    }

    /// <summary>
    /// Model 状态变化回调 — 执行 Exit旧 → Enter新 的物理操作。
    /// </summary>
    private void OnModelStateChanged(EnemyState oldState, EnemyState newState)
    {
      // Exit 旧状态
      switch (oldState)
      {
        case EnemyState.Idle: ExitIdle(); break;
        case EnemyState.Run: ExitRun(); break;
        case EnemyState.Attack: ExitAttack(); break;
        case EnemyState.Die: ExitDie(); break;
      }

      // Enter 新状态
      switch (newState)
      {
        case EnemyState.Idle: EnterIdle(); break;
        case EnemyState.Run: EnterRun(); break;
        case EnemyState.Attack: EnterAttack(); break;
        case EnemyState.Die: EnterDie(); break;
      }
    }

    // ========== Idle状态 ==========
    protected virtual void EnterIdle()
    {
      TrackPlayer();
      if (animator != null)
      {
        animator.SetBool("isRun", false);
      }
    }

    protected virtual void UpdateIdle()
    {
      if (PlayerTarget)
      {
        TransitionToState(EnemyState.Run);
      }
      else
      {
        TrackPlayer();
        return;
      }
    }

    protected virtual void ExitIdle() { }

    // ========== Run状态 ==========
    protected virtual void EnterRun()
    {
      if (animator != null)
      {
        animator.SetBool("isRun", true);
      }
    }

    protected virtual void UpdateRun()
    {
      TrackPlayer();

      if (!PlayerTarget)
      {
        TransitionToState(EnemyState.Idle);
      }
      else
      {
        Vector3 moveDirection = (
            PlayerTarget.transform.position - transform.position
        ).normalized;
        if (rb != null)
        {
          rb.MovePosition(
              transform.position + moveDirection * enemyData.attribute.moveSpeed * UnityEngine.Time.deltaTime
          );
        }
        if (PlayerTarget != null)
        {
          transform.LookAt(
              new Vector3(
                  PlayerTarget.transform.position.x,
                  transform.position.y,
                  PlayerTarget.transform.position.z
              )
          );
        }
      }
    }

    protected virtual void ExitRun()
    {
      if (animator != null)
      {
        animator.SetBool("isRun", false);
      }
    }

    // ========== Attack状态 ==========
    protected virtual void EnterAttack() { }

    protected virtual void UpdateAttack() { }

    protected virtual void ExitAttack() { }

    // ========== Die状态 ==========
    protected virtual void EnterDie()
    {
      EventChannelLocator.MainContainer.enemyReportChannel.Raise(new EnemyReportData(transform.position, _network.ViewID));

      ServiceLocator.Get<MatchStatisticsManager>()?.RecordKill();

      if (rb != null)
      {
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.isKinematic = true;
      }

      var navAgent = GetComponent<NavMeshAgent>();
      if (navAgent != null && navAgent.isActiveAndEnabled)
      {
        navAgent.isStopped = true;
        if (navAgent.isOnNavMesh)
          navAgent.ResetPath();
      }

      foreach (Collider collider in colliders)
      {
        collider.enabled = false;
      }

      if (animator != null)
      {
        animator.SetTrigger("die");
        animator.Update(0f);
      }

      DropExperience();

      Invoke(nameof(ReturnToPool), 3f);
    }

    protected virtual void UpdateDie() { }

    protected virtual void ExitDie() { }
    #endregion

    // 返回对象池（子类可重写以指定池名）
    protected virtual void ReturnToPool()
    {
      if (EventChannelLocator.MainContainer.gameSettings.IsTest)
      {
        EventChannelLocator.MainContainer.poolOperationChannel.Raise(
            PoolOperationData.CreateDespawn(GetPoolName(), gameObject));
        return;
      }

      if (_network.IsMasterClient)
      {
        EventChannelLocator.MainContainer.poolOperationChannel.Raise(
            PoolOperationData.CreateDespawn(GetPoolName(), gameObject));
      }
      else
      {
        NetworkServiceLocator.DomainRpcService?.InvokeRPC(
            "RPC_RequestEnemyDestroy",
            NetworkTarget.MasterClient,
            _network.ViewID);
        gameObject.SetActive(false);
      }
    }

    protected virtual string GetPoolName()
    {
      return PoolConst.Skeleton;
    }

    public virtual EnemyState GetCurrentState()
    {
      return Model.CurrentState;
    }

    public virtual bool GetIsDie()
    {
      if (!EventChannelLocator.MainContainer.gameSettings.IsTest
          && _network != null
          && !_network.IsMasterClient)
      {
        return false;
      }
      return Model.GetIsDie();
    }

    public bool IsDeadOrDying()
    {
      return Model.IsDeadOrDying();
    }

    protected virtual void OnEnable()
    {
      if (Model != null && Model.IsInitialized)
      {
        ResetState();

        Model.EnsureAttribute(enemyData.attribute ?? new AttributeEnemyBase(
            enemyConfig.maxHealth, enemyConfig.maxHealth,
            enemyConfig.attackPower, enemyConfig.moveSpeed));

        InitializeEnemy();
      }

      damageEventChannel = EventChannelLocator.MainContainer?.damageEventChannel;
      if (damageEventChannel != null)
      {
        damageEventChannel.RegisterListener(OnDamageReceived);
      }
      else
      {
        Debug.LogWarning($"[EnemyBase] {gameObject.name} 无法获取 damageEventChannel", this);
      }
    }

    protected virtual void OnDisable()
    {
      CancelInvoke();
      StopAllCoroutines();
      damageEventChannel?.UnregisterListener(OnDamageReceived);
    }

    protected virtual void OnDamageReceived(EventArgsBase args)
    {
      if (enemyData.attribute == null)
      {
        Debug.LogWarning($"[EnemyBase] OnDamageReceived: attribute 为空，敌人对象: {gameObject.name}");
        return;
      }

      if (Model.GetIsDie())
        return;

      DamageEventArgs damageEventArgs = args as DamageEventArgs;
      if (damageEventArgs == null)
      {
        Debug.LogWarning($"期望 DamageEventArgs，但收到 {args?.GetType()}");
        return;
      }
      if (damageEventArgs.damgeTarget != gameObject)
        return;

      TakeDamage(damageEventArgs);
    }

    public virtual void TakeDamage(DamageEventArgs damageEventArgs)
    {
      if (Model.GetIsDie())
        return;

      damageEventArgs.CalculateFinalDamageValue();
      damageEventArgs.finalDamageValue = Mathf.Ceil(damageEventArgs.finalDamageValue);

      // 委托 Model 处理伤害
      var result = Model.ApplyDamage(damageEventArgs.finalDamageValue, damageEventArgs.element);

      if (!result.IsValid)
        return;

      // 外部联动（Controller 职责）
      ServiceLocator.Get<MatchStatisticsManager>()?.RecordDamage(Mathf.RoundToInt(result.FinalDamage));
      ServiceLocator.Get<QuestTaskManager>()?.RecordDamage(Mathf.RoundToInt(result.FinalDamage));

      if (EventChannelLocator.MainContainer.gameSettings.IsTest)
      {
        EventChannelLocator.MainContainer.matchSystem.damageDisplayChannel?.Raise(
            DamageDisplayEventArgs.GetShared(
                result.FinalDamage,
                transform.position,
                damageEventArgs.isCritical
            )
        );
      }

      if (result.Died)
      {
        TransitionToState(EnemyState.Die);
      }
    }

    //死亡后掉落经验
    protected virtual void DropExperience()
    {
      Debug.Log("敌人死亡，掉落经验");

      float dropChance = enemyConfig.powerUpDropChance;
      if (Random.value <= dropChance && ServiceLocator.Get<PowerUpManager>() != null)
      {
        bool isTest = EventChannelLocator.MainContainer.gameSettings.IsTest;
        if (isTest)
        {
          ServiceLocator.Get<PowerUpManager>().SpawnRandomPowerUp(transform.position);
        }
        else if (NetworkServiceLocator.PlayerService.IsMasterClient)
        {
          ServiceLocator.Get<PowerUpManager>().SpawnRandomPowerUp(transform.position);
        }
        Debug.Log("[PowerUp] 敌人掉落道具！");
      }
    }

    protected int GetExpValue()
    {
      return Random.Range(enemyConfig.expMin, enemyConfig.expMax + 1);
    }

    // 重置状态（对象池回收时调用）
    public virtual void ResetState()
    {
      if (rb != null)
        rb.isKinematic = false;

      foreach (Collider collider in colliders)
        collider.enabled = true;

      CancelInvoke();
      StopAllCoroutines();

      // 数据层重置（委托 Model）
      Model?.ResetModel();

      if (animator != null)
      {
        animator.ResetTrigger("die");
        animator.SetBool("isRun", false);
        animator.Rebind();
        animator.Update(0f);
      }

      if (rb != null)
      {
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
      }

      PlayerTarget = null;

      // 池是"根壳+本体"结构：跑动的是本体，回收时把它拉回根，否则下次生成位置错乱
      transform.localPosition = Vector3.zero;
      transform.localRotation = Quaternion.identity;
    }
  }
}
