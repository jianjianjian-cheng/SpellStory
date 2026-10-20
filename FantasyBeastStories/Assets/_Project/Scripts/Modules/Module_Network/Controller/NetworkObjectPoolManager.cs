using System.Collections;
using System.Collections.Generic;
using Controllers.Battle;
using Controllers.Network;
using Controllers.Experience;
using Photon.Pun;
using UnityEngine;
using Core;
using Core.Channels.General;

namespace Controllers.Network
{
  /// <summary>
  /// 统一网络对象池管理器
  /// </summary>
  public class NetworkObjectPoolManager : MonoBehaviourPunCallbacks, IPunPrefabPool
  {
    #region 单例模式

    void Awake()
    {
      ServiceLocator.Register(this);
    }

    void OnEnable()
    {
      EventChannelLocator.MainContainer.poolOperationChannel.RegisterListener(OnPoolOperation);
    }

    void OnDisable()
    {
      EventChannelLocator.MainContainer.poolOperationChannel.UnregisterListener(OnPoolOperation);
    }

    private void OnPoolOperation(PoolOperationData data)
    {
      switch (data.operationType)
      {
        case PoolOperationType.Spawn:
          var obj = Spawn(data.poolName, data.position, data.rotation);
          data.resultCallback?.Invoke(obj);
          break;
        case PoolOperationType.Despawn:
          Despawn(data.poolName, data.targetObject, data.delay);
          break;
        case PoolOperationType.DespawnAll:
          if (string.IsNullOrEmpty(data.poolName))
            DespawnAll();
          else
            DespawnAll(data.poolName);
          break;
      }
    }
    #endregion

    // ===================== Inspector 配置 =====================
    [System.Serializable]
    public class PoolConfig
    {
      [Tooltip("与 NetworkObjectPoolConst 中常量保持一致")]
      public string poolName;
      public GameObject prefab;

      [Tooltip("游戏启动时预创建的数量")]
      public int preloadCount = 5;
    }

    [Header("网络对象池配置（在 Inspector 中添加）")]
    [SerializeField]
    private List<PoolConfig> poolConfigs = new List<PoolConfig>();

    // ===================== 内部数据 =====================
    private Dictionary<string, Queue<GameObject>> pools =
        new Dictionary<string, Queue<GameObject>>();
    private Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>();
    private readonly Dictionary<string, HashSet<GameObject>> _activeObjects = new();
    private bool photonReady = false;

    /// <summary>MonsterSimCore 读活跃怪物用</summary>
    public Dictionary<string, HashSet<GameObject>> ActiveObjects => _activeObjects;

    /// <summary>怪物池（预制体带 EnemyBase）→ 本地实例化，不进 Photon</summary>
    private bool IsMonsterPool(string poolName) =>
        prefabs.TryGetValue(poolName, out var prefab) && prefab.GetComponentInChildren<EnemyBase>(true) != null;

    // ===================== 初始化 =====================
    void Start()
    {
      PhotonNetwork.PrefabPool = this;

      if (EventChannelLocator.MainContainer.gameSettings.IsTest || (PhotonNetwork.IsConnectedAndReady && PhotonNetwork.InRoom))
      {
        InitPools();
        photonReady = true;
      }
    }

    void Update()
    {
      // 等待 Photon 就绪后再初始化（仅网络模式）
      if (
          !photonReady
          && !EventChannelLocator.MainContainer.gameSettings.IsTest
          && PhotonNetwork.IsConnectedAndReady
          && PhotonNetwork.InRoom
      )
      {
        InitPools();
        photonReady = true;
      }
    }

    private void InitPools()
    {
      foreach (var cfg in poolConfigs)
      {
        if (cfg.prefab == null)
        {
          Debug.LogWarning($"[NetworkObjectPool] {cfg.poolName} 未配置预制体");
          continue;
        }
        // 跳过已由 RegisterPool 运行时注册的池，避免覆盖和重复预加载
        if (pools.ContainsKey(cfg.poolName))
        {
          Debug.Log($"[NetworkObjectPool] 池 {cfg.poolName} 已存在，跳过 InitPools 重复注册");
          continue;
        }
        prefabs[cfg.poolName] = cfg.prefab;
        pools[cfg.poolName] = new Queue<GameObject>();
        Preload(cfg.poolName, cfg.preloadCount);
      }
      Debug.Log("[NetworkObjectPool] 初始化完成");
    }

    // ===================== 公共接口 =====================

    /// <summary>
    /// 运行时动态注册对象池（由生成器等组件在 Start 中调用）
    /// 如果池名已存在则跳过，不会重复注册
    /// </summary>
    /// <param name="poolName">池名称</param>
    /// <param name="prefab">预制体引用</param>
    /// <param name="preloadCount">预创建数量</param>
    public void RegisterPool(string poolName, GameObject prefab, int preloadCount = 10)
    {
      if (string.IsNullOrEmpty(poolName) || prefab == null)
        return;
      if (pools.ContainsKey(poolName))
        return; // 已存在，不重复注册

      this.prefabs[poolName] = prefab;
      pools[poolName] = new Queue<GameObject>();
      Preload(poolName, preloadCount);
      Debug.Log($"[NetworkObjectPool] 运行时注册池: {poolName}, 预加载 x{preloadCount}");
    }

    /// <summary>生成对象</summary>
    public GameObject Spawn(string poolName, Vector3 position, Quaternion rotation = default)
    {
      if (rotation == default)
        rotation = Quaternion.identity;

      if (EventChannelLocator.MainContainer.gameSettings.IsTest || IsMonsterPool(poolName))
        return GetFromPool(poolName, position, rotation);

      if (!PhotonNetwork.IsMasterClient)
      {
        Debug.LogWarning("[NetworkObjectPool] 只有房主可以生成网络对象");
        return null;
      }
      return PhotonNetwork.Instantiate(poolName, position, rotation);
    }

    /// <summary>回收对象</summary>
    public void Despawn(string poolName, GameObject obj, float delay = 0f)
    {
      if (obj == null)
        return;
      if (delay > 0f)
        StartCoroutine(DespawnDelay(poolName, obj, delay));
      else
        DespawnNow(poolName, obj);
    }

    /// <summary>回收该池内所有激活中的怪物</summary>
    public void DespawnAll(string poolName)
    {
      if (!EventChannelLocator.MainContainer.gameSettings.IsTest && !PhotonNetwork.IsMasterClient)
        return;
      if (!_activeObjects.TryGetValue(poolName, out var active) || active.Count == 0)
        return;

      foreach (var obj in new List<GameObject>(active))
      {
        if (obj != null && obj.activeSelf)
          DespawnNow(poolName, obj);
      }
    }

    /// <summary>回收所有池的对象</summary>
    public void DespawnAll()
    {
      foreach (var poolName in new List<string>(prefabs.Keys))
        DespawnAll(poolName);
    }

    // ===================== IPunPrefabPool 实现（Photon 回调） =====================

    /// <summary>Photon 调用：必须返回未激活的对象，Photon 自行激活</summary>
    public GameObject Instantiate(string prefabId, Vector3 position, Quaternion rotation)
    {
      // 库内对象（怪物/掉落物）走对象池
      if (prefabs.ContainsKey(prefabId))
        return GetFromPool(prefabId, position, rotation, activateOnGet: false);

      var prefab = Resources.Load<GameObject>(prefabId);
      if (prefab != null)
        return Object.Instantiate(prefab, position, rotation);

      Debug.LogError(
          $"[NetworkObjectPool] 无法实例化: {prefabId}，未在对象池或 Resources 中找到"
      );
      return null;
    }

    public void Destroy(GameObject go)
    {
      string poolName = FindPoolName(go);
      if (string.IsNullOrEmpty(poolName))
      {
        Object.Destroy(go);
        return;
      }
      ReturnToPool(poolName, go);
    }

    // ===================== 内部方法 =====================

    private IEnumerator DespawnDelay(string poolName, GameObject obj, float delay)
    {
      yield return new WaitForSeconds(delay);
      DespawnNow(poolName, obj);
    }

    private void DespawnNow(string poolName, GameObject obj)
    {
      if (EventChannelLocator.MainContainer.gameSettings.IsTest || IsMonsterPool(poolName))
      {
        // 测试模式：直接本地回池
        ReturnToPool(poolName, obj);
      }
      else
      {
        // 网络模式：使用 PhotonNetwork.Destroy 同步销毁到所有客户端
        // Destroy 会触发 IPunPrefabPool.Destroy() 回调，在那里执行回池
        if (PhotonNetwork.IsConnectedAndReady)
        {
          PhotonNetwork.Destroy(obj);
        }
        else
        {
          ReturnToPool(poolName, obj);
        }
      }
    }

    private GameObject GetFromPool(
        string poolName,
        Vector3 position,
        Quaternion rotation,
        bool activateOnGet = true
    )
    {
      // 从池中取出可用对象
      if (pools.TryGetValue(poolName, out var pool))
      {
        while (pool.Count > 0)
        {
          var obj = pool.Dequeue();
          if (obj == null)
            continue;
          obj.transform.SetPositionAndRotation(position, rotation);
          // 取出时重新启用 PhotonView（预加载时禁用过）
          var photonView = obj.GetComponent<PhotonView>();
          if (photonView != null)
            photonView.enabled = true;
          if (activateOnGet)
            obj.SetActive(true);
          TrackActive(poolName, obj);
          NotifyPoolOperation(PoolOperationType.GetFromPoolAndActivate, poolName);
          return obj;
        }
      }

      // 池空了则动态创建
      if (prefabs.TryGetValue(poolName, out var prefab))
      {
        //创建对象并且添加到对象池
        var newObj = Object.Instantiate(prefab, position, rotation);
        newObj.name = poolName;
        newObj.SetActive(false);
        // ✅ 不要放回池子，直接返回使用
        // ✅ 尊重 activateOnGet 参数
        if (activateOnGet)
          newObj.SetActive(true);
        else
          newObj.SetActive(false);
        TrackActive(poolName, newObj);
        NotifyPoolOperation(PoolOperationType.GetFromPoolAndActivate, poolName);
        return newObj;
      }

      Debug.LogError($"[NetworkObjectPool] 未找到预制体: {poolName}");
      return null;
    }

    private void ReturnToPool(string poolName, GameObject obj, bool notify = true)
    {
      if (obj == null)
        return;

      UntrackActive(poolName, obj);
      if (notify)
        NotifyPoolOperation(PoolOperationType.ReturnToPool, poolName);
      // 调试日志
      if (poolName == "Enemies/SkeletonRoot" || poolName == "Enemies/DragonRoot")
        Debug.Log($"[NetworkPool] ReturnToPool: {poolName} (name={obj.name}) notify={notify}");

      // 重置对应组件状态
      obj.GetComponentInChildren<EnemyBase>()?.ResetState();
      obj.GetComponent<DropItemBase>()?.ResetState();
      obj.SetActive(false);
      obj.name = poolName; // 统一标记名称，方便 FindPoolName 匹配
      obj.transform.SetParent(transform);
      obj.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);

      var rb = obj.GetComponent<Rigidbody>();
      if (rb != null)
      {
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
      }

      if (!pools.ContainsKey(poolName))
        pools[poolName] = new Queue<GameObject>();
      pools[poolName].Enqueue(obj);
    }

    private void Preload(string poolName, int count)
    {
      for (int i = 0; i < count; i++)
      {
        // 预加载时先禁用 PhotonView，防止本地 Instantiate 触发 Photon 网络销毁流程
        var obj = Object.Instantiate(prefabs[poolName], Vector3.zero, Quaternion.identity);
        obj.name = poolName;
        var photonView = obj.GetComponent<PhotonView>();
        if (photonView != null)
          photonView.enabled = false;
        // Preload 不触发 ReturnToPool 事件，避免 MonsterCountMonitor 收到虚假 Decrement
        ReturnToPool(poolName, obj, notify: false);
      }
      Debug.Log($"[NetworkObjectPool] 预创建 {poolName} x{count}");
    }

    /// <summary>通过对象名称反查所属池（回收时已统一设为 poolName）</summary>
    private string FindPoolName(GameObject obj)
    {
      // 优先精确匹配：回收时已将 name 设为 poolName
      if (pools.ContainsKey(obj.name))
        return obj.name;

      // 备用模糊匹配：应对网络模式下 Photon 返回的对象（名称可能带 (Clone) 等后缀）
      foreach (var kvp in prefabs)
      {
        if (obj.name.Contains(kvp.Value.name) || obj.name.Contains(kvp.Key))
          return kvp.Key;
      }

      // 最后尝试通过组件匹配（Dragon/Skeleton 等有特定组件）
      if (obj.GetComponent<Dragon>() != null)
        return PoolConst.Dragon;
      if (obj.GetComponent<Skeleton>() != null)
        return PoolConst.Skeleton;

      Debug.LogWarning($"[NetworkObjectPool] FindPoolName 失败，无法识别对象: {obj.name}");
      return null;
    }

    // ===================== 活跃对象追踪 =====================

    private void TrackActive(string poolName, GameObject obj)
    {
      if (!_activeObjects.TryGetValue(poolName, out var set))
      {
        set = new HashSet<GameObject>();
        _activeObjects[poolName] = set;
      }
      set.Add(obj);
    }

    private void UntrackActive(string poolName, GameObject obj)
    {
      if (_activeObjects.TryGetValue(poolName, out var set))
        set.Remove(obj);
    }

    /// <summary>通知 MonsterCountMonitor 等监听者池操作发生（仅运行时取/还时调用，预加载不调用）</summary>
    private void NotifyPoolOperation(PoolOperationType type, string poolName)
    {
      EventChannelLocator.MainContainer.poolOperationChannel?.Raise(
          new PoolOperationData { operationType = type, poolName = poolName });
    }

    // ===================== 调试工具 =====================

    public int GetPoolCount(string poolName) =>
        pools.TryGetValue(poolName, out var pool) ? pool.Count : 0;

    // ===================== 销毁清理 =====================

    void OnDestroy()
    {
      ServiceLocator.Unregister<NetworkObjectPoolManager>();
      foreach (var pool in pools.Values)
        while (pool.Count > 0)
        {
          var obj = pool.Dequeue();
          if (obj)
            Object.Destroy(obj);
        }

      pools.Clear();
      prefabs.Clear();
      _activeObjects.Clear();
    }
  }

  /// <summary>
  /// 网络对象池名称常量
  /// 与 Inspector 中 PoolConfig.poolName 保持一致
  /// </summary>
  public static class NetworkObjectPoolConst
  {
    // 掉落物
    public const string ExperienceBall_Blue = "ExperienceBall_BluePool";

    // 怪物（池名需与 Resources 路径一致，作为 PhotonNetwork.Instantiate 的 prefabId）
    public const string Skeleton = "Enemies/SkeletonRoot";
    public const string Dragon = "Enemies/DragonRoot";
  }
}