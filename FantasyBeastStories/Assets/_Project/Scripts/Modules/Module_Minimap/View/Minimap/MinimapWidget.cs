using System.Collections.Generic;
using Controllers.Battle;
using Controllers.Player;
using Core;
using Core.Network;
using UI.Framework.Base;
using UnityEngine;

namespace UI
{
    /// <summary>
    /// 小地图红点控件：继承 UIWidget，与 BossHPUIListener / EXPBar 同模式。
    /// 通过 poolOperationChannel 监听敌人 Spawn/Despawn 事件（脏标记），
    /// 定时扫描场景中 EnemyBase 并在小地图上更新红点位置。
    /// 同时追踪存活队友（白点），超出范围贴边显示。
    /// </summary>
    public class MinimapWidget : UIWidget
    {
        [Header("小地图设置")]
        [SerializeField] private float mapRange = 30f;
        [SerializeField] private float updateInterval = 0.1f;

        [Header("UI 引用")]
        [SerializeField] private RectTransform dotContainer;
        [SerializeField] private RectTransform playerArrow;
        [SerializeField] private GameObject dotPrefab;

        [Header("敌人 Dot 样式")]
        [SerializeField] private Color enemyColor = new Color(0.85f, 0.15f, 0.15f, 1f);
        [SerializeField] private Color bossColor = new Color(0.6f, 0f, 0f, 1f);
        [SerializeField] private float dotSize = 8f;
        [SerializeField] private float bossDotSize = 12f;

        [Header("队友 Dot 样式")]
        [SerializeField] private Color teammateColor = Color.white;
        [SerializeField] private float teammateDotSize = 8f;

        private readonly List<MinimapFeedEntry> _trackedEnemies = new();
        private readonly List<MinimapDot> _activeDots = new();
        private readonly Stack<MinimapDot> _dotPool = new();

        private readonly List<Transform> _trackedTeammates = new();
        private readonly List<MinimapDot> _activeTeammateDots = new();
        private readonly Stack<MinimapDot> _teammateDotPool = new();

        private Sprite _teammateSprite;

        private float _timer;
        private bool _needFullRefresh = true;
        private bool _hasSubscribed;
        private RectTransform _containerRect;

        private float _forceRefreshTimer;
        private const float ForceRefreshInterval = 5f;

        protected override void AutoBindComponents()
        {
            if (dotContainer == null)
                dotContainer = transform.Find("Mask/DotContainer") as RectTransform;
            if (playerArrow == null)
                playerArrow = transform.Find("Mask/PlayerArrow") as RectTransform;

            _containerRect = dotContainer;

            _teammateSprite = AssetLoader.TryLoadAsset<Sprite>(
                "Level1_Sprites_UI_white_dot");
        }

        protected override void SubscribeEvents()
        {
            if (_hasSubscribed) return;
            if (EventChannelLocator.MainContainer?.poolOperationChannel == null) return;

            EventChannelLocator.MainContainer.poolOperationChannel.RegisterListener(OnPoolOperation);
            _hasSubscribed = true;
        }

        protected override void UnsubscribeEvents()
        {
            if (!_hasSubscribed) return;
            if (EventChannelLocator.MainContainer?.poolOperationChannel != null)
                EventChannelLocator.MainContainer.poolOperationChannel.UnregisterListener(OnPoolOperation);
            _hasSubscribed = false;
        }

        private void Update()
        {
            _timer += Time.deltaTime;
            if (_timer < updateInterval) return;
            _timer = 0f;

            RefreshTrackedEnemies();
            RefreshTrackedTeammates();
            UpdateDotPositions();
        }
        private void OnPoolOperation(PoolOperationData data)
        {
            switch (data.operationType)
            {
                case PoolOperationType.Spawn:
                case PoolOperationType.GetFromPoolAndActivate:
                case PoolOperationType.Despawn:
                case PoolOperationType.ReturnToPool:
                    _needFullRefresh = true;
                    break;
            }
        }

        private void RefreshTrackedEnemies()
        {
            _trackedEnemies.Clear();
            _trackedEnemies.AddRange(MonsterSimCore.MinimapFeed);

            // dot 数量同步（原样保留）
            while (_activeDots.Count < _trackedEnemies.Count) { var dot = GetDot(); _activeDots.Add(dot); }
            while (_activeDots.Count > _trackedEnemies.Count) { int idx = _activeDots.Count - 1; ReturnDot(_activeDots[idx]); _activeDots.RemoveAt(idx); }
        }


        private void UpdateDotPositions()
        {
            var player = GetLocalPlayer();
            if (player == null || _containerRect == null) return;

            var playerPos = player.transform.position;
            var halfSize = _containerRect.rect.size * 0.5f;
            float maxRadius = Mathf.Min(halfSize.x, halfSize.y);

            // 玩家箭头跟随朝向旋转
            // 世界 forward 投影到 XZ 平面 → 小地图 2D 方向
            // 箭头图片默认朝上(0°)，Z+ 为正北
            if (playerArrow != null)
            {
                var fwd = player.transform.forward;
                fwd.y = 0;
                if (fwd.sqrMagnitude > 0.001f)
                {
                    float angle = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                    playerArrow.localEulerAngles = new Vector3(0, 0, -angle);
                }
            }

            for (int i = 0; i < _trackedEnemies.Count && i < _activeDots.Count; i++)
            {
                var entry = _trackedEnemies[i];
                _activeDots[i].SetColor(entry.big ? bossColor : enemyColor);
                _activeDots[i].SetSize(entry.big ? bossDotSize : dotSize);

                var offset = new Vector2(entry.pos.x - playerPos.x, entry.pos.z - playerPos.z);
                var normalized = offset / mapRange;
                if (normalized.magnitude > 1f) normalized = normalized.normalized;
                _activeDots[i].SetPosition(normalized * maxRadius);
                _activeDots[i].SetVisible(true);
            }

            // ── 队友 Dot ──
            for (int i = 0; i < _trackedTeammates.Count && i < _activeTeammateDots.Count; i++)
            {
                var teammate = _trackedTeammates[i];
                if (teammate == null)
                {
                    _activeTeammateDots[i].SetVisible(false);
                    continue;
                }

                var teammatePos = teammate.position;
                var teammateOffset = new Vector2(teammatePos.x - playerPos.x, teammatePos.z - playerPos.z);
                var teammateNorm = teammateOffset / mapRange;

                // 超出范围贴边
                if (teammateNorm.magnitude > 1f)
                    teammateNorm = teammateNorm.normalized;

                _activeTeammateDots[i].SetPosition(teammateNorm * maxRadius);
                _activeTeammateDots[i].SetVisible(true);
            }
        }

        private void RefreshTrackedTeammates()
        {
            _trackedTeammates.Clear();

            if (ServiceLocator.Get<PlayerManager>() != null && NetworkServiceLocator.IsInitialized)
            {
                int localActorNumber = NetworkServiceLocator.PlayerService.GetLocalActorNumber();
                var teammates = ServiceLocator.Get<PlayerManager>().GetAliveTeammateTransforms(localActorNumber);
                if (teammates != null)
                    _trackedTeammates.AddRange(teammates);
            }

            // 同步队友 dot 数量
            while (_activeTeammateDots.Count < _trackedTeammates.Count)
            {
                var dot = GetTeammateDot();
                _activeTeammateDots.Add(dot);
            }
            while (_activeTeammateDots.Count > _trackedTeammates.Count)
            {
                int idx = _activeTeammateDots.Count - 1;
                ReturnTeammateDot(_activeTeammateDots[idx]);
                _activeTeammateDots.RemoveAt(idx);
            }
        }

        private GameObject GetLocalPlayer()
        {
            var players = ServiceLocator.Get<PlayerManager>()?.ActivePlayerObjects;
            if (players == null || players.Count == 0) return null;

            foreach (var go in players)
            {
                if (go == null) continue;
                var pc = go.GetComponent<Controllers.Character.PlayerController>();
                if (pc != null && pc.IsLocalPlayer())
                    return go;
            }

            // 降级：返回第一个有效玩家
            foreach (var go in players)
            {
                if (go != null) return go;
            }
            return null;
        }

        private MinimapDot GetDot()
        {
            MinimapDot dot;
            if (_dotPool.Count > 0)
            {
                dot = _dotPool.Pop();
                dot.gameObject.SetActive(true);
            }
            else
            {
                var go = Instantiate(dotPrefab, dotContainer);
                dot = go.GetComponent<MinimapDot>();
                if (dot == null)
                    dot = go.AddComponent<MinimapDot>();
                dot.SetColor(enemyColor);
                dot.SetSize(dotSize);
            }
            return dot;
        }

        private void ReturnDot(MinimapDot dot)
        {
            if (dot == null) return;
            dot.SetVisible(false);
            _dotPool.Push(dot);
        }

        private MinimapDot GetTeammateDot()
        {
            MinimapDot dot;
            if (_teammateDotPool.Count > 0)
            {
                dot = _teammateDotPool.Pop();
                dot.gameObject.SetActive(true);
            }
            else
            {
                var go = Instantiate(dotPrefab, dotContainer);
                dot = go.GetComponent<MinimapDot>();
                if (dot == null)
                    dot = go.AddComponent<MinimapDot>();
                dot.SetSprite(_teammateSprite);
                dot.SetColor(teammateColor);
                dot.SetSize(teammateDotSize);
            }
            return dot;
        }

        private void ReturnTeammateDot(MinimapDot dot)
        {
            if (dot == null) return;
            dot.SetVisible(false);
            _teammateDotPool.Push(dot);
        }

        private void OnDisable()
        {
            base.OnDisable();

            // 敌人 dots 回收
            foreach (var dot in _activeDots)
            {
                if (dot != null)
                    dot.gameObject.SetActive(false);
            }
            _dotPool.Clear();
            foreach (var dot in _activeDots)
            {
                if (dot != null)
                    _dotPool.Push(dot);
            }
            _activeDots.Clear();
            _trackedEnemies.Clear();

            // 队友 dots 回收
            foreach (var dot in _activeTeammateDots)
            {
                if (dot != null)
                    dot.gameObject.SetActive(false);
            }
            _teammateDotPool.Clear();
            foreach (var dot in _activeTeammateDots)
            {
                if (dot != null)
                    _teammateDotPool.Push(dot);
            }
            _activeTeammateDots.Clear();
            _trackedTeammates.Clear();
        }
    }
}
