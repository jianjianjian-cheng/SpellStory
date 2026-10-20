using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Controllers
{
    public class MonsterAoiGrid : MonoBehaviour
    {
        /// <summary>
        /// 怪物格子索引
        /// </summary>
        public struct CellMonster
        {
            public GameObject go;
            public ushort id;
            public byte type;
        }
    }
}
