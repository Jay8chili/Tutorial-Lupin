using System;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Spawns one LanguageButton per code from the API response, and handles clicks.
///
/// Click flow:
///   1. LocalizationManager.SetLanguage(code) - stores the code and fires the
///      event. Every LocalizedText updates here. Static text is done.
///   2. Download the pack, unless the code is the default language (the scene
///      and bundles are already authored in it, so there is nothing to fetch).
///   3. The download fires the event again on completion, refreshing everything
///      a second time. Nothing needs to know which finished first.
/// </summary>
public class LanguageSelector : MonoBehaviour
{
    [Header("Spawning")]
    [SerializeField] private LanguageButton buttonPrefab;

    [Tooltip("Parent for spawned buttons - usually a Horizontal or Vertical Layout Group.")]
    [SerializeField] private Transform buttonContainer;

    [Header("Main scene translations")]
    [Tooltip("Table asset holding the main scene's translations. Read directly at runtime - no download, no JSON.")]
    [SerializeField] private LocalizationTable table;

    [Header("Simulation pack (asset bundle content)")]
    [Tooltip("Optional. Only needed if asset bundle text/audio is also localized. The main scene does not use this.")]
    [SerializeField] private LocalizationDownloader downloader;

    [Tooltip("Fetch the simulation ZIP when the language changes. Turn off to download it later, right before loading the bundle.")]
    [SerializeField] private bool downloadSimulationPack = false;


    public UnityEvent OnLanguageSelected;
    private LanguageButton[] spawnedButtons = Array.Empty<LanguageButton>();

    /// <summary>
    /// Builds the button row from the API payload:
    ///
    ///     var response = JsonUtility.FromJson&lt;LanguageListResponse&gt;(json);
    ///     selector.Build(response);
    /// </summary>
    public void Build(LanguageListResponse response)
    {
        if (response.languages == null || response.languages.Count == 0)
        {
            Debug.LogWarning("[LanguageSelector] Response contained no languages.");
            return;
        }

        if (buttonPrefab == null || buttonContainer == null)
        {
            Debug.LogWarning("[LanguageSelector] Prefab or container not assigned.");
            return;
        }

        // The API is the source of truth for which language the content is
        // authored in - that is what decides when a download can be skipped.
        LocalizationManager.Instance.SetDefaultLanguage(response.default_language);

        ClearButtons();

        spawnedButtons = new LanguageButton[response.languages.Count];

        for (int i = 0; i < response.languages.Count; i++)
        {
            string code = response.languages[i];
            if (string.IsNullOrWhiteSpace(code)) continue;

            LanguageButton instance = Instantiate(buttonPrefab, buttonContainer);
            instance.name = $"LanguageButton_{code}";
            instance.Setup(code, GetDisplayName(code), this);

            spawnedButtons[i] = instance;
        }

        //RefreshSelectedStates();
    }

    private void ClearButtons()
    {
        for (int i = 0; i < spawnedButtons.Length; i++)
            if (spawnedButtons[i] != null) Destroy(spawnedButtons[i].gameObject);

        spawnedButtons = Array.Empty<LanguageButton>();
    }

    /// <summary>Called by a button. Safe to call directly from code too.</summary>
    public void SelectLanguage(string languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode)) return;

        // Static text updates on this line, before any download starts.
        LocalizationManager.Instance.SetLanguage(languageCode);
        //RefreshSelectedStates();

        // Default language needs no pack: SetLanguage already cleared the old
        // one, so every label is showing its authored string - which IS the
        // default language. Nothing to load.
        if (LocalizationManager.Instance.IsDefaultLanguage) 
        {
            OnLanguageSelected?.Invoke();

            return; 
        }
        OnLanguageSelected?.Invoke();
        // Main scene: read the table asset directly. It is already in the build,
        // so this is synchronous - the labels update on this very line.
        if (table != null && LocalizationManager.Instance.CurrentLanguage != LocalizationManager.Instance.DefaultLanguage)
        {
            LocalizationManager.Instance.LoadPackage(table.BuildPackage(languageCode));
        }
        else
        {
            Debug.LogWarning("[LanguageSelector] No table assigned - authored text will be used.");
        }

        // Asset bundle content is separate: it lives outside the build, so it
        // still has to be downloaded. Leave off if you only localize the scene.
        if (downloadSimulationPack && downloader != null)
            downloader.DownloadLanguage(languageCode);
    }

    private void RefreshSelectedStates()
    {
        string active = LocalizationManager.Instance.CurrentLanguage;

        for (int i = 0; i < spawnedButtons.Length; i++)
        {
            if (spawnedButtons[i] == null) continue;

            bool isActive = string.Equals(spawnedButtons[i].LanguageCode, active,
                                          StringComparison.OrdinalIgnoreCase);
            spawnedButtons[i].SetSelected(isActive);
        }
    }

    /// <summary>
    /// Endonym for a code - the language's name in its own language, which is
    /// what a picker should show. Unknown codes fall back to the uppercased code.
    /// </summary>
    private static string GetDisplayName(string code)
    {
        switch (code.ToLowerInvariant())
        {
            case "en": return "English";
            case "es": return "Espanol";
            case "fr": return "Francais";
            case "de": return "Deutsch";
            case "pt": return "Portugues";
            case "it": return "Italiano";
            case "ja": return "\u65E5\u672C\u8A9E";
            case "ko": return "\uD55C\uAD6D\uC5B4";
            case "zh": return "\u4E2D\u6587";
            case "hi": return "\u0939\u093F\u0928\u094D\u0926\u0940";
            case "ar": return "\u0627\u0644\u0639\u0631\u0628\u064A\u0629";
            default: return code.ToUpperInvariant();
        }
    }
}