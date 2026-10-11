using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Controllers
{
    /// <summary>
    /// 小地图的数据条目
    /// </summary>
    public class MinimapFeedEntery
    {
        public Vector3 pos;
        public bool big;
    }

    /// <summary>
    /// 怪物权威的核心
    /// 为池中活跃怪物分配SimID
    /// 每10帧对各个玩家筛选AOI半径的怪
    /// 每帧重建小地图数据源
    /// 怪物受伤
    /// </summary>
    public class MonsterSimCore : MonoBehaviour
    {
        /// <summary>
        /// 快照
        /// </summary>
        public const byte EventCodeSnapshot = 150;
        public const byte EventCodeHit = 151;
        public const byte EventCodeMinimap = 152;

        public const float SnapshotInterval = 0.1f;
        public const float MinimapInterval = 1f;
        /// <summary>
        /// 刷新半径
        /// </summary>
        public const float AoiRadius = 40f;
        public const float AoiExitRadius = 50f;   // 滞回：进了 40 圈，出了 50 才移除

        [System.Serializable]
        public class TypeEntry { public string poolName; public byte typeIndex; public bool bigDot; }

        [Header("池名→类型索引（与 MonsterVisualManager.typePrefabs 下标对齐）")]
        [SerializeField] private TypeEntry[] typeEntries;

    }
}
