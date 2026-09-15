using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using LightSide;
using SimulationSystem.V02.StateInteractions;
using SimulationSystem.V02.Utility;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

/// <summary>
/// Window -> Localization -> Scene To Spanish JSON.
///
/// Self-contained. Scans every UniText and TMP_Text in the open scenes,
/// generates an id, machine-translates the text, writes localization.json, and
/// can push the translations straight back into the scene to check them.
///
/// Flow: Scan -> Translate -> Apply to Scene (check it) -> Restore -> Save JSON.
///
/// Output shape matches SimulationLocalizationData.cs (SimulationLocalizationRoot
/// -> SimulationLocalizationBody -> List&lt;GameObjectLocalizationEntry&gt;), the
/// same DTOs SimulationLocalizationInjector reads at runtime. Object_GUID /
/// InteractionGameObjectGUID are plain ids ("01", "02", ...) resolved through
/// this scene's LocalizationObjectRegistry - a serialized GameObject reference
/// that survives scene -> AssetBundle -> runtime load unmodified. Scan()
/// assigns ids as needed (reusing existing ones on a re-scan) and writes them
/// into the registry component in the scene, so the scene must be saved after
/// scanning for those ids to stick.
///
/// Put this file in a folder named "Editor" so it stays out of player builds.
///
/// If your project has no TextMeshPro package, delete the "using TMPro;" line
/// above and the two blocks marked TMP BLOCK below; everything else works
/// unchanged with UniText alone.
/// </summary>
public class SceneToSpanishJson : EditorWindow
{
    // Backup written to Library/ so Apply is always reversible.
    [Serializable] private class BackupEntry { public string id; public string original; }
    [Serializable] private class BackupFile  { public BackupEntry[] entries; }

    // ---------------------------------------------------------------------

    /// <summary>Which SimulationState string field a row points at.</summary>
    private enum StateField { None, PromptText, PrePromptText, PostPromptText }

    private class Row
    {
        public UniText uniText;
        public TMP_Text tmpText;   // TMP BLOCK

        /// <summary>Set when this row is a plain string field on a SimulationState
        /// rather than a label component.</summary>
        public SimulationState state;
        public StateField field = StateField.None;

        /// <summary>True when this row is a UIInteraction's own label.</summary>
        public bool isUIInteraction;

        /// <summary>
        /// Set when this row targets a UIInteraction's plain `content` string
        /// rather than a label component - the bot-handled case, where
        /// uiText/uiTextUni are null and AssistantManager.TriggerBotUI reads
        /// `content` directly.
        /// </summary>
        public UIInteraction uiInteractionContent;

        /// <summary>
        /// Id (from the scene's LocalizationObjectRegistry) of the object this
        /// row's text belongs to when exported: the state's own id for
        /// state-field/generic rows, or the interaction's own GameObject's id
        /// for UIInteraction rows.
        /// </summary>
        public string registryId;

        /// <summary>Only set for UIInteraction rows: which state's UIInteractions list this nests under.</summary>
        public string parentStateRegistryId;

        /// <summary>Human-readable label shown/edited in the UI - display only, not used for matching.</summary>
        public string key;

        /// <summary>True when the key came from the hierarchy and may be widened to resolve a collision.</summary>
        public bool autoKey;

        public string source;
        public string translated;
        public string path;
        public bool include = true;
        public bool duplicate;

        public bool IsStateField => field != StateField.None;

        public bool IsValid => uniText != null || tmpText != null || state != null || uiInteractionContent != null;

        public UnityEngine.Object Obj
        {
            get
            {
                if (uniText != null) return uniText;
                if (tmpText != null) return tmpText;
                if (uiInteractionContent != null) return uiInteractionContent;
                return state;
            }
        }

        public Transform Transform
        {
            get
            {
                if (uniText != null) return uniText.transform;
                if (tmpText != null) return tmpText.transform;
                if (uiInteractionContent != null) return uiInteractionContent.transform;
                return state != null ? state.transform : null;
            }
        }

        public string Kind
        {
            get
            {
                if (IsStateField) return "State";
                if (isUIInteraction) return uiInteractionContent != null ? "UI*" : "UI";
                return uniText != null ? "UniText" : "TMP";
            }
        }

        /// <summary>
        /// UniText.Text, NOT CleanText - CleanText strips markup, so reading it
        /// would silently drop &lt;color&gt; and &lt;b&gt; tags from the export.
        /// </summary>
        public string Text
        {
            get
            {
                if (state != null)
                {
                    switch (field)
                    {
                        case StateField.PromptText:     return state.promptText;
                        case StateField.PrePromptText:  return state.prePromptText;
                        case StateField.PostPromptText: return state.postPromptText;
                    }
                    return string.Empty;
                }

                if (uiInteractionContent != null) return uiInteractionContent.content;

                if (uniText != null) return uniText.Text;
                return tmpText != null ? tmpText.text : string.Empty;
            }
            set
            {
                if (state != null)
                {
                    switch (field)
                    {
                        case StateField.PromptText:     state.promptText     = value; break;
                        case StateField.PrePromptText:  state.prePromptText  = value; break;
                        case StateField.PostPromptText: state.postPromptText = value; break;
                    }
                    return;
                }

                if (uiInteractionContent != null) { uiInteractionContent.content = value; return; }

                if (uniText != null) uniText.Text = value;
                else if (tmpText != null) tmpText.text = value;
            }
        }
    }

    private readonly List<Row> rows = new List<Row>();

    /// <summary>Labels already added via a SimulationState, to avoid duplicate rows.</summary>
    private readonly List<UnityEngine.Object> claimed = new List<UnityEngine.Object>();

    private bool includeSimulationStates = true;

    /// <summary>Folder holding the already-recorded target-language audio files.</summary>
    [System.NonSerialized] private string audioSourceFolder = "";

    /// <summary>Where localization_{code}.zip is written.</summary>
    [System.NonSerialized] private string packOutputFolder = "";

    /// <summary>
    /// When true, the leading number in a file name is treated as a 1-based
    /// position in the scanned state list rather than the state's own name.
    /// Off suits states literally named "1", "2"; on suits "Step_01" etc.
    /// </summary>
    private bool stepNumberIsIndex;

    /// <summary>Ordered state names, filled during Scan, used to map step numbers when stepNumberIsIndex is off.</summary>
    private readonly List<string> stateOrder = new List<string>();

    /// <summary>Ordered state registry ids, parallel to stateOrder, used when stepNumberIsIndex is on.</summary>
    private readonly List<string> stateOrderIds = new List<string>();

    /// <summary>State GameObject name -> registry id, used when stepNumberIsIndex is off.</summary>
    private readonly Dictionary<string, string> stateNameToId = new Dictionary<string, string>();

    /// <summary>The scene's id registry - found or created fresh each Scan().</summary>
    private LocalizationObjectRegistry registry;

    private int nextId = 1;

    private string sourceLanguage = "en";
    private string targetLanguage = "es";
    private int delayMs = 400;

    private string search = "";
    private Vector2 scroll;
    private string status = "";

    private bool running;
    private int cursor;
    private UnityWebRequest inFlight;
    private double nextRequestTime;

    private static string BackupPath =>
        Path.Combine(Directory.GetCurrentDirectory(), "Library", "scene_to_spanish_backup.json");

    [MenuItem("Window/Localization/Scene To Spanish JSON")]
    public static void Open()
    {
        SceneToSpanishJson window = GetWindow<SceneToSpanishJson>("Scene To Spanish");
        window.minSize = new Vector2(720, 460);
    }

    // ---------------------------------------------------------------------
    // GUI
    // ---------------------------------------------------------------------

    private void OnGUI()
    {
        DrawToolbar();

        if (rows.Count == 0)
        {
            EditorGUILayout.HelpBox("Press Scan to collect every UniText and TMP_Text in the open scenes.",
                                    MessageType.Info);
            return;
        }

        if (running)
        {
            Rect r = EditorGUILayout.GetControlRect(false, 18);
            EditorGUI.ProgressBar(r, (float)cursor / Mathf.Max(1, rows.Count), $"{cursor}/{rows.Count}");
            if (GUILayout.Button("Cancel")) EndRun("Cancelled.");
        }

        DrawRows();
        DrawFooter();

        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);

        if (HasBackup())
            EditorGUILayout.HelpBox("Translations are applied to the scene. Restore before saving it.",
                                    MessageType.Warning);
    }

    private void DrawToolbar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            using (new EditorGUI.DisabledScope(running))
            {
                if (GUILayout.Button("Scan", EditorStyles.toolbarButton, GUILayout.Width(60))) Scan();
            }

            GUILayout.Space(10);
            GUILayout.Label("From", GUILayout.Width(34));
            sourceLanguage = EditorGUILayout.TextField(sourceLanguage, EditorStyles.toolbarTextField, GUILayout.Width(40));

            GUILayout.Label("To", GUILayout.Width(20));
            targetLanguage = EditorGUILayout.TextField(targetLanguage, EditorStyles.toolbarTextField, GUILayout.Width(40));

            GUILayout.Space(10);
            GUILayout.Label("Delay ms", GUILayout.Width(56));
            delayMs = EditorGUILayout.IntField(delayMs, EditorStyles.toolbarTextField, GUILayout.Width(50));

            GUILayout.Space(8);
            includeSimulationStates = GUILayout.Toggle(includeSimulationStates, "Simulation states",
                                                       EditorStyles.toolbarButton, GUILayout.Width(115));

            GUILayout.FlexibleSpace();
            search = EditorGUILayout.TextField(search, EditorStyles.toolbarSearchField, GUILayout.Width(180));
        }
    }

    private void DrawRows()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        for (int i = 0; i < rows.Count; i++)
        {
            Row row = rows[i];
            if (!row.IsValid || !MatchesSearch(row)) continue;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    row.include = EditorGUILayout.Toggle(row.include, GUILayout.Width(18));

                    // Duplicate keys would silently share one translation.
                    Color previous = GUI.color;
                    if (row.duplicate) GUI.color = new Color(1f, 0.6f, 0.6f);

                    EditorGUI.BeginChangeCheck();
                    row.key = EditorGUILayout.TextField(row.key, GUILayout.Width(290));
                    if (EditorGUI.EndChangeCheck()) MarkDuplicates();

                    GUI.color = previous;

                    GUILayout.Label(row.Kind, EditorStyles.miniLabel, GUILayout.Width(35));
                    GUILayout.Label(row.registryId, EditorStyles.miniLabel, GUILayout.Width(30));
                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button("Select", GUILayout.Width(55)))
                        EditorGUIUtility.PingObject(row.Obj);
                }

                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.TextField(sourceLanguage, row.source);

                row.translated = EditorGUILayout.TextField(targetLanguage, row.translated);

                EditorGUILayout.LabelField(row.path, EditorStyles.miniLabel);
            }
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawFooter()
    {
        int included = 0, done = 0, duplicates = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].include) included++;
            if (!string.IsNullOrEmpty(rows[i].translated)) done++;
            if (rows[i].duplicate) duplicates++;
        }

        using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
        {
            GUILayout.Label($"{rows.Count} found, {included} selected, {done} translated", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(running || included == 0))
            {
                if (GUILayout.Button($"Translate to '{targetLanguage}'", GUILayout.Width(160))) BeginRun();
            }

            using (new EditorGUI.DisabledScope(running))
            {
                // Fills the translated column from an existing file, so Apply
                // works without re-running the translator.
                if (GUILayout.Button("Load JSON...", GUILayout.Width(105))) LoadJson();
            }

            using (new EditorGUI.DisabledScope(running || done == 0))
            {
                if (GUILayout.Button("Apply to Scene", GUILayout.Width(115))) ApplyToScene();
            }

            using (new EditorGUI.DisabledScope(running || !HasBackup()))
            {
                if (GUILayout.Button("Restore", GUILayout.Width(75))) RestoreScene();
            }

            using (new EditorGUI.DisabledScope(running || duplicates > 0 || done == 0))
            {
                if (GUILayout.Button("Save JSON...", GUILayout.Width(105))) SaveJson();
            }
        }

        if (duplicates > 0)
            EditorGUILayout.HelpBox($"{duplicates} duplicate keys - two labels would share one translation.",
                                    MessageType.Warning);

        // The most common confusion is Apply being greyed out, so say why.
        if (done == 0)
            EditorGUILayout.HelpBox(
                "Apply to Scene needs translated text. Either press Translate, or Load JSON to pull in " +
                "a file you already have.",
                MessageType.Info);

        DrawPackBar(done);
    }

    /// <summary>
    /// Pack building: copy the recorded audio next to the JSON and zip it into
    /// the layout LocalizationDownloader expects.
    /// </summary>
    private void DrawPackBar(int translatedCount)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Build language pack", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Audio source",
                                           string.IsNullOrEmpty(audioSourceFolder) ? "(none - optional)" : audioSourceFolder,
                                           EditorStyles.textField);

                if (GUILayout.Button("Browse", GUILayout.Width(65)))
                {
                    string picked = EditorUtility.OpenFolderPanel("Folder holding the recorded audio", audioSourceFolder, "");
                    if (!string.IsNullOrEmpty(picked)) audioSourceFolder = picked;
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Output",
                                           string.IsNullOrEmpty(packOutputFolder) ? "(none)" : packOutputFolder,
                                           EditorStyles.textField);

                if (GUILayout.Button("Browse", GUILayout.Width(65)))
                {
                    string picked = EditorUtility.SaveFolderPanel("Where to write the ZIP", packOutputFolder, "");
                    if (!string.IsNullOrEmpty(picked)) packOutputFolder = picked;
                }
            }

            using (new EditorGUI.DisabledScope(running || translatedCount == 0 || string.IsNullOrEmpty(packOutputFolder)))
            {
                if (GUILayout.Button($"Build localization_{targetLanguage}.zip", GUILayout.Height(26)))
                    BuildPack();
            }

            EditorGUILayout.LabelField(
                "Audio file names must identify which state + variant they belong to " +
                "(e.g. 1_pre_es-ES.mp3), matched against the scanned states below.",
                EditorStyles.wordWrappedMiniLabel);

            stepNumberIsIndex = EditorGUILayout.ToggleLeft(
                "Leading number is a step index, not the state's own name", stepNumberIsIndex);

            if (GUILayout.Button("Preview audio mapping", GUILayout.Width(160))) PreviewRename();
        }
    }

    private bool MatchesSearch(Row row)
    {
        if (string.IsNullOrEmpty(search)) return true;

        return row.key.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
            || (row.source ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // ---------------------------------------------------------------------
    // Registry
    // ---------------------------------------------------------------------

    /// <summary>Finds the scene's id registry, or creates one if this scene has never been scanned before.</summary>
    private LocalizationObjectRegistry FindOrCreateRegistry()
    {
        LocalizationObjectRegistry found = UnityEngine.Object.FindFirstObjectByType<LocalizationObjectRegistry>(FindObjectsInactive.Include);
        if (found != null) return found;

        GameObject go = new GameObject("~LocalizationRegistry");
        LocalizationObjectRegistry created = go.AddComponent<LocalizationObjectRegistry>();
        Undo.RegisterCreatedObjectUndo(go, "Create Localization Registry");
        EditorUtility.SetDirty(go);
        return created;
    }

    /// <summary>
    /// The id for a GameObject, reusing whatever is already registered so a
    /// re-scan never renumbers (and so invalidates) ids already sent out for
    /// translation.
    /// </summary>
    private string AssignId(GameObject target)
    {
        if (registry.TryGetIdForTarget(target, out string existing)) return existing;

        string id;
        do
        {
            id = nextId.ToString("D2");
            nextId++;
        }
        while (registry.HasId(id));

        Undo.RecordObject(registry, "Assign Localization Id");
        registry.SetEntry(id, target);
        EditorUtility.SetDirty(registry);

        return id;
    }

    // ---------------------------------------------------------------------
    // Scan
    // ---------------------------------------------------------------------

    private void Scan()
    {
        rows.Clear();
        claimed.Clear();
        stateOrder.Clear();
        stateOrderIds.Clear();
        stateNameToId.Clear();
        nextId = 1;

        registry = FindOrCreateRegistry();

        // Simulation states FIRST. Their interaction labels get ids assigned
        // here too, and are recorded in 'claimed' so the generic sweep below
        // does not re-add them under a different, hierarchy-derived id.
        if (includeSimulationStates) ScanSimulationStates();

        int stateRows = rows.Count;

        for (int s = 0; s < SceneManager.sceneCount; s++)
        {
            Scene scene = SceneManager.GetSceneAt(s);
            if (!scene.isLoaded) continue;

            GameObject[] roots = scene.GetRootGameObjects();

            for (int r = 0; r < roots.Length; r++)
            {
                // true = include inactive. Menus and popups sit disabled in the
                // scene and are exactly what needs translating.
                UniText[] uni = roots[r].GetComponentsInChildren<UniText>(true);
                for (int i = 0; i < uni.Length; i++)
                {
                    if (claimed.Contains(uni[i])) continue;
                    AddRow(new Row { uniText = uni[i] });
                }

                // TMP BLOCK
                TMP_Text[] tmp = roots[r].GetComponentsInChildren<TMP_Text>(true);
                for (int i = 0; i < tmp.Length; i++)
                {
                    // Skip when a UniText sits on the same object - already added.
                    if (tmp[i].GetComponent<UniText>() != null) continue;
                    if (claimed.Contains(tmp[i])) continue;
                    AddRow(new Row { tmpText = tmp[i] });
                }
            }
        }

        int collisions = ResolveDuplicateKeys();

        MarkDuplicates();
        MarkScenesDirty();   // The registry just gained entries - make sure that sticks.

        status = $"Found {rows.Count} strings ({stateRows} from simulation states). " +
                  "Save the scene so the assigned ids persist."
               + (collisions > 0 ? $" Auto-resolved {collisions} key collisions." : "");
    }

    /// <summary>
    /// Collects the string fields on every SimulationState in the open scenes,
    /// plus the label on each UIInteraction - the only interaction type with a
    /// text field, matching SimulationLocalizationInjector's UIInteractionEntry.
    /// </summary>
    private void ScanSimulationStates()
    {
        // Fully qualified: 'Object' alone is ambiguous between UnityEngine.Object
        // and System.Object once both 'using System;' and 'using UnityEngine;'
        // are in scope.
        SimulationState[] states = UnityEngine.Object.FindObjectsByType<SimulationState>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        // FindObjectsByType order is unspecified, so sort by hierarchy position
        // to give step numbers a stable meaning.
        System.Array.Sort(states, (a, b) =>
            string.Compare(GetPath(a.transform), GetPath(b.transform), StringComparison.Ordinal));

        for (int i = 0; i < states.Length; i++)
        {
            SimulationState state = states[i];
            if (state == null) continue;

            string name = state.gameObject.name;
            string stateId = AssignId(state.gameObject);

            stateOrder.Add(name);
            stateOrderIds.Add(stateId);
            stateNameToId[name] = stateId;

            AddStateRow(state, StateField.PromptText,     stateId, $"[{stateId}] {name} - MainText");
            AddStateRow(state, StateField.PrePromptText,  stateId, $"[{stateId}] {name} - PrePromptText");
            AddStateRow(state, StateField.PostPromptText, stateId, $"[{stateId}] {name} - PostPromptText");

            // The static UI text components are DISPLAY TARGETS, not sources:
            // at runtime promptText / prePromptText / postPromptText are written
            // into them. Whatever placeholder they hold in the scene is never
            // shown, so it must not be exported. Claimed here so the generic
            // sweep below skips them too.
            ClaimStaticUITargets(state);

            AddInteractionRows(state, stateId);
        }
    }

    private void AddStateRow(SimulationState state, StateField field, string registryId, string key)
    {
        Row row = new Row { state = state, field = field, registryId = registryId, key = key };

        string text = row.Text;
        if (string.IsNullOrWhiteSpace(text)) return;   // Unused field on this state.

        row.source = text;
        row.path = GetPath(state.transform) + $" .{field}";

        rows.Add(row);
    }

    /// <summary>
    /// Marks the static UI text components as already handled.
    ///
    /// promptStaticUIText / promptStaticUITextUni / clauseStaticUIText /
    /// clauseStaticUITextUni are written to at runtime from promptText and the
    /// pre/post prompt strings. Their scene contents are placeholder, so
    /// exporting them would produce rows for text nobody ever reads.
    ///
    /// Found by reflection so renaming or adding a static UI field does not
    /// break this file.
    /// </summary>
    private void ClaimStaticUITargets(SimulationState state)
    {
        FieldInfo[] fields = typeof(SimulationState).GetFields(BindingFlags.Public | BindingFlags.Instance);

        for (int i = 0; i < fields.Length; i++)
        {
            FieldInfo field = fields[i];

            bool isLabel = typeof(UniText).IsAssignableFrom(field.FieldType)
                        || typeof(TMP_Text).IsAssignableFrom(field.FieldType);   // TMP BLOCK

            if (!isLabel) continue;

            Component target = field.GetValue(state) as Component;
            if (target != null) claimed.Add(target);
        }
    }

    /// <summary>
    /// Adds a plain label row found by the generic scene sweep, keyed from its
    /// hierarchy position (display only - matching uses the registry id).
    /// </summary>
    private void AddRow(Row row)
    {
        string text = row.Text;

        // Blank labels are filled at runtime; a row for them is just noise.
        if (string.IsNullOrWhiteSpace(text)) return;

        row.source = text;
        row.key = BuildKey(row.Transform);
        row.autoKey = true;
        row.path = GetPath(row.Transform);
        row.registryId = AssignId(row.Transform.gameObject);

        rows.Add(row);
    }

    /// <summary>
    /// Adds one row per UIInteraction step in listOfInteractions that has a
    /// label. UIInteraction is the only interaction type with a text field -
    /// matches SimulationLocalizationInjector, which only ever reads
    /// UIInteraction.uiText/uiTextUni for the UIInteractions list.
    /// </summary>
    private void AddInteractionRows(SimulationState state, string stateId)
    {
        List<Interactions> interactions = state.listOfInteractions;
        if (interactions == null) return;

        for (int index = 0; index < interactions.Count; index++)
        {
            UIInteraction ui = interactions[index] as UIInteraction;
            if (ui == null) continue;

            bool hasLabel = TextCompat.HasTarget(ui.uiTextUni, ui.uiText);

            // Two modes, two different text sources - see UIInteraction:
            //   scene-authored panel -> the uiText/uiTextUni label
            //   bot-handled          -> the plain `content` string, which
            //                           AssistantManager.TriggerBotUI reads.
            // Exporting only the label case left every bot-handled step
            // untranslatable, since nothing else carries its text.
            UnityEngine.Object target = hasLabel
                ? (ui.uiTextUni != null ? (UnityEngine.Object)ui.uiTextUni : ui.uiText)
                : ui;

            if (claimed.Contains(target)) continue;

            string interactionId = AssignId(ui.gameObject);

            Row row = new Row
            {
                uniText = hasLabel ? ui.uiTextUni : null,
                tmpText = hasLabel && ui.uiTextUni == null ? ui.uiText : null,
                uiInteractionContent = hasLabel ? null : ui,
                isUIInteraction = true,
                registryId = interactionId,
                parentStateRegistryId = stateId,
                key = $"[{interactionId}] {ui.gameObject.name} (UI #{index} of {stateId})"
            };

            string text = row.Text;
            if (string.IsNullOrWhiteSpace(text)) continue;

            row.source = text;
            row.path = GetPath(ui.transform)
                     + $"  (UIInteraction #{index}{(hasLabel ? "" : " .content")})";

            rows.Add(row);
            claimed.Add(target);
        }
    }

    /// <summary>
    /// Key from the last three hierarchy levels, e.g. "SettingsPanel_Row_Label".
    /// Path-based rather than name-based: names like "Label" and "Text" repeat
    /// constantly across a scene, and colliding keys are just confusing in the
    /// UI (they no longer affect runtime matching - the registry id does that).
    /// </summary>
    private static string BuildKey(Transform target, int levels = 3)
    {
        List<string> parts = new List<string>();

        Transform current = target;
        while (current != null)
        {
            string clean = Sanitize(current.name);
            if (!string.IsNullOrEmpty(clean)) parts.Insert(0, clean);
            current = current.parent;
        }

        // Canvas roots and layout wrappers add depth without meaning, so only
        // the deepest few levels are used - widened by the collision resolver
        // when three is not specific enough.
        int start = Mathf.Max(0, parts.Count - levels);

        StringBuilder sb = new StringBuilder();
        for (int i = start; i < parts.Count; i++)
        {
            if (sb.Length > 0) sb.Append('_');
            sb.Append(parts[i]);
        }

        return sb.ToString();
    }

    private static string Sanitize(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        // Instantiated objects carry "(Clone)"; leaving it in would give a
        // spawned copy a different key than the prefab it came from.
        int clone = raw.IndexOf("(Clone)", StringComparison.Ordinal);
        if (clone >= 0) raw = raw.Substring(0, clone);

        StringBuilder sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
            if (char.IsLetterOrDigit(raw[i])) sb.Append(raw[i]);

        return sb.ToString();
    }

    private static string GetPath(Transform t)
    {
        StringBuilder sb = new StringBuilder(t.name);

        Transform current = t.parent;
        while (current != null)
        {
            sb.Insert(0, current.name + "/");
            current = current.parent;
        }

        return sb.ToString();
    }


    /// <summary>
    /// Makes generated display keys unique by widening them. Purely cosmetic
    /// now (the registry id is what runtime matching actually uses), but a
    /// unique, readable key still makes the row list easier to scan and edit.
    ///
    /// Widens to 4, 5, ... levels for the colliding rows only, keeping the short
    /// readable key everywhere else. If widening cannot separate them - genuinely
    /// identical paths, i.e. same-named siblings - a sibling index is appended as
    /// a last resort.
    /// </summary>
    private int ResolveDuplicateKeys()
    {
        const int maxLevels = 12;

        int resolved = 0;

        for (int pass = 4; pass <= maxLevels; pass++)
        {
            List<int> colliding = FindCollidingAutoRows();
            if (colliding.Count == 0) break;

            for (int i = 0; i < colliding.Count; i++)
            {
                Row row = rows[colliding[i]];

                string widened = BuildKey(row.Transform, pass);
                if (string.Equals(widened, row.key, StringComparison.Ordinal)) continue;

                row.key = widened;
                resolved++;
            }
        }

        // Anything still colliding has an identical hierarchy path, which means
        // same-named siblings. Only position can separate those.
        List<int> stubborn = FindCollidingAutoRows();

        for (int i = 0; i < stubborn.Count; i++)
        {
            Row row = rows[stubborn[i]];
            Transform t = row.Transform;

            if (t == null) continue;

            row.key = $"{row.key}_{t.GetSiblingIndex()}";
            resolved++;
        }

        return resolved;
    }

    /// <summary>
    /// Indices of auto-keyed rows whose display key is shared with any other row.
    /// </summary>
    private List<int> FindCollidingAutoRows()
    {
        List<int> result = new List<int>();

        for (int i = 0; i < rows.Count; i++)
        {
            if (!rows[i].autoKey || string.IsNullOrEmpty(rows[i].key)) continue;

            for (int j = 0; j < rows.Count; j++)
            {
                if (i == j) continue;
                if (!string.Equals(rows[i].key, rows[j].key, StringComparison.Ordinal)) continue;

                result.Add(i);
                break;
            }
        }

        return result;
    }

    private void MarkDuplicates()
    {
        for (int i = 0; i < rows.Count; i++) rows[i].duplicate = false;

        for (int i = 0; i < rows.Count; i++)
        {
            if (string.IsNullOrEmpty(rows[i].key)) continue;

            for (int j = i + 1; j < rows.Count; j++)
            {
                if (!string.Equals(rows[i].key, rows[j].key, StringComparison.Ordinal)) continue;

                rows[i].duplicate = true;
                rows[j].duplicate = true;
            }
        }
    }

    // ---------------------------------------------------------------------
    // Apply / Restore
    // ---------------------------------------------------------------------

    /// <summary>
    /// Writes the translated strings into the actual label components so the
    /// result can be checked in the Scene view - clipping, wrapping, missing
    /// glyphs. Originals are backed up to disk first.
    /// </summary>
    private void ApplyToScene()
    {
        // Back up BEFORE the first write. Applying twice without this would
        // overwrite the backup with already-Spanish text and lose the English.
        if (!HasBackup()) SaveBackup();

        Undo.SetCurrentGroupName("Apply Translations");
        int group = Undo.GetCurrentGroup();

        int applied = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            Row row = rows[i];

            if (!row.IsValid || !row.include) continue;
            if (string.IsNullOrEmpty(row.translated)) continue;   // Untranslated keeps its English.

            Undo.RecordObject(row.Obj, "Apply Translations");
            row.Text = row.translated;
            EditorUtility.SetDirty(row.Obj);

            applied++;
        }

        Undo.CollapseUndoOperations(group);
        MarkScenesDirty();

        status = $"Applied {applied} translations to the scene. Restore before saving.";
    }

    private void RestoreScene()
    {
        if (!HasBackup()) { status = "Nothing to restore."; return; }

        BackupFile file;
        try
        {
            file = JsonUtility.FromJson<BackupFile>(File.ReadAllText(BackupPath));
        }
        catch (Exception e)
        {
            status = $"Backup unreadable: {e.Message}";
            return;
        }

        if (file?.entries == null) { status = "Backup was empty."; return; }

        Undo.SetCurrentGroupName("Restore Original Text");
        int group = Undo.GetCurrentGroup();

        int restored = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            Row row = rows[i];
            if (!row.IsValid) continue;

            string id = BackupId(row);

            for (int b = 0; b < file.entries.Length; b++)
            {
                if (!string.Equals(file.entries[b].id, id, StringComparison.Ordinal)) continue;

                Undo.RecordObject(row.Obj, "Restore Original Text");
                row.Text = file.entries[b].original;
                EditorUtility.SetDirty(row.Obj);

                restored++;
                break;
            }
        }

        Undo.CollapseUndoOperations(group);
        MarkScenesDirty();

        File.Delete(BackupPath);
        status = $"Restored {restored} labels.";
    }

    /// <summary>
    /// Identity for restore. Hierarchy path alone is not enough: the three
    /// string fields on a SimulationState all live on the SAME transform, so
    /// path-only matching would restore all three from whichever entry matched
    /// first. The field name disambiguates them.
    /// </summary>
    private static string BackupId(Row row)
    {
        string path = GetPath(row.Transform);

        return row.IsStateField ? $"{path}#{row.field}" : path;
    }

    /// <summary>Backup lives in Library/ - outside Assets/, so Unity never imports it and git never sees it.</summary>
    private static bool HasBackup() => File.Exists(BackupPath);

    private void SaveBackup()
    {
        List<BackupEntry> entries = new List<BackupEntry>();

        for (int i = 0; i < rows.Count; i++)
        {
            if (!rows[i].IsValid) continue;

            entries.Add(new BackupEntry { id = BackupId(rows[i]), original = rows[i].source });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(BackupPath));
        File.WriteAllText(BackupPath, JsonUtility.ToJson(new BackupFile { entries = entries.ToArray() }, true));
    }

    private static void MarkScenesDirty()
    {
        for (int s = 0; s < SceneManager.sceneCount; s++)
        {
            Scene scene = SceneManager.GetSceneAt(s);
            if (scene.isLoaded) EditorSceneManager.MarkSceneDirty(scene);
        }
    }

    // ---------------------------------------------------------------------
    // Translate
    // ---------------------------------------------------------------------

    private void BeginRun()
    {
        cursor = 0;
        running = true;
        nextRequestTime = 0;
        status = "Translating...";

        // Editor coroutines need a package; pumping update is dependency-free.
        EditorApplication.update += Pump;
    }

    private void EndRun(string message)
    {
        EditorApplication.update -= Pump;

        if (inFlight != null) { inFlight.Dispose(); inFlight = null; }

        running = false;
        status = message;
        Repaint();
    }

    private void Pump()
    {
        if (!running) return;

        if (inFlight == null)
        {
            while (cursor < rows.Count && (!rows[cursor].include || !string.IsNullOrEmpty(rows[cursor].translated)))
                cursor++;

            if (cursor >= rows.Count) { EndRun("Translation finished."); return; }

            // Spacing calls keeps the unofficial endpoint from rate-limiting.
            if (EditorApplication.timeSinceStartup < nextRequestTime) return;

            inFlight = CreateRequest(rows[cursor].source);
            inFlight.SendWebRequest();
            return;
        }

        if (!inFlight.isDone) return;

        if (inFlight.result == UnityWebRequest.Result.Success)
            rows[cursor].translated = ParseTranslation(inFlight.downloadHandler.text);
        else
            Debug.LogWarning($"[SceneToSpanish] '{rows[cursor].key}' failed: {inFlight.error}");

        inFlight.Dispose();
        inFlight = null;

        cursor++;
        nextRequestTime = EditorApplication.timeSinceStartup + (delayMs / 1000.0);

        Repaint();
    }

    /// <summary>
    /// Google's free translate endpoint. No API key, but unofficial: it
    /// rate-limits on volume and can change without notice.
    /// </summary>
    private UnityWebRequest CreateRequest(string text)
    {
        string url = "https://translate.googleapis.com/translate_a/single"
                   + "?client=gtx"
                   + $"&sl={UnityWebRequest.EscapeURL(sourceLanguage)}"
                   + $"&tl={UnityWebRequest.EscapeURL(targetLanguage)}"
                   + "&dt=t"
                   + $"&q={UnityWebRequest.EscapeURL(text)}";

        UnityWebRequest request = UnityWebRequest.Get(url);
        request.timeout = 20;
        request.SetRequestHeader("User-Agent", "Mozilla/5.0");   // Error page otherwise.

        return request;
    }

    /// <summary>
    /// Extracts the translation from the nested-array response:
    ///   [[["Hola","Hello",null,null,10]],null,"en"]
    ///
    /// Not an object, so JsonUtility cannot parse it. Walked by bracket depth
    /// rather than regex, because the strings contain commas, brackets and
    /// escaped quotes. Long input arrives as several segments that must be
    /// concatenated - taking only the first truncates the sentence.
    /// </summary>
    private static string ParseTranslation(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;

        StringBuilder result = new StringBuilder();
        int depth = 0, i = 0;
        bool started = false;

        while (i < json.Length)
        {
            char c = json[i];

            if (c == '[') { depth++; i++; continue; }

            if (c == ']')
            {
                depth--;
                i++;

                // The trailing [["en"],null,[1.0],["en"]] sits at depth 3 too,
                // so without stopping here "Hola" comes back as "Holaenen".
                if (started && depth <= 1) break;
                continue;
            }

            if (c == '"')
            {
                string literal = ReadString(json, ref i);

                if (depth == 3)
                {
                    if (literal != null) result.Append(literal);
                    started = true;

                    SkipSegment(json, ref i, ref depth);   // Skip the source text.
                    if (depth <= 1) break;
                }

                continue;
            }

            i++;
        }

        return result.Length > 0 ? result.ToString() : null;
    }

    private static string ReadString(string json, ref int index)
    {
        index++;   // Opening quote.

        StringBuilder sb = new StringBuilder();

        while (index < json.Length)
        {
            char c = json[index];

            if (c == '\\' && index + 1 < json.Length)
            {
                char next = json[index + 1];

                if (next == 'u' && index + 5 < json.Length &&
                    ushort.TryParse(json.Substring(index + 2, 4),
                                    System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    out ushort code))
                {
                    sb.Append((char)code);   // Accented characters arrive escaped.
                    index += 6;
                    continue;
                }

                switch (next)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    default:  sb.Append(next); break;
                }

                index += 2;
                continue;
            }

            if (c == '"') { index++; return sb.ToString(); }

            sb.Append(c);
            index++;
        }

        return sb.ToString();
    }

    private static void SkipSegment(string json, ref int index, ref int depth)
    {
        while (index < json.Length)
        {
            char c = json[index];

            if (c == '"') { ReadString(json, ref index); continue; }
            if (c == '[') depth++;
            if (c == ']') { depth--; index++; return; }

            index++;
        }
    }


    // ---------------------------------------------------------------------
    // Load existing JSON
    // ---------------------------------------------------------------------

    /// <summary>
    /// Fills the translated column from a localization.json, matching on
    /// registry id (and, for UIInteraction rows, the parent state's id +
    /// InteractionGameObjectGUID). Lets you apply or regenerate audio from a
    /// file you already have, without re-running the translator.
    /// </summary>
    private void LoadJson()
    {
        string path = EditorUtility.OpenFilePanel("Load localization.json", "", "json");
        if (string.IsNullOrEmpty(path)) return;

        SimulationLocalizationRoot root;
        try
        {
            root = JsonUtility.FromJson<SimulationLocalizationRoot>(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            status = $"Could not read that file: {e.Message}";
            return;
        }

        List<GameObjectLocalizationEntry> gameObjects = root?.localization_json?.GameObjects;
        if (gameObjects == null)
        {
            status = "That file has no GameObjects array.";
            return;
        }

        if (rows.Count == 0) Scan();   // Nothing to match against yet.

        int matched = 0;
        int unmatched = 0;

        for (int g = 0; g < gameObjects.Count; g++)
        {
            GameObjectLocalizationEntry entry = gameObjects[g];
            if (entry == null || string.IsNullOrEmpty(entry.Object_GUID)) continue;

            bool anyHit = false;

            for (int r = 0; r < rows.Count; r++)
            {
                Row row = rows[r];
                if (row.isUIInteraction) continue;
                if (!string.Equals(row.registryId, entry.Object_GUID, StringComparison.Ordinal)) continue;

                string value = row.state != null
                    ? (row.field == StateField.PromptText ? entry.MainText
                     : row.field == StateField.PrePromptText ? entry.PrePromptText
                     : row.field == StateField.PostPromptText ? entry.PostPromptText
                     : null)
                    : entry.MainText;

                if (string.IsNullOrEmpty(value)) continue;

                row.translated = value;
                matched++;
                anyHit = true;
            }

            if (entry.UIInteractions != null)
            {
                for (int u = 0; u < entry.UIInteractions.Count; u++)
                {
                    UIInteractionLocalizationEntry uiEntry = entry.UIInteractions[u];
                    if (uiEntry == null || string.IsNullOrEmpty(uiEntry.InteractionGameObjectGUID)) continue;
                    if (string.IsNullOrEmpty(uiEntry.UITextField)) continue;

                    for (int r = 0; r < rows.Count; r++)
                    {
                        Row row = rows[r];
                        if (!row.isUIInteraction) continue;
                        if (!string.Equals(row.registryId, uiEntry.InteractionGameObjectGUID, StringComparison.Ordinal)) continue;

                        row.translated = uiEntry.UITextField;
                        matched++;
                        anyHit = true;
                    }
                }
            }

            if (!anyHit) unmatched++;
        }

        if (!string.IsNullOrEmpty(root.localization_json.SceneName))
            status = $"Loaded {matched} translations from '{root.localization_json.SceneName}'.";
        else
            status = $"Loaded {matched} translations.";

        // Unmatched usually means a renamed GameObject or an id from a different scene.
        if (unmatched > 0)
            status += $" {unmatched} object(s) in the file had no matching row in this scene.";
    }


    // ---------------------------------------------------------------------
    // Pack building
    // ---------------------------------------------------------------------

    /// <summary>
    /// Writes localization.json, copies the recorded audio beside it, and zips
    /// the result into exactly the layout LocalizationDownloader reads:
    ///
    ///     localization_es.zip
    ///       |- localization.json
    ///       \- Audio/
    ///            \- 01_PromptAudio.wav
    ///
    /// Requires Api Compatibility Level = .NET Standard 2.1 for ZipFile.
    /// </summary>
    private void BuildPack()
    {
        // Staged on disk first: ZipFile compresses a DIRECTORY, so the layout
        // has to physically exist before it can be zipped.
        string staging = Path.Combine(packOutputFolder, $"localization_{targetLanguage}");

        try
        {
            // Wipe first, or a file left from a previous build gets silently
            // zipped into the new pack.
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
        }
        catch (Exception e)
        {
            status = $"Could not prepare '{staging}': {e.Message}";
            return;
        }

        List<(string registryId, string variant, string fileName)> audio = CopyAudio(staging, out int skipped);
        List<GameObjectLocalizationEntry> gameObjects = BuildGameObjectEntries(audio);

        SimulationLocalizationRoot root = new SimulationLocalizationRoot
        {
            localization_json = new SimulationLocalizationBody
            {
                SceneName = SceneManager.GetActiveScene().name,
                GameObjects = gameObjects
            }
        };

        try
        {
            // No BOM: JsonUtility fails to parse a file that starts with one.
            File.WriteAllText(Path.Combine(staging, "localization.json"),
                              JsonUtility.ToJson(root, true),
                              new UTF8Encoding(false));

            string zipPath = Path.Combine(packOutputFolder, $"localization_{targetLanguage}.zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);

            // includeBaseDirectory: false - true would nest everything under
            // "localization_es/" inside the archive and the downloader would
            // report localization.json missing.
            ZipFile.CreateFromDirectory(staging, zipPath,
                                        System.IO.Compression.CompressionLevel.Optimal, false);

            Directory.Delete(staging, true);

            status = $"Built {Path.GetFileName(zipPath)} - {gameObjects.Count} object(s), {audio.Count} audio file(s)"
                   + (skipped > 0 ? $", {skipped} unmatched/unsupported file(s) skipped." : ".");

            EditorUtility.RevealInFinder(zipPath);
        }
        catch (Exception e)
        {
            status = $"Build failed: {e.Message}";
        }
    }

    /// <summary>
    /// Groups the flat translated rows back into one entry per GameObject,
    /// keyed by registry id, and folds in the audio file assignments from
    /// CopyAudio. Untranslated rows are simply skipped - the field stays null,
    /// so the runtime injector leaves the authored value in place.
    /// </summary>
    private List<GameObjectLocalizationEntry> BuildGameObjectEntries(
        List<(string registryId, string variant, string fileName)> audioAssignments)
    {
        Dictionary<string, GameObjectLocalizationEntry> byId = new Dictionary<string, GameObjectLocalizationEntry>();

        GameObjectLocalizationEntry GetOrCreate(string id, bool isStep)
        {
            if (string.IsNullOrEmpty(id)) return null;

            if (!byId.TryGetValue(id, out GameObjectLocalizationEntry entry))
            {
                entry = new GameObjectLocalizationEntry
                {
                    Object_GUID = id,
                    IsAStepGameObject = isStep ? "true" : "false",
                    HasUIInteraction = "false",
                    UIInteractions = new List<UIInteractionLocalizationEntry>()
                };
                byId[id] = entry;
            }

            return entry;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            Row row = rows[i];
            if (!row.include || string.IsNullOrEmpty(row.translated)) continue;   // Untranslated -> omit, authored text stays.

            if (row.isUIInteraction)
            {
                if (string.IsNullOrEmpty(row.parentStateRegistryId) || string.IsNullOrEmpty(row.registryId)) continue;

                GameObjectLocalizationEntry parent = GetOrCreate(row.parentStateRegistryId, true);
                parent.UIInteractions.Add(new UIInteractionLocalizationEntry
                {
                    InteractionGameObjectGUID = row.registryId,
                    UITextField = row.translated
                });
                parent.HasUIInteraction = "true";
                continue;
            }

            GameObjectLocalizationEntry own = GetOrCreate(row.registryId, row.state != null);
            if (own == null) continue;

            if (row.state != null)
            {
                switch (row.field)
                {
                    case StateField.PromptText:     own.MainText = row.translated; break;
                    case StateField.PrePromptText:  own.PrePromptText = row.translated; break;
                    case StateField.PostPromptText: own.PostPromptText = row.translated; break;
                }
            }
            else
            {
                own.MainText = row.translated;
            }
        }

        for (int i = 0; i < audioAssignments.Count; i++)
        {
            (string registryId, string variant, string fileName) = audioAssignments[i];
            GameObjectLocalizationEntry entry = GetOrCreate(registryId, true);
            if (entry == null) continue;

            if (variant == "Prompt") entry.AudioFile = fileName;
            else if (variant == "PrePrompt") entry.PrePromptAudioFile = fileName;
            else if (variant == "PostPrompt") entry.PostPromptAudioFile = fileName;
        }

        return new List<GameObjectLocalizationEntry>(byId.Values);
    }

    /// <summary>
    /// Copies every playable audio file from the source folder into the pack's
    /// Audio folder, renamed to "{registryId}_{variant}Audio.{ext}", and
    /// returns the (registryId, variant, fileName) triples so BuildGameObjectEntries
    /// can attach each one to the right GameObjectLocalizationEntry field.
    /// A file whose name doesn't resolve to a known state is skipped (counted
    /// in skipped) - there is nothing to attach it to.
    /// </summary>
    private List<(string registryId, string variant, string fileName)> CopyAudio(string staging, out int skipped)
    {
        List<(string, string, string)> entries = new List<(string, string, string)>();
        skipped = 0;

        if (string.IsNullOrEmpty(audioSourceFolder) || !Directory.Exists(audioSourceFolder))
            return entries;   // Audio is optional - a text-only pack is valid.

        string audioFolder = Path.Combine(staging, "Audio");
        Directory.CreateDirectory(audioFolder);

        string[] files = Directory.GetFiles(audioSourceFolder);

        for (int i = 0; i < files.Length; i++)
        {
            string extension = Path.GetExtension(files[i]).ToLowerInvariant();

            // The runtime maps extension -> AudioType. Anything else would copy
            // fine and then silently fail to decode on device, so skip it here.
            if (extension != ".wav" && extension != ".ogg" && extension != ".mp3")
            {
                // .meta files sit beside assets in the project and are not worth reporting.
                if (extension != ".meta") skipped++;
                continue;
            }

            string stem = Path.GetFileNameWithoutExtension(files[i]);
            (string registryId, string variant) = ResolveAudioTarget(stem);

            if (string.IsNullOrEmpty(registryId))
            {
                skipped++;
                continue;
            }

            string fileName = $"{registryId}_{variant}Audio{extension}";

            try
            {
                File.Copy(files[i], Path.Combine(audioFolder, fileName), true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SceneToSpanish] could not copy '{stem}': {e.Message}");
                skipped++;
                continue;
            }

            entries.Add((registryId, variant, fileName));
        }

        // An empty Audio folder makes the downloader log a misleading warning.
        if (Directory.Exists(audioFolder) && Directory.GetFiles(audioFolder).Length == 0)
            Directory.Delete(audioFolder, true);

        return entries;
    }


    /// <summary>Tokens that name a prompt variant rather than a locale or step.</summary>
    private static readonly string[] VariantTokens =
        { "pre", "preprompt", "post", "postprompt", "prompt", "main" };

    /// <summary>
    /// Turns a recording's file name into the state registry id + prompt
    /// variant it belongs to.
    ///
    ///     1_es-ES        -> (id of state "1", Prompt)
    ///     1_pre_es-ES    -> (id of state "1", PrePrompt)
    ///     1_post_es-ES   -> (id of state "1", PostPrompt)
    ///
    /// Locale tails are stripped, the variant token is pulled out, and what is
    /// left is the step identifier, resolved to a registry id via
    /// ResolveStateRegistryId. Order matters: "pre" has the same shape as a
    /// language code, so variant tokens are excluded from locale stripping or
    /// "1_pre_es-ES" loses its "pre" and becomes a main prompt.
    /// </summary>
    private (string registryId, string variant) ResolveAudioTarget(string stem)
    {
        if (string.IsNullOrEmpty(stem)) return (null, null);

        List<string> parts = new List<string>(stem.Split('_'));

        // Strip trailing locale tokens ("es", "es-ES", and the "US" of "en_US").
        while (parts.Count > 1 && !IsVariantToken(parts[parts.Count - 1])
                               && LooksLikeLocale(parts[parts.Count - 1]))
        {
            parts.RemoveAt(parts.Count - 1);
        }

        string variant = "Prompt";

        for (int i = parts.Count - 1; i >= 0; i--)
        {
            string low = parts[i].ToLowerInvariant();

            if (low == "pre" || low == "preprompt")        { variant = "PrePrompt";  parts.RemoveAt(i); break; }
            if (low == "post" || low == "postprompt")      { variant = "PostPrompt"; parts.RemoveAt(i); break; }
            if (low == "prompt" || low == "main")          { variant = "Prompt";     parts.RemoveAt(i); break; }
        }

        string step = string.Join("_", parts);

        return (ResolveStateRegistryId(step), variant);
    }

    private static bool IsVariantToken(string token)
    {
        string low = token.ToLowerInvariant();

        for (int i = 0; i < VariantTokens.Length; i++)
            if (low == VariantTokens[i]) return true;

        return false;
    }

    /// <summary>Matches "es", "en-GB", "US" - two or three letters, optional region tail.</summary>
    private static bool LooksLikeLocale(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;

        return System.Text.RegularExpressions.Regex.IsMatch(
            token, @"^[A-Za-z]{2,3}([-_][A-Za-z]{2,4})?$");
    }

    /// <summary>
    /// Maps the step token parsed from an audio file name to the matching
    /// state's registry id.
    ///
    /// Off (default): the token IS the state's own GameObject name - right
    /// when states are named "1", "2", "3", matching how the recordings are
    /// numbered.
    /// On: the token is a 1-based position in the scanned state list, for
    /// projects whose states are named something else entirely.
    /// </summary>
    private string ResolveStateRegistryId(string step)
    {
        if (stepNumberIsIndex)
        {
            if (int.TryParse(step, out int index) && index >= 1 && index <= stateOrderIds.Count)
                return stateOrderIds[index - 1];

            Debug.LogWarning($"[SceneToSpanish] step '{step}' is not a valid 1-based state index.");
            return null;
        }

        if (stateNameToId.TryGetValue(step, out string id)) return id;

        // States are conventionally named "<number>_Description" rather than
        // just the bare number ("1_Check and ensure..." not "1"), so an exact
        // match against the audio file's step token usually misses. Fall back
        // to whichever state's name STARTS WITH "{step}_" - mirrors
        // SimulationLocalizationInjector's own leading-number fallback for the
        // same mismatch on the text side.
        foreach (KeyValuePair<string, string> kvp in stateNameToId)
        {
            if (kvp.Key.StartsWith(step + "_", StringComparison.Ordinal))
                return kvp.Value;
        }

        Debug.LogWarning($"[SceneToSpanish] No state named or starting with '{step}_' found for audio file mapping.");
        return null;
    }


    /// <summary>Logs how each audio file would be mapped, so it can be checked before building.</summary>
    private void PreviewRename()
    {
        if (string.IsNullOrEmpty(audioSourceFolder) || !Directory.Exists(audioSourceFolder))
        {
            status = "Pick an audio source folder first.";
            return;
        }

        string[] files = Directory.GetFiles(audioSourceFolder);
        StringBuilder sb = new StringBuilder("[SceneToSpanish] audio mapping preview:\n");

        int shown = 0;

        for (int i = 0; i < files.Length; i++)
        {
            string extension = Path.GetExtension(files[i]).ToLowerInvariant();
            if (extension != ".wav" && extension != ".ogg" && extension != ".mp3") continue;

            string stem = Path.GetFileNameWithoutExtension(files[i]);
            (string registryId, string variant) = ResolveAudioTarget(stem);

            string target = string.IsNullOrEmpty(registryId)
                ? "(no matching state)"
                : $"{registryId}_{variant}Audio{extension}";

            sb.AppendLine($"  {stem}{extension}  ->  {target}");
            shown++;
        }

        Debug.Log(sb.ToString());
        status = $"Previewed {shown} files - see the Console.";
    }

    // ---------------------------------------------------------------------
    // Save
    // ---------------------------------------------------------------------

    private void SaveJson()
    {
        string path = EditorUtility.SaveFilePanel("Save localization.json", "", "localization", "json");
        if (string.IsNullOrEmpty(path)) return;

        List<GameObjectLocalizationEntry> gameObjects = BuildGameObjectEntries(
            new List<(string registryId, string variant, string fileName)>());

        SimulationLocalizationRoot root = new SimulationLocalizationRoot
        {
            localization_json = new SimulationLocalizationBody
            {
                SceneName = SceneManager.GetActiveScene().name,
                GameObjects = gameObjects
            }
        };

        // No BOM: JsonUtility fails to parse a file that starts with one.
        File.WriteAllText(path, JsonUtility.ToJson(root, true), new UTF8Encoding(false));

        status = $"Wrote {gameObjects.Count} object(s) to {path}";
        EditorUtility.RevealInFinder(path);
    }
}
