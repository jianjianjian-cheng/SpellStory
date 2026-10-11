using System.Collections;
using Controllers.Character;
using Core;
using Core.SharedModel;
using ExitGames.Client.Photon;
using Photon.Pun;
using UI;
using UI.Framework.Panel;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Hashtable = ExitGames.Client.Photon;
using Core.Contracts;
using Core.Network;
using Controllers.Battle;
using Controllers.Game;

namespace Controllers.Network
{
  /// <summary>
  /// 大厅控制器 — 薄层 MonoBehaviour，持有 LobbyModel 实例。
  ///
  /// 职责：
  /// - PUN 回调路由
  /// - UI 引用与事件绑定
  /// - 协程驱动
  /// - 委托 NetworkSceneFlow / SpawnPointManager 处理子逻辑
  /// - 连接状态委托 LobbyModel 管理
  /// </summary>
  public class Launcher : MonoBehaviourPunCallbacks, IObjectPoolService, IGameActionService
  {
    [SerializeField]
    public GameObject currentlySelectedCharacter;

    [SerializeField] private Core.SceneConfigSO sceneConfig;

    [SerializeField] private Controllers.Character.CharacterInfoLibrarySO characterInfoLibrary;
    private InputField nameUI;
    private GameObject joinRoom;
    private GameObject TBGC;
    private GameObject joinRoomButton;
    private GameObject joinRoomInput;

    private Photon.Realtime.Player localPlayer;

    // 拆分后的逻辑管理器
    private SpawnPointManager _spawnPointManager;
    private NetworkSceneFlow _sceneFlow;

    /// <summary>大厅模型实例（纯 C#，可单测）</summary>
    public LobbyModel Model { get; private set; }

    #region 单例模式
    public static Launcher Instance { get; private set; }

    void Awake()
    {
      if (Instance == null)
      {
        Instance = this;
        DontDestroyOnLoad(gameObject);
        NetworkServiceLocator.RegisterObjectPoolService(this);
        NetworkServiceLocator.RegisterGameActionService(this);

        // 设置早期服务回调（GameServiceRegistrar 中的 EarlyXxxService 会委托到这些回调）
        GameServiceRegistrar.ReturnToLobbyCallback = () => ReturnToLobby();
        GameServiceRegistrar.QuitToMainMenuCallback = () => QuitToMainMenu();
        GameServiceRegistrar.SetLocalReadyCallback = (ready) => SetLocalReady(ready);

        Model = new LobbyModel();
      }
      else
      {
        Destroy(gameObject);
      }

      _sceneFlow = new NetworkSceneFlow(this);
      _spawnPointManager = new SpawnPointManager(this, currentlySelectedCharacter, characterInfoLibrary);

      int lobbyIndex = sceneConfig != null ? sceneConfig.lobbySceneIndex : 1;
      if (SceneManager.GetActiveScene().buildIndex == lobbyIndex)
      {
        InitGetUI();
      }
    }
    #endregion

    void Start()
    {
      Model.SetIsTest(EventChannelLocator.MainContainer != null
          && EventChannelLocator.MainContainer.gameSettings != null
          && EventChannelLocator.MainContainer.gameSettings.IsTest);

      if (Model.IsTest) return;

      PhotonNetwork.AutomaticallySyncScene = true;
      UnityEngine.Application.runInBackground = true;

      //离线模式不能调用ConnectUsingSettings
      if (PhotonNetwork.OfflineMode) return;

      PhotonNetwork.ConnectUsingSettings();
    }

    private void Update()
    {
      if (Model.IsTest) return;

      if (Input.GetKeyDown(KeyCode.Return))
        ConfirmNickname();

      if (Input.GetKeyDown(KeyCode.Escape))
        CloseJoinRoomPanel();
    }

    // ==================== 大厅 UI ====================

    public void ConfirmNickname()
    {
      if (string.IsNullOrEmpty(nameUI.text))
      {
        Debug.LogError("Name is empty");
        return;
      }
      PhotonNetwork.LocalPlayer.NickName = nameUI.text;
    }

    public void CloseJoinRoomPanel()
    {
      if (TBGC != null) TBGC.SetActive(false);
      if (joinRoomButton != null) joinRoomButton.SetActive(false);
      if (joinRoomInput != null) joinRoomInput.SetActive(false);
    }

    private void InitGetUI()
    {
      nameUI = GameObject.Find("NameUI")?.GetComponent<InputField>();
      TBGC = GetInactiveObjectByName("TBGC");
      joinRoomButton = GetInactiveObjectByName("JoinRoomButton");
      joinRoomInput = GetInactiveObjectByName("JoinRoomInput");
      joinRoom = GameObject.Find("JoinRoom");

      _spawnPointManager.SetNameUI(nameUI);
      Initialize();
    }

    private void Initialize()
    {
      joinRoom.GetComponent<Button>().onClick.AddListener(() =>
      {
        TBGC.SetActive(true);
        joinRoomButton.SetActive(true);
        joinRoomInput.SetActive(true);
      });

      joinRoomButton.GetComponent<Button>().onClick.AddListener(() =>
      {
        string roomName = joinRoomInput.GetComponent<InputField>().text;
        if (string.IsNullOrEmpty(roomName))
        {
          Debug.LogError("Room name is empty");
          return;
        }
        SwitchRoom(roomName);
        CloseJoinRoomPanel();
      });

      if (TBGC != null)
      {
        Button closeBtn = TBGC.GetComponentInChildren<Button>(true);
        if (closeBtn != null && closeBtn.gameObject != joinRoomButton)
          closeBtn.onClick.AddListener(CloseJoinRoomPanel);
      }
    }

    public GameObject GetInactiveObjectByName(string objectName)
    {
      foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
      {
        if (root.name == objectName && !root.activeInHierarchy)
          return root;
        var found = FindChildRecursive(root.transform, objectName);
        if (found != null) return found.gameObject;
      }
      return null;
    }

    private static Transform FindChildRecursive(Transform parent, string name)
    {
      for (int i = 0; i < parent.childCount; i++)
      {
        var child = parent.GetChild(i);
        if (child.name == name) return child;
        var result = FindChildRecursive(child, name);
        if (result != null) return result;
      }
      return null;
    }

    // ==================== 连接 & 房间管理 ====================

    public void SwitchRoom(string newRoomName)
    {
      if (PhotonNetwork.InRoom)
      {
        Model.TryStartRoomSwitch(newRoomName);
        PhotonNetwork.LeaveRoom();
      }
    }

    private void SetDefaultName()
    {
      if (string.IsNullOrEmpty(PhotonNetwork.LocalPlayer.NickName))
        PhotonNetwork.LocalPlayer.NickName = "玩家" + PhotonNetwork.LocalPlayer.UserId;
    }

    public override void OnConnectedToMaster()
    {
      if (Model.IsTest || SceneManager.GetActiveScene().buildIndex < 1)
        return;
      base.OnConnectedToMaster();
      PhotonNetwork.AutomaticallySyncScene = true;
      SetDefaultName();

      // 切换房间：从旧房间离开后回到 Master Server
      if (Model.IsJoiningRoom)
      {
        string roomName = Model.ConsumePendingRoomName();
        if (!string.IsNullOrEmpty(roomName))
        {
          PhotonNetwork.JoinRoom(roomName);
          return;
        }
      }

      if (!Model.IsAutoCreate)
      {
        string roomName = "Room_" + Random.Range(1000, 9999);
        PhotonNetwork.CreateRoom(
            roomName,
            new Photon.Realtime.RoomOptions() { MaxPlayers = 4, PublishUserId = true },
            default
        );
        Model.IsAutoCreate = true;
      }
    }

    public override void OnCreatedRoom()
    {
      if (Model.IsTest) return;
      base.OnCreatedRoom();
    }

    public override void OnLeftRoom()
    {
      if (Model.IsQuittingToMenu)
      {
        StartCoroutine(ShowAndLoadMainMenu());
        return;
      }
    }

    public override void OnJoinRoomFailed(short returnCode, string message)
    {
      Model.ResetRoomJoinFailure();

      var topNotice = FindObjectOfType<UI.Framework.TopNotice>();
      topNotice?.Show("房间不存在");

      SetDefaultName();
      PhotonNetwork.CreateRoom(
          "Room_" + Random.Range(1000, 9999),
          new Photon.Realtime.RoomOptions() { MaxPlayers = 4, PublishUserId = true },
          default
      );
      Model.IsAutoCreate = true;
    }

    /// <summary>退出房间并返回开始界面</summary>
    public void QuitToMainMenu()
    {
      if (PhotonNetwork.InRoom)
      {
        PhotonNetwork.AutomaticallySyncScene = false;
        _spawnPointManager.ClearLocalPlayerSpawnPoint();
        Model.IsQuittingToMenu = true;
        Model.IsAutoCreate = false;
        PhotonNetwork.LeaveRoom();
      }
      else
      {
        StartCoroutine(ShowAndLoadMainMenu());
      }
    }

    private IEnumerator ShowAndLoadMainMenu()
    {
      yield return StartCoroutine(ServiceLocator.Get<Loading>().Show());
      LoadMainMenuScene();
    }

    private void LoadMainMenuScene()
    {
      Model.ResetConnectionState();
      PhotonNetwork.AutomaticallySyncScene = false;
      SceneManager.LoadScene(sceneConfig != null ? sceneConfig.mainMenuSceneIndex : 0);
    }

    // ==================== PUN 回调路由 ====================

    public override void OnEnable()
    {
      base.OnEnable();
      SceneManager.sceneLoaded += OnSceneLoaded;
    }

    public override void OnDisable()
    {
      base.OnDisable();
      SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    public override void OnJoinedRoom()
    {
      if (Model.IsTest) return;
      base.OnJoinedRoom();
      _spawnPointManager.EnsurePlayerManagerExists();
      StartCoroutine(DelayJoinRoom());
    }

    IEnumerator DelayJoinRoom()
    {
      yield return new WaitForSeconds(0.5f);
      _spawnPointManager.CreatedOrJoinedRoom();
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
      if (scene.buildIndex == 1)
      {
        EventChannelLocator.MainContainer.roomJoinedChannel?.Raise(new RoomJoinedEventData());
      }

      if (Model.IsTest) return;

      if (scene.buildIndex == 1)
      {
        EventChannelLocator.MainContainer.gameSettings.IsStayLobby = true;

        if (Controllers.Battle.GamePauseManager.isPaused)
          EventChannelLocator.MainContainer.pauseChannel?.Raise(false);

        InitGetUI();
        _sceneFlow.ResetForLobby();
        _sceneFlow.ResetLocalReady();

        if (PhotonNetwork.IsConnected && PhotonNetwork.InRoom)
        {
          StartCoroutine(_spawnPointManager.SpawnPlayerAfterDelay());
        }

        if (PhotonNetwork.IsConnected && !PhotonNetwork.InRoom && !Model.IsAutoCreate)
        {
          PhotonNetwork.AutomaticallySyncScene = true;
          SetDefaultName();
          PhotonNetwork.CreateRoom(
              "Room_" + Random.Range(1000, 9999),
              new Photon.Realtime.RoomOptions() { MaxPlayers = 4, PublishUserId = true },
              default
          );
          Model.IsAutoCreate = true;
        }
        return;
      }

      if (scene.buildIndex > 1)
      {
        ServiceLocator.Get<GameManager>().isReady = false;
        EventChannelLocator.MainContainer.gameSettings.IsStayLobby = false;

        if (Controllers.Battle.GamePauseManager.isPaused)
          EventChannelLocator.MainContainer.pauseChannel?.Raise(false);

        _sceneFlow.ResetForLobby();

        var loadProps = new ExitGames.Client.Photon.Hashtable
        {
            { PlayerPropertyKeys.Level, true },
            { PlayerPropertyKeys.Loaded, true },
            { PlayerPropertyKeys.PlayerName, PhotonNetwork.LocalPlayer.NickName },
        };
        PhotonNetwork.LocalPlayer.SetCustomProperties(loadProps);

        _spawnPointManager.CreatedOrJoinedRoom();
      }
    }

    public override void OnPlayerLeftRoom(Photon.Realtime.Player otherPlayer)
    {
      _spawnPointManager.HandlePlayerLeftRoom(otherPlayer);
    }

    public override void OnPlayerPropertiesUpdate(
        Photon.Realtime.Player targetPlayer,
        ExitGames.Client.Photon.Hashtable changedProps)
    {
      if (changedProps.ContainsKey(PlayerPropertyKeys.Ready))
        _sceneFlow.CheckAllPlayersReady();

      if (changedProps.ContainsKey(PlayerPropertyKeys.Loaded))
        _sceneFlow.CheckAllPlayersLoaded();
    }

    void OnDestroy()
    {
      _spawnPointManager.HandleDestroy();
    }

    // ==================== 接口门面 ====================

    public void SetLocalReady(bool ready) => _sceneFlow.SetLocalReady(ready);
    public void ReturnToLobby() => _sceneFlow.ReturnToLobby();
    public void SwitchCharacter(string newCharacterName) => _spawnPointManager.SwitchCharacter(newCharacterName);
  }
}
