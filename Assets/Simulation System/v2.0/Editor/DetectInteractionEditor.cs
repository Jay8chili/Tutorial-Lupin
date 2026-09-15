using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Custom editor for DetectInteraction.
/// Draws a live zone gizmo (blue = collider bounds, green = shell preview) that updates
/// as the Shell Offset slider is dragged — works regardless of which GameObject is
/// selected in the hierarchy, including when the parent SimulationState is selected.
///
/// LIVE SHELL-OFFSET MESH PREVIEW
/// ───────────────────────────────
/// The zone's visual mesh (the MeshFilter/MeshRenderer sitting on the same
/// GameObject, baked by the Interaction Builder) does NOT rebuild itself when
/// shellOffset changes — it's a static snapshot. This editor now extrudes that
/// mesh live, in Edit Mode, whenever the slider moves:
///   1. The first time this editor sees the object (OnEnable, or after the
///      mesh reference changes underneath us — e.g. a rebuild), it caches the
///      mesh's vertices as the "base" shape by subtracting off whatever offset
///      is currently baked in (di.shellOffset at that moment), walked back
///      along welded per-vertex normals.
///   2. From then on, dragging the slider recomputes vertices = base + weldedNormal * shellOffset
///      and writes them straight into the mesh instance — no rebuild needed.
/// </summary>
[CustomEditor(typeof(DetectInteraction)), CanEditMultipleObjects]
public class DetectInteractionEditor : Editor
{
    // ── Live mesh preview state (single-selection only) ────────────────────
    private MeshFilter _meshFilter;
    private Mesh _cachedMeshRef;
    private Vector3[] _baseVerts;
    private Vector3[] _weldedDir;
    private float _lastAppliedOffset;

    private void OnEnable()
    {
        RecaptureBaseMesh();
    }

    // Draw in all selection states so the gizmo is visible when the user is
    // dragging the slider inside the SimulationStateEditor (where the
    // SimulationState GO is selected, not the DetectInteraction GO).
    [DrawGizmo(GizmoType.Selected | GizmoType.InSelectionHierarchy | GizmoType.NotInSelectionHierarchy,
               typeof(DetectInteraction))]
    static void DrawZoneGizmo(DetectInteraction di, GizmoType gizmoType)
    {
        if (di == null) return;

        // Gather all trigger BoxColliders — these are the detection zone.
        var colliders = di.GetComponents<BoxCollider>();
        if (colliders == null || colliders.Length == 0) return;

        // Compute world-space union bounds of all trigger colliders.
        bool any = false;
        Bounds combined = default;
        foreach (var bc in colliders)
        {
            if (!bc.isTrigger) continue;
            Vector3 worldCenter = di.transform.TransformPoint(bc.center);
            Vector3 ls = di.transform.lossyScale;
            Vector3 worldSize = new Vector3(
                Mathf.Abs(bc.size.x * ls.x),
                Mathf.Abs(bc.size.y * ls.y),
                Mathf.Abs(bc.size.z * ls.z));
            var b = new Bounds(worldCenter, worldSize);
            if (!any) { combined = b; any = true; }
            else combined.Encapsulate(b);
        }
        if (!any) return;

        bool selected = (gizmoType & (GizmoType.Selected | GizmoType.InSelectionHierarchy)) != 0;
        float fillAlpha = selected ? 0.18f : 0.04f;
        float wireAlpha = selected ? 0.90f : 0.20f;

        // ── Zone boundary (blue) — matches the actual trigger collider(s) ──
        Gizmos.color = new Color(0.25f, 0.65f, 1f, fillAlpha);
        Gizmos.DrawCube(combined.center, combined.size);
        Gizmos.color = new Color(0.25f, 0.65f, 1f, wireAlpha);
        Gizmos.DrawWireCube(combined.center, combined.size);

        // ── Shell preview (green) — zone expanded outward by shellOffset ──
        if (di.shellOffset > 0f)
        {
            Vector3 shellSize = combined.size + Vector3.one * (di.shellOffset * 2f);
            Gizmos.color = new Color(0.35f, 1f, 0.35f, fillAlpha * 0.6f);
            Gizmos.DrawCube(combined.center, shellSize);
            Gizmos.color = new Color(0.35f, 1f, 0.35f, wireAlpha);
            Gizmos.DrawWireCube(combined.center, shellSize);

            // Draw dashed lines from zone corners to shell corners to show expansion.
            if (selected)
            {
                Gizmos.color = new Color(0.35f, 1f, 0.35f, 0.45f);
                DrawExpansionLines(combined.center, combined.extents, di.shellOffset);
            }
        }
    }

    // Draws 8 lines from zone corners outward to shell corners to visualise expansion.
    private static void DrawExpansionLines(Vector3 center, Vector3 extents, float offset)
    {
        Vector3 o = Vector3.one * offset;
        for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    Vector3 sign = new Vector3(sx, sy, sz);
                    Vector3 from = center + Vector3.Scale(extents, sign);
                    Vector3 to   = center + Vector3.Scale(extents + o, sign);
                    Gizmos.DrawLine(from, to);
                }
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        if (targets.Length == 1)
            UpdateLiveMeshPreview();

        EditorGUILayout.Space(4);
        EditorGUILayout.HelpBox(
            "Blue = actual detect zone (trigger colliders).\n" +
            "Green = shell preview — zone boundary after offset is applied.\n" +
            "Dragging Shell Offset also live-updates the zone's visual mesh above (Edit Mode).",
            MessageType.None);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // LIVE SHELL-OFFSET MESH PREVIEW
    // ═════════════════════════════════════════════════════════════════════════

    private void UpdateLiveMeshPreview()
    {
        var di = (DetectInteraction)target;

        // Mesh got rebuilt (or this is the first pass) — resync the base cache.
        var mf = di.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh != _cachedMeshRef)
        {
            RecaptureBaseMesh();
            return;
        }

        if (_baseVerts == null) return;

        if (!Mathf.Approximately(di.shellOffset, _lastAppliedOffset))
            ApplyLiveShellOffset(di.shellOffset);
    }

    /// <summary>
    /// Caches the mesh's un-inflated vertex positions and per-vertex welded
    /// extrude direction, using the CURRENT shellOffset value as the baseline
    /// that's assumed to already be baked into the current mesh shape.
    /// </summary>
    private void RecaptureBaseMesh()
    {
        _meshFilter = null;
        _cachedMeshRef = null;
        _baseVerts = null;
        _weldedDir = null;

        if (targets.Length != 1) return; // live preview only supported for a single selection

        var di = target as DetectInteraction;
        if (di == null) return;

        var mf = di.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null || mf.sharedMesh.vertexCount == 0) return;

        Mesh mesh = mf.sharedMesh;
        Vector3[] verts = mesh.vertices;
        Vector3[] normals = mesh.normals;
        if (normals == null || normals.Length != verts.Length)
        {
            mesh.RecalculateNormals();
            normals = mesh.normals;
        }

        Vector3[] weldedDir = WeldNormals(verts, normals);

        float bakedOffset = di.shellOffset;
        var baseVerts = new Vector3[verts.Length];
        for (int i = 0; i < verts.Length; i++)
            baseVerts[i] = verts[i] - weldedDir[i] * bakedOffset;

        _meshFilter = mf;
        _cachedMeshRef = mesh;
        _baseVerts = baseVerts;
        _weldedDir = weldedDir;
        _lastAppliedOffset = bakedOffset;
    }

    /// <summary>
    /// Averages normals across vertices that share a position so shared edges
    /// don't tear apart when extruded — same weld algorithm the Interaction
    /// Builder uses when it originally bakes the shell offset into a mesh.
    /// </summary>
    private static Vector3[] WeldNormals(Vector3[] verts, Vector3[] normals)
    {
        var sum = new Dictionary<Vector3, Vector3>();
        var count = new Dictionary<Vector3, int>();

        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 key = WeldKey(verts[i]);
            if (sum.ContainsKey(key)) { sum[key] += normals[i]; count[key]++; }
            else { sum[key] = normals[i]; count[key] = 1; }
        }

        var dir = new Vector3[verts.Length];
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 key = WeldKey(verts[i]);
            Vector3 avg = sum[key] / count[key];
            dir[i] = avg.sqrMagnitude > 0.0001f ? avg.normalized : Vector3.zero;
        }
        return dir;
    }

    private static Vector3 WeldKey(Vector3 v) => new Vector3(
        Mathf.Round(v.x * 10000f) / 10000f,
        Mathf.Round(v.y * 10000f) / 10000f,
        Mathf.Round(v.z * 10000f) / 10000f);

    private void ApplyLiveShellOffset(float offset)
    {
        if (_meshFilter == null || _cachedMeshRef == null || _baseVerts == null) return;

        var verts = new Vector3[_baseVerts.Length];
        for (int i = 0; i < verts.Length; i++)
            verts[i] = _baseVerts[i] + _weldedDir[i] * offset;

        _cachedMeshRef.vertices = verts;
        _cachedMeshRef.RecalculateBounds();
        _cachedMeshRef.RecalculateNormals();

        _lastAppliedOffset = offset;

        EditorUtility.SetDirty(_cachedMeshRef);
        EditorUtility.SetDirty(_meshFilter);
        SceneView.RepaintAll();
    }
}
