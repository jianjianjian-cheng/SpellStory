using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Controllers.Battle
{
    /// <summary>
    /// 怪物模拟标识，主机的真怪和客户端的视觉怪都挂，SimID是唯一键
    /// </summary>
    public class MonsterLink : MonoBehaviour
    {
        public ushort SimID;
        public byte TypeIndex;
    }
}
