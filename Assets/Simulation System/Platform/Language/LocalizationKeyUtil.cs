using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// Key generation, shared by the editor scanner and the runtime capture.
///
/// This MUST be one implementation. If the editor and runtime built keys even
/// slightly differently, a prefab scanned in the editor and the same prefab
/// captured at runtime would land in the table as two rows, and one of them
/// would never resolve at runtime.
/// </summary>
public static class LocalizationKeyUtil
{
    /// <summary>
    /// Builds a key from the last few hierarchy levels, e.g.
    /// "SettingsPanel_AudioRow_Label". Path-based rather than name-based
    /// because names like "Label" and "Text" repeat constantly in a scene.
    /// </summary>
    public static string BuildKey(Transform target, string prefix = null, int levels = 3)
    {
        if (target == null) return string.Empty;

        List<string> parts = new List<string>();

        Transform current = target;
        while (current != null)
        {
            string clean = Sanitize(current.name);
            if (!string.IsNullOrEmpty(clean)) parts.Insert(0, clean);
            current = current.parent;
        }

        // Canvas roots and layout wrappers add depth without meaning.
        int start = Mathf.Max(0, parts.Count - levels);

        StringBuilder sb = new StringBuilder();
        if (!string.IsNullOrEmpty(prefix)) sb.Append(Sanitize(prefix));

        for (int i = start; i < parts.Count; i++)
        {
            if (sb.Length > 0) sb.Append('_');
            sb.Append(parts[i]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Strips everything but letters and digits.
    ///
    /// Instantiated objects get a "(Clone)" suffix, and list rows often carry
    /// an index. Both are removed first, otherwise every spawned row would
    /// produce its own key and flood the table with near-duplicates.
    /// </summary>
    public static string Sanitize(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        int clone = raw.IndexOf("(Clone)", System.StringComparison.Ordinal);
        if (clone >= 0) raw = raw.Substring(0, clone);

        StringBuilder sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
            if (char.IsLetterOrDigit(raw[i])) sb.Append(raw[i]);

        return sb.ToString();
    }

    public static string GetHierarchyPath(Transform t)
    {
        if (t == null) return string.Empty;

        StringBuilder sb = new StringBuilder(t.name);

        Transform current = t.parent;
        while (current != null)
        {
            sb.Insert(0, current.name + "/");
            current = current.parent;
        }

        return sb.ToString();
    }
}
