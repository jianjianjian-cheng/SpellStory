using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Controllers
{
    public class CardViewHover : MonoBehaviour, IPointerEnterHandler,
    IPointerExitHandler
    {
        [SerializeField] private RectTransform rectTransform;
        private float closeSpacing = 1200f;
        private float expandSpacing = 2150f;

        private Coroutine spacingCor;

        public void OnPointerEnter(PointerEventData eventData)
        {
            ExpandCard();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            CloseCard();
        }

        /// <summary>
        /// 展开卡牌动画效果入口
        /// </summary>
        private void ExpandCard()
        {
            if (spacingCor != null) StopCoroutine(spacingCor);
            spacingCor = StartCoroutine(TransitionSpacing(expandSpacing));
        }


        private IEnumerator TransitionSpacing(float target)
        {
            float time = 0f;
            float totalTime = 0.2f;
            float start = rectTransform.sizeDelta.x;  // 从当前值出发

            while (time < totalTime)
            {
                time += UnityEngine.Time.deltaTime;
                float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(time / totalTime));
                float x = Mathf.Lerp(start, target, t);
                rectTransform.sizeDelta = new Vector2(x, rectTransform.sizeDelta.y);
                yield return null;
            }
            rectTransform.sizeDelta = new Vector2(target, rectTransform.sizeDelta.y);
            spacingCor = null;
        }


        /// <summary>
        /// 收拢卡牌
        /// </summary>
        private void CloseCard()
        {
            if (spacingCor != null) StopCoroutine(spacingCor);
            spacingCor = StartCoroutine(TransitionSpacing(closeSpacing));
        }
    }
}
