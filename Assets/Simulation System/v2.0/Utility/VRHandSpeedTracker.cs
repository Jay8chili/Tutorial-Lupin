using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Events;

[Serializable]
public class SpeedViolationEvent : UnityEvent<SpeedTrackingArea> { }

public class VRHandSpeedTracker : MonoBehaviour
{
    [Header("Hand Transforms")]
    [SerializeField] private Transform leftHand;
    [SerializeField] private Transform rightHand;

    [Header("Speed Settings")]
    [Tooltip("Hysteresis buffer (m/s) to prevent rapid event toggling.")]
    [SerializeField, Range(0f, 1f)] private float hysteresis = 0.15f;

    [Tooltip("Smoothing factor for speed calculation (0 = no smoothing).")]
    [SerializeField, Range(0f, 0.95f)] private float smoothing = 0.3f;

    [Header("Timing")]
    [Tooltip("How long the speed violation UI stays visible before auto-resolving.")]
    public float resolveDuration = 3f;

    [Header("Events")]
    [Tooltip("Fired with the SpeedTrackingArea whose threshold was exceeded. Left and right hands are tracked " +
             "independently, so this can fire for two different zones at the same time.")]
    public SpeedViolationEvent OnSpeedViolationDetected;
    [Tooltip("Fired with the SpeedTrackingArea whose violation just resolved.")]
    public SpeedViolationEvent OnSpeedViolationResolved;

    // ── public read-only ─────────────────────────────────────────────
    public float LeftHandSpeed => _left.smoothedSpeed;
    public float RightHandSpeed => _right.smoothedSpeed;
    public bool IsLeftAbove => _left.isAbove;
    public bool IsRightAbove => _right.isAbove;

    // ── internal ─────────────────────────────────────────────────────
    // Left and right hands are tracked as fully independent state — each can be inside a
    // different zone's speed tracking area and in violation at the same time, with its own
    // resolve timer. Neither hand ever blocks or resets the other.
    private class HandState
    {
        public Vector3 prevPosition;
        public float smoothedSpeed;
        public bool isAbove;
        public bool initialized;
        public bool isTracked;
        public bool isTriggered;
        public float threshold;
        public SpeedTrackingArea area;
        public Coroutine resolveRoutine;
    }

    private readonly HandState _left = new HandState();
    private readonly HandState _right = new HandState();
    private float _invFixedDt;

    private bool isLeftIn, IsRightIn;
    // ── lifecycle ────────────────────────────────────────────────────

    private void OnEnable()
    {
        SpeedTrackingArea.OnHandEnteredArea += HandleHandEnteredArea;
        SpeedTrackingArea.OnHandExitedArea += HandleHandExitedArea;
        SpeedTrackingArea.OnTrackingDisabled += StopTracking;
        SpeedTrackingArea.OnHandStayedInArea += HandleHandStayArea;
    }

    private void OnDisable()
    {
        SpeedTrackingArea.OnHandEnteredArea -= HandleHandEnteredArea;
        SpeedTrackingArea.OnHandExitedArea -= HandleHandExitedArea;
        SpeedTrackingArea.OnTrackingDisabled -= StopTracking;
        SpeedTrackingArea.OnHandStayedInArea -= HandleHandStayArea;

    }

    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        if (dt <= 0f) return;
        _invFixedDt = 1f / dt;


        if (isLeftIn)
        {
        if (leftHand != null && _left.isTracked && !_left.isTriggered) ProcessHand(_left, leftHand);

        }
        if (IsRightIn)
        {
         if (rightHand != null && _right.isTracked && !_right.isTriggered) ProcessHand(_right, rightHand);

        }
    }

    // ── area events ──────────────────────────────────────────────────

    private void HandleHandEnteredArea(SpeedTrackingArea area, Collider handCollider)
    {
        if (leftHand != null && (handCollider.transform.IsChildOf(leftHand) || handCollider.transform == leftHand))
        {
            _left.isTracked = true;
            _left.area = area;
            _left.threshold = area.SpeedThreshold;
            _left.initialized = false;
        }
        if (rightHand != null && (handCollider.transform.IsChildOf(rightHand) || handCollider.transform == rightHand))
        {
            _right.isTracked = true;
            _right.area = area;
            _right.threshold = area.SpeedThreshold;
            _right.initialized = false;
        }
    }

    private void HandleHandStayArea(SpeedTrackingArea area, Collider handCollider)
    {
       
        if (leftHand != null && (handCollider.transform.IsChildOf(leftHand) || handCollider.transform == leftHand))
        {
            isLeftIn = true;
        }
        if (rightHand != null && (handCollider.transform.IsChildOf(rightHand) || handCollider.transform == rightHand))
        {
           IsRightIn = true;
        }
    
}

 
    private void HandleHandExitedArea(SpeedTrackingArea area, Collider handCollider)
    {
        if (leftHand != null && (handCollider.transform.IsChildOf(leftHand) || handCollider.transform == leftHand))
        {
        isLeftIn = false;
            _left.isTracked = false;
            _left.isAbove = false;
            _left.initialized = false;
        }
        else if (rightHand != null && (handCollider.transform.IsChildOf(rightHand) || handCollider.transform == rightHand))
        {
        IsRightIn = false;
            _right.isTracked = false;
            _right.isAbove = false;
            _right.initialized = false;
        }
    }

    // ── core ─────────────────────────────────────────────────────────

    private void ProcessHand(HandState state, Transform hand)
    {
        Vector3 pos = hand.position;

        if (!state.initialized)
        {
            state.prevPosition = pos;
            state.initialized = true;
            return;
        }

        float rawSpeed = (pos - state.prevPosition).magnitude * _invFixedDt;
        state.prevPosition = pos;

        state.smoothedSpeed = Mathf.Lerp(rawSpeed, state.smoothedSpeed, smoothing);

        float speed = state.smoothedSpeed;

        if (!state.isAbove)
        {
            if (speed > state.threshold)
            {
                state.isAbove = true;
                HandleViolation(state);
            }
        }
        else
        {
            if (speed < state.threshold - hysteresis)
                state.isAbove = false;
        }
    }

    private void HandleViolation(HandState state)
    {
        if (state.isTriggered) return;

      //  state.isTriggered = true;
        OnSpeedViolationDetected?.Invoke(state.area);

        if (state.resolveRoutine != null) StopCoroutine(state.resolveRoutine);
        state.resolveRoutine = StartCoroutine(ResolveAfterDelay(state));
    }

    private IEnumerator ResolveAfterDelay(HandState state)
    {
        yield return new WaitForSeconds(resolveDuration);

        SpeedTrackingArea area = state.area;
        state.isTriggered = false;
        state.resolveRoutine = null;
        state.isAbove = false;
        OnSpeedViolationResolved?.Invoke(area);
    }

    // ── public API ───────────────────────────────────────────────────

    /// <summary>Blunt global stop — halts tracking for both hands. Does not cancel an already-triggered violation's resolve timer.</summary>
    public void StopTracking()
    {
        _left.isTracked = false;
        _left.isAbove = false;
        _left.initialized = false;

        _right.isTracked = false;
        _right.isAbove = false;
        _right.initialized = false;
    }

    // ── gizmos ───────────────────────────────────────────────────────
#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!Application.isPlaying) return;
        DrawHandGizmo(leftHand, _left);
        DrawHandGizmo(rightHand, _right);
    }

    private void DrawHandGizmo(Transform hand, HandState state)
    {
        if (hand == null || !state.isTracked) return;

        float speed = state.smoothedSpeed;
        if (speed > state.threshold) Gizmos.color = Color.red;
        else if (speed > state.threshold - hysteresis) Gizmos.color = Color.yellow;
        else Gizmos.color = Color.green;

        Gizmos.DrawWireSphere(hand.position, 0.05f);
        UnityEditor.Handles.Label(
            hand.position + Vector3.up * 0.08f,
            $"{speed:F2} m/s");
    }
#endif
}
