using Controllers.Network;
using Core.SharedModel;

namespace Controllers.Battle
{
    /// <summary>怪物本地身份：怪物不再是 PhotonView，真怪只存在于 Master 上，恒为 mine+master。
    /// EnemyBase 里所有 _network.IsMine / IsMasterClient / ViewID 判断因此原样成立。</summary>
    public class LocalMonsterIdentity : NetworkIdentityBase
    {
        public int SimID = -1;

        public override bool IsMine => true;
        public override bool IsMasterClient => true;
        public override int ViewID => SimID;

        public override void RPC(string methodName, NetworkTarget target, params object[] args) { }
    }
}