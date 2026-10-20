using Core;
using Photon.Pun;
using UnityEngine;

namespace Controllers.Battle
{
    public class BossSpawner : MonoBehaviour
    {
        [SerializeField] private Transform[] spawnPoints;
        private string bossPrefabPath = "Enemies/Boss_Horror";

        void OnEnable()
        {
            EventChannelLocator.MainContainer.bossSpawnChannel.RegisterListener(OnBossSpawn);
        }

        void OnDisable()
        {
            EventChannelLocator.MainContainer.bossSpawnChannel.UnregisterListener(OnBossSpawn);
        }

        private void OnBossSpawn(string bossName)
        {
            if (!PhotonNetwork.IsMasterClient) return;

            var spawnPoint = spawnPoints[Random.Range(0, spawnPoints.Length)];

            // Boss 只生成一次，不走对象池，直接用 Photon 原生网络实例化（原有逻辑不动）
            var savedPool = PhotonNetwork.PrefabPool;
            PhotonNetwork.PrefabPool = new DefaultPool();
            var boss = PhotonNetwork.InstantiateRoomObject(bossPrefabPath,     //接住返回值
                spawnPoint.position, spawnPoint.rotation);
            PhotonNetwork.PrefabPool = savedPool;

            // ← 改动2：新增这一行
            MonsterSimCore.Instance?.RegisterExternal(boss);
        }
    }

}