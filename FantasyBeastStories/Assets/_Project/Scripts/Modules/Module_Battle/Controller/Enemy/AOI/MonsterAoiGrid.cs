using System.Collections.Generic;
using UnityEngine;

namespace Controllers.Battle
{
    /// <summary>
    /// 怪物格子索引（纯 C#，可单测）：
    /// 由地图范围(中心+尺寸)和格子边长构建，内部 W×H 扁平数组。
    /// 每 tick Clear → Insert 重建；GetCell 为 O(1) 数组直查。
    /// </summary>
    public class MonsterAoiGrid
    {
        public struct CellMonster
        {
            public GameObject go;
            public ushort id;
            public byte type;
        }

        public float CellSize { get; }
        public int W { get; }
        public int H { get; }
        public Vector2 Origin { get; }   // 地图左下角 (minX, minZ)
        public List<CellMonster>[] Cells => _cells;

        private readonly List<CellMonster>[] _cells;

        public MonsterAoiGrid(float cellSize, Vector2 center, Vector2 size)
        {
            CellSize = Mathf.Max(1f, cellSize);
            W = Mathf.CeilToInt(size.x / CellSize);
            H = Mathf.CeilToInt(size.y / CellSize);
            Origin = center - size * 0.5f;

            _cells = new List<CellMonster>[W * H];
            for (int i = 0; i < _cells.Length; i++)
                _cells[i] = new List<CellMonster>(32);
        }

        public void Clear()
        {
            for (int i = 0; i < _cells.Length; i++)
                _cells[i].Clear();
        }

        /// <summary>越界钳制到边缘格：地图外的怪/玩家不会从 AOI 里凭空消失</summary>
        public (int x, int z) CellOf(Vector3 pos)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt((pos.x - Origin.x) / CellSize), 0, W - 1);
            int z = Mathf.Clamp(Mathf.FloorToInt((pos.z - Origin.y) / CellSize), 0, W - 1);
            return (x, z);
        }

        public void Insert(Vector3 pos, GameObject go, ushort id, byte type)
        {
            var c = CellOf(pos);
            _cells[c.z * W + c.x].Add(new CellMonster { go = go, id = id, type = type });
        }

        public List<CellMonster> GetCell(int x, int z)
        {
            if (x < 0 || x >= W || z < 0 || z >= H) return null;
            return _cells[z * W + x];
        }
    }
}