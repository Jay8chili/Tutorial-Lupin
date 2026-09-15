using System.Collections.Generic;
using System.Text;
using LightSide;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

#if LOCALIZATION_TMP
using TMPro;
#endif

/// <summary>
/// Finds every text component in the open scene and in prefab assets, adds a
/// LocalizedText to each, generates a key, and records the authored string in
/// the table.
///
/// Two rules that keep re-scanning safe:
///  - An existing LocalizedText with a key is never re-keyed. Renaming a
///    GameObject after translation would otherwise orphan every translation.
///  - Table rows already carrying translations keep them; only sourceText and
///    origin refresh.
/// </summary>
public static class LocalizationScanner
{
    /// <summary>What a scan touched, for the summary line in the window.</summary>
    public struct ScanResult
    {
        public int componentsAdded;
        public int keysGenerated;
        public int rowsTouched;
        public int prefabsModified;
    }

    // ---------------------------------------------------------------------
    // Scene
    // ---------------------------------------------------------------------

    /// <summary>Scans every open scene, including inactive objects.</summary>
    public static ScanResult ScanOpenScenes(LocalizationTable table, bool addMissingComponents)
    {
        ScanResult result = default;
        if (table == null) return result;

        Undo.SetCurrentGroupName("Localization Scan");
        int group = Undo.GetCurrentGroup();

        for (int s = 0; s < SceneManager.sceneCount; s++)
        {
            Scene scene = SceneManager.GetSceneAt(s);
            if (!scene.isLoaded) continue;

            GameObject[] roots = scene.GetRootGameObjects();
            for (int r = 0; r < roots.Length; r++)
                ProcessHierarchy(roots[r], table, addMissingComponents, scene.name, ref result, true);

            EditorSceneManager.MarkSceneDirty(scene);
        }

        Undo.CollapseUndoOperations(group);
        EditorUtility.SetDirty(table);
        AssetDatabase.SaveAssets();

        return result;
    }

    // ---------------------------------------------------------------------
    // Prefabs
    // ---------------------------------------------------------------------

    /// <summary>
    /// Scans prefab assets under the given folders. Prefab assets cannot be
    /// edited in place - each is loaded into an isolated scene via
    /// LoadPrefabContents, modified, saved, then unloaded.
    /// </summary>
    public static ScanResult ScanPrefabs(LocalizationTable table, string[] searchFolders, bool addMissingComponents)
    {
        ScanResult result = default;
        if (table == null) return result;

        if (searchFolders == null || searchFolders.Length == 0)
            searchFolders = new[] { "Assets" };

        string[] guids = AssetDatabase.FindAssets("t:Prefab", searchFolders);

        try
        {
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);

                if (EditorUtility.DisplayCancelableProgressBar(
                        "Scanning prefabs", path, (float)i / Mathf.Max(1, guids.Length)))
                    break;

                // Editing the loaded contents, not the asset itself. Anything
                // else silently fails or corrupts the prefab.
                GameObject contents = PrefabUtility.LoadPrefabContents(path);
                if (contents == null) continue;

                try
                {
                    ScanResult before = result;
                    ProcessHierarchy(contents, table, addMissingComponents, contents.name, ref result, false);

                    bool changed = result.componentsAdded != before.componentsAdded ||
                                   result.keysGenerated != before.keysGenerated;

                    if (changed)
                    {
                        PrefabUtility.SaveAsPrefabAsset(contents, path);
                        result.prefabsModified++;
                    }
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);   // Leaks a hidden scene otherwise.
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        EditorUtility.SetDirty(table);
        AssetDatabase.SaveAssets();

        return result;
    }

    // ---------------------------------------------------------------------
    // Shared walk
    // ---------------------------------------------------------------------

    private static void ProcessHierarchy(GameObject root, LocalizationTable table, bool addMissingComponents,
                                         string originPrefix, ref ScanResult result, bool useUndo)
    {
        List<Component> targets = CollectTextComponents(root);

        for (int i = 0; i < targets.Count; i++)
        {
            Component target = targets[i];
            GameObject go = target.gameObject;

            LocalizedText localized = go.GetComponent<LocalizedText>();

            if (localized == null)
            {
                if (!addMissingComponents) continue;

                localized = useUndo
                    ? Undo.AddComponent<LocalizedText>(go)
                    : go.AddComponent<LocalizedText>();

                result.componentsAdded++;
            }

            localized.ResolveTarget();

            string authored = localized.ReadTarget();

            // Blank labels are placeholders filled at runtime - a key for them
            // would just be noise in the table.
            if (string.IsNullOrWhiteSpace(authored)) continue;

            if (useUndo) Undo.RecordObject(localized, "Localization Key");

            // Never re-key something that already has one: a GameObject rename
            // after translation would orphan every translation for that key.
            if (string.IsNullOrEmpty(localized.Key))
            {
                localized.Key = GenerateKey(go, originPrefix);
                result.keysGenerated++;
            }

            localized.Fallback = authored;
            EditorUtility.SetDirty(localized);

            table.AddOrUpdate(localized.Key, authored, GetHierarchyPath(go.transform));
            result.rowsTouched++;
        }
    }

    /// <summary>Collects UniText (and optionally TMP_Text), including inactive children.</summary>
    private static List<Component> CollectTextComponents(GameObject root)
    {
        List<Component> found = new List<Component>();

        UniText[] uniTexts = root.GetComponentsInChildren<UniText>(true);
        for (int i = 0; i < uniTexts.Length; i++) found.Add(uniTexts[i]);

#if LOCALIZATION_TMP
        TMP_Text[] tmpTexts = root.GetComponentsInChildren<TMP_Text>(true);
        for (int i = 0; i < tmpTexts.Length; i++)
        {
            // Skip labels that already have a UniText on the same object.
            if (tmpTexts[i].GetComponent<UniText>() != null) continue;
            found.Add(tmpTexts[i]);
        }
#endif
        return found;
    }

    // ---------------------------------------------------------------------
    // Keys
    // ---------------------------------------------------------------------

    /// <summary>
    /// Builds a key from the hierarchy path, e.g. "MainMenu_SettingsPanel_Title".
    /// Path-based rather than name-based because names like "Label" and "Title"
    /// repeat constantly, and colliding keys would silently share a translation.
    /// </summary>
    // Shared with RuntimeTextCapture - see LocalizationKeyUtil.
    public static string GenerateKey(GameObject go, string prefix) =>
        LocalizationKeyUtil.BuildKey(go.transform, prefix);

    public static string GetHierarchyPath(Transform t) =>
        LocalizationKeyUtil.GetHierarchyPath(t);
}