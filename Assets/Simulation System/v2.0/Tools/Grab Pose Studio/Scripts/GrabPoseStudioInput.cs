using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Grab Pose Studio V1 — input, controller detector discovery,
/// controller pose recording for both hands, multi-grabbable navigation
/// (Next and Previous), recording-status awareness, and World Space status
/// panel management.
///
/// INPUT:
/// LEFT controller Primary Button  → records the RIGHT controller's pose.
/// RIGHT controller Primary Button → records the LEFT controller's pose.
/// RIGHT controller Secondary Button → advances to the Next grabbable.
/// LEFT controller Secondary Button  → goes back to the Previous grabbable.
///
/// CONTROLLER DETECTORS:
/// On enable, independently scans the scene for the live runtime
/// GrabPinchDetector representing each controller (inputMode == Controller,
/// handedness == Right/Left). Includes inactive GameObjects
/// (FindObjectsInactive.Include) since XRInputModalityManager can have
/// either controller GameObject temporarily disabled.
///
/// GRABBABLE LIST / NAVIGATION:
/// GrabPoseStudioLauncher discovers all active GrabInteraction objects once
/// and hands the full list (plus the RecordingPoint transform) to this
/// component via SetGrabbables. This component owns _currentGrabbableIndex
/// and is the single source of truth for the current Studio grabbable.
/// Each grabbable's original world position/rotation is captured lazily and
/// restored when navigating away from it. Runtime-only — nothing is saved.
///
/// RECORDING:
/// Both recording paths call the EXISTING RecordedPose.CaptureController(...)
/// (unmodified) to create a real RecordedPose .asset under
/// Assets/RecordedPoses, using the existing naming convention
/// "{ObjectName}_RightHand_Controller" / "{ObjectName}_LeftHand_Controller".
/// EnsureSaveFolderExists() is called before every capture to guarantee
/// Assets/RecordedPoses/ exists — it is a no-op if the folder is already
/// present.
///
/// POSE STATUS:
/// Checked via AssetDatabase.LoadAssetAtPath against the exact expected
/// asset paths — no separate status database or ScriptableObject. Results
/// are passed to the World Space panel by value (pre-computed booleans) so
/// the panel itself contains no business logic.
///
/// WORLD SPACE PANEL:
/// GrabPoseStudioPanel is created as a child GameObject when SetGrabbables
/// is called (once RecordingPoint is known), and destroyed automatically
/// when this component is disabled/destroyed with the Studio scene.
///
/// Does NOT modify GrabPoseRecorderWindow, GrabPinchDetector, RecordedPose,
/// or GrabInteraction.
/// </summary>
public class GrabPoseStudioInput : MonoBehaviour
{
    private const string SaveFolder = "Assets/RecordedPoses";

    private InputAction _recordRightPoseAction; // LEFT button → records RIGHT pose
    private InputAction _recordLeftPoseAction;  // RIGHT button → records LEFT pose
    private InputAction _nextGrabbableAction;   // RIGHT secondary button → Next grabbable
    private InputAction _previousGrabbableAction; // LEFT secondary button → Previous grabbable

    private GrabPinchDetector _rightControllerDetector;
    private GrabPinchDetector _leftControllerDetector;

    // ── Grabbable list / navigation state ───────────────────────────────────

    private GrabInteraction[] _grabbables = System.Array.Empty<GrabInteraction>();
    private int _currentGrabbableIndex = -1;
    private Transform _recordingPoint;

    private bool _hasStoredOriginalTransform;
    private Vector3 _originalPosition;
    private Quaternion _originalRotation;

    // Same discovery call as GrabPoseRecorderWindow.RefreshGrabbables:
    // FindObjectsByType<GrabInteraction> with no FindObjectsInactive flag,
    // so a disabled/inactive GrabInteraction drops out automatically and a
    // re-enabled one reappears — polled periodically during the session.
    private const float RefreshInterval = 0.5f;
    private float _nextRefreshTime;

    // ── Platform safety net ──────────────────────────────────────────────────
    // Teleports the XR rig back to the platform centre (PlayerPoint) if it
    // strays past the Floor's edge, so the developer can move freely in the
    // Studio without walking off into empty space. Boundary is derived from
    // the Floor MeshRenderer's world bounds at runtime, so resizing Floor in
    // the scene needs no code change. BoundaryBuffer keeps the teleport just
    // inside the visible edge rather than exactly on it.
    private const string XrRigName = "XR Origin Hands (XR Rig)";
    private const string FloorObjectName = "Floor";
    private const float BoundaryBuffer = 0.3f;

    private Transform _xrRig;
    private Vector3 _boundaryCenter;
    private float _boundaryRadius = -1f;

    // ── Session-scoped completion tracking ───────────────────────────────────
    // Exit button must only appear once every grab has been recorded THIS
    // Studio session — checking Assets/RecordedPoses/ on disk would show it
    // immediately whenever a dev re-opens the Studio on objects already
    // recorded in an earlier session.
    private readonly System.Collections.Generic.HashSet<GrabInteraction> _sessionLeftRecorded =
        new System.Collections.Generic.HashSet<GrabInteraction>();
    private readonly System.Collections.Generic.HashSet<GrabInteraction> _sessionRightRecorded =
        new System.Collections.Generic.HashSet<GrabInteraction>();

    private GrabInteraction CurrentGrabbable =>
        (_currentGrabbableIndex >= 0 && _currentGrabbableIndex < _grabbables.Length)
            ? _grabbables[_currentGrabbableIndex]
            : null;

    // ── World Space panel ────────────────────────────────────────────────────

    private GrabPoseStudioPanel _panel;

    private void CreatePanel()
    {
        if (_recordingPoint == null) return;

        GameObject panelGo = new GameObject("GrabPoseStudioPanel");
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(
            panelGo, gameObject.scene);

        _panel = panelGo.AddComponent<GrabPoseStudioPanel>();
        _panel.Initialise(_recordingPoint, OnExitRequested);
    }

    private void OnExitRequested()
    {
#if UNITY_EDITOR
        Debug.Log("[GrabPoseStudio] Exit Play Mode requested from panel — all grabs recorded this session.");
        EditorApplication.ExitPlaymode();
#endif
    }

    private void DestroyPanel()
    {
        if (_panel != null)
        {
            Destroy(_panel.gameObject);
            _panel = null;
        }
    }

    private void RefreshPanel()
    {
        if (_panel == null) return;

        GrabInteraction current = CurrentGrabbable;
        if (current == null) return;

#if UNITY_EDITOR
        bool hasLeft  = PoseAssetExists(current, "LeftHand");
        bool hasRight = PoseAssetExists(current, "RightHand");
        bool allRecorded = AllGrabbablesRecorded();
#else
        bool hasLeft  = false;
        bool hasRight = false;
        bool allRecorded = false;
#endif

        _panel.UpdateDisplay(
            current.gameObject.name,
            _currentGrabbableIndex + 1,
            _grabbables.Length,
            hasLeft,
            hasRight,
            allRecorded);
    }

    /// <summary>
    /// True once every currently-tracked grabbable (the live, enabled-only
    /// list) has both a Left and Right pose covered — either recorded THIS
    /// session, or already present on disk from an earlier session (so
    /// re-opening the Studio on an already-finished set doesn't force a
    /// full re-record before Exit shows up). Drives the panel's Exit Play
    /// Mode button.
    /// </summary>
    private bool AllGrabbablesRecorded()
    {
        if (_grabbables.Length == 0) return false;

#if UNITY_EDITOR
        foreach (GrabInteraction grabbable in _grabbables)
        {
            if (grabbable == null) continue;

            bool leftDone  = _sessionLeftRecorded.Contains(grabbable)  || PoseAssetExists(grabbable, "LeftHand");
            bool rightDone = _sessionRightRecorded.Contains(grabbable) || PoseAssetExists(grabbable, "RightHand");
            if (!leftDone || !rightDone) return false;
        }
        return true;
#else
        return false;
#endif
    }

    // ── Public API ───────────────────────────────────────────────────────────

    public void SetGrabbables(GrabInteraction[] grabbables, Transform recordingPoint)
    {
        _grabbables = grabbables ?? System.Array.Empty<GrabInteraction>();
        _recordingPoint = recordingPoint;
        _currentGrabbableIndex = -1;
        _hasStoredOriginalTransform = false;

        CreatePanel();

        if (_grabbables.Length == 0)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: No active GrabInteraction objects found in the simulation.");
            return;
        }

        MoveToGrabbable(0);
    }

    private void OnEnable()
    {
        _recordRightPoseAction = new InputAction(
            name: "GrabPoseStudio_RecordRightPose",
            type: InputActionType.Button,
            binding: "<XRController>{LeftHand}/primaryButton");
        _recordRightPoseAction.performed += OnRecordRightPosePerformed;
        _recordRightPoseAction.Enable();

        _recordLeftPoseAction = new InputAction(
            name: "GrabPoseStudio_RecordLeftPose",
            type: InputActionType.Button,
            binding: "<XRController>{RightHand}/primaryButton");
        _recordLeftPoseAction.performed += OnRecordLeftPosePerformed;
        _recordLeftPoseAction.Enable();

        _nextGrabbableAction = new InputAction(
            name: "GrabPoseStudio_NextGrabbable",
            type: InputActionType.Button,
            binding: "<XRController>{RightHand}/secondaryButton");
        _nextGrabbableAction.performed += OnNextGrabbablePerformed;
        _nextGrabbableAction.Enable();

        _previousGrabbableAction = new InputAction(
            name: "GrabPoseStudio_PreviousGrabbable",
            type: InputActionType.Button,
            binding: "<XRController>{LeftHand}/secondaryButton");
        _previousGrabbableAction.performed += OnPreviousGrabbablePerformed;
        _previousGrabbableAction.Enable();

        Debug.Log("[GrabPoseStudio] Record input ready. LEFT Primary Button → RIGHT pose.");
        Debug.Log("[GrabPoseStudio] Record input ready. RIGHT Primary Button → LEFT pose.");
        Debug.Log("[GrabPoseStudio] Navigation input ready. RIGHT Secondary Button → Next grabbable.");
        Debug.Log("[GrabPoseStudio] Navigation input ready. LEFT Secondary Button → Previous grabbable.");

        DetectRightControllerReference();
        DetectLeftControllerReference();
        DetectPlatformBoundary();
    }

    private void OnDisable()
    {
        if (_recordRightPoseAction != null)
        {
            _recordRightPoseAction.performed -= OnRecordRightPosePerformed;
            _recordRightPoseAction.Disable();
            _recordRightPoseAction.Dispose();
            _recordRightPoseAction = null;
        }

        if (_recordLeftPoseAction != null)
        {
            _recordLeftPoseAction.performed -= OnRecordLeftPosePerformed;
            _recordLeftPoseAction.Disable();
            _recordLeftPoseAction.Dispose();
            _recordLeftPoseAction = null;
        }

        if (_nextGrabbableAction != null)
        {
            _nextGrabbableAction.performed -= OnNextGrabbablePerformed;
            _nextGrabbableAction.Disable();
            _nextGrabbableAction.Dispose();
            _nextGrabbableAction = null;
        }

        if (_previousGrabbableAction != null)
        {
            _previousGrabbableAction.performed -= OnPreviousGrabbablePerformed;
            _previousGrabbableAction.Disable();
            _previousGrabbableAction.Dispose();
            _previousGrabbableAction = null;
        }

        DestroyPanel();
    }

    private void OnRecordRightPosePerformed(InputAction.CallbackContext ctx)
    {
        Debug.Log("[GrabPoseStudio] RECORD RIGHT POSE requested.");
        RecordRightControllerPose();
    }

    private void OnRecordLeftPosePerformed(InputAction.CallbackContext ctx)
    {
        Debug.Log("[GrabPoseStudio] RECORD LEFT POSE requested.");
        RecordLeftControllerPose();
    }

    private void OnNextGrabbablePerformed(InputAction.CallbackContext ctx)
    {
        Debug.Log("[GrabPoseStudio] Next grabbable requested.");
        AdvanceToNextGrabbable();
    }

    private void OnPreviousGrabbablePerformed(InputAction.CallbackContext ctx)
    {
        Debug.Log("[GrabPoseStudio] Previous grabbable requested.");
        AdvanceToPreviousGrabbable();
    }

    // ── Grabbable navigation ─────────────────────────────────────────────────

    private void AdvanceToNextGrabbable()
    {
        if (_grabbables.Length == 0)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: Cannot advance — no grabbables available.");
            return;
        }

        int nextIndex = _currentGrabbableIndex + 1;
        if (nextIndex >= _grabbables.Length) nextIndex = 0;

        MoveToGrabbable(nextIndex);
    }

    private void AdvanceToPreviousGrabbable()
    {
        if (_grabbables.Length == 0)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: Cannot go back — no grabbables available.");
            return;
        }

        int previousIndex = _currentGrabbableIndex - 1;
        if (previousIndex < 0) previousIndex = _grabbables.Length - 1;

        MoveToGrabbable(previousIndex);
    }

    private void MoveToGrabbable(int index)
    {
        RestoreCurrentGrabbableTransform();

        _currentGrabbableIndex = index;
        GrabInteraction current = CurrentGrabbable;
        if (current == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: Current grabbable index is invalid.");
            return;
        }

        if (_recordingPoint == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: RecordingPoint not found in GrabPoseStudio scene.");
            return;
        }

        _originalPosition = current.transform.position;
        _originalRotation = current.transform.rotation;
        _hasStoredOriginalTransform = true;

        current.transform.SetPositionAndRotation(_recordingPoint.position, _recordingPoint.rotation);

        Debug.Log($"[GrabPoseStudio] Current grabbable [{_currentGrabbableIndex + 1}/{_grabbables.Length}]: {current.gameObject.name}");

        LogPoseStatus(current);
        RefreshPanel();
    }

    private void RestoreCurrentGrabbableTransform()
    {
        if (!_hasStoredOriginalTransform) return;

        GrabInteraction current = CurrentGrabbable;
        if (current != null)
        {
            current.transform.SetPositionAndRotation(_originalPosition, _originalRotation);
        }

        _hasStoredOriginalTransform = false;
    }

    /// <summary>
    /// Re-scans for GrabInteraction objects using the exact same
    /// FindObjectsByType call GrabPoseRecorderWindow.RefreshGrabbables uses
    /// (no FindObjectsInactive flag), so enabling/disabling a grab at
    /// runtime updates this list the same way it updates the recorder
    /// window's list. Keeps the current grabbable selected if it is still
    /// present; otherwise falls back to the first available one.
    /// </summary>
    private void RefreshGrabbableList()
    {
        GrabInteraction[] updated = Object.FindObjectsByType<GrabInteraction>(FindObjectsSortMode.None)
            .OrderBy(g => g.gameObject.name)
            .ToArray();

        if (updated.SequenceEqual(_grabbables)) return;

        GrabInteraction previousCurrent = CurrentGrabbable;
        RestoreCurrentGrabbableTransform();

        _grabbables = updated;

        if (previousCurrent != null)
        {
            int idx = System.Array.IndexOf(_grabbables, previousCurrent);
            if (idx >= 0)
            {
                MoveToGrabbable(idx);
                return;
            }
        }

        if (_grabbables.Length == 0)
        {
            _currentGrabbableIndex = -1;
            Debug.LogWarning("[GrabPoseStudio] No active GrabInteraction objects remain.");
            return;
        }

        MoveToGrabbable(0);
    }

    // ── Pose status ──────────────────────────────────────────────────────────

    private void LogPoseStatus(GrabInteraction grabbable)
    {
#if UNITY_EDITOR
        if (grabbable == null) return;

        string leftText  = PoseAssetExists(grabbable, "LeftHand")  ? "Recorded" : "Missing";
        string rightText = PoseAssetExists(grabbable, "RightHand") ? "Recorded" : "Missing";

        Debug.Log($"[GrabPoseStudio] Pose Status — Left: {leftText} | Right: {rightText}");
#endif
    }

#if UNITY_EDITOR
    private bool PoseAssetExists(GrabInteraction grabbable, string handLabel)
    {
        string assetPath = $"{SaveFolder}/{grabbable.gameObject.name}_{handLabel}_Controller.asset";
        return AssetDatabase.LoadAssetAtPath<RecordedPose>(assetPath) != null;
    }
#endif

    // ── Save folder guarantee ────────────────────────────────────────────────

#if UNITY_EDITOR
    /// <summary>
    /// Ensures Assets/RecordedPoses/ exists before any CaptureController
    /// call. Uses AssetDatabase.CreateFolder so Unity tracks the folder
    /// correctly as a proper project asset rather than a raw OS directory.
    /// No-op if the folder already exists. Does not change asset naming,
    /// paths, or overwrite behaviour in any way.
    /// </summary>
    private static void EnsureSaveFolderExists()
    {
        if (!AssetDatabase.IsValidFolder(SaveFolder))
        {
            AssetDatabase.CreateFolder("Assets", "RecordedPoses");
            AssetDatabase.Refresh();
            Debug.Log("[GrabPoseStudio] Created missing Assets/RecordedPoses/ folder.");
        }
    }
#endif

    // ── Controller detector discovery ────────────────────────────────────────

    private void DetectRightControllerReference()
    {
        GrabPinchDetector[] detectors = Object.FindObjectsByType<GrabPinchDetector>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        _rightControllerDetector = null;
        foreach (GrabPinchDetector detector in detectors)
        {
            if (detector.inputMode == InputMode.Controller &&
                detector.handedness == Handedness.Right)
            {
                _rightControllerDetector = detector;
                break;
            }
        }

        if (_rightControllerDetector == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: Right Controller GrabPinchDetector not found in scene.");
            return;
        }

        Debug.Log($"[GrabPoseStudio] Right Controller detector found: {_rightControllerDetector.gameObject.name}");
        ValidateControllerReferences(_rightControllerDetector, "Right");
    }

    private void DetectLeftControllerReference()
    {
        GrabPinchDetector[] detectors = Object.FindObjectsByType<GrabPinchDetector>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        _leftControllerDetector = null;
        foreach (GrabPinchDetector detector in detectors)
        {
            if (detector.inputMode == InputMode.Controller &&
                detector.handedness == Handedness.Left)
            {
                _leftControllerDetector = detector;
                break;
            }
        }

        if (_leftControllerDetector == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: Left Controller GrabPinchDetector not found in scene.");
            return;
        }

        Debug.Log($"[GrabPoseStudio] Left Controller detector found: {_leftControllerDetector.gameObject.name}");
        ValidateControllerReferences(_leftControllerDetector, "Left");
    }

    private void ValidateControllerReferences(GrabPinchDetector detector, string sideLabel)
    {
        ControllerPoseLock poseLock = detector.controllerPoseLock;
        if (poseLock == null)
        {
            Debug.LogError($"[GrabPoseStudio] ERROR: {sideLabel} Controller detector has no controllerPoseLock.");
            return;
        }

        Animator handAnimator = poseLock.handAnimator;
        if (handAnimator == null)
        {
            Debug.LogError($"[GrabPoseStudio] ERROR: {sideLabel} Controller controllerPoseLock has no handAnimator.");
            return;
        }
        Debug.Log($"[GrabPoseStudio] Animator: {handAnimator.name}");

        Transform controllerRoot = detector.controllerRoot;
        if (controllerRoot == null)
        {
            Debug.LogError($"[GrabPoseStudio] ERROR: {sideLabel} Controller detector has no controllerRoot.");
            return;
        }
        Debug.Log($"[GrabPoseStudio] ControllerRoot: {controllerRoot.name}");

        Transform pinchPoint = detector.pinchPoint;
        if (pinchPoint == null)
        {
            Debug.LogError($"[GrabPoseStudio] ERROR: {sideLabel} Controller detector has no pinchPoint.");
            return;
        }
        Debug.Log($"[GrabPoseStudio] PinchPoint: {pinchPoint.name}");
    }

    // ── Platform safety net ──────────────────────────────────────────────────

    private void DetectPlatformBoundary()
    {
        GameObject rigObject = GameObject.Find(XrRigName);
        if (rigObject == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: XR Rig not found — platform safety net disabled.");
            return;
        }
        _xrRig = rigObject.transform;

        // GameObject.Find searches every loaded scene. The Studio scene loads
        // additively on top of the simulation scene, which can have its own
        // "Floor" object — a global Find can grab that one instead of the
        // Studio's, producing a wrong boundary centre. Scope the search to
        // this component's own scene (the Studio scene) only.
        Transform floorTransform = FindChildByNameInOwnScene(FloorObjectName);
        if (floorTransform == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: Floor GameObject not found in Studio scene — platform safety net disabled.");
            return;
        }

        Renderer floorRenderer = floorTransform.GetComponent<Renderer>();
        if (floorRenderer == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: Floor has no Renderer — platform safety net disabled.");
            return;
        }

        Bounds bounds = floorRenderer.bounds;
        _boundaryCenter = bounds.center;
        _boundaryRadius = Mathf.Min(bounds.extents.x, bounds.extents.z) - BoundaryBuffer;

        Debug.Log($"[GrabPoseStudio] Platform safety net ready. Centre: {_boundaryCenter}, Radius: {_boundaryRadius:F2}m");
    }

    /// <summary>
    /// Searches only this component's own scene (the Studio scene) for a
    /// child by name — unlike GameObject.Find, which searches every loaded
    /// scene and can collide with an identically-named object in whichever
    /// simulation scene the Studio is loaded additively on top of.
    /// </summary>
    private Transform FindChildByNameInOwnScene(string targetName)
    {
        foreach (GameObject root in gameObject.scene.GetRootGameObjects())
        {
            Transform found = FindChildRecursive(root.transform, targetName);
            if (found != null) return found;
        }
        return null;
    }

    private static Transform FindChildRecursive(Transform parent, string targetName)
    {
        if (parent.name == targetName) return parent;

        for (int i = 0; i < parent.childCount; i++)
        {
            Transform result = FindChildRecursive(parent.GetChild(i), targetName);
            if (result != null) return result;
        }
        return null;
    }

    private void EnforcePlatformBoundary()
    {
        if (_xrRig == null || _boundaryRadius < 0f) return;

        Vector3 rigPos = _xrRig.position;
        Vector3 delta = new Vector3(rigPos.x - _boundaryCenter.x, 0f, rigPos.z - _boundaryCenter.z);

        if (delta.sqrMagnitude <= _boundaryRadius * _boundaryRadius) return;

        Debug.LogWarning("[GrabPoseStudio] Rig crossed platform boundary — teleporting back to centre.");

        // Disable CharacterController before a direct position set — otherwise
        // it retains stale collision state and can read as "stuck" on the
        // rig's next move. Same pattern as TeleportManager.SyncPlayerPosition.
        CharacterController cc = _xrRig.GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;

        _xrRig.position = new Vector3(_boundaryCenter.x, rigPos.y, _boundaryCenter.z);

        if (cc != null) cc.enabled = true;
    }

    // ── Recording ────────────────────────────────────────────────────────────

    private void RecordRightControllerPose()
    {
#if UNITY_EDITOR
        if (_rightControllerDetector == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: Cannot record — Right Controller detector not found.");
            return;
        }

        ControllerPoseLock poseLock = _rightControllerDetector.controllerPoseLock;
        if (poseLock == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: Cannot record — controllerPoseLock is missing.");
            return;
        }

        Animator handAnimator = poseLock.handAnimator;
        if (handAnimator == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: Cannot record — handAnimator is missing.");
            return;
        }

        Transform controllerRoot = _rightControllerDetector.controllerRoot;
        if (controllerRoot == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: Cannot record — controllerRoot is missing.");
            return;
        }

        Transform pinchPoint = _rightControllerDetector.pinchPoint;
        if (pinchPoint == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: Cannot record — pinchPoint is missing.");
            return;
        }

        GrabInteraction targetGrabbable = CurrentGrabbable;
        if (targetGrabbable == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: Cannot record — no current grabbable set.");
            return;
        }

        Debug.Log($"[GrabPoseStudio] Recording RIGHT Controller pose for '{targetGrabbable.gameObject.name}'.");

        EnsureSaveFolderExists();

        string assetName = $"{targetGrabbable.gameObject.name}_RightHand_Controller";

        RecordedPose recorded = RecordedPose.CaptureController(
            handAnimator,
            controllerRoot,
            Handedness.Right,
            pinchPoint,
            targetGrabbable,
            assetName,
            SaveFolder);

        if (recorded == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: RecordedPose.CaptureController returned null.");
            _panel?.SetFeedback("\u274c Recording Failed");
            return;
        }

        Debug.Log($"[GrabPoseStudio] RIGHT Controller pose recorded: {recorded.name}");
        _sessionRightRecorded.Add(targetGrabbable);
        _panel?.SetFeedback("\u2705 Right Pose Recorded");
        LogPoseStatus(targetGrabbable);
        RefreshPanel();
#else
        Debug.LogError("[GrabPoseStudio] ERROR: Recording is only available in the Editor.");
#endif
    }

    private void RecordLeftControllerPose()
    {
#if UNITY_EDITOR
        if (_leftControllerDetector == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: Cannot record — Left Controller detector not found.");
            return;
        }

        ControllerPoseLock poseLock = _leftControllerDetector.controllerPoseLock;
        if (poseLock == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: Cannot record — controllerPoseLock is missing.");
            return;
        }

        Animator handAnimator = poseLock.handAnimator;
        if (handAnimator == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: Cannot record — handAnimator is missing.");
            return;
        }

        Transform controllerRoot = _leftControllerDetector.controllerRoot;
        if (controllerRoot == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: Cannot record — controllerRoot is missing.");
            return;
        }

        Transform pinchPoint = _leftControllerDetector.pinchPoint;
        if (pinchPoint == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: Cannot record — pinchPoint is missing.");
            return;
        }

        GrabInteraction targetGrabbable = CurrentGrabbable;
        if (targetGrabbable == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: Cannot record — no current grabbable set.");
            return;
        }

        Debug.Log($"[GrabPoseStudio] Recording LEFT Controller pose for '{targetGrabbable.gameObject.name}'.");

        EnsureSaveFolderExists();

        string assetName = $"{targetGrabbable.gameObject.name}_LeftHand_Controller";

        RecordedPose recorded = RecordedPose.CaptureController(
            handAnimator,
            controllerRoot,
            Handedness.Left,
            pinchPoint,
            targetGrabbable,
            assetName,
            SaveFolder);

        if (recorded == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: RecordedPose.CaptureController returned null.");
            _panel?.SetFeedback("\u274c Recording Failed");
            return;
        }

        Debug.Log($"[GrabPoseStudio] LEFT Controller pose recorded: {recorded.name}");
        _sessionLeftRecorded.Add(targetGrabbable);
        _panel?.SetFeedback("\u2705 Left Pose Recorded");
        LogPoseStatus(targetGrabbable);
        RefreshPanel();
#else
        Debug.LogError("[GrabPoseStudio] ERROR: Recording is only available in the Editor.");
#endif
    }

    private void Update()
    {
        EnforcePlatformBoundary();

        if (Time.unscaledTime >= _nextRefreshTime)
        {
            _nextRefreshTime = Time.unscaledTime + RefreshInterval;
            RefreshGrabbableList();
        }

#if UNITY_EDITOR
        // ── Editor-only keyboard debug navigation (no Quest required) ───────
        //
        // N → Next grabbable (same as RIGHT Secondary Button)
        // P → Previous grabbable (same as LEFT Secondary Button)
        if (Keyboard.current == null) return;

        if (Keyboard.current.nKey.wasPressedThisFrame)
        {
            Debug.Log("[GrabPoseStudio] Next grabbable requested. (debug: N key)");
            AdvanceToNextGrabbable();
        }

        if (Keyboard.current.pKey.wasPressedThisFrame)
        {
            Debug.Log("[GrabPoseStudio] Previous grabbable requested. (debug: P key)");
            AdvanceToPreviousGrabbable();
        }
#endif
    }
}
