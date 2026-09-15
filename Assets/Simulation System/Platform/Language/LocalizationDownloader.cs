using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Downloads localization_{code}.zip, unzips into persistentDataPath, loads the
/// JSON into the manager, then streams in the audio files.
///
/// Expected ZIP layout:
///     localization_es.zip
///       |- localization.json
///       \- Audio/
///            \- state01_prompt.wav
///
/// Failure policy: warn and abort cleanly at every step. A failed download means
/// content shows in its authored language - never blank text or silence.
///
/// Requires Player Settings -> Api Compatibility Level = .NET Standard 2.1 for
/// System.IO.Compression.ZipFile. Swap ExtractZip() for SharpZipLib otherwise.
/// </summary>
public class LocalizationDownloader : MonoBehaviour
{
    [Header("Remote")]
    [Tooltip("Base URL. '{baseUrl}/localization_{code}.zip' is requested.")]
    [SerializeField] private string baseUrl = "https://example.com/localization";

    [SerializeField] private int timeoutSeconds = 30;

    [Header("Pack layout")]
    [SerializeField] private string jsonFileName = "localization.json";
    [SerializeField] private string audioFolderName = "Audio";

    [Header("Caching")]
    [Tooltip("Reuse an already-extracted pack instead of re-downloading. Turn off while authoring content.")]
    [SerializeField] private bool useCachedPack = true;

    /// <summary>True while a download is in flight. Drive a loading spinner off this.</summary>
    public bool IsBusy { get; private set; }

    public static LocalizationDownloader Instance { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    /// <summary>Fetches and installs the pack for a language code.</summary>
    public void DownloadLanguage(string languageCode, Action<bool> onComplete = null)
    {
        if (IsBusy)
        {
            Debug.LogWarning("[LocalizationDownloader] Already downloading - request ignored.");
            onComplete?.Invoke(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(languageCode))
        {
            onComplete?.Invoke(false);
            return;
        }

        StartCoroutine(DownloadRoutine(languageCode, onComplete));
    }

    private IEnumerator DownloadRoutine(string languageCode, Action<bool> onComplete)
    {
        IsBusy = true;

        string zipName     = $"localization_{languageCode}.zip";
        string remoteUrl   = $"{baseUrl.TrimEnd('/')}/{zipName}";
        string zipPath     = Path.Combine(Application.persistentDataPath, zipName);
        string extractRoot = Path.Combine(Application.persistentDataPath, $"localization_{languageCode}");
        string jsonPath    = Path.Combine(extractRoot, jsonFileName);

        bool cached = useCachedPack && File.Exists(jsonPath);

        // ---- 1. Download -------------------------------------------------
        if (!cached)
        {
            // DownloadHandlerFile streams to disk rather than buffering the whole
            // archive in managed memory - important on mobile.
            using (UnityWebRequest request = UnityWebRequest.Get(remoteUrl))
            {
                request.downloadHandler = new DownloadHandlerFile(zipPath) { removeFileOnAbort = true };
                request.timeout = timeoutSeconds;

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[LocalizationDownloader] Download failed for '{remoteUrl}': " +
                                     $"{request.error}. Using authored content.");
                    SafeDelete(zipPath);
                    Finish(onComplete, false);
                    yield break;
                }
            }

            // ---- 2. Extract ----------------------------------------------
            if (!ExtractZip(zipPath, extractRoot))
            {
                SafeDelete(zipPath);
                Finish(onComplete, false);
                yield break;
            }

            SafeDelete(zipPath);   // Redundant once extracted.
        }

        // ---- 3. Load the JSON --------------------------------------------
        if (!File.Exists(jsonPath))
        {
            Debug.LogWarning($"[LocalizationDownloader] '{jsonFileName}' missing at '{extractRoot}'.");
            Finish(onComplete, false);
            yield break;
        }

        string json;
        try
        {
            json = File.ReadAllText(jsonPath);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[LocalizationDownloader] Could not read '{jsonPath}': {e.Message}");
            Finish(onComplete, false);
            yield break;
        }

        // This fires OnLanguageChanged, so all text updates the moment it lands.
        if (!LocalizationManager.Instance.LoadFromJson(json))
        {
            Finish(onComplete, false);
            yield break;
        }

        // ---- 4. Audio -----------------------------------------------------
        string audioFolder = Path.Combine(extractRoot, audioFolderName);
        if (!Directory.Exists(audioFolder))
        {
            // Not fatal: text is localized, audio falls back per key.
            Debug.LogWarning($"[LocalizationDownloader] No Audio folder at '{audioFolder}'. " +
                             "Text localized; audio will use authored clips.");
            Finish(onComplete, true);
            yield break;
        }

        yield return LoadAllAudio(audioFolder);

        LocalizationManager.Instance.NotifyAudioReady();
        Finish(onComplete, true);
    }

    private void Finish(Action<bool> onComplete, bool success)
    {
        IsBusy = false;
        onComplete?.Invoke(success);
    }

    // ---------------------------------------------------------------------
    // Per-simulation language assets
    //
    // Same zip layout as the global pack above (localization.json + Audio/),
    // but sourced from an explicit server-supplied URL instead of one built
    // from baseUrl+code, and scoped to one simulation instead of the whole
    // app. Deliberately independent of IsBusy/Finish above - a per-simulation
    // download can legitimately run alongside a global language-pack download.
    //
    // Download-and-cache only, vs. load-into-LocalizationManager, are kept as
    // two separate calls: the caller downloads ahead of time (e.g. alongside
    // a simulation bundle download) without disturbing whatever text is
    // currently on screen, then loads it in only once actually needed (e.g.
    // right before that simulation launches).
    // ---------------------------------------------------------------------

    /// <summary>
    /// Where a per-simulation language pack for (bundleCode, languageCode) is
    /// extracted to. Public so SimulationLocalizationInjector can find and read
    /// the same pack this class downloaded, without duplicating the naming
    /// convention.
    /// </summary>
    public static string GetSimulationExtractRoot(string bundleCode, string languageCode) =>
        Path.Combine(Application.persistentDataPath, $"simlang_{bundleCode}_{languageCode}");

    /// <summary>
    /// Downloads and unpacks a simulation's language asset zip to disk. Does NOT
    /// touch LocalizationManager - SimulationLocalizationInjector reads the
    /// extracted localization.json directly once the content is actually
    /// needed. Skips the network step entirely if already extracted, same
    /// cache-hit rule as the global pack's useCachedPack.
    /// </summary>
    public void DownloadSimulationLanguageAsset(string zipUrl, string bundleCode, string languageCode, Action<bool> onComplete = null)
    {
        if (string.IsNullOrWhiteSpace(zipUrl) || string.IsNullOrWhiteSpace(bundleCode))
        {
            onComplete?.Invoke(false);
            return;
        }

        StartCoroutine(DownloadSimulationLanguageRoutine(zipUrl, bundleCode, languageCode, onComplete));
    }

    private IEnumerator DownloadSimulationLanguageRoutine(string zipUrl, string bundleCode, string languageCode, Action<bool> onComplete)
    {
        string extractRoot = GetSimulationExtractRoot(bundleCode, languageCode);
        string jsonPath = Path.Combine(extractRoot, jsonFileName);

        if (useCachedPack && File.Exists(jsonPath))
        {
            onComplete?.Invoke(true);
            yield break;
        }

        string zipPath = Path.Combine(Application.persistentDataPath, $"simlang_{bundleCode}_{languageCode}.zip");
        Debug.Log("ZipPath " +  zipPath);
        using (UnityWebRequest request = UnityWebRequest.Get(zipUrl))
        {
            request.downloadHandler = new DownloadHandlerFile(zipPath) { removeFileOnAbort = true };
            request.timeout = timeoutSeconds;

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[LocalizationDownloader] Simulation language download failed for '{bundleCode}' ({zipUrl}): {request.error}.");
                SafeDelete(zipPath);
                onComplete?.Invoke(false);
                yield break;
            }
        }

        if (!ExtractZip(zipPath, extractRoot))
        {
            SafeDelete(zipPath);
            onComplete?.Invoke(false);
            yield break;
        }

        SafeDelete(zipPath);
        onComplete?.Invoke(true);
    }

    /// <summary>
    /// Walks audioData by index and streams each file in. Index-driven because
    /// AudioData is a struct - see LocalizationManager for why foreach fails.
    /// </summary>
    private IEnumerator LoadAllAudio(string audioFolder)
    {
        LocalizationManager loc = LocalizationManager.Instance;
        int total = loc.AudioEntryCount;
        int loaded = 0;

        for (int i = 0; i < total; i++)
        {
            if (!loc.TryGetAudioEntryInfo(i, out string key, out string fileName)) continue;
            if (string.IsNullOrEmpty(fileName)) continue;

            string fullPath = Path.Combine(audioFolder, fileName);
            if (!File.Exists(fullPath))
            {
                Debug.LogWarning($"[LocalizationDownloader] Missing audio for '{key}': {fullPath}");
                continue;
            }

            AudioType audioType = ResolveAudioType(fullPath);
            if (audioType == AudioType.UNKNOWN)
            {
                Debug.LogWarning($"[LocalizationDownloader] Unsupported extension on '{fileName}' (.wav/.ogg/.mp3).");
                continue;
            }

            // Local files still go through UnityWebRequest - it is the only
            // supported runtime path to an AudioClip outside Resources.
            string uri = new Uri(fullPath).AbsoluteUri;   // file:// with escaping

            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(uri, audioType))
            {
                if (request.downloadHandler is DownloadHandlerAudioClip handler)
                    handler.streamAudio = true;   // Keeps memory flat for narration.

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[LocalizationDownloader] Failed to load '{key}': {request.error}");
                    continue;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null) continue;

                clip.name = key;                 // Easier to spot in the profiler.
                loc.AssignAudioClip(i, clip);    // In-place struct mutation.
                loaded++;
            }
        }

        Debug.Log($"[LocalizationDownloader] Loaded {loaded}/{total} clips.");
    }

    private static AudioType ResolveAudioType(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".wav": return AudioType.WAV;
            case ".ogg": return AudioType.OGGVORBIS;
            case ".mp3": return AudioType.MPEG;
            default:     return AudioType.UNKNOWN;
        }
    }

    private static bool ExtractZip(string zipPath, string destinationRoot)
    {
        try
        {
            // Wipe first so a shrinking pack leaves no stale files behind.
            if (Directory.Exists(destinationRoot)) Directory.Delete(destinationRoot, true);

            Directory.CreateDirectory(destinationRoot);
            ZipFile.ExtractToDirectory(zipPath, destinationRoot);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[LocalizationDownloader] Extract failed for '{zipPath}': {e.Message}");
            return false;
        }
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) { Debug.LogWarning($"[LocalizationDownloader] Delete failed '{path}': {e.Message}"); }
    }
}
