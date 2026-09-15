using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;

namespace SimulationSystem.V02.Simulation.Managers
{
    /// <summary>
    /// Identifies which controller(s) should receive haptic feedback.
    /// </summary>
    public enum HapticHand
    {
        Left,
        Right,
        Both
    }

    /// <summary>
    /// Inspector-tunable amplitude/duration/hand triple for a single haptic event.
    /// Replaces hardcoded literals — shows as a foldout in the Inspector.
    /// </summary>
    [System.Serializable]
    public struct HapticPreset
    {
        [Range(0f, 1f)] public float amplitude;
        [Min(0f)] public float duration;
        public HapticHand hand;

        public HapticPreset(float amplitude, float duration, HapticHand hand)
        {
            this.amplitude = amplitude;
            this.duration = duration;
            this.hand = hand;
        }
    }

    /// <summary>
    /// Centralized service for playing XR haptic feedback.
    /// Use this instead of calling XR haptics APIs directly.
    ///
    /// USAGE — call static methods directly from anywhere, no reference needed:
    ///   await HapticManager.Grab(HapticHand.Right);
    ///   HapticManager.UIClick(HapticHand.Left);
    ///   await HapticManager.DoubleBuzz(HapticHand.Both);
    /// </summary>
    public class HapticManager : MonoBehaviour
    {
        #region Singleton

        public static HapticManager Instance { get; private set; }

        #endregion

        // ─────────────────────────────────────────────────────────────────
        #region Static API — call from anywhere, no reference needed

        // ── Assessment ───────────────────────────────────────────────────
        public static Task UIClick(HapticHand hand, CancellationToken ct = default)
                             => Instance._UIClick(hand, ct);
        public static Task Grab(HapticHand hand, CancellationToken ct = default)
                             => Instance._Grab(hand, ct);
        public static Task WrongGrab(HapticHand hand, CancellationToken ct = default)
                             => Instance._WrongGrab(hand, ct);
        public static Task Detecting(HapticHand hand, CancellationToken ct = default)
                             => Instance._Detecting(hand, ct);
        public static Task DetectionComplete(CancellationToken ct = default)
                             => Instance._DetectionComplete(ct);
        public static Task Error(CancellationToken ct = default)
                             => Instance._Error(ct);

        // ── Interaction lifecycle ─────────────────────────────────────────
        public static Task InteractionStart(HapticHand hand, CancellationToken ct = default)
                             => Instance._InteractionStart(hand, ct);
        public static Task InteractionEnd(HapticHand hand, CancellationToken ct = default)
                             => Instance._InteractionEnd(hand, ct);
        public static Task InteractionSuspend(HapticHand hand, CancellationToken ct = default)
                             => Instance._InteractionSuspend(hand, ct);

        // Grab
        public static Task OnGrabbed(HapticHand hand, CancellationToken ct = default)
                             => Instance._OnGrabbed(hand, ct);
        public static Task OnProximityGrab(HapticHand hand, CancellationToken ct = default)
                             => Instance._OnProximityGrabb(hand, ct);
        public static Task OnUngrabbed(CancellationToken ct = default)
                             => Instance._OnUngrabbed(ct);

        // ── Simulation lifecycle ────────────────────────────────────────────
        public static Task SimulationStart(CancellationToken ct = default)
                             => Instance._SimulationStart(ct);
        public static Task SimulationComplete(CancellationToken ct = default)
                             => Instance._SimulationComplete(ct);

        // ── Per-type interaction complete ──────────────────────────────────
        public static Task UIComplete(CancellationToken ct = default)
                             => Instance._UIComplete(ct);
        public static Task GrabComplete(CancellationToken ct = default)
                             => Instance._GrabComplete(ct);
        public static Task DetectStart(CancellationToken ct = default)
                             => Instance._DetectStart(ct);
        public static Task GazeStart(CancellationToken ct = default)
                             => Instance._GazeStart(ct);
        public static Task GazeComplete(CancellationToken ct = default)
                             => Instance._GazeComplete(ct);

        // ── On Detecting / On Gazing — curve-driven ticking haptic ──────────
        /// <summary>
        /// Runs onDetectingCurve over <paramref name="duration"/> seconds, sending a
        /// short haptic impulse every onDetectingTickInterval seconds with amplitude
        /// taken from the curve. Intended to run alongside — not replace — the calling
        /// interaction's own Timer, which already calls OnInteractionComplete when it ends.
        /// </summary>
        public static Task PlayOnDetectingCurve(float duration, CancellationToken ct = default)
                             => Instance._PlayOnDetectingCurve(duration, ct);
        public static void StopOnDetectingCurve()
                             => Instance._StopOnDetectingCurve();

        /// <summary>Same as PlayOnDetectingCurve, but for gaze interactions — its own curve/tick/hand fields and routine handle.</summary>
        public static Task PlayOnGazingCurve(float duration, CancellationToken ct = default)
                             => Instance._PlayOnGazingCurve(duration, ct);
        public static void StopOnGazingCurve()
                             => Instance._StopOnGazingCurve();

        // ── Special ──────────────────────────────────────────────────────
        /// <summary>
        /// Two buzzes in sequence — first buzz plays, then a short gap, then the second.
        /// </summary>
        public static Task DoubleBuzz(HapticHand hand, CancellationToken ct = default)
                             => Instance._DoubleBuzz(hand, ct);

        #endregion

        // ─────────────────────────────────────────────────────────────────
        #region Haptic Output

        [Header("Haptic Output")]
        [Tooltip("HapticImpulsePlayer on the Left Controller (bound to its own Haptic input action).")]
        [SerializeField] private HapticImpulsePlayer leftHapticPlayer;
        [Tooltip("HapticImpulsePlayer on the Right Controller (bound to its own Haptic input action).")]
        [SerializeField] private HapticImpulsePlayer rightHapticPlayer;

        #endregion

        // ─────────────────────────────────────────────────────────────────
        #region Haptic Presets

        [Header("Interaction Lifecycle")]
        [SerializeField] private HapticPreset interactionStart = new HapticPreset(0.5f, 0.20f, HapticHand.Both);

        [Header("Simulation Lifecycle")]
        [SerializeField] private HapticPreset simulationStart = new HapticPreset(0.3f, 0.15f, HapticHand.Both);
        [SerializeField] private HapticPreset simulationComplete = new HapticPreset(0.6f, 0.25f, HapticHand.Both);

        [Header("Per-Type Interaction Complete")]
        [SerializeField] private HapticPreset uiComplete = new HapticPreset(0.3f, 0.08f, HapticHand.Both);
        [SerializeField] private HapticPreset grabComplete = new HapticPreset(0.5f, 0.15f, HapticHand.Both);
        [SerializeField] private HapticPreset detectComplete = new HapticPreset(0.6f, 0.20f, HapticHand.Both);

        [Header("Grab / Ungrab")]
        [SerializeField] private HapticPreset onGrabbed = new HapticPreset(0.25f, 0.10f, HapticHand.Both);
        [SerializeField] private HapticPreset onUngrabbed = new HapticPreset(0.2f, 0.08f, HapticHand.Both);

        [Header("Detect Start")]
        [SerializeField] private HapticPreset detectStart = new HapticPreset(0.2f, 0.10f, HapticHand.Both);

        [Header("Gaze Start / Complete")]
        [SerializeField] private HapticPreset gazeStart = new HapticPreset(0.2f, 0.10f, HapticHand.Both);
        [SerializeField] private HapticPreset gazeComplete = new HapticPreset(0.6f, 0.20f, HapticHand.Both);

        [Header("On Detecting — Curve")]
        [Tooltip("Haptic amplitude over normalized interaction progress (0 = detect start, 1 = detect complete). " +
                 "Sampled repeatedly while the detect interaction's own Timer runs — it does not drive completion " +
                 "itself, it just tracks the same duration.")]
        [SerializeField] private AnimationCurve onDetectingCurve = AnimationCurve.Linear(0f, 0.1f, 1f, 0.6f);
        [Tooltip("Seconds between each sampled haptic pulse while the curve plays.")]
        [SerializeField] private float onDetectingTickInterval = 0.08f;
        [SerializeField] private HapticHand onDetectingHand = HapticHand.Both;

        [Header("On Gazing — Curve")]
        [Tooltip("Same idea as On Detecting's curve, but for gaze interactions (0 = gaze start, 1 = gaze complete).")]
        [SerializeField] private AnimationCurve onGazingCurve = AnimationCurve.Linear(0f, 0.1f, 1f, 0.6f);
        [Tooltip("Seconds between each sampled haptic pulse while the curve plays.")]
        [SerializeField] private float onGazingTickInterval = 0.08f;
        [SerializeField] private HapticHand onGazingHand = HapticHand.Both;

        #endregion

        // ─────────────────────────────────────────────────────────────────
        #region Internal State

        private Coroutine _hapticRoutine;
        private Coroutine _detectingCurveRoutine; // separate handles — never stomp _hapticRoutine's one-shot pulses
        private Coroutine _gazingCurveRoutine;    // or each other, so Detect and Gaze curves can run independently

        #endregion

        // ─────────────────────────────────────────────────────────────────
        #region Unity Lifecycle

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                //DontDestroyOnLoad(gameObject);
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────
        #region Instance Implementation — Presets

        private Task _UIClick(HapticHand hand, CancellationToken ct) => PlayHapticAsync(0.2f, 0.05f, hand, ct: ct);
        private Task _Grab(HapticHand hand, CancellationToken ct) => PlayHapticAsync(0.4f, 0.10f, hand, ct: ct);
        private Task _WrongGrab(HapticHand hand, CancellationToken ct) => PlayHapticAsync(0.8f, 0.30f, hand, ct: ct);
        private Task _Detecting(HapticHand hand, CancellationToken ct) => PlayHapticAsync(0.15f, 0.20f, hand, ct: ct);
        private Task _DetectionComplete(CancellationToken ct) => PlayPreset(detectComplete, ct);
        private Task _Error(CancellationToken ct) => PlayHapticAsync(1.0f, 0.40f, HapticHand.Both, ct: ct);

        // ── Interaction lifecycle ─────────────────────────────────────────
        // Start  — firm double-length pulse so the user knows something began
        private Task _InteractionStart(HapticHand hand, CancellationToken ct) => PlayHapticAsync(interactionStart.amplitude, interactionStart.duration, hand, ct: ct);
        // End    — clean short confirm pulse
        private Task _InteractionEnd(HapticHand hand, CancellationToken ct) => PlayHapticAsync(0.6f, 0.15f, hand, ct: ct);
        // Suspend — soft low-amplitude nudge to signal pause
        private Task _InteractionSuspend(HapticHand hand, CancellationToken ct) => PlayHapticAsync(0.25f, 0.10f, hand, ct: ct);

        //Grab
        private Task _OnGrabbed(HapticHand hand, CancellationToken ct) => PlayHapticAsync(onGrabbed.amplitude, onGrabbed.duration, hand, ct: ct);
        private Task _OnProximityGrabb(HapticHand hand, CancellationToken ct) => PlayHapticAsync(0.25f, 0.10f, hand, ct: ct);
        private Task _OnUngrabbed(CancellationToken ct) => PlayPreset(onUngrabbed, ct);

        // ── Simulation lifecycle ────────────────────────────────────────────
        private Task _SimulationStart(CancellationToken ct) => PlayPreset(simulationStart, ct);
        private Task _SimulationComplete(CancellationToken ct) => PlayPreset(simulationComplete, ct);

        // ── Per-type interaction complete ──────────────────────────────────
        private Task _UIComplete(CancellationToken ct) => PlayPreset(uiComplete, ct);
        private Task _GrabComplete(CancellationToken ct) => PlayPreset(grabComplete, ct);
        private Task _DetectStart(CancellationToken ct) => PlayPreset(detectStart, ct);
        private Task _GazeStart(CancellationToken ct) => PlayPreset(gazeStart, ct);
        private Task _GazeComplete(CancellationToken ct) => PlayPreset(gazeComplete, ct);

        // ── Double buzz ───────────────────────────────────────────────────
      
        private async Task _DoubleBuzz(HapticHand hand, CancellationToken ct)
        {
            await PlayHapticAsync(0.6f, 0.10f, hand, ct: ct);

            if (ct.IsCancellationRequested) return;

            // Short silent gap between the two buzzes
            await Task.Delay(80, ct);

            if (ct.IsCancellationRequested) return;

            await PlayHapticAsync(0.6f, 0.10f, hand, ct: ct);
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────
        #region Core Async Haptic Engine

        /// <summary>Plays a fully-configured HapticPreset (amplitude/duration/hand all inspector-driven).</summary>
        private Task PlayPreset(HapticPreset preset, CancellationToken ct) =>
            PlayHapticAsync(preset.amplitude, preset.duration, preset.hand, ct: ct);

        /// <summary>
        /// Sends a haptic impulse and returns a Task that completes once
        /// <paramref name="duration"/> seconds have elapsed.
        /// Cancelling the token resolves the Task early but does NOT cut
        /// the hardware impulse short (XR haptics have no stop API).
        /// </summary>
        private Task PlayHapticAsync(
            float amplitude,
            float duration,
            HapticHand hand,
            CancellationToken ct = default)
        {
            amplitude = Mathf.Clamp01(amplitude);

#if UNITY_EDITOR
#endif

            SendImpulse(hand, amplitude, duration);

            var tcs = new TaskCompletionSource<bool>();

            if (_hapticRoutine != null)
                StopCoroutine(_hapticRoutine);

            _hapticRoutine = StartCoroutine(HapticWaitRoutine(duration, ct, tcs));

            return tcs.Task;
        }

        private IEnumerator HapticWaitRoutine(
            float duration,
            CancellationToken ct,
            TaskCompletionSource<bool> tcs)
        {
            float elapsed = 0f;

            while (elapsed < duration)
            {
                if (ct.IsCancellationRequested)
                {
                    tcs.TrySetCanceled();
                    yield break;
                }
                elapsed += Time.deltaTime;
                yield return null;
            }

            tcs.TrySetResult(true);
        }

        /// <summary>
        /// Sends the impulse directly through the per-hand HapticImpulsePlayer components on the
        /// rig's Left/Right Controller (bound to their own Haptic input action), instead of the
        /// static HapticsUtility device lookup — which resolves the wrong/no device in builds.
        /// </summary>
        private bool SendImpulse(HapticHand hand, float amplitude, float duration)
        {
            var success = true;

            if (hand == HapticHand.Left || hand == HapticHand.Both)
                success &= leftHapticPlayer != null && leftHapticPlayer.SendHapticImpulse(amplitude, duration);

            if (hand == HapticHand.Right || hand == HapticHand.Both)
                success &= rightHapticPlayer != null && rightHapticPlayer.SendHapticImpulse(amplitude, duration);

            return success;
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────
        #region Curve-Driven Ticking Haptic (On Detecting / On Gazing)

        /// <summary>
        /// Runs its own elapsed-time loop (independent of the calling interaction's Timer,
        /// though both are given the same duration and so finish in step with each other),
        /// sampling <paramref name="curve"/> every <paramref name="tickInterval"/> seconds and
        /// firing a short impulse at the resulting amplitude. Does not itself signal
        /// completion — the interaction's own Timer already calls OnInteractionComplete
        /// when it ends. Shared engine — callers each own their own routine handle so
        /// Detect and Gaze curves can run independently without stomping each other.
        /// </summary>
        private Task StartTickingCurve(
            AnimationCurve curve, float tickInterval, HapticHand hand, float duration,
            Coroutine existingRoutine, System.Action<Coroutine> setRoutine, CancellationToken ct)
        {
            if (existingRoutine != null)
                StopCoroutine(existingRoutine);

            if (duration <= 0f)
            {
                setRoutine(null);
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<bool>();
            Coroutine handle = StartCoroutine(
                TickingCurveRoutine(curve, tickInterval, hand, duration, ct, tcs, () => setRoutine(null)));
            setRoutine(handle);
            return tcs.Task;
        }

        private IEnumerator TickingCurveRoutine(
            AnimationCurve curve, float tickInterval, HapticHand hand, float duration,
            CancellationToken ct, TaskCompletionSource<bool> tcs, System.Action onFinished)
        {
            float elapsed = 0f;
            float sinceLastTick = tickInterval; // fire the first pulse immediately

            while (elapsed < duration)
            {
                if (ct.IsCancellationRequested)
                {
                    onFinished();
                    tcs.TrySetCanceled();
                    yield break;
                }

                float dt = Time.deltaTime;
                elapsed += dt;
                sinceLastTick += dt;

                if (sinceLastTick >= tickInterval)
                {
                    sinceLastTick = 0f;
                    float t = Mathf.Clamp01(elapsed / duration);
                    float amplitude = Mathf.Clamp01(curve.Evaluate(t));

#if UNITY_EDITOR
#endif

                    SendImpulse(hand, amplitude, tickInterval);
                }

                yield return null;
            }

            onFinished();
            tcs.TrySetResult(true);
        }

        private Task _PlayOnDetectingCurve(float duration, CancellationToken ct) =>
            StartTickingCurve(onDetectingCurve, onDetectingTickInterval, onDetectingHand, duration,
                _detectingCurveRoutine, r => _detectingCurveRoutine = r, ct);

        private void _StopOnDetectingCurve()
        {
            if (_detectingCurveRoutine == null) return;
            StopCoroutine(_detectingCurveRoutine);
            _detectingCurveRoutine = null;
        }

        private Task _PlayOnGazingCurve(float duration, CancellationToken ct) =>
            StartTickingCurve(onGazingCurve, onGazingTickInterval, onGazingHand, duration,
                _gazingCurveRoutine, r => _gazingCurveRoutine = r, ct);

        private void _StopOnGazingCurve()
        {
            if (_gazingCurveRoutine == null) return;
            StopCoroutine(_gazingCurveRoutine);
            _gazingCurveRoutine = null;
        }

        #endregion

        public void OnUIHover()
        {
            UIClick(HapticHand.Both);
        }
    }
}
