using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using LightSide;
using SimulationSystem.V02.Utility;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Localizes one loaded simulation bundle from its own pre-fetched language
/// pack (see ContentManager / LocalizationDownloader.DownloadSimulationLanguageAsset),
/// using the registry-based JSON SceneToSpanishJson exports - see
/// SimulationLocalizationData.cs for the exact shape.
///
/// Object_GUID / InteractionGameObjectGUID are plain ids ("01", "02", ...)
/// resolved through this scene's LocalizationObjectRegistry: a serialized
/// GameObject reference the editor tool assigns once, which survives the
/// scene -> AssetBundle -> runtime pipeline unmodified, unlike a computed
/// identity (GlobalObjectId, instance ID) which cannot be recomputed at
/// runtime at all.
///
/// Fully self-contained: does not read or write LocalizationManager's
/// text/audio package - that stays reserved for the platform UI's own
/// LocalizedText-driven content. Untranslated fields are simply left as
/// whatever the bundle authored, so a missing pack or a partial translation
/// never blanks anything.
///
/// Call CaptureAndInject() once after the bundle's scene has loaded (see
/// SimulationManager.Start()).
/// </summary>
public class SimulationLocalizationInjector : MonoBehaviour
{
    [Tooltip("Log every id in the pack that has no matching registry entry, and every audio file that's missing.")]
    [SerializeField] private bool logMissingEntries = false;

    private bool captured;

    /// <summary>Call once after the bundle's scene has loaded. Safe to call more than once - only the first call does anything.</summary>
    public void CaptureAndInject()
    {
        if (captured) return;
        captured = true;

        StartCoroutine(InjectRoutine());
    }

    private IEnumerator InjectRoutine()
    {
        string bundleCode = AssetBundleManager.Instance != null ? AssetBundleManager.Instance.CurrentBundleCode : null;
        if (string.IsNullOrEmpty(bundleCode))
        {
            Debug.LogWarning("[SimulationLocalizationInjector] No current bundle code - skipping.");
            yield break;
        }

        string languageCode = LocalizationManager.Instance.CurrentLanguage;
        string extractRoot = LocalizationDownloader.GetSimulationExtractRoot(bundleCode, languageCode);
        string jsonPath = Path.Combine(extractRoot, "localization.json");

        if (!File.Exists(jsonPath))
        {
            Debug.LogWarning($"[SimulationLocalizationInjector] No pre-fetched pack at '{jsonPath}' - using authored content.");
            yield break;
        }

        SimulationLocalizationRoot root;
        try
        {
            root = JsonUtility.FromJson<SimulationLocalizationRoot>(File.ReadAllText(jsonPath));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SimulationLocalizationInjector] Could not parse '{jsonPath}': {e.Message}");
            yield break;
        }

        List<GameObjectLocalizationEntry> entries = root?.localization_json?.GameObjects;
        if (entries == null)
        {
            Debug.LogWarning($"[SimulationLocalizationInjector] '{jsonPath}' has no GameObjects.");
            yield break;
        }

        LocalizationObjectRegistry registry = LocalizationObjectRegistry.Instance;
        if (registry == null)
        {
            Debug.LogWarning("[SimulationLocalizationInjector] No LocalizationObjectRegistry in this scene - cannot resolve ids.");
            yield break;
        }

        string audioFolder = Path.Combine(extractRoot, "Audio");

        int states = 0, labels = 0, interactions = 0;

        for (int i = 0; i < entries.Count; i++)
        {
            GameObjectLocalizationEntry entry = entries[i];
            if (entry == null || string.IsNullOrEmpty(entry.Object_GUID)) continue;

            if (!registry.TryGetObject(entry.Object_GUID, out GameObject go) || go == null)
            {
                if (logMissingEntries)
                    Debug.Log($"[SimulationLocalizationInjector] No object registered for id '{entry.Object_GUID}'.");
            }
            else
            {
                SimulationState state = go.GetComponent<SimulationState>();
                if (state != null)
                {
                    if (!string.IsNullOrEmpty(entry.MainText)) state.promptText = entry.MainText;
                    if (!string.IsNullOrEmpty(entry.PrePromptText)) state.prePromptText = entry.PrePromptText;
                    if (!string.IsNullOrEmpty(entry.PostPromptText)) state.postPromptText = entry.PostPromptText;

                    yield return LoadAudioIfPresent(audioFolder, entry.AudioFile, clip => state.promptAudio = clip);
                    yield return LoadAudioIfPresent(audioFolder, entry.PrePromptAudioFile, clip => state.prePromptAudio = clip);
                    yield return LoadAudioIfPresent(audioFolder, entry.PostPromptAudioFile, clip => state.postPromptAudio = clip);

                    states++;
                }
                else if (ApplyGenericLabel(go, entry.MainText))
                {
                    labels++;
                }
            }

            if (entry.UIInteractions == null) continue;

            for (int u = 0; u < entry.UIInteractions.Count; u++)
            {
                UIInteractionLocalizationEntry uiEntry = entry.UIInteractions[u];
                if (uiEntry == null || string.IsNullOrEmpty(uiEntry.InteractionGameObjectGUID)) continue;

                if (!registry.TryGetObject(uiEntry.InteractionGameObjectGUID, out GameObject interactionGo) || interactionGo == null)
                {
                    if (logMissingEntries)
                        Debug.Log($"[SimulationLocalizationInjector] No object registered for interaction id '{uiEntry.InteractionGameObjectGUID}'.");
                    continue;
                }

                UIInteraction interaction = interactionGo.GetComponent<UIInteraction>();
                if (interaction == null || string.IsNullOrEmpty(uiEntry.UITextField)) continue;

                // BOTH targets, deliberately. A UIInteraction has two independent
                // text sources and which one is read depends on its mode:
                //   - scene-authored panel  -> the uiText/uiTextUni label
                //   - bot-handled (IsBotHandled) -> the plain `content` string,
                //     passed to AssistantManager.TriggerBotUI; the label is null
                //     in this mode and never read.
                // Writing only the label left every bot-handled step in the
                // authored language.
                interaction.content = uiEntry.UITextField;

                if (TextCompat.HasTarget(interaction.uiTextUni, interaction.uiText))
                    TextCompat.SetText(interaction.uiTextUni, interaction.uiText, uiEntry.UITextField);

                interactions++;
            }
        }

        Debug.Log($"[SimulationLocalizationInjector] Injected {states} states, {labels} labels, " +
                  $"{interactions} interactions ({languageCode}).");
    }

    /// <summary>Applies MainText to a non-step object's own label component. Returns false if there's nothing to write to, or nothing to write.</summary>
    private static bool ApplyGenericLabel(GameObject go, string mainText)
    {
        if (string.IsNullOrEmpty(mainText)) return false;

        UniText uni = go.GetComponent<UniText>();
        TMP_Text tmp = uni == null ? go.GetComponent<TMP_Text>() : null;

        if (!TextCompat.HasTarget(uni, tmp)) return false;

        TextCompat.SetText(uni, tmp, mainText);
        return true;
    }

    private IEnumerator LoadAudioIfPresent(string audioFolder, string fileName, Action<AudioClip> assign)
    {
        if (string.IsNullOrEmpty(fileName)) yield break;

        string fullPath = Path.Combine(audioFolder, fileName);
        if (!File.Exists(fullPath))
        {
            if (logMissingEntries)
                Debug.Log($"[SimulationLocalizationInjector] Missing audio file '{fullPath}'.");
            yield break;
        }

        AudioType audioType = ResolveAudioType(fullPath);
        if (audioType == AudioType.UNKNOWN)
        {
            if (logMissingEntries)
                Debug.Log($"[SimulationLocalizationInjector] Unsupported extension on '{fileName}' (.wav/.ogg/.mp3).");
            yield break;
        }

        // Local files still go through UnityWebRequest - it is the only
        // supported runtime path to an AudioClip outside Resources.
        string uri = new Uri(fullPath).AbsoluteUri;

        using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(uri, audioType))
        {
            if (request.downloadHandler is DownloadHandlerAudioClip handler)
                handler.streamAudio = true;   // Keeps memory flat for narration.

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[SimulationLocalizationInjector] Failed to load '{fileName}': {request.error}");
                yield break;
            }

            AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
            if (clip == null) yield break;

            clip.name = Path.GetFileNameWithoutExtension(fileName);
            assign(clip);
        }
    }

    private static AudioType ResolveAudioType(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".wav": return AudioType.WAV;
            case ".ogg": return AudioType.OGGVORBIS;
            case ".mp3": return AudioType.MPEG;
            default: return AudioType.UNKNOWN;
        }
    }
}
