using System;
using System.Collections.Generic;

/// <summary>
/// Data containers for the per-simulation (AssetBundle) localization pack,
/// shared between SceneToSpanishJson (writes it) and SimulationLocalizationInjector
/// (reads it). Object_GUID / InteractionGameObjectGUID are plain ids ("01",
/// "02", ...) resolved through the scene's LocalizationObjectRegistry, not a
/// computed/derived identity.
///
/// Matches:
/// {
///   "localization_json": {
///     "SceneName": "...",
///     "GameObjects": [
///       {
///         "Object_GUID": "01", "MainText": "...", "IsAStepGameObject": "true",
///         "HasUIInteraction": "true", "AudioFile": "...", "PrePromptAudioFile": "...",
///         "PostPromptAudioFile": "...", "PrePromptText": "...", "PostPromptText": "...",
///         "UIInteractions": [ { "InteractionGameObjectGUID": "01_a", "UITextField": "..." } ]
///       }
///     ]
///   }
/// }
/// </summary>

[Serializable]
public class UIInteractionLocalizationEntry
{
    public string InteractionGameObjectGUID;
    public string UITextField;
}

[Serializable]
public class GameObjectLocalizationEntry
{
    public string Object_GUID;
    public string MainText;
    public string IsAStepGameObject;
    public string HasUIInteraction;
    public string AudioFile;
    public string PrePromptAudioFile;
    public string PostPromptAudioFile;
    public string PrePromptText;
    public string PostPromptText;
    public List<UIInteractionLocalizationEntry> UIInteractions;
}

[Serializable]
public class SimulationLocalizationBody
{
    public string SceneName;
    public List<GameObjectLocalizationEntry> GameObjects;
}

[Serializable]
public class SimulationLocalizationRoot
{
    public SimulationLocalizationBody localization_json;
}
