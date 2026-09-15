using LightSide;   // UniText
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Sits on the language button prefab. The prefab knows nothing about which
/// language it represents until LanguageSelector calls Setup() after Instantiate.
///
/// Prefab contents: Button + a child UniText + this script.
/// Do NOT add a LocalizedText to the label - a language name should read in its
/// own language ("Espanol", not "Spanish"), so it is never translated.
///
/// Because UniText is a MaskableGraphic, it can serve as the Button's target
/// graphic directly if you want the tint transition to hit the text itself.
/// </summary>
public class LanguageButton : MonoBehaviour
{
    [SerializeField] private Button button;
    [SerializeField] private UniText label;

    [Tooltip("Optional. Enabled only on the button for the active language.")]
    [SerializeField] private GameObject selectedIndicator;

    private string languageCode;
    private LanguageSelector selector;

    public string LanguageCode => languageCode;

    private void Awake()
    {
        if (button == null) button = GetComponent<Button>();
        if (label == null) label = GetComponentInChildren<UniText>();
    }

    /// <summary>Binds this instance to one language code.</summary>
    public void Setup(string code, string displayName, LanguageSelector selector)
    {
        languageCode = code;
        this.selector = selector;

        // UniText shapes this through HarfBuzz, so endonyms in Arabic, Hindi,
        // Japanese etc. render correctly with no per-language font juggling.
        if (label != null) label.Text = displayName;
        
        if (button != null)
        {
            // An instantiated prefab carries any listeners wired on the source
            // asset in the inspector - clear before adding to avoid doubles.
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(HandleClick);
        }
    }

    private void HandleClick()
    {
        if (selector != null) selector.SelectLanguage(languageCode);
    }

    /// <summary>Called on every button whenever the active language changes.</summary>
    public void SetSelected(bool isSelected)
    {
        if (selectedIndicator != null) selectedIndicator.SetActive(isSelected);

        // Block re-clicking the language that is already active.
        if (button != null) button.interactable = !isSelected;
    }
}