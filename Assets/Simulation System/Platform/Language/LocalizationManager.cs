using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Stores the active language code and the loaded pack, and answers key lookups.
///
/// It deliberately does NOT keep a list of labels to update. It fires
/// OnLanguageChanged; every LocalizedText subscribes for itself. That means
/// there is nothing to register with, nothing to clean up when objects are
/// destroyed, and no way for a runtime-spawned prefab to be missed.
/// </summary>
public class LocalizationManager : MonoBehaviour
{
    private const string PREF_KEY = "localization.language";

    private static LocalizationManager _instance;
    private LanguageListLoader listLoader;
    public static LocalizationManager Instance
    {
        get
        {
            if (_instance != null) return _instance;

            _instance = FindFirstObjectByType<LocalizationManager>();
            if (_instance == null)
                _instance = new GameObject("[LocalizationManager]").AddComponent<LocalizationManager>();

            return _instance;
        }
    }

    private void Awake()
    {
        if (_instance != null && _instance != this) { Destroy(gameObject); return; }

        _instance = this;
        DontDestroyOnLoad(gameObject);

        // Restore before any UI Awake/OnEnable runs, so labels come up correct
        // on frame one instead of flashing English first.
        currentLanguage = PlayerPrefs.GetString(PREF_KEY, defaultLanguage);

        listLoader = GetComponent<LanguageListLoader>();
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    // ---------------------------------------------------------------------
    // State
    // ---------------------------------------------------------------------

    [Tooltip("Language the scene and asset bundles are authored in. Overwritten by the API's default_language.")]
    [SerializeField] private string defaultLanguage = "en";

    [Tooltip("Authoring table, used only to resolve a key from its English source text - see GetTextBySource. " +
             "Not required for normal key-based lookups; a downloaded JSON pack never carries source text.")]
    [SerializeField] private LocalizationTable table;

    private LocalizationPackage currentPackage;
    private string currentLanguage;
    private List<string> listOfLanguages;

    public LanguageSelector languageSelector;
    /// <summary>Active language code. Never null.</summary>
    public string CurrentLanguage =>
        string.IsNullOrEmpty(currentLanguage) ? defaultLanguage : currentLanguage;

    public string DefaultLanguage => defaultLanguage;

    /// <summary>True when the active language is the authored one, so no pack is needed.</summary>
    public bool IsDefaultLanguage =>
        string.Equals(CurrentLanguage, defaultLanguage, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fires on a language switch AND when a pack finishes loading. The second
    /// case is what makes download order irrelevant: whatever lands last
    /// refreshes everything already onscreen.
    /// </summary>
    public event Action OnLanguageChanged;

    /// <summary>Lets the API response drive the default instead of the inspector value.</summary>
    public void SetDefaultLanguage(string languageCode)
    {
        if (!string.IsNullOrWhiteSpace(languageCode)) defaultLanguage = languageCode;
    }

    // ---------------------------------------------------------------------
    // The one method the language buttons call
    // ---------------------------------------------------------------------

    /// <summary>
    /// Stores the code, drops the old pack, fires the event.
    /// Fires immediately, so labels snap to their authored fallbacks while the
    /// new pack downloads - the UI is never blank.
    /// </summary>
    public void SetLanguage(string languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode)) return;
        if (string.Equals(currentLanguage, languageCode, StringComparison.OrdinalIgnoreCase)) return;

        currentLanguage = languageCode;
        PlayerPrefs.SetString(PREF_KEY, languageCode);
        PlayerPrefs.Save();


        ClearPackage();   // Old-language entries must not survive the switch.
        RaiseChanged();
    }

    // ---------------------------------------------------------------------
    // Package loading
    // ---------------------------------------------------------------------
    /// <summary>
    /// Parses a localization.json payload. Returns false and keeps existing data
    /// on failure, so a corrupt file cannot blank the UI.
    /// </summary>
    public bool LoadFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;

        LocalizationPackage parsed;
        try
        {
            parsed = JsonUtility.FromJson<LocalizationPackage>(json);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Localization] Parse failed: {e.Message}");
            return false;
        }

        // JsonUtility returns default(T) rather than throwing on unrelated JSON.
        if (parsed.textData == null && parsed.audioData == null)
        {
            Debug.LogWarning("[Localization] JSON had no textData or audioData.");
            return false;
        }

        if (parsed.textData == null) parsed.textData = Array.Empty<TextData>();
        if (parsed.audioData == null) parsed.audioData = Array.Empty<AudioData>();

        currentPackage = parsed;

        Debug.Log($"[Localization] Loaded {parsed.textData.Length} text / {parsed.audioData.Length} audio entries.");

        RaiseChanged();
        return true;
    }

    /// <summary>
    /// Installs an already-built package. Used by the main scene, which reads
    /// its translations from a LocalizationTable asset rather than JSON.
    /// </summary>
    public void LoadPackage(LocalizationPackage package)
    {
        if (package.textData == null) package.textData = Array.Empty<TextData>();
        if (package.audioData == null) package.audioData = Array.Empty<AudioData>();

        currentPackage = package;

        Debug.Log($"[Localization] Loaded {package.textData.Length} text entries for '{package.languageCode}'.");

        RaiseChanged();
    }

    public void ClearPackage()
    {
        AudioData[] entries = currentPackage.audioData;
        if (entries != null)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].clip == null) continue;

                Destroy(entries[i].clip);   // Downloaded clips are ours to free.
                entries[i].clip = null;
            }
        }

        currentPackage = default;
    }

    private void RaiseChanged()
    {
        // Isolated so one throwing listener cannot stop the rest from updating.
        try { OnLanguageChanged?.Invoke(); }
        catch (Exception e) { Debug.LogError($"[Localization] Listener threw: {e}"); }
    }

    // ---------------------------------------------------------------------
    // Lookups
    // ---------------------------------------------------------------------

    public string GetText(string key, string fallbackText) =>
        TryGetText(key, out string value) ? value : fallbackText;

    /// <summary>
    /// Looks up a key with no fallback string to fall back to. Returns the
    /// translated text, or null if the table has no non-empty entry for it -
    /// so a null check alone tells the caller whether the key was found.
    /// </summary>
    public string GetText(string key) => TryGetText(key, out string value) ? value : null;

    /// <summary>
    /// Looks the key up without masking a miss behind a fallback - use this when
    /// calling code needs to know whether a translation actually exists
    /// (validation, conditional UI, debug tooling) rather than just wanting a
    /// displayable string. A present-but-empty entry counts as "not translated
    /// yet" and returns false, same as GetText's fallback rule.
    ///
    /// Checks the currently loaded runtime package first, then falls back to the
    /// `table` asset directly if assigned. This matters because nothing loads a
    /// package for the DEFAULT language - there is nothing to download, the
    /// scene is already authored in it - so currentPackage is empty until a
    /// non-default language is actively selected. Without this fallback, a key
    /// that is genuinely defined in the table would read as "missing" any time
    /// no package happens to be loaded yet.
    /// </summary>
    public bool TryGetText(string key, out string value)
    {
        value = null;
        if (string.IsNullOrEmpty(key)) return false;

        TextData[] entries = currentPackage.textData;
        if (entries != null)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                if (!string.Equals(entries[i].key, key, StringComparison.Ordinal)) continue;
                if (string.IsNullOrEmpty(entries[i].value)) return false;

                value = entries[i].value;
                return true;
            }
        }

        if (table == null) return false;

        int tableIndex = table.IndexOfKey(key);
        if (tableIndex < 0) return false;

        string tableValue = IsDefaultLanguage
            ? table.entries[tableIndex].sourceText
            : table.GetTranslation(tableIndex, CurrentLanguage);

        if (string.IsNullOrEmpty(tableValue)) return false;

        value = tableValue;
        return true;
    }

    /// <summary>True when the currently loaded table has a non-empty translation for this key.</summary>
    public bool HasKey(string key) => TryGetText(key, out _);

    /// <summary>
    /// Resolves a translation from its AUTHORED (English) source text instead of a
    /// key - scans the assigned authoring table's sourceText column for an exact
    /// match, then looks up that entry's key the normal way. Requires the `table`
    /// field above to be assigned; a downloaded JSON pack never carries source
    /// text, only key/value pairs, so it cannot support this lookup on its own.
    /// Falls back to returning sourceText unchanged if the table is missing, has
    /// no matching row, or that row has no translation yet - same "never blank"
    /// guarantee as the key-based GetText overload.
    /// </summary>
    public string GetTextBySource(string sourceText)
    {
        if (string.IsNullOrEmpty(sourceText) || table == null) return sourceText;

        for (int i = 0; i < table.entries.Length; i++)
        {
            if (!string.Equals(table.entries[i].sourceText, sourceText, StringComparison.Ordinal)) continue;
            return GetText(table.entries[i].key, sourceText);
        }

        return sourceText;
    }

    public AudioClip GetAudio(string key, AudioClip fallbackClip)
    {
        if (string.IsNullOrEmpty(key)) return fallbackClip;

        AudioData[] entries = currentPackage.audioData;
        if (entries == null) return fallbackClip;

        for (int i = 0; i < entries.Length; i++)
            if (string.Equals(entries[i].key, key, StringComparison.Ordinal))
                return entries[i].clip != null ? entries[i].clip : fallbackClip;

        return fallbackClip;
    }

    // ---------------------------------------------------------------------
    // Audio binding, used by LocalizationDownloader
    // ---------------------------------------------------------------------
    //
    // AudioData is a struct: `foreach (var a in audioData) a.clip = x;` assigns
    // to a throwaway copy and silently does nothing. Clips can ONLY be attached
    // through the array indexer, which is why this is index-driven.

    public int AudioEntryCount => currentPackage.audioData?.Length ?? 0;

    public bool TryGetAudioEntryInfo(int index, out string key, out string fileName)
    {
        key = null;
        fileName = null;

        AudioData[] entries = currentPackage.audioData;
        if (entries == null || index < 0 || index >= entries.Length) return false;

        key = entries[index].key;
        fileName = entries[index].fileName;
        return true;
    }

    public void AssignAudioClip(int index, AudioClip clip)
    {
        AudioData[] entries = currentPackage.audioData;
        if (entries == null || index < 0 || index >= entries.Length) return;

        entries[index].clip = clip;   // In place. Do not refactor to foreach.
    }

    /// <summary>Call after a batch of clips lands so audio-driven content refreshes.</summary>
    public void NotifyAudioReady() => RaiseChanged();


    public void GetAllLanguages()
    {
        var collections = NewAPIManager.Instance.GetAPICollections();
        var langs = collections.GetLanguages();
        Debug.Log("Languages : " + langs);

        LanguageListResponse resquest = new LanguageListResponse()
        {
            default_language = currentLanguage,
            languages = listOfLanguages
        };
        string response = JsonUtility.ToJson(resquest);
        StartCoroutine(NewAPIManager.Instance.PostWebRequest(langs, response, (res) =>
        {
            Debug.Log("Ressss" + res);
            PopulateLanguages(res);
        }, true));
    }

    public void PopulateLanguages(string languageResponse)
    {
        Debug.Log("Languagess" + languageResponse);

        LanguageListResponse list = JsonUtility.FromJson<LanguageListResponse>(languageResponse);

        Debug.Log("Default  : " + list.default_language);
        Debug.Log("Languagee 1" + list.languages[0]);
        Debug.Log("Languagee 2" + list.languages[01]);

        languageSelector.Build(list);
    }

}

    
   