using LightSide;
using TMPro;
using UnityEngine;

namespace SimulationSystem.V02.Utility
{
    /// <summary>
    /// Central switch for scripts that must drive either a UniText or a legacy
    /// TMP_Text label from the same call site during the UniText migration.
    /// UniText always wins when both are assigned.
    /// </summary>
    public static class TextCompat
    {
        /// <summary>Writes <paramref name="value"/> to whichever label is assigned.</summary>
        public static void SetText(UniText uniText, TMP_Text tmpText, string value)
        {
            string TrnaslatedValue = LocalizationManager.Instance.GetTextBySource(value);
            if (uniText != null) { uniText.Text = value; return; }
            if (tmpText != null) tmpText.text = value;
        }

        /// <summary>Reads from whichever label is assigned, preferring UniText.</summary>
        public static string GetText(UniText uniText, TMP_Text tmpText)
        {
            if (uniText != null) return uniText.Text;
            if (tmpText != null) return tmpText.text;
            return string.Empty;
        }

        /// <summary>True if either label is assigned.</summary>
        public static bool HasTarget(UniText uniText, TMP_Text tmpText) => uniText != null || tmpText != null;

        /// <summary>Writes <paramref name="color"/> to whichever label is assigned.</summary>
        public static void SetColor(UniText uniText, TMP_Text tmpText, Color color)
        {
            if (uniText != null) { uniText.color = color; return; }
            if (tmpText != null) tmpText.color = color;
        }

        /// <summary>
        /// Fills in a missing UniText reference by looking on the same GameObject as
        /// the TMP_Text field, so existing Inspector-wired TMP_Text fields keep working
        /// while objects that only carry a UniText resolve automatically.
        /// </summary>
        public static void ResolveUniText(ref UniText uniText, TMP_Text tmpText)
        {
            if (uniText != null || tmpText == null) return;
            uniText = tmpText.GetComponent<UniText>();
        }
    }
}
