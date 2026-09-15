using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Window -> Localization -> Table.
///
/// One screen for the whole authoring loop: scan the scene and prefabs, review
/// and translate keys, preview a language live in the editor, and export the
/// JSON that ships in the ZIP.
///
/// Preview writes straight to the labels, since no LocalizationManager exists
/// in edit mode. That dirties the scene, which is why "Restore Source" sits
/// right next to it - always restore before saving.
/// </summary>
public class LocalizationTableWindow : EditorWindow
{
    private LocalizationTable table;

    private string search = "";
    private bool onlyUntranslated;
    private string previewLanguage = "";
    private Vector2 scroll;

    private string prefabFolder = "Assets";
    private bool addMissingComponents = true;

    private string status = "";

    [MenuItem("Window/Localization/Table")]
    public static void Open()
    {
        LocalizationTableWindow window = GetWindow<LocalizationTableWindow>("Localization");
        window.minSize = new Vector2(720, 400);
    }

    private void OnGUI()
    {
        DrawTableField();
        if (table == null)
        {
            EditorGUILayout.HelpBox(
                "Assign a LocalizationTable.\nCreate one via Assets -> Create -> Localization -> Table.",
                MessageType.Info);
            return;
        }

        DrawScanBar();
        DrawFilterBar();
        DrawPreviewBar();
        DrawRows();
        DrawExportBar();

        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);
    }

    // ---------------------------------------------------------------------

    private void DrawTableField()
    {
        EditorGUI.BeginChangeCheck();
        table = (LocalizationTable)EditorGUILayout.ObjectField("Table", table, typeof(LocalizationTable), false);
        if (EditorGUI.EndChangeCheck()) status = "";
    }

    private void DrawScanBar()
    {
        EditorGUILayout.Space(4);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Scan", EditorStyles.boldLabel);

            addMissingComponents = EditorGUILayout.ToggleLeft(
                "Add LocalizedText where missing", addMissingComponents);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Scan Open Scenes"))
                {
                    LocalizationScanner.ScanResult r =
                        LocalizationScanner.ScanOpenScenes(table, addMissingComponents);

                    status = $"Scene: +{r.componentsAdded} components, {r.keysGenerated} new keys, {r.rowsTouched} rows.";
                }

                if (GUILayout.Button("Scan Prefabs"))
                {
                    LocalizationScanner.ScanResult r =
                        LocalizationScanner.ScanPrefabs(table, new[] { prefabFolder }, addMissingComponents);

                    status = $"Prefabs: {r.prefabsModified} modified, +{r.componentsAdded} components, {r.keysGenerated} new keys.";
                }
            }

            prefabFolder = EditorGUILayout.TextField("Prefab folder", prefabFolder);

            EditorGUILayout.Space(2);
            DrawRuntimeCapture();
        }
    }

    /// <summary>
    /// Runtime capture picks up labels the scanners cannot see - anything
    /// Instantiate() creates only exists in play mode.
    /// </summary>
    private void DrawRuntimeCapture()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            if (!Application.isPlaying)
            {
                EditorGUILayout.LabelField(
                    "Runtime capture: enter Play mode with a RuntimeTextCapture in the scene.",
                    EditorStyles.miniLabel);
                return;
            }

            RuntimeTextCapture capture = Object.FindFirstObjectByType<RuntimeTextCapture>();

            if (capture == null)
            {
                EditorGUILayout.LabelField("No RuntimeTextCapture in the scene.", EditorStyles.miniLabel);
                return;
            }

            EditorGUILayout.LabelField("Runtime capture active", EditorStyles.miniLabel);

            if (GUILayout.Button("Sweep Now", GUILayout.Width(90)))
            {
                capture.Sweep();
                status = "Swept the live hierarchy.";
            }
        }
    }

    private void DrawFilterBar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            search = EditorGUILayout.TextField(search, EditorStyles.toolbarSearchField, GUILayout.Width(220));
            onlyUntranslated = GUILayout.Toggle(onlyUntranslated, "Untranslated only", EditorStyles.toolbarButton);

            GUILayout.FlexibleSpace();
            GUILayout.Label($"{table.entries.Length} keys", EditorStyles.miniLabel);
        }
    }

    private void DrawPreviewBar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
        {
            GUILayout.Label("Preview", GUILayout.Width(55));

            // Index 0 is the authored language, i.e. "show me the source".
            List<string> options = new List<string> { table.defaultLanguage + " (source)" };
            for (int i = 0; i < table.languages.Length; i++) options.Add(table.languages[i]);

            int current = 0;
            for (int i = 0; i < table.languages.Length; i++)
                if (table.languages[i] == previewLanguage) current = i + 1;

            int picked = EditorGUILayout.Popup(current, options.ToArray(), GUILayout.Width(160));
            previewLanguage = picked == 0 ? "" : table.languages[picked - 1];

            if (GUILayout.Button("Apply to Scene", GUILayout.Width(110))) ApplyPreview();
            if (GUILayout.Button("Restore Source", GUILayout.Width(110))) RestoreSource();
        }
    }

    private void DrawRows()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        for (int i = 0; i < table.entries.Length; i++)
        {
            LocalizationTable.Entry entry = table.entries[i];

            if (!Matches(entry, i)) continue;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.SelectableLabel(entry.key, EditorStyles.boldLabel,
                                                    GUILayout.Height(16), GUILayout.Width(280));

                    GUILayout.Label(entry.origin, EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button("X", GUILayout.Width(22)))
                    {
                        Undo.RecordObject(table, "Remove Key");
                        table.RemoveAt(i);
                        EditorUtility.SetDirty(table);
                        break;   // Indices shifted; redraw next frame.
                    }
                }

                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.TextField(table.defaultLanguage, entry.sourceText);

                for (int l = 0; l < table.languages.Length; l++)
                {
                    string code = table.languages[l];
                    string existing = table.GetTranslation(i, code);

                    EditorGUI.BeginChangeCheck();
                    string edited = EditorGUILayout.TextField(code, existing);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(table, "Edit Translation");
                        table.SetTranslation(i, code, edited);
                        EditorUtility.SetDirty(table);
                    }
                }
            }
        }

        EditorGUILayout.EndScrollView();
    }

    private bool Matches(LocalizationTable.Entry entry, int index)
    {
        if (!string.IsNullOrEmpty(search))
        {
            bool hit = entry.key.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                       (entry.sourceText ?? "").IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0;
            if (!hit) return false;
        }

        if (!onlyUntranslated) return true;

        // Untranslated means missing in ANY column - those are the rows to work on.
        for (int l = 0; l < table.languages.Length; l++)
            if (string.IsNullOrEmpty(table.GetTranslation(index, table.languages[l])))
                return true;

        return false;
    }

    private void DrawExportBar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
        {
            for (int i = 0; i < table.languages.Length; i++)
            {
                string code = table.languages[i];
                int done = table.TranslatedCount(code);
                GUILayout.Label($"{code}: {done}/{table.entries.Length}", EditorStyles.miniLabel);
            }

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Export JSON...", GUILayout.Width(120))) ExportAll();
        }
    }

    // ---------------------------------------------------------------------

    private void ApplyPreview()
    {
        LocalizedText[] all = Object.FindObjectsByType<LocalizedText>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        Undo.RecordObjects(all, "Localization Preview");

        for (int i = 0; i < all.Length; i++)
        {
            int index = table.IndexOfKey(all[i].Key);

            string value = all[i].Fallback;
            if (index >= 0 && !string.IsNullOrEmpty(previewLanguage))
            {
                string translated = table.GetTranslation(index, previewLanguage);
                if (!string.IsNullOrEmpty(translated)) value = translated;
            }

            all[i].ApplyPreview(value);
            EditorUtility.SetDirty(all[i]);
        }

        status = string.IsNullOrEmpty(previewLanguage)
            ? $"Restored source on {all.Length} labels."
            : $"Previewing '{previewLanguage}' on {all.Length} labels. Restore before saving the scene.";
    }

    private void RestoreSource()
    {
        previewLanguage = "";
        ApplyPreview();
    }

    private void ExportAll()
    {
        string folder = EditorUtility.SaveFolderPanel("Export localization JSON", "", "");
        if (string.IsNullOrEmpty(folder)) return;

        int written = 0;
        for (int i = 0; i < table.languages.Length; i++)
        {
            string code = table.languages[i];

            // Named localization.json inside a per-language folder, matching
            // what LocalizationDownloader expects to find in the ZIP.
            string dir = Path.Combine(folder, $"localization_{code}");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "localization.json"), table.ExportJson(code));

            written++;
        }

        status = $"Exported {written} language file(s) to {folder}. Zip each folder as localization_{{code}}.zip.";
    }
}