using UnityEngine;

namespace Controllers.Battle
{
    /// <summary>怪物模拟标识：Master 的真怪和客户端的视觉怪都挂，SimID 是唯一键</summary>
    public class MonsterLink : MonoBehaviour
    {
        public ushort SimID;
        public byte TypeIndex;
    }
}