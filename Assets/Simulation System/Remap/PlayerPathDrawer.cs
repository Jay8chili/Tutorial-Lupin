using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class PlayerPathDrawer : MonoBehaviour
{
    [Header("References")]
    public GridPathfinder pathfinder;

    [Header("Line Renderer")]
    public float lineWidth = 0.1f;
    public Color lineColor = Color.yellow;

    [Header("Refresh")]
    [Tooltip("How often (seconds) the path is recalculated while drawing.")]
    [Min(0.02f)] public float refreshInterval = 0.15f;
    [Tooltip("Recalculate if the player or target moved at least this far since the last calculation.")]
    [Min(0f)] public float refreshDistanceThreshold = 0.1f;

    private LineRenderer _lineRenderer;
    private Transform _target;
    private bool _isDrawing;
    private float _nextRefreshTime;
    private Vector3 _lastOrigin;
    private Vector3 _lastTargetPos;

    private void Awake()
    {
        _lineRenderer = GetComponent<LineRenderer>();
        _lineRenderer.useWorldSpace = true;
        _lineRenderer.positionCount = 0;
        _lineRenderer.startWidth = lineWidth;
        _lineRenderer.endWidth = lineWidth;
        _lineRenderer.startColor = lineColor;
        _lineRenderer.endColor = lineColor;
    }

    public void DrawPathTo(Transform target)
    {
        _target = target;
        _isDrawing = target != null && pathfinder != null;

        if (_isDrawing)
        {
            _nextRefreshTime = 0f;
            RefreshPath();
        }
        else
        {
            ClearLine();
        }
    }

    public void StopDrawingPath()
    {
        _isDrawing = false;
        _target = null;
        ClearLine();
    }

    private void Update()
    {
        if (!_isDrawing)
            return;

        if (_target == null)
        {
            StopDrawingPath();
            return;
        }

        if (Time.time < _nextRefreshTime)
            return;

        bool moved = Vector3.Distance(transform.position, _lastOrigin) >= refreshDistanceThreshold
                     || Vector3.Distance(_target.position, _lastTargetPos) >= refreshDistanceThreshold;

        if (moved)
            RefreshPath();

        _nextRefreshTime = Time.time + refreshInterval;
    }

    private void RefreshPath()
    {
        _lastOrigin = transform.position;
        _lastTargetPos = _target.position;

        if (pathfinder.ComputePathWorld(transform.position, _target.position, out List<Vector3> path))
        {
            _lineRenderer.positionCount = path.Count;
            _lineRenderer.SetPositions(path.ToArray());
        }
        else
        {
            ClearLine();
        }
    }

    private void ClearLine()
    {
        _lineRenderer.positionCount = 0;
    }
}
