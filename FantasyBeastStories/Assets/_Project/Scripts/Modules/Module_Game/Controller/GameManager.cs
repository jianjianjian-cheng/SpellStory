using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using Core;
using Core.SharedModel;
using Core.Channels.General;
using Core.Contracts;
using Core.Network;
using System.Collections.Generic;
using UI.Framework.Panel;
using Controllers.Network;
using Core.Audio;
using UI;
using Core.Save;
using Controllers.Battle;
using Controllers.Game;

namespace Controllers.Game
{
    public class GameManager : MonoBehaviour, ISpawnPointService
    {
        private static GameManager _instance;


        public int sceneIndex = 2;
        public bool isReady = false;

        [Header("场景配置")]
        [SerializeField] private SceneConfigSO sceneConfig;

        [SerializeField]
        public GameObject[] spawnPoints = { };

        [SerializeField]
        private bool isStayLobbyInspector;

        [SerializeField]
        private bool isTestInspector;

        /// <summary>游戏模型实例（纯 C#，可单测）</summary>
        public GameModel Model { get; private set; }

        private bool _isDuplicate;

        void Awake()
        {
            if (_instance != null && _instance != this)
            {
                _isDuplicate = true;
                Destroy(gameObject);
                return;
            }

            _instance = this;

            Model = new GameModel();

            ServiceLocator.Register(this);
            ServiceLocator.Register<ISpawnPointService>(this);
            EventChannelLocator.MainContainer.gameSettings.IsStayLobby = isStayLobbyInspector;
            EventChannelLocator.MainContainer.gameSettings.IsTest = isTestInspector;
            DontDestroyOnLoad(gameObject);

            SetCustomCursor();

            StartCoroutine(InitAfterUpdate());
        }

        [Header("鼠标指针")]
        [SerializeField] private Texture2D cursorTexture;
        [SerializeField] private Vector2 cursorHotspot = Vector2.zero;

        private void SetCustomCursor()
        {
            if (cursorTexture == null)
                cursorTexture = AssetLoader.TryLoadAsset<Texture2D>("Local_GameCursor");
            if (cursorTexture != null)
            {
                Cursor.SetCursor(cursorTexture, cursorHotspot, CursorMode.Auto);
            }
            else
            {
                Debug.LogWarning("[GameManager] 未能加载自定义光标纹理，使用系统默认");
            }
        }

        private void Update()
        {
            // UpdateCursorVisibility();
        }

        private void UpdateCursorVisibility()
        {
            if (!Model.IsInBattle)
            {
                if (!Cursor.visible) Cursor.visible = true;
                return;
            }

            bool showCursor = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

            if (!showCursor && ServiceLocator.Get<MagicUpgradeManager>() != null && ServiceLocator.Get<MagicUpgradeManager>().IsPanelActive)
                showCursor = true;

            if (Cursor.visible != showCursor)
                Cursor.visible = showCursor;
        }

        /// <summary>等待 Addressables 热更预下载完成后初始化 Lua 环境</summary>
        private IEnumerator InitAfterUpdate()
        {
            yield return new WaitUntil(() => AddressablesUpdater.IsUpdateComplete);
            LuaEnvManager.Instance.Init();
        }

        void OnEnable()
        {
            if (_isDuplicate) return;
            SceneManager.sceneLoaded += OnSceneLoaded;
            EventChannelLocator.MainContainer.gameActionChannel.RegisterListener(OnGameActionReceived);
            EventChannelLocator.MainContainer.bossDeathChannel?.RegisterListener(OnBossDeath);
        }

        void OnDisable()
        {
            if (_isDuplicate) return;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            EventChannelLocator.MainContainer.gameActionChannel.UnregisterListener(OnGameActionReceived);
            EventChannelLocator.MainContainer.bossDeathChannel?.UnregisterListener(OnBossDeath);
        }

        void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
                ServiceLocator.Unregister<GameManager>();
                ServiceLocator.Unregister<ISpawnPointService>();
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            FindSpawnPoints();

            Model.SetIsInBattle(scene.buildIndex == sceneConfig.battleSceneIndex);

            if (scene.buildIndex == sceneConfig.lobbySceneIndex)
            {
                Cursor.visible = true;

                if (ServiceLocator.Get<SaveManager>() != null)
                    ServiceLocator.Get<SaveManager>().LoadGame();

                AudioManager.Instance?.PlayBGM("bgm_main_menu");
            }
            else if (scene.buildIndex == sceneConfig.battleSceneIndex)
            {
                AudioManager.Instance?.PlayBGM("bgm_combat");
                ServiceLocator.Get<MatchStatisticsManager>()?.ResetMatchStats();
                Debug.Log("[GameManager.OnSceneLoaded] 进入战斗场景，ResetMatchStats 已调用");
            }
        }

        #region 生成点管理
        public void FindSpawnPoints()
        {
            spawnPoints = GameObject.FindGameObjectsWithTag("SpawnPoint");
            Model.ClearSpawnPoints();

            for (int i = 0; i < spawnPoints.Length; i++)
            {
                if (spawnPoints[i] != null)
                {
                    ISpawnPoint sp = spawnPoints[i].GetComponent<ISpawnPoint>();
                    if (sp != null)
                    {
                        Model.RegisterSpawnPoint(sp.Id, sp);
                    }
                }
            }
        }

        public GameObject GetEmptySpawnPoint()
        {
            foreach (GameObject spawnPoint in spawnPoints)
            {
                if (spawnPoint == null) continue;

                ISpawnPoint sp = spawnPoint.GetComponent<ISpawnPoint>();
                if (sp == null) continue;

                if (sp.IsEmpty())
                    return spawnPoint;
            }

            return null;
        }

        public ISpawnPoint GetSpawnPointByPlayer(int actorNumber)
            => Model.GetSpawnPointByPlayer(actorNumber);

        public ISpawnPoint GetSpawnPointById(int id)
            => Model.GetSpawnPointById(id);

        public ISpawnPoint GetSpawnPointForPlayer(int actorNumber)
            => Model.GetSpawnPointForPlayer(actorNumber);

        public void OnSpawnPointStateChanged()
        {
            Debug.Log("[GameManager] 生成点状态已更新");
        }
        #endregion

        #region Boss死亡→返回大厅
        private void OnBossDeath()
        {
            if (!EventChannelLocator.MainContainer.gameSettings.IsTest
                && !NetworkServiceLocator.PlayerService.IsMasterClient)
                return;

            StartCoroutine(DelayedLobbyTransition());
        }

        public void TriggerLobbyTransition(float delay = 10f)
        {
            StartCoroutine(DelayedLobbyTransition(delay));
        }

        private IEnumerator DelayedLobbyTransition(float delay = 10f)
        {
            yield return new WaitForSeconds(delay);

            if (ServiceLocator.Get<SaveManager>() != null)
                ServiceLocator.Get<SaveManager>().SaveGame();

            if (EventChannelLocator.MainContainer.gameSettings.IsTest)
            {
                SceneManager.LoadScene(sceneConfig.lobbySceneIndex);
            }
            else if (NetworkServiceLocator.PlayerService.IsMasterClient)
            {
                NetworkServiceLocator.ObjectService.InvokeRPC(
                    ManagerRpcBridge.Instance,
                    "RPC_ReturnToLobby",
                    NetworkTarget.All
                );
            }
            else
            {
                if (ServiceLocator.Get<Loading>() != null)
                    yield return ServiceLocator.Get<Loading>().Show();
            }
        }
        #endregion

        void OnGameActionReceived(GameActionType actionType)
        {
            switch (actionType)
            {
                case GameActionType.QuitToLobby:
                case GameActionType.QuitToMainMenu:
                    NetworkServiceLocator.GameActionService?.QuitToMainMenu();
                    break;
                case GameActionType.SwitchCharacter:
                    break;
                case GameActionType.SetLocalReady:
                    NetworkServiceLocator.GameActionService?.SetLocalReady(true);
                    break;
                case GameActionType.SyncAllPlayers:
                    break;
                default:
                    Debug.LogWarning($"未处理的游戏操作类型: {actionType}");
                    break;
            }
        }
    }
}
