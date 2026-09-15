using System.Collections.Generic;
using System.IO;
using System.Text;
using LightSide;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Window -> Localization -> Scan Scene.
///
/// Standalone: finds every UniText in the open scenes, shows its text and an
/// auto-generated key, and lets you review before anything is written. Nothing
/// is modified until you press Apply.
///
/// Self-contained on purpose - it needs no table asset, so you can run it on a
/// scene immediately and see what you are dealing with.
/// </summary>
public class UniTextSceneScanner : EditorWindow
{
    /// <summary>One scanned label. Not serialized - rebuilt on every scan.</summary>
    private class Row
    {
        public UniText target;
        public string key;
        public string text;
        public string path;
        public bool include = true;
        public bool alreadyLocalized;   // Had a LocalizedText before the scan.
        public bool duplicateKey;
    }

    private readonly List<Row> rows = new List<Row>();

    private Vector2 scroll;
    private string search = "";
    private bool hideAlreadyLocalized;
    private bool skipEmpty = true;
    private string keyPrefix = "";
    private string status = "";

    [MenuItem("Window/Localization/Scan Scene")]
    public static void Open()
    {
        UniTextSceneScanner window = GetWindow<UniTextSceneScanner>("Scan Scene");
        window.minSize = new Vector2(760, 420);
    }

    // ---------------------------------------------------------------------

    private void OnGUI()
    {
        DrawToolbar();

        if (rows.Count == 0)
        {
            EditorGUILayout.HelpBox("Press Scan to list every UniText in the open scenes.", MessageType.Info);
            return;
        }

        DrawFilters();
        DrawRows();
        DrawFooter();

        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);
    }

    private void DrawToolbar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            if (GUILayout.Button("Scan", EditorStyles.toolbarButton, GUILayout.Width(60))) Scan();

            GUILayout.Space(8);
            GUILayout.Label("Prefix", GUILayout.Width(42));
            keyPrefix = EditorGUILayout.TextField(keyPrefix, EditorStyles.toolbarTextField, GUILayout.Width(110));

            if (GUILayout.Button("Regenerate Keys", EditorStyles.toolbarButton, GUILayout.Width(120)))
            {
                RegenerateKeys();
                status = "Keys regenerated.";
            }

            GUILayout.FlexibleSpace();

            skipEmpty = GUILayout.Toggle(skipEmpty, "Skip empty", EditorStyles.toolbarButton);
        }
    }

    private void DrawFilters()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            search = EditorGUILayout.TextField(search, EditorStyles.toolbarSearchField, GUILayout.Width(220));
            hideAlreadyLocalized = GUILayout.Toggle(hideAlreadyLocalized, "Hide already localized", EditorStyles.toolbarButton);

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("All", EditorStyles.toolbarButton, GUILayout.Width(40)))
                SetAllIncluded(true);

            if (GUILayout.Button("None", EditorStyles.toolbarButton, GUILayout.Width(45)))
                SetAllIncluded(false);
        }
    }

    private void DrawRows()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        for (int i = 0; i < rows.Count; i++)
        {
            Row row = rows[i];
            if (row.target == null) continue;
            if (hideAlreadyLocalized && row.alreadyLocalized) continue;
            if (!MatchesSearch(row)) continue;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    row.include = EditorGUILayout.Toggle(row.include, GUILayout.Width(18));

                    // A duplicate key means two labels would share one
                    // translation - flagged red so it is fixed before Apply.
                    Color previous = GUI.color;
                    if (row.duplicateKey) GUI.color = new Color(1f, 0.6f, 0.6f);

                    EditorGUI.BeginChangeCheck();
                    row.key = EditorGUILayout.TextField(row.key, GUILayout.Width(300));
                    if (EditorGUI.EndChangeCheck()) MarkDuplicates();

                    GUI.color = previous;

                    if (row.alreadyLocalized)
                        GUILayout.Label("has LocalizedText", EditorStyles.miniLabel, GUILayout.Width(110));

                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button("Select", GUILayout.Width(55)))
                    {
                        Selection.activeGameObject = row.target.gameObject;
                        EditorGUIUtility.PingObject(row.target.gameObject);
                    }
                }

                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.TextField(row.text);
                    EditorGUILayout.LabelField(row.path, EditorStyles.miniLabel);
                }
            }
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawFooter()
    {
        int included = 0;
        int duplicates = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].include) included++;
            if (rows[i].duplicateKey) duplicates++;
        }

        using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
        {
            GUILayout.Label($"{rows.Count} found, {included} selected", EditorStyles.miniLabel);

            if (duplicates > 0)
                EditorGUILayout.LabelField($"{duplicates} duplicate keys", EditorStyles.boldLabel, GUILayout.Width(140));

            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(duplicates > 0))
            {
                if (GUILayout.Button("Apply (add LocalizedText)", GUILayout.Width(190))) Apply();
            }

            if (GUILayout.Button("Export JSON...", GUILayout.Width(120))) Export();
        }

        if (duplicates > 0)
            EditorGUILayout.HelpBox("Fix duplicate keys before applying - they would share one translation.",
                                    MessageType.Warning);
    }

    private bool MatchesSearch(Row row)
    {
        if (string.IsNullOrEmpty(search)) return true;

        return row.key.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0
            || (row.text ?? "").IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0
            || row.path.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void SetAllIncluded(bool value)
    {
        for (int i = 0; i < rows.Count; i++) rows[i].include = value;
    }

    // ---------------------------------------------------------------------
    // Scan
    // ---------------------------------------------------------------------

    private void Scan()
    {
        rows.Clear();

        for (int s = 0; s < SceneManager.sceneCount; s++)
        {
            Scene scene = SceneManager.GetSceneAt(s);
            if (!scene.isLoaded) continue;

            GameObject[] roots = scene.GetRootGameObjects();
            for (int r = 0; r < roots.Length; r++)
            {
                // true = include inactive. Menus and popups are usually disabled
                // in the scene, and they are exactly what needs localizing.
                UniText[] found = roots[r].GetComponentsInChildren<UniText>(true);

                for (int i = 0; i < found.Length; i++) AddRow(found[i]);
            }
        }

        RegenerateKeys();
        status = $"Found {rows.Count} UniText components.";
    }

    private void AddRow(UniText target)
    {
        // .Text, not .CleanText: CleanText strips markup, and storing the
        // stripped version would silently drop <color>/<b> tags.
        string text = target.Text;

        if (skipEmpty && string.IsNullOrWhiteSpace(text)) return;

        LocalizedText existing = target.GetComponent<LocalizedText>();

        rows.Add(new Row
        {
            target = target,
            text = text,
            path = GetHierarchyPath(target.transform),
            alreadyLocalized = existing != null,

            // Keep a key that already exists - re-keying after translation
            // would orphan every translation tied to the old key.
            key = existing != null && !string.IsNullOrEmpty(existing.Key) ? existing.Key : null,

            include = existing == null
        });
    }

    // ---------------------------------------------------------------------
    // Keys
    // ---------------------------------------------------------------------

    /// <summary>Fills in any blank key. Existing keys are left alone.</summary>
    private void RegenerateKeys()
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].alreadyLocalized && !string.IsNullOrEmpty(rows[i].key)) continue;

            rows[i].key = BuildKey(rows[i].target.transform);
        }

        MarkDuplicates();
    }

    /// <summary>
    /// Builds a key from the last three hierarchy levels, e.g.
    /// "SettingsPanel_AudioRow_Label". Path-based rather than name-based
    /// because names like "Label" and "Text" repeat constantly across a scene.
    /// </summary>
    // Shared with RuntimeTextCapture - the editor and runtime must generate
    // identical keys or the same label lands in the table twice.
    private string BuildKey(Transform target) =>
        LocalizationKeyUtil.BuildKey(target, keyPrefix);

    /// <summary>
    /// Flags keys used more than once. Two labels sharing a key silently share
    /// a translation, which is near-impossible to diagnose later - so Apply is
    /// blocked until they are resolved.
    /// </summary>
    private void MarkDuplicates()
    {
        for (int i = 0; i < rows.Count; i++) rows[i].duplicateKey = false;

        for (int i = 0; i < rows.Count; i++)
        {
            if (string.IsNullOrEmpty(rows[i].key)) continue;

            for (int j = i + 1; j < rows.Count; j++)
            {
                if (!string.Equals(rows[i].key, rows[j].key, System.StringComparison.Ordinal)) continue;

                rows[i].duplicateKey = true;
                rows[j].duplicateKey = true;
            }
        }
    }

    // ---------------------------------------------------------------------
    // Apply
    // ---------------------------------------------------------------------

    private void Apply()
    {
        Undo.SetCurrentGroupName("Add LocalizedText");
        int group = Undo.GetCurrentGroup();

        int added = 0;
        int updated = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            Row row = rows[i];
            if (!row.include || row.target == null) continue;

            GameObject go = row.target.gameObject;
            LocalizedText localized = go.GetComponent<LocalizedText>();

            if (localized == null)
            {
                localized = Undo.AddComponent<LocalizedText>(go);
                added++;
            }
            else
            {
                updated++;
            }

            Undo.RecordObject(localized, "Set Localization Key");

            localized.ResolveTarget();
            localized.Key = row.key;
            localized.Fallback = row.text;   // Authored text becomes the fallback.

            EditorUtility.SetDirty(localized);
        }

        Undo.CollapseUndoOperations(group);

        for (int s = 0; s < SceneManager.sceneCount; s++)
            EditorSceneManager.MarkSceneDirty(SceneManager.GetSceneAt(s));

        status = $"Added {added}, updated {updated}. Save the scene to keep this.";
        Scan();   // Refresh so the 'has LocalizedText' flags are current.
    }

    // ---------------------------------------------------------------------
    // Export
    // ---------------------------------------------------------------------

    /// <summary>
    /// Writes a localization.json with every value pre-filled with the English
    /// source, so a translator overwrites rather than works from a blank file.
    /// </summary>
    private void Export()
    {
        string path = EditorUtility.SaveFilePanel("Export localization.json", "", "localization", "json");
        if (string.IsNullOrEmpty(path)) return;

        List<TextData> texts = new List<TextData>();

        for (int i = 0; i < rows.Count; i++)
        {
            if (!rows[i].include || string.IsNullOrEmpty(rows[i].key)) continue;

            texts.Add(new TextData { key = rows[i].key, value = rows[i].text });
        }

        LocalizationPackage package = new LocalizationPackage
        {
            languageCode = "",   // Translator fills this in, e.g. "es".
            textData = texts.ToArray(),
            audioData = new AudioData[0]
        };

        File.WriteAllText(path, JsonUtility.ToJson(package, true));

        status = $"Exported {texts.Count} keys to {path}. Set languageCode and translate the values.";
    }

    private static string GetHierarchyPath(Transform t) =>
        LocalizationKeyUtil.GetHierarchyPath(t);
}