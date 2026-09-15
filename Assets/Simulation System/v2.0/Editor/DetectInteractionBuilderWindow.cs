// DetectInteraction Builder — Editor/DetectInteractionBuilderWindow.cs
// Open via:  Tools → Detect Interaction Builder

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

public class DetectInteractionBuilderWindow : EditorWindow
{
    private const string SHADER_NAME = "Bot/InteractionForTransparentMat";
    private const string WINDOW_TITLE = "Detect Interaction Builder";

    // ── Per-entry data ────────────────────────────────────────────────────────

    [System.Serializable]
    private class DetectEntry
    {
        public string label = "Detect";
        public bool expanded = true;

        public List<GameObject> detectObjects = new List<GameObject>();

        public GameObject targetObject;

        public bool separateSources = false;
        public GameObject combinedSource;
        public GameObject meshSource;
        public GameObject positionSource;
    }

    // ── Window state ──────────────────────────────────────────────────────────

    private List<DetectEntry> _entries = new List<DetectEntry>();

    // Scene pick state
    private bool _pickFromScene = false;
    private int _pickEntryIndex;

    private enum PickTarget { Combined, Mesh, Position }
    private PickTarget _pickTarget;

    private Vector2 _scrollPos;

    // ── Styles (lazily built) ─────────────────────────────────────────────────

    private GUIStyle _headerStyle;
    private GUIStyle _entryBoxStyle;

    private static readonly Color COL_PICK_ACTIVE = new Color(0.3f, 0.7f, 1f);
    private static readonly Color COL_BUILD = new Color(0.4f, 0.9f, 0.5f);
    private static readonly Color COL_REMOVE = new Color(1f, 0.4f, 0.4f);
    private static readonly Color COL_SECTION = new Color(0.18f, 0.18f, 0.18f, 0.55f);

    // ── Menu item ─────────────────────────────────────────────────────────────

    [MenuItem("Tools/Detect Interaction Builder")]
    public static void Open() =>
        GetWindow<DetectInteractionBuilderWindow>(WINDOW_TITLE).minSize = new Vector2(380, 500);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void OnEnable() => SceneView.duringSceneGui += OnSceneGUI;
    private void OnDisable() { SceneView.duringSceneGui -= OnSceneGUI; _pickFromScene = false; }

    // ── Main GUI ──────────────────────────────────────────────────────────────

    private void OnGUI()
    {
        BuildStyles();

        // Window header
        var prev = GUI.backgroundColor;
        GUI.backgroundColor = COL_SECTION;
        GUILayout.Box(WINDOW_TITLE, _headerStyle, GUILayout.ExpandWidth(true), GUILayout.Height(28));
        GUI.backgroundColor = prev;
        EditorGUILayout.Space(6);

        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

        for (int i = 0; i < _entries.Count; i++)
            DrawEntry(i);

        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space(6);

        // Add entry button
        if (GUILayout.Button("+ Add Detect", GUILayout.Height(26)))
            _entries.Add(new DetectEntry { label = $"Detect {_entries.Count + 1}" });

        EditorGUILayout.Space(4);

        // Build all button
        GUI.backgroundColor = COL_BUILD;
        if (GUILayout.Button("Build All", GUILayout.Height(32)))
            BuildAll();
        GUI.backgroundColor = Color.white;

        EditorGUILayout.Space(4);
    }

    // ── Entry drawer ──────────────────────────────────────────────────────────

    private void DrawEntry(int i)
    {
        var e = _entries[i];

        // Entry container
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        // ── Header row ───────────────────────────────────────────────────────
        EditorGUILayout.BeginHorizontal();

        e.expanded = EditorGUILayout.Foldout(e.expanded, GUIContent.none, true, GUIStyle.none);
        // Clickable label / name field
        e.label = EditorGUILayout.TextField(e.label, EditorStyles.boldLabel);

        GUILayout.FlexibleSpace();

        // Build single entry
        GUI.backgroundColor = COL_BUILD;
        if (GUILayout.Button("Build", GUILayout.Width(52), GUILayout.Height(18)))
        {
            string err = Validate(e);
            if (err != null) EditorUtility.DisplayDialog("Validation Error", err, "OK");
            else Build(e);
        }
        GUI.backgroundColor = Color.white;

        // Remove entry
        GUI.backgroundColor = COL_REMOVE;
        if (GUILayout.Button("✕", GUILayout.Width(22), GUILayout.Height(18)))
        {
            _entries.RemoveAt(i);
            if (_pickFromScene && _pickEntryIndex == i) _pickFromScene = false;
            GUIUtility.ExitGUI();
        }
        GUI.backgroundColor = Color.white;

        EditorGUILayout.EndHorizontal();

        if (!e.expanded) { EditorGUILayout.EndVertical(); EditorGUILayout.Space(2); return; }

        EditorGUILayout.Space(4);

        // ── Objects to detect ────────────────────────────────────────────────
        DrawSectionLabel("Objects To Detect");

        for (int j = 0; j < e.detectObjects.Count; j++)
        {
            EditorGUILayout.BeginHorizontal();
            e.detectObjects[j] = (GameObject)EditorGUILayout.ObjectField(
                $"  [{j}]", e.detectObjects[j], typeof(GameObject), true);
            if (GUILayout.Button("✕", GUILayout.Width(22)))
            {
                e.detectObjects.RemoveAt(j);
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();
        }
        if (GUILayout.Button("  + Add Object"))
            e.detectObjects.Add(null);

        EditorGUILayout.Space(6);

        // ── Target object ────────────────────────────────────────────────────
        DrawSectionLabel("Detect Object");
        e.targetObject = (GameObject)EditorGUILayout.ObjectField(
            "Target Object", e.targetObject, typeof(GameObject), true);
        EditorGUILayout.HelpBox(
            e.targetObject == null
                ? "No target assigned — a new GameObject will be created on Build."
                : "DetectInteraction will be added to the assigned object.",
            MessageType.None);

        EditorGUILayout.Space(6);

        // ── Mesh & position source ───────────────────────────────────────────
        DrawSectionLabel("Mesh & Position Source");
        e.separateSources = EditorGUILayout.Toggle(
            new GUIContent("Separate Mesh / Position",
                "Pick different objects for mesh shape and world position."),
            e.separateSources);

        EditorGUILayout.Space(4);

        if (!e.separateSources)
        {
            DrawPickRow(i, "Source Object", ref e.combinedSource, PickTarget.Combined);
            if (IsPickingFor(i, PickTarget.Combined))
                EditorGUILayout.HelpBox("Click an object in the Scene View.", MessageType.Warning);
        }
        else
        {
            DrawPickRow(i, "Mesh Source", ref e.meshSource, PickTarget.Mesh);
            DrawPickRow(i, "Position Source", ref e.positionSource, PickTarget.Position);
            if (_pickFromScene && _pickEntryIndex == i)
                EditorGUILayout.HelpBox(
                    $"Click an object in the Scene View to set {_pickTarget}.",
                    MessageType.Warning);
        }

        EditorGUILayout.EndVertical();
        EditorGUILayout.Space(4);
    }

    private void DrawPickRow(int entryIndex, string label, ref GameObject field, PickTarget target)
    {
        EditorGUILayout.BeginHorizontal();
        field = (GameObject)EditorGUILayout.ObjectField(label, field, typeof(GameObject), true);

        bool active = IsPickingFor(entryIndex, target);
        GUI.backgroundColor = active ? COL_PICK_ACTIVE : Color.white;
        if (GUILayout.Button(active ? "Cancel" : "Pick", GUILayout.Width(54)))
        {
            if (active)
            {
                _pickFromScene = false;
            }
            else
            {
                _pickFromScene = true;
                _pickEntryIndex = entryIndex;
                _pickTarget = target;
            }
        }
        GUI.backgroundColor = Color.white;
        EditorGUILayout.EndHorizontal();
    }

    private bool IsPickingFor(int entryIndex, PickTarget target) =>
        _pickFromScene && _pickEntryIndex == entryIndex && _pickTarget == target;

    // ── Scene picking ─────────────────────────────────────────────────────────

    private void OnSceneGUI(SceneView sv)
    {
        if (!_pickFromScene) return;

        HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
        Event ev = Event.current;

        if (ev.type == EventType.MouseDown && ev.button == 0)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(ev.mousePosition);
            GameObject picked = Physics.Raycast(ray, out RaycastHit hit)
                ? hit.collider.gameObject
                : HandleUtility.PickGameObject(ev.mousePosition, false);

            if (picked != null && _pickEntryIndex < _entries.Count)
            {
                var e = _entries[_pickEntryIndex];
                if (!e.separateSources || _pickTarget == PickTarget.Combined)
                    e.combinedSource = picked;
                else if (_pickTarget == PickTarget.Mesh)
                    e.meshSource = picked;
                else
                    e.positionSource = picked;

                _pickFromScene = false;
                Repaint();
                ev.Use();
            }
        }

        if (ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Escape)
        {
            _pickFromScene = false;
            Repaint();
            ev.Use();
        }
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private string Validate(DetectEntry e)
    {
        if (!e.separateSources && e.combinedSource == null)
            return "Assign a Source Object (or pick one from the scene).";
        if (e.separateSources && e.meshSource == null)
            return "Assign a Mesh Source.";
        if (e.separateSources && e.positionSource == null)
            return "Assign a Position Source.";
        if (e.detectObjects.Count == 0)
            return "Add at least one Object To Detect.";
        for (int i = 0; i < e.detectObjects.Count; i++)
            if (e.detectObjects[i] == null)
                return $"Objects To Detect slot [{i}] is empty.";
        return null;
    }

    private void BuildAll()
    {
        int built = 0;
        for (int i = 0; i < _entries.Count; i++)
        {
            string err = Validate(_entries[i]);
            if (err != null)
            {
                continue;
            }
            Build(_entries[i]);
            built++;
        }
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    private void Build(DetectEntry e)
    {
        GameObject meshObj = e.separateSources ? e.meshSource : e.combinedSource;
        GameObject positionObj = e.separateSources ? e.positionSource : e.combinedSource;

        // 1 — Target GameObject
        GameObject go = e.targetObject;
        if (go == null)
        {
            go = new GameObject("Detect_" + meshObj.name);
            Undo.RegisterCreatedObjectUndo(go, "Create Detect");
        }

        Undo.RecordObject(go.transform, "Setup Detect Transform");
        go.transform.position = positionObj.transform.position;
        go.transform.rotation = positionObj.transform.rotation;
        go.transform.localScale = Vector3.one;

        // 2 — Find mesh on source or children
        var sourceMR = meshObj.GetComponent<MeshRenderer>()
                    ?? meshObj.GetComponentInChildren<MeshRenderer>();
        var sourceMF = meshObj.GetComponent<MeshFilter>()
                    ?? meshObj.GetComponentInChildren<MeshFilter>();

        if (sourceMR != null && sourceMF != null)
        {
            // Mesh filter
            var mf = go.GetComponent<MeshFilter>() ?? Undo.AddComponent<MeshFilter>(go);
            mf.sharedMesh = sourceMF.sharedMesh;

            // Mesh renderer — Bot/Interaction material only, original removed
            var mr = go.GetComponent<MeshRenderer>() ?? Undo.AddComponent<MeshRenderer>(go);
            var shader = Shader.Find(SHADER_NAME);
            if (shader != null)
                mr.sharedMaterials = new[] { new Material(shader) };

            // Match scale
            go.transform.localScale = meshObj.transform.lossyScale;
        }
        else
        {
        }

        // 3 — Collider (convex MeshCollider as trigger, fallback BoxCollider)
        if (sourceMF != null)
        {
            var col = go.GetComponent<MeshCollider>() ?? Undo.AddComponent<MeshCollider>(go);
            col.sharedMesh = sourceMF.sharedMesh;
            col.convex = true;
            col.isTrigger = true;
        }
        else
        {
            var col = go.GetComponent<BoxCollider>() ?? Undo.AddComponent<BoxCollider>(go);
            col.isTrigger = true;
        }

        // 4 — Rigidbody, kinematic (no gravity, no physics movement)
        var rb = go.GetComponent<Rigidbody>() ?? Undo.AddComponent<Rigidbody>(go);
        rb.useGravity = false;
        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.None;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

        // 5 — DetectInteraction component
        var detect = go.GetComponent<DetectInteraction>() ?? Undo.AddComponent<DetectInteraction>(go);

        // 6 — Populate ObjectsToBeDetectedList
        var so = new SerializedObject(detect);
        var list = so.FindProperty("ObjectsToBeDetectedList");
        list.ClearArray();
        for (int i = 0; i < e.detectObjects.Count; i++)
        {
            list.InsertArrayElementAtIndex(i);
            list.GetArrayElementAtIndex(i).objectReferenceValue = e.detectObjects[i];
        }
        so.ApplyModifiedProperties();

        // 7 — Select result
        Selection.activeGameObject = go;
        EditorUtility.SetDirty(go);
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private void BuildStyles()
    {
        if (_headerStyle != null) return;

        _headerStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 13,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Color.white }
        };
    }

    private static void DrawSectionLabel(string title)
    {
        EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
        var r = GUILayoutUtility.GetLastRect();
        r.y += EditorGUIUtility.singleLineHeight - 1;
        r.height = 1;
        EditorGUI.DrawRect(r, new Color(0.5f, 0.5f, 0.5f, 0.4f));
        EditorGUILayout.Space(2);
    }
}