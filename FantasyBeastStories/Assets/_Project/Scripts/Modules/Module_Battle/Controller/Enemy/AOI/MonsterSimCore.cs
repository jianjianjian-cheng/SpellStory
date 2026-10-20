using System.Collections.Generic;
using Controllers.Network;
using Controllers.Player;
using Core;
using Core.SharedModel;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace Controllers.Battle
{
    /// <summary>小地图数据条目（Master 由 SimCore 写，客户端由 VisualManager 写）</summary>
    [System.Serializable]
    public class MinimapFeedEntry
    {
        public Vector3 pos;
        public bool big;
    }

    /// <summary>
    /// 怪物权威核心（Master / IsTest 恒真侧）：
    /// - 格子边长由相机高度/FOV 推导，格网范围由地图尺寸决定
    /// - 给池中活跃怪分配 SimID，每 tick 重建格子索引
    /// - 10Hz：对每个远程玩家，订阅 3×3 格（滞回 5×5）→ 快照单发
    /// - 1Hz：重建小地图数据源 + 广播远程客户端
    /// - 收玩家的打怪请求(151) → 走本地 damageEventChannel 扣血（EnemyBase 原逻辑）
    /// </summary>
    public class MonsterSimCore : MonoBehaviourPunCallbacks, IOnEventCallback
    {
        public const byte EventCodeSnapshot = 150;
        public const byte EventCodeHit = 151;
        public const byte EventCodeMinimap = 152;

        public const float SnapshotInterval = 0.1f;   // 10Hz
        public const float MinimapInterval = 1f;
        public const int InterestRadius = 1;          // 进入订阅：3×3
        public const int KeepRadius = 2;              // 超出 5×5 才移除（滞回）
        private readonly Dictionary<int, (int x, int z)> _debugPlayerCells = new();

        [Header("格子来源")]
        [SerializeField] private Camera aoiCamera;                          // 战斗主相机
        [SerializeField] private float manualCellSize = 60f;                 // 相机没拖时的兜底
        [SerializeField, Range(1f, 2f)] private float coverageMargin = 1.3f; // 斜视角余量

        [Header("地图范围（世界 XZ，宁大勿小）")]
        [SerializeField] private Vector2 mapCenter = new Vector2(100f, 0f);
        [SerializeField] private Vector2 mapSize = new Vector2(200f, 200f);

        [System.Serializable]
        public class TypeEntry { public string poolName; public byte typeIndex; public bool bigDot; }

        [Header("池名→类型索引（与 MonsterVisualManager.typePrefabs 下标对齐）")]
        [SerializeField] private TypeEntry[] typeEntries;

        public static MonsterSimCore Instance { get; private set; }
        public static readonly List<MinimapFeedEntry> MinimapFeed = new List<MinimapFeedEntry>();

        private readonly Dictionary<GameObject, ushort> _ids = new Dictionary<GameObject, ushort>();
        private readonly Dictionary<ushort, EnemyBase> _byId = new Dictionary<ushort, EnemyBase>();
        private readonly Dictionary<int, HashSet<(int x, int z)>> _cellSubs = new Dictionary<int, HashSet<(int, int)>>();
        private readonly List<GameObject> _external = new List<GameObject>();   // Boss 等非池怪（只进小地图）
        private ushort _nextId;

        private MonsterAoiGrid _grid;
        private bool _gridReady;
        private NetworkObjectPoolManager _pool;
        private float _snapTimer, _minimapTimer;

        private bool IsAuthority =>
            EventChannelLocator.MainContainer.gameSettings.IsTest || PhotonNetwork.IsMasterClient;

        void Awake() { Instance = this; }

        void Update()
        {
            if (!IsAuthority) return;
            if (_pool == null) _pool = ServiceLocator.Get<NetworkObjectPoolManager>();

            _snapTimer += UnityEngine.Time.deltaTime;
            if (_snapTimer >= SnapshotInterval)
            {
                _snapTimer = 0f;
                BuildGrid();
                SendSnapshots();
            }

            _minimapTimer += UnityEngine.Time.deltaTime;
            if (_minimapTimer >= MinimapInterval)
            {
                _minimapTimer = 0f;
                RebuildMinimapFeed();
            }
        }

        // ================= 格子构建 =================

        /// <summary>由相机高度/FOV 推导格子边长：3×3 最坏情况正好罩住屏幕</summary>
        public float CalculateCellSize()
        {
            if (aoiCamera == null) return manualCellSize;

            float height = aoiCamera.transform.position.y;             // 相机离地高度（地面按 y=0）
            float fovY = aoiCamera.fieldOfView * Mathf.Deg2Rad;        // Unity 的 FOV 是垂直角
            float visibleH = 2f * height * Mathf.Tan(fovY * 0.5f);     // 地面可视高度（平视投影近似）
            float visibleW = visibleH * aoiCamera.aspect;              // 地面可视宽度
            return Mathf.Max(visibleH, visibleW) * 0.5f * coverageMargin;
        }

        private void EnsureGrid()
        {
            if (_gridReady) return;
            float cellSize = CalculateCellSize();
            _grid = new MonsterAoiGrid(cellSize, mapCenter, mapSize);
            _gridReady = true;
            Debug.Log($"[AOI] 格网构建: cellSize={cellSize:F1}m, {_grid.W}×{_grid.H} 格 (地图 {mapSize.x}×{mapSize.y}m)");
        }

        [ContextMenu("重算格子")]   // 相机缩放后手动/代码调用
        public void RecalculateGrid()
        {
            _gridReady = false;
            EnsureGrid();
        }

        /// <summary>每 tick 重建格子索引 + 给新怪分配 SimID</summary>
        private void BuildGrid()
        {
            EnsureGrid();
            _grid.Clear();

            foreach (var kvp in _pool.ActiveObjects)
            {
                byte type = GetTypeIndex(kvp.Key);
                if (type == 255) continue;   // 非怪物池（掉落物等）

                foreach (var go in kvp.Value)
                {
                    if (go == null) continue;

                    var eb = go.GetComponentInChildren<EnemyBase>();
                    if (eb == null) continue;

                    ushort id;
                    if (_ids.TryGetValue(go, out var oldId))
                    {
                        id = oldId;
                    }
                    else
                    {
                        id = _nextId++;
                        _ids[go] = id;
                        _byId[id] = eb;
                        eb.GetComponent<LocalMonsterIdentity>().SimID = id;
                        var link = eb.gameObject.AddComponent<MonsterLink>();
                        link.SimID = id;
                        link.TypeIndex = type;
                    }

                    // 位置用本体：根壳不动，本体（rb/NavMesh）才动
                    _grid.Insert(eb.transform.position, go, id, type);
                }
            }
        }

        // ================= 快照 =================

        private void SendSnapshots()
        {
            var others = PhotonNetwork.PlayerListOthers;
            if (others.Length == 0) return;                    // 单机/离线/IsTest：不发包
            var pm = ServiceLocator.Get<PlayerManager>();
            if (pm == null) return;

            foreach (var p in others)
            {
                var playerGo = FindPlayerGO(pm.ActivePlayerObjects, p.ActorNumber);
                if (playerGo == null) continue;

                var playerCell = _grid.CellOf(playerGo.transform.position);
                _debugPlayerCells[p.ActorNumber] = playerCell;

                if (!_cellSubs.TryGetValue(p.ActorNumber, out var subs))
                    _cellSubs[p.ActorNumber] = subs = new HashSet<(int, int)>();

                // 订阅更新：3×3 无条件进入；超出 5×5 才移除 —— 格子级滞回
                for (int dx = -InterestRadius; dx <= InterestRadius; dx++)
                    for (int dz = -InterestRadius; dz <= InterestRadius; dz++)
                        subs.Add((playerCell.x + dx, playerCell.z + dz));

                subs.RemoveWhere(c =>
                    Mathf.Abs(c.x - playerCell.x) > KeepRadius ||
                    Mathf.Abs(c.z - playerCell.z) > KeepRadius);

                // 快照 = 订阅格子里的所有怪，全程零距离计算
                var packet = new List<object>(96);
                int count = 0;
                foreach (var cell in subs)
                {
                    var list = _grid.GetCell(cell.x, cell.z);   // O(1) 直查，空格返回 null
                    if (list == null) continue;
                    foreach (var m in list)
                    {
                        var eb = _byId[m.id];
                        if (eb == null) continue;
                        count++;
                        packet.Add((int)m.id);
                        packet.Add((int)m.type);
                        packet.Add(eb.transform.position);
                        packet.Add(eb.transform.rotation.eulerAngles.y);
                        packet.Add((int)eb.Model.CurrentState);
                    }
                }

                packet.Insert(0, count);   // 空包也要发：客户端靠它回收走出视野的全部怪
                PhotonNetwork.RaiseEvent(EventCodeSnapshot, packet.ToArray(),
                    new Photon.Realtime.RaiseEventOptions { TargetActors = new[] { p.ActorNumber } },
                    SendOptions.SendUnreliable);
            }
        }

        private GameObject FindPlayerGO(IReadOnlyList<GameObject> players, int actorNumber)
        {
            foreach (var go in players)
            {
                if (go == null) continue;
                var pv = go.GetComponent<PhotonView>();
                if (pv != null && pv.Owner != null && pv.Owner.ActorNumber == actorNumber)
                    return go;
            }
            return null;
        }

        // ================= 小地图 =================

        private void RebuildMinimapFeed()
        {
            MinimapFeed.Clear();
            foreach (var kvp in _pool.ActiveObjects)
            {
                byte type = GetTypeIndex(kvp.Key);
                if (type == 255) continue;
                bool big = IsBigDot(type);
                foreach (var go in kvp.Value)
                {
                    var e = go != null ? go.GetComponentInChildren<EnemyBase>() : null;
                    if (e == null || e.IsDeadOrDying()) continue;
                    MinimapFeed.Add(new MinimapFeedEntry { pos = e.transform.position, big = big });
                }
            }
            foreach (var go in _external)
            {
                if (go != null)
                    MinimapFeed.Add(new MinimapFeedEntry { pos = go.transform.position, big = true });
            }

            // 远程客户端靠事件收数据（单机/离线时 PlayerListOthers 为空，自然不发包）
            if (PhotonNetwork.PlayerListOthers.Length > 0)
            {
                var packet = new List<object>(MinimapFeed.Count * 3 + 1) { MinimapFeed.Count };
                foreach (var e in MinimapFeed)
                {
                    packet.Add(e.pos.x);
                    packet.Add(e.pos.z);
                    packet.Add(e.big);
                }
                PhotonNetwork.RaiseEvent(EventCodeMinimap, packet.ToArray(),
                    new Photon.Realtime.RaiseEventOptions { Receivers = Photon.Realtime.ReceiverGroup.Others },
                    SendOptions.SendUnreliable);
            }
        }

        // ================= 伤害 =================

        /// <summary>Master/IsTest 直接调用；参数格式与 RaiseEvent(151) 负载一致</summary>
        public void ApplyHit(ushort simId, float damage, bool isCritical, float criticalMultiplier, Element element)
        {
            if (!_byId.TryGetValue(simId, out var enemy) || enemy == null) return;
            var args = DamageEventArgs.GetShared(element, null, enemy.gameObject, damage, isCritical, criticalMultiplier);
            EventChannelLocator.MainContainer.damageEventChannel.Raise(args);
        }

        public void OnEvent(ExitGames.Client.Photon.EventData photonEvent)
        {
            if (photonEvent.Code != EventCodeHit || !IsAuthority) return;
            var d = (object[])photonEvent.CustomData;
            ApplyHit((ushort)(int)d[0], (float)d[1], (bool)d[2], (float)d[3], (Element)(int)d[4]);
        }

        // ================= Boss 等非池怪注册（小地图用） =================

        public void RegisterExternal(GameObject go)
        {
            if (go != null && !_external.Contains(go)) _external.Add(go);
        }

        // ================= 辅助 =================

        private byte GetTypeIndex(string poolName)
        {
            foreach (var t in typeEntries)
                if (t.poolName == poolName) return t.typeIndex;
            return 255;
        }

        private bool IsBigDot(byte typeIndex)
        {
            foreach (var t in typeEntries)
                if (t.typeIndex == typeIndex) return t.bigDot;
            return false;
        }

        // ================= Scene 视图可视化（调试用） =================
#if UNITY_EDITOR
#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (_grid == null) return;
            float cell = _grid.CellSize;
            float minX = _grid.Origin.x, minZ = _grid.Origin.y;
            float y = transform.position.y;

            // ── 1. 格网线（青）──
            Gizmos.color = new Color(0f, 1f, 1f, 0.25f);
            for (int x = 0; x <= _grid.W; x++)
            {
                float wx = minX + x * cell;
                Gizmos.DrawLine(new Vector3(wx, y, minZ), new Vector3(wx, y, minZ + _grid.H * cell));
            }
            for (int z = 0; z <= _grid.H; z++)
            {
                float wz = minZ + z * cell;
                Gizmos.DrawLine(new Vector3(minX, y, wz), new Vector3(minX + _grid.W * cell, y, wz));
            }

            // ── 2. 有怪的格子（橙色填充）──
            Gizmos.color = new Color(1f, 0.5f, 0f, 0.4f);
            var cells = _grid.Cells;
            for (int z = 0; z < _grid.H; z++)
                for (int x = 0; x < _grid.W; x++)
                {
                    var list = cells[z * _grid.W + x];
                    if (list.Count == 0) continue;
                    Gizmos.DrawCube(CellCenter(x, z, y, minX, minZ, cell),
                        new Vector3(cell * 0.92f, 0.1f, cell * 0.92f));
                }

            // ── 3. 玩家的 3×3 当前兴趣（亮绿填充）+ 所在格（白描边）──
            foreach (var pc in _debugPlayerCells)
            {
                Gizmos.color = new Color(0f, 1f, 0f, 0.3f);
                for (int dx = -InterestRadius; dx <= InterestRadius; dx++)
                    for (int dz = -InterestRadius; dz <= InterestRadius; dz++)
                        Gizmos.DrawCube(CellCenter(pc.Value.x + dx, pc.Value.z + dz, y, minX, minZ, cell),
                            new Vector3(cell * 0.95f, 0.05f, cell * 0.95f));

                Gizmos.color = Color.white;
                Gizmos.DrawWireCube(CellCenter(pc.Value.x, pc.Value.z, y, minX, minZ, cell),
                    new Vector3(cell, 0.15f, cell));
            }

            // ── 4. 订阅集合（黄描边，含 5×5 滞回带）──
            Gizmos.color = new Color(1f, 1f, 0f, 0.6f);
            foreach (var kvp in _cellSubs)
                foreach (var c in kvp.Value)
                    Gizmos.DrawWireCube(CellCenter(c.x, c.z, y, minX, minZ, cell),
                        new Vector3(cell, 0.12f, cell));
        }

        private Vector3 CellCenter(int x, int z, float y, float minX, float minZ, float cell)
        {
            return new Vector3(minX + (x + 0.5f) * cell, y, minZ + (z + 0.5f) * cell);
        }
#endif
#endif
    }
}