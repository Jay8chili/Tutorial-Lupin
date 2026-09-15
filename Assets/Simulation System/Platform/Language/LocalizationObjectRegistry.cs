using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Per-scene registry mapping a simple authored id ("01", "02", ...) to a
/// specific GameObject, so the localization pipeline can look objects up by a
/// plain string instead of any computed/derived identity (GlobalObjectId,
/// instance ID, etc).
///
/// A serialized object reference survives scene save -> build -> AssetBundle
/// -> runtime load unmodified - it works exactly like every other
/// Inspector-assigned reference in this project, so there is nothing to
/// recompute, verify, or keep in sync at runtime. This registry just gives
/// each reference a short, stable string name so JSON can address it.
///
/// Populated once by the SceneToSpanishJson editor tool when it scans the
/// scene. One instance per simulation scene - NOT DontDestroyOnLoad, since it
/// is destroyed and rebuilt fresh every time a different bundle/scene loads.
/// </summary>
public class LocalizationObjectRegistry : MonoBehaviour
{
    [Serializable]
    public struct Entry
    {
        public string id;
        public GameObject target;
    }

    [SerializeField] private List<Entry> entries = new List<Entry>();

    private Dictionary<string, GameObject> lookup;

    private static LocalizationObjectRegistry _instance;

    /// <summary>The registry in the currently loaded scene, or null if this scene has none.</summary>
    public static LocalizationObjectRegistry Instance
    {
        get
        {
            if (_instance != null) return _instance;

            _instance = FindFirstObjectByType<LocalizationObjectRegistry>();
            return _instance;
        }
    }

    private void Awake()
    {
        _instance = this;
        BuildLookup();
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    private void BuildLookup()
    {
        lookup = new Dictionary<string, GameObject>(entries.Count);

        for (int i = 0; i < entries.Count; i++)
        {
            if (string.IsNullOrEmpty(entries[i].id) || entries[i].target == null) continue;
            lookup[entries[i].id] = entries[i].target;
        }
    }

    /// <summary>Looks up a registered GameObject by id. False if the id is unknown or unassigned.</summary>
    public bool TryGetObject(string id, out GameObject target)
    {
        target = null;
        if (string.IsNullOrEmpty(id) || lookup == null) return false;

        return lookup.TryGetValue(id, out target) && target != null;
    }

#if UNITY_EDITOR
    /// <summary>
    /// Editor-only setup step: adds a new entry, or updates an existing one's
    /// target if the id is already registered. Used by SceneToSpanishJson so
    /// re-scanning the same scene reuses ids instead of duplicating them.
    /// </summary>
    public void SetEntry(string id, GameObject target)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            if (!string.Equals(entries[i].id, id, StringComparison.Ordinal)) continue;

            Entry e = entries[i];
            e.target = target;
            entries[i] = e;
            return;
        }

        entries.Add(new Entry { id = id, target = target });
    }

    /// <summary>True if this id is already registered (regardless of which target it points to).</summary>
    public bool HasId(string id)
    {
        for (int i = 0; i < entries.Count; i++)
            if (string.Equals(entries[i].id, id, StringComparison.Ordinal)) return true;

        return false;
    }

    /// <summary>
    /// Reverse lookup: the id already registered for this target, if any. Lets a
    /// re-scan reuse existing ids instead of renumbering everything, which would
    /// silently invalidate every id already sent out for translation.
    /// </summary>
    public bool TryGetIdForTarget(GameObject target, out string id)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].target != target) continue;
            id = entries[i].id;
            return true;
        }

        id = null;
        return false;
    }
#endif
}
