using System.Collections.Generic;
using Controllers.Network;
using Core;
using Core.SharedModel;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace Controllers.Battle
{
    /// <summary>
    /// 客户端视觉怪：收 SimCore 快照 → diff → 本地视觉池生成/回收/插值。
    /// Master/IsTest 侧收不到快照，自动闲置（真怪直接可见）。
    /// </summary>
    public class MonsterVisualManager : MonoBehaviourPunCallbacks, IOnEventCallback
    {
        [Header("typeIndex → 视觉预制体（与 MonsterSimCore.typeEntries 对齐）")]
        [SerializeField] private GameObject[] typePrefabs;

        public const float InterpDuration = 0.15f;   // 两帧快照间的插值时长（略大于发送间隔，抹平网络抖动）
        public const float DieRecycleDelay = 3f;

        private class Visual
        {
            public GameObject go;
            public Animator animator;
            public int typeIndex;
            public Vector3 fromPos, toPos;
            public float fromRotY, toRotY;
            public float startTime;
            public bool diePlayed;
            public float dieTime;
        }

        private readonly Dictionary<int, Visual> _visuals = new Dictionary<int, Visual>();
        private readonly Dictionary<int, Queue<GameObject>> _pool = new Dictionary<int, Queue<GameObject>>();
        private readonly List<int> _recycleBuf = new List<int>();

        void Update()
        {
            float now = UnityEngine.Time.time;
            _recycleBuf.Clear();

            foreach (var kvp in _visuals)
            {
                var v = kvp.Value;
                float t = Mathf.Clamp01((now - v.startTime) / InterpDuration);
                v.go.transform.SetPositionAndRotation(
                    Vector3.Lerp(v.fromPos, v.toPos, t),
                    Quaternion.Euler(0f, Mathf.Lerp(v.fromRotY, v.toRotY, t), 0f));

                if (v.diePlayed && now - v.dieTime > DieRecycleDelay)
                    _recycleBuf.Add(kvp.Key);
            }
            for (int i = 0; i < _recycleBuf.Count; i++)
                RecycleVisual(_recycleBuf[i]);
        }

        public void OnEvent(ExitGames.Client.Photon.EventData photonEvent)
        {
            var data = photonEvent.CustomData as object[];
            if (data == null) return;
            if (photonEvent.Code == MonsterSimCore.EventCodeSnapshot) OnSnapshot(data);
            else if (photonEvent.Code == MonsterSimCore.EventCodeMinimap) OnMinimapFeed(data);
        }

        private void OnSnapshot(object[] data)
        {
            int count = (int)data[0];
            var seen = new HashSet<int>();

            for (int i = 0; i < count; i++)
            {
                int b = 1 + i * 5;
                int id = (int)data[b];
                seen.Add(id);

                if (!_visuals.TryGetValue(id, out var v))
                    v = SpawnVisual(id, (int)data[b + 1]);

                // 从当前渲染位置续插，动画中途被打断也顺滑
                v.fromPos = v.go.transform.position;
                v.toPos = (Vector3)data[b + 2];
                v.fromRotY = v.go.transform.rotation.eulerAngles.y;
                v.toRotY = (float)data[b + 3];
                v.startTime = UnityEngine.Time.time;
                ApplyState(v, id, (int)data[b + 4]);
            }

            // 快照里消失的 → 回收（死亡动画已播过；普通出圈直接消失）
            List<int> gone = null;
            foreach (var kvp in _visuals)
                if (!seen.Contains(kvp.Key))
                    (gone ??= new List<int>()).Add(kvp.Key);
            if (gone != null)
                foreach (var id in gone) RecycleVisual(id);
        }

        private Visual SpawnVisual(int id, int type)
        {
            GameObject go;
            if (_pool.TryGetValue(type, out var q) && q.Count > 0)
            {
                go = q.Dequeue();
                ResetVisual(go);
                go.SetActive(true);
            }
            else
            {
                go = CreateStripped(type);
            }

            var v = new Visual { go = go, animator = go.GetComponentInChildren<Animator>(), typeIndex = type };
            _visuals[id] = v;
            return v;
        }

        /// <summary>用同一份怪物预制体，运行时剥掉逻辑/网络组件，只留视觉</summary>
        private GameObject CreateStripped(int type)
        {
            var go = Instantiate(typePrefabs[type]);

            // 记住本体（EnemyBase 所在物体）：弹道打中的是它的碰撞体，MonsterLink 必须挂它
            var eb = go.GetComponentInChildren<EnemyBase>(true);
            GameObject body = eb != null ? eb.gameObject : go;

            foreach (var e in go.GetComponentsInChildren<EnemyBase>(true)) Destroy(e);
            foreach (var nid in go.GetComponentsInChildren<NetworkIdentityBase>(true)) Destroy(nid);
            foreach (var pv in go.GetComponentsInChildren<PhotonView>(true)) Destroy(pv);
            foreach (var ml in go.GetComponentsInChildren<MonsterLink>(true)) Destroy(ml);
            var rb = go.GetComponentInChildren<Rigidbody>();
            if (rb != null) rb.isKinematic = true;

            body.AddComponent<MonsterLink>().TypeIndex = (byte)type;
            return go;
        }

        private void ResetVisual(GameObject go)
        {
            var anim = go.GetComponentInChildren<Animator>();
            if (anim != null) { anim.Rebind(); anim.Update(0f); }
            foreach (var c in go.GetComponentsInChildren<Collider>()) c.enabled = true;
        }

        private void ApplyState(Visual v, int id, int state)
        {
            if (v.diePlayed) return;   // 已在死亡演出中

            if (state == (int)EnemyState.Die)
            {
                v.diePlayed = true;
                v.dieTime = UnityEngine.Time.time;
                if (v.animator != null) v.animator.SetTrigger("die");
                foreach (var c in v.go.GetComponentsInChildren<Collider>()) c.enabled = false;

                // 对齐老行为的各端表现：击杀统计 + 死亡上报（老代码在各端 EnemyBase.EnterDie 里做）
                EventChannelLocator.MainContainer.enemyReportChannel?.Raise(
                    new Core.Channels.Combat.EnemyReportData(v.go.transform.position, id));
                ServiceLocator.Get<MatchStatisticsManager>()?.RecordKill();
                return;
            }
            if (v.animator != null)
                v.animator.SetBool("isRun", state == (int)EnemyState.Run);
        }

        private void RecycleVisual(int id)
        {
            if (!_visuals.TryGetValue(id, out var v)) return;
            _visuals.Remove(id);
            v.go.SetActive(false);
            if (!_pool.TryGetValue(v.typeIndex, out var q))
                _pool[v.typeIndex] = q = new Queue<GameObject>();
            q.Enqueue(v.go);
        }

        private void OnMinimapFeed(object[] data)
        {
            MonsterSimCore.MinimapFeed.Clear();
            int count = (int)data[0];
            for (int i = 0; i < count; i++)
            {
                MonsterSimCore.MinimapFeed.Add(new MinimapFeedEntry
                {
                    pos = new Vector3((float)data[1 + i * 3], 0f, (float)data[2 + i * 3]),
                    big = (bool)data[3 + i * 3]
                });
            }
        }
    }
}