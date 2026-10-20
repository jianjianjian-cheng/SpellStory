using System.Collections;
using System.Collections.Generic;
using Core;
using Controllers.Battle;
using Controllers.Battle.ImpactCannon;
using Photon.Pun;
using UnityEngine;
using Controllers.Network;
using UI;
using Core.SharedModel;
using Controllers.Task;
using Photon.Realtime;
using ExitGames.Client.Photon;

namespace Controllers.Network
{
  public class CastNetwork : MonoBehaviourPun, INetworkFireballCaster
  {
    /// <summary>
    /// 请求发射火球（由 AttackRangeBase 调用）
    /// 功能：向其他玩家广播发射指令
    /// </summary>
    public void RequestFireball(
        Vector3 spawnPos,
        Vector3 direction,
        float speed,
        Element element
    )
    {
      photonView.RPC(
          "RPC_OnFireballCast",
          RpcTarget.Others,
          spawnPos,
          direction,
          speed,
          (int)element
      );
    }

    /// <summary>
    /// RPC：其他玩家收到后，在本地生成火发射物
    /// </summary>
    [PunRPC]
    void RPC_OnFireballCast(Vector3 spawnPos, Vector3 direction, float speed, int elementInt)
    {
      Element element = (Element)elementInt;
      SpawnFireballForOthers(spawnPos, direction, element);
    }

    /// <summary>
    /// 为其他玩家生成本地火球（isMine = false，不负责伤害判定）
    /// </summary>
    private void SpawnFireballForOthers(Vector3 spawnPos, Vector3 direction, Element element)
    {
      // 1. 根据元素类型选择视觉特效池
      string visualPool = GetVisualPoolByElement(element);
      GameObject visualObj = PoolHelper.Get(visualPool, spawnPos);
      if (visualObj != null)
      {
        visualObj.GetComponentInChildren<ParticleSystem>()?.Play();
        visualObj.transform.rotation = Quaternion.LookRotation(direction);
      }

      GameObject triggerObj = PoolHelper.Get(ObjectPoolConst.ImpactCannonTriggerPool, spawnPos);
      if (triggerObj != null)
      {
        ImpactCannon cannon = triggerObj.GetComponent<ImpactCannon>();
        if (cannon == null)
        {
          cannon = triggerObj.AddComponent<ImpactCannon>();
        }
        // 重要：传入 isMine = false，这个火球不会判定伤害
        //绑定token
        AttackToken token = new AttackToken
        {
          hitCollider = triggerObj,
          vfxEffect = visualObj,
          vfxPoolName = visualPool,
        };
        cannon.SetToken(token);
        cannon.SetElement(element);
        cannon.StartShoot(direction, isMine: false);
      }
    }

    // ==================== 辅助方法 ====================

    /// <summary>
    /// 根据元素类型获取视觉特效池名
    /// </summary>
    private string GetVisualPoolByElement(Element element)
    {
      switch (element)
      {
        case Element.Lightning:
          return ObjectPoolConst.ImpactCannonLightenPool;
        case Element.Winter:
          return ObjectPoolConst.ImpactCannonWinterPool;
        default:
          return ObjectPoolConst.ImpactCannonCommonPool;
      }
    }

    // ==================== 伤害相关 ====================

    public void BroadcastDamage(
        GameObject enemyObj,
        float damage,
        bool isCritical,
        float criticalMultiplier,
        Vector3 hitPoint,
        Element element
    )
    {
      var enemyView = enemyObj.GetComponent<PhotonView>();

      // 老网络对象（Boss 等房间对象）：保留原 RPC 路径，行为不变
      if (enemyView != null)
      {
        photonView.RPC("RPC_DealDamage", RpcTarget.All, enemyView.ViewID, damage, isCritical, criticalMultiplier, hitPoint, (int)element);
        photonView.RPC("RPC_ShowDamageNum", RpcTarget.All, damage, hitPoint, isCritical, criticalMultiplier);
        return;
      }

      // 新：AOI 怪物（无 PhotonView，凭 MonsterLink 定位）
      var link = enemyObj.GetComponent<MonsterLink>();
      if (link == null) return;

      // 本地预测：命中者立刻看伤害数字（IsTest 下由 TakeDamage 的既有通道显示，避免双份）
      float finalDamage = Mathf.Ceil(damage * (isCritical ? criticalMultiplier : 1f));
      if (!EventChannelLocator.MainContainer.gameSettings.IsTest)
      {
        EventChannelLocator.MainContainer.matchSystem.damageDisplayChannel?.Raise(
            DamageDisplayEventArgs.GetShared(finalDamage, hitPoint, isCritical));

        // 非房主端：本地记伤害统计/任务进度（房主由 TakeDamage 全量记，避免重复）
        if (!PhotonNetwork.IsMasterClient)
        {
          ServiceLocator.Get<MatchStatisticsManager>()?.RecordDamage(Mathf.RoundToInt(finalDamage));
          ServiceLocator.Get<QuestTaskManager>()?.RecordDamage(Mathf.RoundToInt(finalDamage));
        }
      }

      // 房主/IsTest：直接扣血；远程客户端：上报给房主
      if (EventChannelLocator.MainContainer.gameSettings.IsTest || PhotonNetwork.IsMasterClient)
      {
        MonsterSimCore.Instance.ApplyHit(link.SimID, damage, isCritical, criticalMultiplier, element);
      }
      else
      {
        PhotonNetwork.RaiseEvent(
            MonsterSimCore.EventCodeHit,
            new object[] { (int)link.SimID, damage, isCritical, criticalMultiplier, (int)element },
            new RaiseEventOptions { TargetActors = new[] { PhotonNetwork.MasterClient.ActorNumber } },
            SendOptions.SendReliable);
      }
    }

    /// <summary>
    /// RPC：所有客户端执行扣血和特效
    /// </summary>
    [PunRPC]
    void RPC_DealDamage(
        int enemyViewID,
        float damage,
        bool isCritical,
        float criticalMultiplier,
        Vector3 hitPoint,
        int elementInt
    )
    {
      // 1. 通过 ViewID 找到敌人对象
      PhotonView enemyView = PhotonView.Find(enemyViewID);
      if (enemyView == null)
        return;

      // 2. 触发伤害事件（扣血）
      // 注意：击中特效由各投射物系统自行处理，不在 RPC 中重复播放。
      // - ImpactCannon: ImpactCannon.OnTriggerEnter → PlayHitEffect()
      // - GuiLing:      GuiLingBase.OnTriggerEnter → LaunchEffect(hitVFX)
      Element element = (Element)elementInt;
      DamageEventArgs damageEventArgs = DamageEventArgs.GetShared(
          element,
          gameObject,
          enemyView.gameObject,
          damage,
          isCritical,
          criticalMultiplier
      );

      // 使用事件通道触发伤害事件
      var damageChannel = EventChannelLocator.MainContainer?.damageEventChannel;
      damageChannel?.Raise(damageEventArgs);
      Debug.Log($"[CastNetwork] 通过事件通道触发伤害，目标ViewID: {enemyViewID}");
    }

    /// <summary>
    /// RPC：所有客户端显示伤害数字
    /// 用于在敌人被攻击时，显示伤害数字
    /// </summary>
    /// <param name="damageValue"></param>
    /// <param name="position"></param>
    [PunRPC]
    void RPC_ShowDamageNum(
            float damageValue,
            Vector3 position,
            bool isCritical,
            float criticalMultiplier
        )
    {
      if (isCritical)
      {
        damageValue *= criticalMultiplier;
      }
      damageValue = Mathf.Ceil(damageValue);
      Vector3 spawnPos = position + Vector3.up * 0f;
      // 1. 从对象池获取伤害数字对象
      GameObject damageNumObj = PoolHelper.Get(ObjectPoolConst.DamageNumPool, spawnPos);
      if (damageNumObj != null)
      {
        DamageNum damageNum = damageNumObj.GetComponent<DamageNum>();
        if (damageNum != null)
        {
          damageNum.Play(damageValue, spawnPos, isCritical);
        }
      }
      else
      {
        Debug.LogError(
            $"DamageNumPool 为空，无法显示伤害数字：{damageValue}, {position}, {isCritical}"
        );
      }
    }

    // ==================== GuiLing（鬼灵弹）投射物同步 ====================

    /// <summary>
    /// 接口实现：请求广播 GuiLing 发射（由 AttackRange_BingNv 调用）
    /// </summary>
    public void RequestGuiLingCast(Vector3 spawnPos, Vector3 direction, int targetViewID, int elementInt)
    {
      photonView.RPC(
          "RPC_OnGuiLingCast",
          RpcTarget.Others,
          spawnPos,
          direction,
          targetViewID,
          elementInt
      );
    }

    /// <summary>
    /// RPC：其他客户端收到后，在本地生成 GuiLing 视觉投射物
    /// </summary>
    [PunRPC]
    void RPC_OnGuiLingCast(Vector3 spawnPos, Vector3 direction, int targetViewID, int elementInt)
    {
      Element element = (Element)elementInt;
      SpawnGuiLingForOthers(spawnPos, direction, targetViewID, element);
    }

    /// <summary>
    /// 为其他客户端生成本地 GuiLing（isMine = false，仅视觉表现）
    /// </summary>
    private void SpawnGuiLingForOthers(Vector3 spawnPos, Vector3 direction, int targetViewID, Element element)
    {
      string poolName = GetGuiLingPoolByElement(element);

      GameObject guiLing = PoolHelper.Get(poolName, spawnPos);
      if (guiLing == null)
      {
        Debug.LogWarning($"[CastNetwork] 从对象池 {poolName} 获取 GuiLing 失败");
        return;
      }

      // 查找目标
      PhotonView targetView = PhotonView.Find(targetViewID);
      Transform target = targetView != null ? targetView.transform : null;

      var guiLingBase = guiLing.GetComponent<GuiLingBase>();
      guiLingBase.poolName = poolName;
      guiLingBase.SetTargetAndLaunch(target, direction);
      // 重要：isMine = false，此投射物不负责伤害判定
      guiLingBase.SetDamageData(false, 0f, 0f, 1f, element);
      guiLingBase.SetSplitData(false, 0);
    }

    /// <summary>
    /// 根据元素类型获取 GuiLing 对象池名称
    /// </summary>
    private static string GetGuiLingPoolByElement(Element element)
    {
      switch (element)
      {
        case Element.Fire: return PoolConst.GuiLingFirePool;
        case Element.Lightning: return PoolConst.GuiLingLightningPool;
        case Element.Grass: return PoolConst.GuiLingGrassPool;
        case Element.Winter:
        default: return PoolConst.GuiLingWinterPool;
      }
    }

    // ==================== GuiLing 分裂弹同步 ====================

    /// <summary>
    /// 广播 GuiLing 分裂弹（由 GuiLingBase 命中时调用）
    /// 其他客户端收到后生成本地视觉分裂弹
    /// </summary>
    public void BroadcastSplitGuiLingCast(Vector3 spawnPos, Vector3 direction, GameObject targetEnemy, int elementInt)
    {
      PhotonView targetView = targetEnemy.GetComponent<PhotonView>();
      if (targetView == null)
        return;

      if (photonView == null)
        return;

      photonView.RPC(
          "RPC_OnSplitGuiLingCast",
          RpcTarget.Others,
          spawnPos,
          direction,
          targetView.ViewID,
          elementInt
      );
    }

    /// <summary>
    /// RPC：其他客户端收到后，在本地生成 GuiLing 分裂弹
    /// </summary>
    [PunRPC]
    void RPC_OnSplitGuiLingCast(Vector3 spawnPos, Vector3 direction, int targetViewID, int elementInt)
    {
      Element element = (Element)elementInt;
      SpawnGuiLingForOthers(spawnPos, direction, targetViewID, element);
    }

    // ==================== GuiLing 击中特效同步 ====================

    /// <summary>
    /// 广播 GuiLing 击中特效（由 GuiLingBase 命中时调用）
    /// 其他客户端在精确位置播放击中特效，确保所有玩家看到一致的命中效果
    /// </summary>
    public void BroadcastGuiLingHitVFX(Vector3 hitPos, Vector3 normal, int elementInt)
    {
      if (photonView == null)
        return;

      photonView.RPC(
          "RPC_PlayGuiLingHitVFX",
          RpcTarget.Others,
          hitPos,
          normal,
          elementInt
      );
    }

    /// <summary>
    /// RPC：其他客户端收到后，在本地播放 GuiLing 击中特效
    /// </summary>
    [PunRPC]
    void RPC_PlayGuiLingHitVFX(Vector3 hitPos, Vector3 normal, int elementInt)
    {
      Element element = (Element)elementInt;
      string hitPoolName = GetGuiLingHitPoolByElement(element);
      if (string.IsNullOrEmpty(hitPoolName))
        return;

      GameObject hitEffect = PoolHelper.Get(hitPoolName, hitPos);
      if (hitEffect == null)
        return;

      hitEffect.transform.position = hitPos;
      hitEffect.transform.rotation = Quaternion.FromToRotation(Vector3.up, normal);
      // GuiLingHit 脚本会在 OnEnable 中自动播放粒子并归还池
    }

    /// <summary>
    /// 根据元素类型获取 GuiLing 击中特效对象池名称
    /// </summary>
    private static string GetGuiLingHitPoolByElement(Element element)
    {
      switch (element)
      {
        case Element.Fire: return PoolConst.GuiLingHitFirePool;
        case Element.Lightning: return PoolConst.GuiLingHitLightningPool;
        case Element.Grass: return PoolConst.GuiLingHitGrassPool;
        case Element.Winter:
        default: return PoolConst.GuiLingHitWinterPool;
      }
    }

    // ==================== ImpactCannon 分裂弹（已有） ====================

    public void RequestSplitBullet(Vector3 spawnPos, Vector3 direction, int elementInt)
    {
      photonView.RPC(
          "RPC_OnSplitBulletCast",
          RpcTarget.Others,
          spawnPos,
          direction,
          elementInt
      );
    }

    [PunRPC]
    void RPC_OnSplitBulletCast(Vector3 spawnPos, Vector3 direction, int elementInt)
    {
      Element element = (Element)elementInt;
      SpawnSplitBulletForOthers(spawnPos, direction, element);
    }

    private void SpawnSplitBulletForOthers(Vector3 spawnPos, Vector3 direction, Element element)
    {
      // 为其他玩家生成分裂弹（只负责视觉表现）
      string visualPool = GetVisualPoolByElement(element);
      GameObject visualObj = PoolHelper.Get(visualPool, spawnPos);

      if (visualObj != null)
      {
        visualObj.GetComponentInChildren<ParticleSystem>()?.Play();
        visualObj.transform.rotation = Quaternion.LookRotation(direction);
      }

      GameObject triggerObj = PoolHelper.Get(ObjectPoolConst.ImpactCannonTriggerPool, spawnPos);

      if (triggerObj != null)
      {
        ImpactCannon cannon = triggerObj.GetComponent<ImpactCannon>();
        if (cannon != null)
        {
          cannon.SetElement(element);
          cannon.StartShoot(direction, isMine: false);
          cannon.SetCanSplit(false);

          AttackToken token = new AttackToken
          {
            hitCollider = triggerObj,
            vfxEffect = visualObj,
            vfxPoolName = visualPool,
          };
          cannon.SetToken(token);
        }
      }
    }

    // ==================== 统一投射物 RPC（热更专用） ====================

    /// <summary>
    /// 统一投射物生成入口 — 供 SkillRpcProxy 调用，Lua 间接调用。
    /// 所有客户端执行：从 Addressables 加载投射物并实例化。
    /// 本地 owner (isMine=true) 负责伤害判定，远程客户端纯视觉。
    /// </summary>
    public void RequestSpawnProjectile(
        int templateId,
        Vector3 spawnPos,
        Vector3 direction,
        int targetViewId,
        float damage,
        float critChance,
        float critMultiplier,
        int elementInt,
        bool canSplit,
        int splitCount,
        float splitDamageMultiplier)
    {
      if (photonView == null) return;

      photonView.RPC(
          "RPC_SpawnProjectile",
          RpcTarget.All,
          templateId, spawnPos, direction, targetViewId,
          damage, critChance, critMultiplier, elementInt,
          canSplit, splitCount, splitDamageMultiplier
      );
    }

    [PunRPC]
    void RPC_SpawnProjectile(
        int templateId,
        Vector3 spawnPos,
        Vector3 direction,
        int targetViewId,
        float damage,
        float critChance,
        float critMultiplier,
        int elementInt,
        bool canSplit,
        int splitCount,
        float splitDamageMultiplier)
    {
      string prefabPath = ProjectileTemplateRegistry.GetName(templateId);
      if (string.IsNullOrEmpty(prefabPath))
      {
        if (photonView.IsMine)
          Debug.LogWarning($"[CastNetwork] 未注册投射物模板 ID: {templateId}");
        return;
      }

      var prefab = AssetLoader.TryLoadAsset<GameObject>(prefabPath);
      if (prefab == null)
      {
        if (photonView.IsMine)
          Debug.LogWarning($"[CastNetwork] 无法加载投射物预制体: {prefabPath}");
        return;
      }

      var go = Instantiate(prefab, spawnPos, Quaternion.LookRotation(direction));
      var projectile = go.GetComponent<ProjectileBase>();
      if (projectile == null)
        projectile = ComponentFactory.GetOrCreateProjectile(go);

      if (projectile == null)
      {
        if (photonView.IsMine)
          Debug.LogWarning($"[CastNetwork] 投射物缺少 ProjectileBase 组件: {prefabPath}");
        return;
      }

      bool isMine = photonView.IsMine;
      projectile.Initialize(
          spawnPos, direction, targetViewId,
          damage, critChance, critMultiplier,
          (Element)elementInt, canSplit, splitCount,
          splitDamageMultiplier, isMine
      );
      projectile.SetCastNetwork(this);

      if (isMine)
      {
        projectile.SetDamageCallback(
            (enemy, dmg, isCrit, critMult, hitPoint, elem) =>
            {
              BroadcastDamage(enemy, dmg, isCrit, critMult, hitPoint, elem);
            }
        );
      }
    }
  }
}