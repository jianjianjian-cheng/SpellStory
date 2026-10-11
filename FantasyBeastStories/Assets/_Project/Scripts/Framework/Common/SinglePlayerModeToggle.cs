using System.Collections;
using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;

namespace Core
{
    public class SinglePlayerModeToggle : MonoBehaviour
    {
        /// <summary>
        /// 按下FF1切换单机/联网模式
        /// </summary>
        private bool waittingDisconnetion;

        void Update()
        {
            if (waittingDisconnetion && !PhotonNetwork.IsConnected)
            {
                waittingDisconnetion = false;
                EnterOffline();
            }

            if (!Input.GetKeyDown(KeyCode.F1)) return;

            if (PhotonNetwork.OfflineMode)
            {
                PhotonNetwork.Disconnect();

                PhotonNetwork.ConnectUsingSettings();   // 重新联机
                return;
            }

            if (PhotonNetwork.IsConnected)
            {
                PhotonNetwork.Disconnect();  // 联机中：先断线（异步完成后再进离线）
                waittingDisconnetion = true;
            }
            else
            {
                EnterOffline();  // 还没连：直接进离线
            }

        }

        /// <summary>
        /// 进入单机模式
        /// </summary>
        private void EnterOffline()
        {
            PhotonNetwork.OfflineMode = true;

            if (!PhotonNetwork.InRoom)
                PhotonNetwork.CreateRoom("单机模式房间");

            Debug.LogWarning("已进入单机模式");
        }
    }


}
