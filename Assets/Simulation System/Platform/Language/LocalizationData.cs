using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Data containers for the localization system.
/// Structs + arrays only - no Dictionary. Public fields only, so JsonUtility
/// can round-trip them.
/// </summary>

[Serializable]
public struct TextData
{
    public string key;
    public string value;
}

[Serializable]
public struct AudioData
{
    public string key;

    /// <summary>File name inside the pack's Audio folder, with extension.</summary>
    public string fileName;

    /// <summary>Runtime only. Filled in by the downloader once the clip decodes.</summary>
    [NonSerialized] public AudioClip clip;
}

/// <summary>
/// One language pack, matching localization.json:
/// {
///   "languageCode": "es",
///   "textData":  [ { "key": "MainMenu_Play",  "value": "Jugar" } ],
///   "audioData": [ { "key": "State_01_PromptAudio", "fileName": "s01.wav" } ]
/// }
/// </summary>
[Serializable]
public struct LocalizationPackage
{
    public string languageCode;
    public TextData[] textData;
    public AudioData[] audioData;
}

/// <summary>
/// The language list endpoint response:
///   { "default_language": "en", "languages": ["en","es"] }
///
/// JsonUtility cannot parse a bare array as a document ROOT, but a string[]
/// FIELD inside an object is fine - which is this shape. Field names must match
/// the JSON keys exactly, so the snake_case name stays as-is.
/// </summary>
[Serializable]
public struct LanguageListResponse
{
    public string default_language;
    public List<string> languages;
}