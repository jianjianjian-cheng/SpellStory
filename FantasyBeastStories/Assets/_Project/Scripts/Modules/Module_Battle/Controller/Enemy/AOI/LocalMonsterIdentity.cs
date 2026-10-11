using System.Collections;
using System.Collections.Generic;
using Controllers.Network;
using Core.SharedModel;
using UnityEngine;

namespace Controllers.Battle
{
    /// <summary>
    /// 怪物的本地身份,真怪只存在于主机上
    /// </summary>
    public class LocalMonsterIdentity : NetworkIdentityBase
    {
        public int SimID = -1;

        public override bool IsMine => true;
        public override bool IsMasterClient => true;

        public override int ViewID => SimID;

        public override void RPC(string methodName, NetworkTarget target, params object[] args)
        {

        }
    }
}
