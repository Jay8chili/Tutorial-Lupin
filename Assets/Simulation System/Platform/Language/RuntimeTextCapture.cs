using System.Collections;
using System.Collections.Generic;
using LightSide;
using UnityEngine;
using UnityEngine.SceneManagement;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Captures UniText labels that only exist at runtime - list rows, spawned
/// popups, anything Instantiate() creates - and writes them into the table.
///
/// The editor scanner cannot see these: they do not exist until play mode. So
/// this sweeps the live hierarchy while you play, adds a LocalizedText to any
/// label that lacks one, and records key + text in the table asset.
///
/// EDITOR ONLY in effect. The whole capture path is wrapped in UNITY_EDITOR,
/// so in a player build this component does nothing and costs nothing. Leave it
/// in the scene; it is inert once shipped.
///
/// Usage: add to the [Localization] GameObject, assign the table, press Play,
/// then exercise the UI - open every menu, spawn every list. Anything that
/// appears on screen gets captured.
/// </summary>
public class RuntimeTextCapture : MonoBehaviour
{
    [Tooltip("Table to write captured keys into. Editor only - never read in a build.")]
    [SerializeField] private LocalizationTable table;

    [Tooltip("Seconds between sweeps. Lower catches things sooner, costs more.")]
    [SerializeField] private float sweepInterval = 1f;

    [Tooltip("Add a LocalizedText to captured labels so they localize immediately, not just next session.")]
    [SerializeField] private bool addLocalizedText = true;

    [Tooltip("Log each newly captured key.")]
    [SerializeField] private bool verbose = true;

    /// <summary>
    /// Keys already handled this session, so a label seen on every sweep is
    /// only processed once. A List of strings rather than a HashSet keeps this
    /// consistent with the no-Dictionary constraint; capture runs once a second
    /// in the editor, so the linear scan is irrelevant.
    /// </summary>
    private readonly List<string> seen = new List<string>();

    private int captured;

#if UNITY_EDITOR
    private void OnEnable()
    {
        if (table == null)
        {
            Debug.LogWarning("[RuntimeCapture] No table assigned - capture disabled.");
            enabled = false;
            return;
        }

        StartCoroutine(SweepLoop());
    }

    private void OnDisable()
    {
        if (captured == 0) return;

        // ScriptableObject edits made during play mode DO persist, unlike scene
        // changes - but only if the asset is saved before the domain reloads.
        EditorUtility.SetDirty(table);
        AssetDatabase.SaveAssets();

        Debug.Log($"[RuntimeCapture] Saved {captured} captured keys to '{table.name}'.");
    }

    private IEnumerator SweepLoop()
    {
        // One frame's grace so the first scene's UI has spawned.
        yield return null;

        WaitForSeconds wait = new WaitForSeconds(sweepInterval);

        while (enabled)
        {
            Sweep();
            yield return wait;
        }
    }

    /// <summary>Walks every loaded scene and captures anything new.</summary>
    public void Sweep()
    {
        if (table == null) return;

        // Includes inactive: panels are often built then hidden, and they still
        // need keys. Sorting is skipped - it is pure cost here.
        UniText[] all = Object.FindObjectsByType<UniText>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        for (int i = 0; i < all.Length; i++) Capture(all[i]);
    }

    private void Capture(UniText target)
    {
        if (target == null) return;

        LocalizedText existing = target.GetComponent<LocalizedText>();

        // A label already carrying a key keeps it - re-keying at runtime would
        // orphan whatever translation is tied to the old one.
        string key = existing != null && !string.IsNullOrEmpty(existing.Key)
            ? existing.Key
            : LocalizationKeyUtil.BuildKey(target.transform);

        if (string.IsNullOrEmpty(key)) return;
        if (Contains(key)) return;

        seen.Add(key);

        // The AUTHORED text, not what is currently displayed. If a language is
        // already active, the visible string is a translation, and capturing it
        // as the source would poison the table with Spanish in the English row.
        string source = existing != null && !string.IsNullOrEmpty(existing.Fallback)
            ? existing.Fallback
            : target.Text;

        if (string.IsNullOrWhiteSpace(source)) return;

        // Rows already in the table (from the editor scan) are left alone -
        // AddOrUpdate refreshes sourceText but never touches translations.
        table.AddOrUpdate(key, source, LocalizationKeyUtil.GetHierarchyPath(target.transform));

        if (addLocalizedText && existing == null)
        {
            LocalizedText added = target.gameObject.AddComponent<LocalizedText>();
            added.ResolveTarget();
            added.Bind(key, source);   // Bind subscribes and applies immediately.
        }
        else if (existing != null && string.IsNullOrEmpty(existing.Key))
        {
            existing.Bind(key, source);
        }

        captured++;

        if (verbose) Debug.Log($"[RuntimeCapture] + {key}  \"{Truncate(source)}\"");
    }

    private bool Contains(string key)
    {
        for (int i = 0; i < seen.Count; i++)
            if (string.Equals(seen[i], key, System.StringComparison.Ordinal))
                return true;

        return false;
    }

    private static string Truncate(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= 40) return value;
        return value.Substring(0, 40) + "...";
    }

#else
    // Player build: the component exists so scene references stay valid, but
    // does nothing. No sweeps, no FindObjectsByType, no cost.
    private void Awake() => enabled = false;
#endif
}
