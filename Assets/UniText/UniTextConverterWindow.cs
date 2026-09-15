// Assets/Editor/UniText/UniTextConverterWindow.cs
// Tools > UniText > Migrate TextMeshPro to UniText

using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UniTextTools.EditorTools
{
    public class UniTextConverterWindow : EditorWindow
    {
        private enum Scope
        {
            Selection,
            OpenScenes,
            ScenesInBuildSettings,
            AllPrefabsInProject,
            EntireProject
        }

        private enum Tab { Setup, Migrate, References }

        private const string PrefKeySettings = "UniTextConverter.SettingsGuid";

        /// <summary>
        /// Height of the inner log panels. Scales with the window but stays bounded so the
        /// controls above never get pushed off-screen.
        /// </summary>
        private float LogHeight => Mathf.Clamp(position.height * 0.35f, 120f, 400f);

        private UniTextConversionSettings _settings;
        private UnityEditor.Editor _settingsEditor;

        private Tab _tab = Tab.Setup;
        private Scope _scope = Scope.OpenScenes;
        private bool _includeInactive = true;
        private bool _dryRun = true;
        private string _prefabFolder = "Assets";

        private Vector2 _mainScroll;
        private Vector2 _logScroll;
        private Vector2 _referenceScroll;

        private UniTextConverter.Report _lastReport;
        private bool _lastReportWasDryRun;
        private bool _runRequested;
        private List<UniTextConverter.ReferenceHit> _referenceHits;

        [MenuItem("Tools/UniText/Migrate TextMeshPro to UniText")]
        public static void Open()
        {
            var window = GetWindow<UniTextConverterWindow>();
            window.titleContent = new GUIContent("UniText Migration");
            window.minSize = new Vector2(520, 560);
            window.Show();
        }

        private void OnEnable()
        {
            var guid = EditorPrefs.GetString(PrefKeySettings, string.Empty);
            if (!string.IsNullOrEmpty(guid))
            {
                _settings = AssetDatabase.LoadAssetAtPath<UniTextConversionSettings>(
                    AssetDatabase.GUIDToAssetPath(guid));
            }

            if (_settings == null)
            {
                var found = AssetDatabase.FindAssets("t:UniTextConversionSettings").FirstOrDefault();
                if (found != null)
                {
                    _settings = AssetDatabase.LoadAssetAtPath<UniTextConversionSettings>(
                        AssetDatabase.GUIDToAssetPath(found));
                }
            }
        }

        private void OnDisable()
        {
            if (_settingsEditor != null) DestroyImmediate(_settingsEditor);
        }

        // ------------------------------------------------------------------

        private void OnGUI()
        {
            EditorGUILayout.Space(4);

            EditorGUI.BeginChangeCheck();
            _settings = (UniTextConversionSettings)EditorGUILayout.ObjectField(
                "Settings Asset", _settings, typeof(UniTextConversionSettings), false);

            if (EditorGUI.EndChangeCheck())
            {
                if (_settingsEditor != null) { DestroyImmediate(_settingsEditor); _settingsEditor = null; }

                if (_settings != null)
                {
                    var path = AssetDatabase.GetAssetPath(_settings);
                    EditorPrefs.SetString(PrefKeySettings, AssetDatabase.AssetPathToGUID(path));
                }
            }

            if (_settings == null)
            {
                EditorGUILayout.HelpBox(
                    "Create a settings asset first. It holds the font stack that replaces each " +
                    "TMP font asset — the one thing the migration cannot infer.",
                    MessageType.Info);

                if (GUILayout.Button("Create Settings Asset")) CreateSettingsAsset();
                return;
            }

            EditorGUILayout.Space(6);
            _tab = (Tab)GUILayout.Toolbar((int)_tab, new[] { "Setup", "Migrate", "References" });
            EditorGUILayout.Space(6);

            // Header above stays pinned; everything below scrolls.
            _mainScroll = EditorGUILayout.BeginScrollView(_mainScroll);

            switch (_tab)
            {
                case Tab.Setup: DrawSetup(); break;
                case Tab.Migrate: DrawMigrate(); break;
                case Tab.References: DrawReferences(); break;
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.EndScrollView();

            // The migration must not run inside OnGUI: an exception mid-layout leaves
            // GUILayout groups unbalanced and corrupts the window for the rest of the
            // session. Defer until every Begin/End pair has closed.
            if (_runRequested)
            {
                _runRequested = false;
                EditorApplication.delayCall += RunSafely;
            }
        }

        private void RunSafely()
        {
            try
            {
                Run();
            }
            catch (System.Exception exception)
            {
                Debug.LogError($"[UniText Migration] Aborted: {exception}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        private void CreateSettingsAsset()
        {
            var path = EditorUtility.SaveFilePanelInProject(
                "Create UniText Conversion Settings",
                "UniTextConversionSettings", "asset",
                "Where should the settings asset live?");

            if (string.IsNullOrEmpty(path)) return;

            var asset = CreateInstance<UniTextConversionSettings>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();

            _settings = asset;
            EditorPrefs.SetString(PrefKeySettings, AssetDatabase.AssetPathToGUID(path));
        }

        // ------------------------------------------------------------------
        // Setup
        // ------------------------------------------------------------------

        private void DrawSetup()
        {
            EditorGUILayout.HelpBox(
                "UniText replaces TMP rather than extending it, so nothing carries over " +
                "automatically. Fonts are the part that needs your input: pair each TMP font " +
                "asset with the UniText font stack that should take its place.",
                MessageType.Info);

            EditorGUILayout.Space(4);

            if (_settingsEditor == null || _settingsEditor.target != _settings)
            {
                if (_settingsEditor != null) DestroyImmediate(_settingsEditor);
                _settingsEditor = UnityEditor.Editor.CreateEditor(_settings);
            }

            _settingsEditor.OnInspectorGUI();

            EditorGUILayout.Space(6);

            if (GUILayout.Button("List TMP Fonts Used in Project", GUILayout.Height(24)))
                ListFontsInUse();

            if (GUILayout.Button("Find Missing Nested Prefabs", GUILayout.Height(24)))
                FindMissingNestedPrefabs();
        }

        /// <summary>
        /// Reports prefabs holding unresolvable nested references. These are unsafe to
        /// save, so the migration refuses to touch them.
        /// </summary>
        private void FindMissingNestedPrefabs()
        {
            var broken = new List<string>();
            var guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });

            try
            {
                for (var i = 0; i < guids.Length; i++)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    EditorUtility.DisplayProgressBar(
                        "Scanning prefabs", path, (float)i / guids.Length);

                    var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (asset == null) continue;

                    if (UniTextConverter.TryFindMissingNestedPrefab(asset, out var objectName))
                        broken.Add($"{path}  ->  {objectName}");
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            Debug.Log(broken.Count == 0
                ? "[UniText] No missing nested prefabs found."
                : $"[UniText] {broken.Count} prefab(s) contain missing nested prefabs. " +
                  "Do not save these until repaired:\n" + string.Join("\n", broken));
        }

        /// <summary>
        /// Reports every TMP font asset actually referenced by prefabs, so the mapping list
        /// can be filled in without hunting through scenes.
        /// </summary>
        private void ListFontsInUse()
        {
            var fonts = new SortedSet<string>();
            var guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });

            try
            {
                for (var i = 0; i < guids.Length; i++)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    EditorUtility.DisplayProgressBar("Scanning fonts", path, (float)i / guids.Length);

                    var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (asset == null) continue;

                    foreach (var text in asset.GetComponentsInChildren<TMP_Text>(true))
                    {
                        if (text != null && text.font != null) fonts.Add(text.font.name);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            Debug.Log(fonts.Count == 0
                ? "[UniText] No TMP fonts found in prefabs."
                : "[UniText] TMP fonts used in prefabs:\n" + string.Join("\n", fonts));
        }

        // ------------------------------------------------------------------
        // Migrate
        // ------------------------------------------------------------------

        private void DrawMigrate()
        {
            EditorGUILayout.LabelField("Scope", EditorStyles.boldLabel);
            _scope = (Scope)EditorGUILayout.EnumPopup("Migrate In", _scope);

            if (_scope == Scope.AllPrefabsInProject || _scope == Scope.EntireProject)
                _prefabFolder = EditorGUILayout.TextField("Prefab Search Folder", _prefabFolder);

            EditorGUILayout.Space(6);
            _includeInactive = EditorGUILayout.Toggle("Include Inactive Objects", _includeInactive);
            _dryRun = EditorGUILayout.Toggle(
                new GUIContent("Dry Run", "Report what would change without modifying anything."),
                _dryRun);

            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox(
                "Each TextMeshProUGUI is destroyed and a UniText added in its place. Fields in " +
                "your own scripts typed as TMP components will be set to None — check the " +
                "References tab first. World-space TextMeshPro is skipped: UniText is a " +
                "MaskableGraphic and needs a Canvas.",
                MessageType.Warning);

            if (!_settings.IsConfigured)
            {
                EditorGUILayout.HelpBox(
                    "No font stack configured. Set a default or add at least one font mapping " +
                    "in Setup, otherwise every component will be skipped.",
                    MessageType.Error);
            }

            EditorGUILayout.Space(6);

            if (_dryRun)
            {
                EditorGUILayout.HelpBox(
                    "Dry Run is on. Nothing will be written — the log below reports what " +
                    "would change. Untick Dry Run, or use the Apply button under the report.",
                    MessageType.Info);
            }

            using (new EditorGUI.DisabledScope(!_settings.IsConfigured))
            {
                var previous = GUI.backgroundColor;
                if (!_dryRun) GUI.backgroundColor = new Color(1f, 0.55f, 0.4f);
                if (GUILayout.Button(_dryRun ? "Scan (Dry Run)" : "Migrate", GUILayout.Height(32)))
                    _runRequested = true;
                GUI.backgroundColor = previous;
            }

            EditorGUILayout.Space(8);
            DrawReport();
        }

        private void DrawReport()
        {
            if (_lastReport == null) return;

            EditorGUILayout.LabelField("Result", EditorStyles.boldLabel);

            if (_lastReportWasDryRun)
            {
                EditorGUILayout.LabelField(
                    $"Preview only — nothing was written. {_lastReport}",
                    EditorStyles.wordWrappedLabel);
            }
            else
            {
                EditorGUILayout.LabelField(_lastReport.ToString(), EditorStyles.wordWrappedLabel);
            }

            // Running the same scope for real is the usual next step after a clean preview,
            // so offer it here rather than making the toggle above the only route.
            if (_lastReportWasDryRun && _lastReport.Converted > 0)
            {
                var previous = GUI.backgroundColor;
                GUI.backgroundColor = new Color(1f, 0.55f, 0.4f);

                if (GUILayout.Button(
                        $"Apply These {_lastReport.Converted} Change(s)", GUILayout.Height(28)))
                {
                    _dryRun = false;
                    _runRequested = true;
                }

                GUI.backgroundColor = previous;
            }

            if (_lastReport.UnmappedFonts.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    "No font stack mapped for: " +
                    string.Join(", ", _lastReport.UnmappedFonts.OrderBy(n => n)),
                    MessageType.Warning);
            }

            if (_lastReport.MarkupWarnings.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{_lastReport.MarkupWarnings.Count} component(s) contain TMP rich-text tags " +
                    "that UniText will parse through its own rules. Review them individually.",
                    MessageType.Warning);
            }

            // Fixed height: a nested ExpandHeight scroll view collapses to nothing.
            _logScroll = EditorGUILayout.BeginScrollView(
                _logScroll, GUILayout.Height(LogHeight), GUILayout.ExpandWidth(true));

            foreach (var message in _lastReport.Messages)
                EditorGUILayout.LabelField(message, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.EndScrollView();

            if (GUILayout.Button("Copy Log to Clipboard"))
                EditorGUIUtility.systemCopyBuffer = string.Join("\n", _lastReport.Messages);
        }

        // ------------------------------------------------------------------
        // References
        // ------------------------------------------------------------------

        private void DrawReferences()
        {
            EditorGUILayout.HelpBox(
                "UniText is a MaskableGraphic, not a TMP type, so a field declared as " +
                "TextMeshProUGUI or TMP_Text cannot hold one. Retype these to UniText and let " +
                "the project compile before migrating — otherwise every one of them is nulled " +
                "out and has to be reassigned by hand.",
                MessageType.Info);

            if (GUILayout.Button("Scan Project Scripts", GUILayout.Height(26)))
                _referenceHits = UniTextConverter.ScanForBrokenReferences();

            if (_referenceHits == null) return;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                _referenceHits.Count == 0
                    ? "No TMP-typed serialized fields found."
                    : $"{_referenceHits.Count} field(s) will break:",
                EditorStyles.boldLabel);

            _referenceScroll = EditorGUILayout.BeginScrollView(
                _referenceScroll, GUILayout.Height(LogHeight), GUILayout.ExpandWidth(true));

            foreach (var hit in _referenceHits)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(
                    $"{hit.DeclaringType}.{hit.FieldName}  ({hit.FieldType})",
                    EditorStyles.miniLabel);

                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(hit.ScriptPath)))
                {
                    if (GUILayout.Button("Open", GUILayout.Width(50)))
                    {
                        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(hit.ScriptPath);
                        if (script != null) AssetDatabase.OpenAsset(script);
                    }
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();

            if (_referenceHits.Count > 0 && GUILayout.Button("Copy List to Clipboard"))
            {
                EditorGUIUtility.systemCopyBuffer = string.Join("\n",
                    _referenceHits.Select(h => $"{h.DeclaringType}.{h.FieldName} ({h.FieldType})"));
            }
        }

        // ------------------------------------------------------------------
        // Execution
        // ------------------------------------------------------------------

        private void Run()
        {
            var report = new UniTextConverter.Report();

            if (!_dryRun)
            {
                if (!EditorUtility.DisplayDialog(
                        "Migrate to UniText",
                        "This destroys TMP components across your project and cannot be fully " +
                        "undone for prefabs and closed scenes.\n\n" +
                        "Commit everything to source control first.",
                        "Migrate", "Cancel"))
                    return;

                Undo.SetCurrentGroupName("Migrate TextMeshPro to UniText");
            }

            try
            {
                // No AssetDatabase.StartAssetEditing here: it suppresses imports, and
                // LoadPrefabContents / OpenScene inside that batch can silently drop saves.
                switch (_scope)
                {
                    case Scope.Selection: RunOnSelection(report); break;
                    case Scope.OpenScenes: RunOnOpenScenes(report); break;
                    case Scope.ScenesInBuildSettings: RunOnBuildScenes(report); break;
                    case Scope.AllPrefabsInProject: RunOnAllPrefabs(report); break;
                    case Scope.EntireProject:
                        RunOnAllPrefabs(report);
                        RunOnBuildScenes(report);
                        break;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (!_dryRun) AssetDatabase.SaveAssets();
            }

            _lastReport = report;
            _lastReportWasDryRun = _dryRun;
            Debug.Log($"[UniText Migration] {(_dryRun ? "[preview] " : string.Empty)}{report}");
        }

        private void RunOnSelection(UniTextConverter.Report report)
        {
            var roots = Selection.gameObjects;
            if (roots.Length == 0)
            {
                report.Log("Nothing selected.");
                return;
            }

            if (_dryRun) { DryRun(roots, report); return; }

            UniTextConverter.ConvertHierarchy(roots, _settings, _includeInactive, true, report);
            MarkOpenScenesDirty();
        }

        private void RunOnOpenScenes(UniTextConverter.Report report)
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                report.Log($"--- Scene: {scene.name} ---");
                var roots = scene.GetRootGameObjects();

                if (_dryRun) { DryRun(roots, report); continue; }

                UniTextConverter.ConvertHierarchy(roots, _settings, _includeInactive, true, report);
                EditorSceneManager.MarkSceneDirty(scene);
            }
        }

        private void RunOnBuildScenes(UniTextConverter.Report report)
        {
            var scenePaths = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToList();

            if (scenePaths.Count == 0)
            {
                report.Log("No enabled scenes in Build Settings.");
                return;
            }

            if (!_dryRun && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            var originalScene = EditorSceneManager.GetActiveScene().path;

            for (var i = 0; i < scenePaths.Count; i++)
            {
                var path = scenePaths[i];
                EditorUtility.DisplayProgressBar("UniText Migration", path, (float)i / scenePaths.Count);

                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                report.Log($"--- Scene: {path} ---");

                var roots = scene.GetRootGameObjects();

                if (_dryRun) { DryRun(roots, report); continue; }

                var before = report.Converted;
                UniTextConverter.ConvertHierarchy(roots, _settings, _includeInactive, false, report);

                if (report.Converted > before)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                }
            }

            if (!string.IsNullOrEmpty(originalScene))
                EditorSceneManager.OpenScene(originalScene, OpenSceneMode.Single);
        }

        private void RunOnAllPrefabs(UniTextConverter.Report report)
        {
            var folder = string.IsNullOrEmpty(_prefabFolder) ? "Assets" : _prefabFolder;
            var guids = AssetDatabase.FindAssets("t:Prefab", new[] { folder });

            for (var i = 0; i < guids.Length; i++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                EditorUtility.DisplayProgressBar("UniText Migration", path, (float)i / guids.Length);

                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset == null) continue;
                if (asset.GetComponentsInChildren<TMP_Text>(true).Length == 0) continue;

                // Saving a prefab whose nested reference cannot be resolved drops that
                // instance for good. Never write one back.
                if (UniTextConverter.TryFindMissingNestedPrefab(asset, out var brokenObject))
                {
                    report.Skipped++;
                    report.Log($"[BLOCKED] {path}: contains a missing nested prefab " +
                               $"('{brokenObject}'). Saving this asset would destroy that " +
                               "reference permanently. Repair it before migrating.");
                    continue;
                }

                if (_dryRun)
                {
                    report.Log($"--- Prefab: {path} ---");
                    DryRun(new[] { asset }, report);
                    continue;
                }

                var contents = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    report.Log($"--- Prefab: {path} ---");
                    var before = report.Converted;

                    UniTextConverter.ConvertHierarchy(
                        new[] { contents }, _settings, _includeInactive, false, report);

                    if (report.Converted > before)
                        PrefabUtility.SaveAsPrefabAsset(contents, path);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }
        }

        private void DryRun(IEnumerable<GameObject> roots, UniTextConverter.Report report)
        {
            foreach (var root in roots)
            {
                if (root == null) continue;

                foreach (var component in root.GetComponentsInChildren<TMP_Text>(_includeInactive))
                {
                    if (component == null) continue;

                    var path = UniTextConverter.GetHierarchyPath(component.transform);

                    if (!(component is TextMeshProUGUI))
                    {
                        report.Skipped++;
                        report.Log($"[Would skip] {path}: world-space {component.GetType().Name}.");
                        continue;
                    }

                    if (PrefabUtility.IsPartOfPrefabInstance(component) &&
                        !PrefabUtility.IsPartOfPrefabAsset(component))
                    {
                        report.Skipped++;
                        report.Log($"[Would skip] {path}: prefab instance — convert the asset.");
                        continue;
                    }

                    if (!_settings.TryResolveFont(component.font, out _, out _))
                    {
                        report.Skipped++;
                        report.UnmappedFonts.Add(
                            component.font != null ? component.font.name : "<none>");
                        report.Log($"[Would skip] {path}: no font stack for " +
                                   $"'{(component.font != null ? component.font.name : "no font")}'.");
                        continue;
                    }

                    report.Converted++;
                    report.Log($"[Would replace] {path}: {component.GetType().Name} -> UniText");
                }
            }
        }

        private static void MarkOpenScenesDirty()
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded) EditorSceneManager.MarkSceneDirty(scene);
            }
        }
    }
}
