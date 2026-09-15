using System;
using UnityEngine;

/// <summary>
/// Authoring asset behind the localization table window. Lives in the project,
/// NOT in a build - it exports to localization.json, which is what ships.
///
/// Still structs + arrays, no Dictionary, to stay consistent with the runtime
/// data and to serialize cleanly in the inspector.
///
/// Create via Assets -> Create -> Localization -> Table.
/// </summary>
[CreateAssetMenu(fileName = "LocalizationTable", menuName = "Localization/Table")]
public class LocalizationTable : ScriptableObject
{
    /// <summary>One language's value for a key.</summary>
    [Serializable]
    public struct Translation
    {
        public string languageCode;
        public string value;

        /// <summary>
        /// True when this came from machine translation and has not been
        /// reviewed. Machine output is a first draft, not a shippable string -
        /// this is what lets the table window flag rows that still need eyes.
        /// </summary>
        public bool machineTranslated;

        /// <summary>Source text this was translated FROM. If the English later
        /// changes, a stale machine translation can be detected and re-run.</summary>
        public string translatedFrom;
    }

    /// <summary>One row of the table.</summary>
    [Serializable]
    public struct Entry
    {
        public string key;

        /// <summary>Authored text in the default language. Filled by the scanner.</summary>
        [TextArea(1, 3)] public string sourceText;

        /// <summary>Where the scanner found it, for context while translating.</summary>
        public string origin;

        public Translation[] translations;
    }

    [Tooltip("Language the scene is authored in. Rows store its text in sourceText, not in translations.")]
    public string defaultLanguage = "en";

    [Tooltip("Codes to show as columns. Should match the API's languages list, minus the default.")]
    public string[] languages = { "es" };

    public Entry[] entries = Array.Empty<Entry>();

    // ---------------------------------------------------------------------

    public int IndexOfKey(string key)
    {
        for (int i = 0; i < entries.Length; i++)
            if (string.Equals(entries[i].key, key, StringComparison.Ordinal))
                return i;

        return -1;
    }

    /// <summary>
    /// Adds a row, or refreshes an existing one's source text.
    /// Existing translations are never touched - re-scanning after a UI tweak
    /// must not discard work already done by translators.
    /// </summary>
    public void AddOrUpdate(string key, string sourceText, string origin)
    {
        if (string.IsNullOrEmpty(key)) return;

        int index = IndexOfKey(key);
        if (index >= 0)
        {
            entries[index].sourceText = sourceText;
            entries[index].origin = origin;
            return;
        }

        Array.Resize(ref entries, entries.Length + 1);
        entries[entries.Length - 1] = new Entry
        {
            key = key,
            sourceText = sourceText,
            origin = origin,
            translations = Array.Empty<Translation>()
        };
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= entries.Length) return;

        for (int i = index; i < entries.Length - 1; i++) entries[i] = entries[i + 1];

        Array.Resize(ref entries, entries.Length - 1);
    }

    /// <summary>Value for a key in a language, or empty when untranslated.</summary>
    public string GetTranslation(int entryIndex, string languageCode)
    {
        if (entryIndex < 0 || entryIndex >= entries.Length) return string.Empty;

        Translation[] list = entries[entryIndex].translations;
        if (list == null) return string.Empty;

        for (int i = 0; i < list.Length; i++)
            if (string.Equals(list[i].languageCode, languageCode, StringComparison.OrdinalIgnoreCase))
                return list[i].value;

        return string.Empty;
    }

    public void SetTranslation(int entryIndex, string languageCode, string value,
                               bool machineTranslated = false, string translatedFrom = null)
    {
        if (entryIndex < 0 || entryIndex >= entries.Length) return;

        Translation[] list = entries[entryIndex].translations ?? Array.Empty<Translation>();

        for (int i = 0; i < list.Length; i++)
        {
            if (!string.Equals(list[i].languageCode, languageCode, StringComparison.OrdinalIgnoreCase)) continue;

            // Struct in array: index-assign, never through a copy.
            list[i].value = value;
            list[i].machineTranslated = machineTranslated;
            list[i].translatedFrom = translatedFrom;
            entries[entryIndex].translations = list;
            return;
        }

        Array.Resize(ref list, list.Length + 1);
        list[list.Length - 1] = new Translation
        {
            languageCode = languageCode,
            value = value,
            machineTranslated = machineTranslated,
            translatedFrom = translatedFrom
        };
        entries[entryIndex].translations = list;
    }

    /// <summary>True when a row's translation is machine output awaiting review.</summary>
    public bool IsMachineTranslated(int entryIndex, string languageCode)
    {
        if (entryIndex < 0 || entryIndex >= entries.Length) return false;

        Translation[] list = entries[entryIndex].translations;
        if (list == null) return false;

        for (int i = 0; i < list.Length; i++)
            if (string.Equals(list[i].languageCode, languageCode, StringComparison.OrdinalIgnoreCase))
                return list[i].machineTranslated;

        return false;
    }

    /// <summary>
    /// True when the English source changed after this was translated, leaving
    /// the translation stale.
    /// </summary>
    public bool IsStale(int entryIndex, string languageCode)
    {
        if (entryIndex < 0 || entryIndex >= entries.Length) return false;

        Translation[] list = entries[entryIndex].translations;
        if (list == null) return false;

        for (int i = 0; i < list.Length; i++)
        {
            if (!string.Equals(list[i].languageCode, languageCode, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(list[i].translatedFrom)) return false;

            return !string.Equals(list[i].translatedFrom, entries[entryIndex].sourceText, StringComparison.Ordinal);
        }

        return false;
    }

    // ---------------------------------------------------------------------

    /// <summary>
    /// Builds the runtime JSON for one language. Untranslated rows are omitted
    /// entirely rather than exported empty, so the runtime falls back to the
    /// authored string instead of rendering a blank label.
    /// </summary>
    public string ExportJson(string languageCode)
    {
        TextData[] texts = Array.Empty<TextData>();

        for (int i = 0; i < entries.Length; i++)
        {
            string value = GetTranslation(i, languageCode);
            if (string.IsNullOrEmpty(value)) continue;

            Array.Resize(ref texts, texts.Length + 1);
            texts[texts.Length - 1] = new TextData { key = entries[i].key, value = value };
        }

        LocalizationPackage package = new LocalizationPackage
        {
            languageCode = languageCode,
            textData = texts,
            audioData = Array.Empty<AudioData>()   // Audio is authored alongside the ZIP.
        };

        return JsonUtility.ToJson(package, true);
    }

    /// <summary>
    /// Builds a runtime package straight from the table - no JSON, no file I/O.
    /// This is how the MAIN SCENE gets its translations: the asset is already in
    /// the build, so there is nothing to download.
    ///
    /// Untranslated rows are skipped rather than added empty, so the runtime
    /// falls back to the authored string instead of rendering a blank label.
    /// </summary>
    public LocalizationPackage BuildPackage(string languageCode)
    {
        TextData[] texts = Array.Empty<TextData>();

        for (int i = 0; i < entries.Length; i++)
        {
            string value = GetTranslation(i, languageCode);
            if (string.IsNullOrEmpty(value)) continue;

            Array.Resize(ref texts, texts.Length + 1);
            texts[texts.Length - 1] = new TextData { key = entries[i].key, value = value };
        }

        return new LocalizationPackage
        {
            languageCode = languageCode,
            textData = texts,
            audioData = Array.Empty<AudioData>()   // Scene audio is not localized here.
        };
    }

    /// <summary>True when this table has any translation for the code.</summary>
    public bool SupportsLanguage(string languageCode)
    {
        if (string.Equals(languageCode, defaultLanguage, StringComparison.OrdinalIgnoreCase)) return true;

        for (int i = 0; i < languages.Length; i++)
            if (string.Equals(languages[i], languageCode, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>Count of rows with a value in this language, for the progress readout.</summary>
    public int TranslatedCount(string languageCode)
    {
        int count = 0;
        for (int i = 0; i < entries.Length; i++)
            if (!string.IsNullOrEmpty(GetTranslation(i, languageCode))) count++;

        return count;
    }
}