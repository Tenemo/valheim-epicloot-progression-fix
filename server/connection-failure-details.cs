using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EpicLootProgressionFix
{
    internal static class ConnectionFailureDetails
    {
        internal static void Append(TMP_Text text, string reason, bool plainText = false)
        {
            if (plainText) text.richText = false;
            text.text = text.text.TrimEnd() + "\n\n" + reason;
            var box = text.rectTransform;
            var panel = (RectTransform)box.parent;
            var button = (RectTransform)panel.GetComponentInChildren<Button>(true).transform;
            float wider = Mathf.Max(0f, 600f - panel.sizeDelta.x);
            text.enableAutoSizing = false;
            text.fontSize = 18f;
            float taller = Mathf.Max(0f, text.GetPreferredValues(text.text, box.rect.width + wider, 0f).y - box.rect.height);
            panel.sizeDelta += new Vector2(wider, taller);
            box.sizeDelta += new Vector2(0f, taller);
            button.anchoredPosition -= new Vector2(0f, taller / 2f);
        }
    }
}
