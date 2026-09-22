using BeatSaberMarkupLanguage.Components;
using TMPro;
using UnityEngine;

namespace PlaylistManager.Types
{
    public class FolderCell : MonoBehaviour
    {
        private TextMeshProUGUI text;

        private TextMeshProUGUI Text
        {
            get
            {
                if (text == null)
                {
                    text = BeatSaberMarkupLanguage.BeatSaberUI.CreateCurvedUIText(transform.Find("Wrapper").GetComponent<RectTransform>(), "", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -5), new Vector2(60, 10));
                    text.alignment = TextAlignmentOptions.Center;
                    text.overflowMode = TextOverflowModes.Ellipsis;
                    text.fontSize = 2.5f;
                    var rectTransform = text.rectTransform;
                    rectTransform.anchorMin = Vector2.zero;
                    rectTransform.anchorMax = Vector2.one;
                    rectTransform.sizeDelta = new Vector2(-3, -3);
                }
                return text;
            }
        }

        private BSMLBoxTableCell tableCell;
        private BSMLBoxTableCell TableCell => tableCell ??= GetComponent<BSMLBoxTableCell>();

        public BSMLBoxTableCell PopulateCell(Sprite sprite, string text = "")
        {
            TableCell.SetData(sprite);
            Text.text = text;
            return TableCell;
        }
    }
}
