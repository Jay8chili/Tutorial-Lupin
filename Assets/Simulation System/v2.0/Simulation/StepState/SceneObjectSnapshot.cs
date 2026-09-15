using UnityEngine;

/// <summary>
/// Generic "put this object back exactly as it was" hook for scene props that aren't a
/// GrabInteraction (doors, valves, discardable props, etc). Drop onto any GameObject and add
/// it to a SimulationState's extraStepStateHooks list — on Restart Step / Restart Interaction
/// it undoes, in one component:
///   - Enabled/disabled: an object that got enabled and moved is disabled and moved back;
///     an object that got disabled is re-enabled.
///   - Position/rotation/scale/parent.
///   - Any Animator on this object: whatever state/parameters it had (e.g. mid-way through a
///     door-open animation, or a bool flipped by AnimatorBooleanController) is restored, so a
///     played animation is undone along with everything else.
/// </summary>
public class SceneObjectSnapshot : StepStateComponent
{
    [Tooltip("Also restore the parent this object had when captured.")]
    public bool restoreParent = true;

    [Tooltip("Also restore this object's active/inactive state.")]
    public bool restoreActiveState = true;

    [Header("Animator (optional)")]
    [Tooltip("Also restore this object's Animator state + parameters when restoring. Auto-found via GetComponent if left empty.")]
    public bool restoreAnimatorState = true;

    [Tooltip("Animator to snapshot/restore. Auto-found via GetComponent on this object if left empty.")]
    public Animator animator;

    // ── Transform / active state ────────────────────────────────────────────
    private Vector3 _position;
    private Quaternion _rotation;
    private Vector3 _localScale;
    private Transform _parent;
    private bool _wasActive;
    private bool _captured;

    // ── Animator state ───────────────────────────────────────────────────────
    private AnimatorControllerParameter[] _animatorParams;
    private object[] _animatorParamValues;
    private int[] _animatorStateHashes;
    private float[] _animatorStateTimes;
    private bool _animatorCaptured;

    private void Awake()
    {
        if (animator == null)
            animator = GetComponent<Animator>();
    }

    public override void CaptureStepState()
    {
        _position = transform.position;
        _rotation = transform.rotation;
        _localScale = transform.localScale;
        _parent = transform.parent;
        _wasActive = gameObject.activeSelf;
        _captured = true;

        CaptureAnimator();
    }

    public override void RestoreStepState()
    {
        if (!_captured) return;

        if (restoreParent && transform.parent != _parent)
            transform.SetParent(_parent, worldPositionStays: true);

        transform.SetPositionAndRotation(_position, _rotation);
        transform.localScale = _localScale;

        if (restoreActiveState)
            gameObject.SetActive(_wasActive);

        RestoreAnimator();
    }

    // ── Animator capture/restore ────────────────────────────────────────────
    // Parameters are restored BEFORE Play() so a bool/trigger left over from the played
    // animation (e.g. AnimatorBooleanController's "IsOpen") can't immediately re-trigger the
    // same transition the moment the state is force-set back.

    private void CaptureAnimator()
    {
        if (animator == null || !restoreAnimatorState)
        {
            _animatorCaptured = false;
            return;
        }

        _animatorParams = animator.parameters;
        _animatorParamValues = new object[_animatorParams.Length];
        for (int i = 0; i < _animatorParams.Length; i++)
        {
            switch (_animatorParams[i].type)
            {
                case AnimatorControllerParameterType.Bool:
                    _animatorParamValues[i] = animator.GetBool(_animatorParams[i].name);
                    break;
                case AnimatorControllerParameterType.Float:
                    _animatorParamValues[i] = animator.GetFloat(_animatorParams[i].name);
                    break;
                case AnimatorControllerParameterType.Int:
                    _animatorParamValues[i] = animator.GetInteger(_animatorParams[i].name);
                    break;
            }
        }

        int layerCount = animator.layerCount;
        _animatorStateHashes = new int[layerCount];
        _animatorStateTimes = new float[layerCount];
        for (int layer = 0; layer < layerCount; layer++)
        {
            AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(layer);
            _animatorStateHashes[layer] = info.fullPathHash;
            _animatorStateTimes[layer] = info.normalizedTime;
        }

        _animatorCaptured = true;
    }

    private void RestoreAnimator()
    {
        if (!_animatorCaptured || animator == null) return;

        for (int i = 0; i < _animatorParams.Length; i++)
        {
            switch (_animatorParams[i].type)
            {
                case AnimatorControllerParameterType.Bool:
                    animator.SetBool(_animatorParams[i].name, (bool)_animatorParamValues[i]);
                    break;
                case AnimatorControllerParameterType.Float:
                    animator.SetFloat(_animatorParams[i].name, (float)_animatorParamValues[i]);
                    break;
                case AnimatorControllerParameterType.Int:
                    animator.SetInteger(_animatorParams[i].name, (int)_animatorParamValues[i]);
                    break;
                case AnimatorControllerParameterType.Trigger:
                    animator.ResetTrigger(_animatorParams[i].name);
                    break;
            }
        }

        for (int layer = 0; layer < _animatorStateHashes.Length; layer++)
            animator.Play(_animatorStateHashes[layer], layer, _animatorStateTimes[layer]);

        // Force the snapped state to apply immediately instead of waiting a frame.
        animator.Update(0f);
    }
}
