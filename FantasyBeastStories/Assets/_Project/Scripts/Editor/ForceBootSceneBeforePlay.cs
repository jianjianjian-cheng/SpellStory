using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 强制从 WaitLobby 进入 PlayMode。
/// 解决：NGC1999Void（Index 2）依赖 WaitLobby 场景运行时生成的
/// DontDestroyOnLoad Manager（Launcher / PlayerManager / CoinManager 等），
/// 直接 Play 战斗场景会因这些 Manager 不存在而 NRE / 逻辑跑不起来。
/// ------------
/// 原理：进入 PlayMode 前把激活场景切换为 WaitLobby，让全局 Manager 先于战斗场景初始化。
/// </summary>
[InitializeOnLoad]
public static class ForceBootSceneBeforePlay
{
    // 注意：这个回调必须在 Editor 编译、且编辑器空闲时能执行的操作里注册。
    // 我们选择在真正进入 PlayMode 之前那一帧触发。
    private const string BootScenePath = "Assets/Scenes/WaitLobby.unity";

    static ForceBootSceneBeforePlay()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        // ExitingEditMode 才是“即将进入播放”的阶段。
        // 官方文档：ExitingEditMode = 正从 EditMode 切换到 PlayMode（此时还处于 Edit 环境）。
        if (state != PlayModeStateChange.ExitingEditMode)
            return;

        // 如果本来就在 WaitLobby，就不要乱动，直接进 Play。
        var active = SceneManager.GetActiveScene();
        if (!active.IsValid() || active.path == BootScenePath)
            return;

        // 保存/提示保存当前编辑场景，避免 OpenScene 把它丢弃时的未保存改动丢失。
        // 返回 false 表示用户取消/放弃，则中断进入播放。
        if (!SaveModifiedScenesIfNeeded())
        {
            Debug.LogWarning("[ForceBootScene] 已取消进入 Play，因为当前场景未保存。");
            EditorApplication.isPlaying = false; // 取消本次进入播放
            return;
        }

        // 切到 WaitLobby，确保它以“单一激活场景”形式就绪
        UnityEngine.Debug.Log($"[ForceBootScene] 当前场景为 {active.name}，" +
                              $"即将进入 Play，自动加载启动场景 {BootScenePath}");
        EditorSceneManager.OpenScene(BootScenePath, OpenSceneMode.Single);
    }

    /// <summary>关闭当前场景前弹一次保存确认；有改动用 Dialog 询问，未改动直接放行。</summary>
    private static bool SaveModifiedScenesIfNeeded()
    {
        // 收集所有已改动的已加载场景
        var modified = new System.Collections.Generic.List<UnityEngine.SceneManagement.Scene>();
        for (int i = 0; i < EditorSceneManager.sceneCount; i++)
        {
            var s = EditorSceneManager.GetSceneAt(i);
            if (s.isDirty)
                modified.Add(s);
        }

        if (modified.Count == 0)
            return true;

        // 简单情形：AdaptToSave 返回 true 表示用户确认保存 / 或场景已存好
        // （这里先用 EditorSceneManager.SaveModifiedScenesIfUserWantsTo() 的统一行为）
        return EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
        // 说明：内部逻辑 = 若有未保存改动，弹“Save / Don't Save / Cancel”对话框
    }
}