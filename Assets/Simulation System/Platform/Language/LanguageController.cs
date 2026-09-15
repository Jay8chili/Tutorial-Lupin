using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Orchestrates a language change from the main scene.
///
/// The order matters, and it is deliberately "fail visible, never blank":
///
///   1. Tell the manager the new language. Foreign packs are purged, the change
///      event fires, and every LocalizedText snaps back to its authored fallback.
///      The UI is now readable in the default language - never empty.
///   2. Register the CORE pack from Resources. This is shipped in the build, so
///      it resolves synchronously and the main scene is localized this frame.
///   3. Kick off the simulation pack download. This is remote and slow, so it
///      runs async. If it fails, the bundle content stays in its authored
///      language and nothing else is affected.
///
/// Splitting core from downloaded is the key decision: main-scene UI must not
/// wait on (or be broken by) the network.
///
/// Core packs live at: Assets/Resources/Localization/core_{code}.json
/// Note the .json extension - Unity imports it as a TextAsset, and
/// Resources.Load takes the path WITHOUT the extension.
/// </summary>
public class LanguageController : MonoBehaviour
{
    public const string CORE_PACKAGE_ID = "core";
    public const string SIMULATION_PACKAGE_ID = "simulation";

    [Header("Core pack (shipped in build)")]
    [Tooltip("Resources path prefix. The file 'Resources/{prefix}{code}.json' is loaded.")]
    [SerializeField] private string coreResourcePrefix = "Localization/core_";

    [Header("Downloaded pack")]
    [Tooltip("Leave empty to skip the download step entirely (e.g. main-menu-only builds).")]
    [SerializeField] private LocalizationDownloader downloader;

    [Tooltip("Start the simulation pack download as soon as the language changes. Turn off to download it yourself right before loading the Asset Bundle.")]
    [SerializeField] private bool downloadSimulationPackOnChange = true;

    /// <summary>True while the remote pack is being fetched. Drive a spinner off this.</summary>
    public bool IsDownloading => downloader != null && downloader.IsBusy;

    /*private void Start()
    {
        // The manager restored the saved code in Awake; load its core pack so a
        // relaunch comes up already localized.
        LoadCorePackage(LocalizationManager.Instance.CurrentLanguage);
    }

    // ---------------------------------------------------------------------
    // Public entry point - wire this to your language buttons/dropdown
    // ---------------------------------------------------------------------

    /// <summary>
    /// Switches to <paramref name="languageCode"/> ("es", "fr", ...).
    /// Main-scene text updates immediately; simulation audio/text follows when
    /// the download lands. <paramref name="onSimulationPackReady"/> reports
    /// whether the remote pack succeeded - it is safe to ignore.
    /// </summary>
    public void SelectLanguage(string languageCode, Action<bool> onSimulationPackReady = null)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            onSimulationPackReady?.Invoke(false);
            return;
        }

        // 1 + 2: language switch, then immediate core localization.
        LocalizationManager.Instance.SetLanguage(languageCode);
        LoadCorePackage(languageCode);

        // 3: remote pack, best-effort.
        if (!downloadSimulationPackOnChange || downloader == null)
        {
            onSimulationPackReady?.Invoke(false);
            return;
        }

        DownloadSimulationPackage(languageCode, onSimulationPackReady);
    }*/

    /// <summary>
    /// Fetches the simulation pack for the active language. Call this directly
    /// if you would rather download lazily, right before the Asset Bundle loads.
    /// </summary>
    /*public void DownloadSimulationPackage(string languageCode, Action<bool> onComplete = null)
    {
        if (downloader == null)
        {
            Debug.LogWarning("[LanguageController] No downloader assigned - simulation content will use bundled defaults.");
            onComplete?.Invoke(false);
            return;
        }

        downloader.DownloadLanguage(SIMULATION_PACKAGE_ID, languageCode, onComplete);
    }*//*

    // ---------------------------------------------------------------------
    // Core pack
    // ---------------------------------------------------------------------
*/
  /*  private void LoadCorePackage(string languageCode)
    {
        // The default language needs no pack: the scene is already authored in it.
        if (LocalizationManager.Instance.IsDefaultLanguage)
        {
            LocalizationManager.Instance.RemovePackage(CORE_PACKAGE_ID);
            return;
        }

        string path = coreResourcePrefix + languageCode;
        TextAsset asset = Resources.Load<TextAsset>(path);

        if (asset == null)
        {
            // Not fatal - every label falls back to its authored string.
            Debug.LogWarning($"[LanguageController] No core pack at 'Resources/{path}'. " +
                             "Main scene will show authored text.");
            return;
        }

        LocalizationManager.Instance.RegisterPackageFromJson(CORE_PACKAGE_ID, asset.text);
        Resources.UnloadAsset(asset);   // The parsed structs are the copy we keep.
    }*/
}
