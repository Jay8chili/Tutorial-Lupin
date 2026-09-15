using UnityEngine;

/// <summary>
/// Drop this on a GrabInteraction object to validate its pose setup at runtime.
/// Logs detailed diagnostics when poses fail to apply. Remove for shipping builds.
///
/// Also provides a manual test button: call TestPose() from a custom inspector
/// or UnityEvent to lock/unlock a specific pose slot without needing to grab.
/// </summary>
public class GrabPoseDiagnostics : MonoBehaviour
{
    [Header("Test Target (optional)")]
    [Tooltip("The GrabPinchDetector to pull lock components from. " +
             "If empty, searches the scene.")]
    public GrabPinchDetector testDetector;

    [Header("Test Parameters")]
    public InputMode testInputMode = InputMode.HandTracking;
    public Handedness testHandedness = Handedness.Right;

    private GrabInteraction _grab;
    private bool _testLocked;

    private void Awake()
    {
        _grab = GetComponent<GrabInteraction>();
        if (_grab == null)
            { }
    }

    private void Start()
    {
        ValidateSetup();
    }

    // ── Validation ───────────────────────────────────────────────────────────

    /// <summary>
    /// Checks every pose slot and its associated lock components.
    /// Call at Start or any time you want a full diagnostic dump.
    /// </summary>
    public void ValidateSetup()
    {
        if (_grab == null) return;


        ValidateSlot("Hand Left",        _grab.poseHandLeft,        PoseType.HandTracking);
        ValidateSlot("Hand Right",       _grab.poseHandRight,       PoseType.HandTracking);
        ValidateSlot("Controller Left",  _grab.poseControllerLeft,  PoseType.Controller);
        ValidateSlot("Controller Right", _grab.poseControllerRight, PoseType.Controller);

        // Check that detectors in the scene can reach the right lock components.
        var detectors = FindObjectsByType<GrabPinchDetector>(FindObjectsSortMode.None);
        foreach (var d in detectors)
        {
            if (d.inputMode == InputMode.HandTracking)
            {
                if (d.handPoseLock == null)
                    { }
                else if (d.handPoseLock.skeletonDriver == null)
                    { }
                else if (d.handPoseLock.skeletonDriver.rootTransform == null)
                    { }
            }
            else
            {
                if (d.controllerPoseLock == null)
                    { }
                else if (d.controllerPoseLock.handAnimator == null)
                    { }

                if (d.controllerRoot == null)
                    { }
            }
        }

    }

    private void ValidateSlot(string slotName, RecordedPose pose, PoseType expectedType)
    {
        if (pose == null)
        {
            return;
        }

        if (pose.poseType != expectedType)
        {
        }

        if (expectedType == PoseType.HandTracking)
        {
            if (pose.jointSnapshots == null || pose.jointSnapshots.Length == 0)
                { }
            else
                { }
        }
        else
        {
        }

        if (!pose.hasAnchorData)
        {
        }
    }

    // ── Manual test ──────────────────────────────────────────────────────────

    /// <summary>
    /// Toggle-test a pose lock without actually grabbing. Useful for verifying
    /// that the hand mesh freezes correctly.
    /// </summary>
    [ContextMenu("Toggle Test Pose")]
    public void TestPose()
    {
        if (!Application.isPlaying)
        {
            return;
        }

        if (_grab == null) return;

        if (_testLocked)
        {
            UnlockTestPose();
        }
        else
        {
            LockTestPose();
        }
    }

    private void LockTestPose()
    {
        var detector = FindDetector();
        if (detector == null) return;

        RecordedPose pose = ResolvePose();
        if (pose == null)
        {
            return;
        }

        if (testInputMode == InputMode.HandTracking)
        {
            var hLock = detector.handPoseLock;
            if (hLock == null)
            {
                return;
            }
            hLock.LockPose(pose);
        }
        else
        {
            var cLock = detector.controllerPoseLock;
            if (cLock == null)
            {
                return;
            }
            cLock.LockPose(pose);
        }

        _testLocked = true;
    }

    private void UnlockTestPose()
    {
        var detector = FindDetector();
        if (detector == null) return;

        if (testInputMode == InputMode.HandTracking)
            detector.handPoseLock?.UnlockPose();
        else
            detector.controllerPoseLock?.UnlockPose();

        _testLocked = false;
    }

    private RecordedPose ResolvePose()
    {
        if (_grab == null) return null;

        if (testInputMode == InputMode.HandTracking)
            return testHandedness == Handedness.Left ? _grab.poseHandLeft : _grab.poseHandRight;
        else
            return testHandedness == Handedness.Left ? _grab.poseControllerLeft : _grab.poseControllerRight;
    }

    private GrabPinchDetector FindDetector()
    {
        if (testDetector != null) return testDetector;

        var detectors = FindObjectsByType<GrabPinchDetector>(FindObjectsSortMode.None);
        foreach (var d in detectors)
        {
            if (d.inputMode == testInputMode && d.handedness == testHandedness)
            {
                testDetector = d;
                return d;
            }
        }

        return null;
    }
}
