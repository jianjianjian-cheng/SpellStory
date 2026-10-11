using Core;
using Core.Channels;
using Core.Network;
using Photon.Pun;
using UnityEngine;

namespace Core
{
    /// <summary>
    /// Infrastructure 层启动注册器 — 在游戏启动时注册基础服务。
    /// Module 级别的服务注册由各 Module 的 Bootstrap 类负责。
    /// </summary>
    public static class InfrastructureRegistrar
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RegisterFactories()
        {
            // 全局网络配置，只设置一次（原先在 PlayerStateSync.Awake 中，每个玩家实例都会重复设置）
            PhotonNetwork.SendRate = 30;
            PhotonNetwork.SerializationRate = 30;

            // 预加载事件通道容器并注册到 ServiceLocator
            // EventChannels 必须用 Resources.Load — 在 BeforeSceneLoad 阶段 Addressables 尚未初始化
            var container = Resources.Load<EventChannelContainerSO>("EventChannels/MainEventChannels");
            if (container != null)
            {
                ServiceLocator.Register(container);
            }
            else
            {
                Debug.LogError("[InfrastructureRegistrar] 无法加载 MainEventChannels，请在 Resources/EventChannels 目录下创建");
            }

            // 注册早期游戏服务（在 Launcher 加载前，确保 LobbyCanvas 等组件可用）
            GameServiceRegistrar.EnsureRegistered();

            // 启动 PUN 回调桥接器（用于 OnPlayerPropertiesUpdate 等回调转发）
            PhotonCallbackBridge.EnsureExists();

            Debug.Log("[InfrastructureRegistrar] 基础服务注册完成");
        }
    }
}
