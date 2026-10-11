using System;
using System.Collections;
using TMPro;
using UI.Framework.Base;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace Controllers
{
    [Serializable]
    public class ItemCardView : UIWidget, IPointerClickHandler
    {
        //卡牌容器父级
        [Header("子UI")]
        [SerializeField]
        private Image iconImage;
        [SerializeField]
        private TextMeshProUGUI cardNameText;
        [SerializeField]
        private TextMeshProUGUI cardQualityText;

        public RectTransform rect;
        public Canvas canvas;

        /// <summary>
        /// 悬浮时间计时器
        /// </summary>
        private float checkTimer = 0f;
        /// <summary>
        /// 悬浮检测间隔
        /// </summary>
        private float checkInterval = 0.1f;
        private bool lastHover = false;

        private Coroutine playHoverCor;
        private float baseY;

        private const float HoverLift = 5f;

        [SerializeField] private RectTransform visual;

        void Start()
        {
            baseY = rect.anchoredPosition.y;
        }

        void Update()
        {
            checkTimer += UnityEngine.Time.deltaTime;
            //处于检查间隔
            if (checkTimer < checkInterval) return;

            //检查鼠标是否在该张卡牌上
            bool hover = RectTransformUtility.RectangleContainsScreenPoint(
                rect,
            Input.mousePosition,
            canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera);

            checkTimer = 0f;
            if (hover != lastHover)
            {
                lastHover = hover;
                HoverCardAnim(hover);
            }
        }

        //点击事件
        public void OnPointerClick(PointerEventData eventData)
        {

        }


        #region 外部调用方法
        //设置卡牌顶层显示的方法
        public void SetCardTop()
        {
            transform.SetAsLastSibling();
        }


        //设置卡牌信息的方法
        public void SetCardInfo()
        {

        }

        //使用卡牌
        public void UseCard()
        {
            UseCardAnimation();
        }
        #endregion


        #region 卡牌动画效果
        /// <summary>
        /// 传入参数为false取消悬浮状态
        /// true播放悬浮状态
        /// </summary>
        /// <param name="isHover"></param>
        private void HoverCardAnim(bool isHover)
        {
            Debug.Log("播放卡牌上浮和轻微放大动画" + isHover);
            if (playHoverCor != null) StopCoroutine(playHoverCor);
            playHoverCor = StartCoroutine(PlayHoverAnimCor(isHover));
        }

        private IEnumerator PlayHoverAnimCor(bool isHover)
        {
            float from = visual.anchoredPosition.y;
            float to = isHover ? 100f : 0f;
            float t = 0f;

            while (t < 0.2f)
            {
                t += UnityEngine.Time.deltaTime;
                float ratio = Mathf.SmoothStep(0f, 1f, t / 0.2f);
                visual.anchoredPosition = new Vector2(0f, Mathf.Lerp(from, to, ratio));
                yield return null;
            }
            visual.anchoredPosition = new Vector2(0f, to);
            playHoverCor = null;
        }


        //获得卡牌后的动画效果：屏幕中心显示，然后移动到卡牌容器位置
        public void ShowCardAnimation()
        {

        }

        //使用卡牌的动画效果
        public void UseCardAnimation()
        {

        }



        #endregion

    }
}
