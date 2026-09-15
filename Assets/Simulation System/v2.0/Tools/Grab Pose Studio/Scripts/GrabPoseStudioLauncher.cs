#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Launcher for Grab Pose Studio.
///
/// Responsibility:
/// 1. Expose "Tools/Grab Pose Studio/Launch Studio" — a single Edit Mode
///    entry point that requests Play Mode (if not already in it), waits for
///    Unity to genuinely finish entering Play Mode, then automatically runs
///    the existing Studio launch flow. "Tools/Grab Pose Studio/Enter Studio"
///    is KEPT unchanged as the original Play-Mode-only debug entry point.
///
///    IMPORTANT: entering Play Mode triggers a Unity domain reload, which
///    clears normal static event subscriptions. The class uses [InitializeOnLoad]
///    so Unity re-runs the static constructor after every domain reload,
///    keeping the playModeStateChanged subscription alive.
///
/// 2. Build Settings guard — both LaunchStudio() and EnterStudio() call
///    IsStudioSceneInBuildSettings() before doing anything. If the Studio
///    scene is missing from Build Settings, a clear Editor dialog is shown
///    with an "Open Build Settings" button and the launch is aborted.
///    Play Mode is never entered and the scene is never loaded until the
///    developer has explicitly added the scene themselves.
///
/// 3. EnterStudio(): additively loads the Grab Pose Studio scene while in
///    Play Mode.
///
/// 4. OnStudioSceneLoaded: once the Studio scene finishes loading —
///    a. Captures the XR rig's world position BEFORE any movement.
///    b. Hides the BOT GameObject from the simulation scene so it does not
///       float in front of the Studio status panel. Unity's Play Mode reset
///       automatically restores BOT.SetActive(true) when Play Mode exits —
///       no manual re-enable needed.
///    c. Moves the Studio root to originalRigPosition + StudioOffset.
///    d. Moves the XR rig to PlayerPoint.
///    e. Discovers runtime GrabInteractions and activates GrabPoseStudioInput.
///
/// 5. GrabPoseStudioInput owns: current grabbable, navigation, recording,
///    pose status, and the World Space status panel.
///
/// Does NOT create a second XR rig, modify GrabPoseRecorderWindow,
/// RecordedPose, or GrabInteraction.
/// </summary>
[InitializeOnLoad]
public static class GrabPoseStudioLauncher
{
    private const string StudioScenePath    = "Assets/Simulation System/v2.0/Tools/Grab Pose Studio/Scenes/GrabPoseStudio.unity";
    private const string StudioSceneName    = "GrabPoseStudio";
    private const string StudioRootName     = "GrabPoseStudio";

    private const string PlayerPointName    = "PlayerPoint";
    private const string RecordingPointName = "RecordingPoint";
    private const string XrRigName          = "XR Origin Hands (XR Rig)";

    // The BOT GameObject in the simulation scene is hidden while the Studio
    // is active so it does not obstruct the World Space status panel.
    // Unity's Play Mode reset automatically restores its active state when
    // Play Mode exits — no manual re-enable is required.
    private const string BotGameObjectName  = "BOT";

    private const string EnterStudioMenuPath  = "Tools/Grab Pose Studio/Enter Studio";
    private const string LaunchStudioMenuPath = "Tools/Grab Pose Studio/Launch Studio";

    /// <summary>
    /// SessionState key marking "Grab Pose Studio should launch as soon as
    /// Play Mode finishes entering." SessionState survives domain reloads for
    /// the duration of the Editor session — normal static fields do not.
    /// </summary>
    private const string PendingLaunchSessionKey = "GrabPoseStudio_PendingLaunch";

    /// <summary>
    /// v0.1 dynamic placement offset relative to the XR Rig's world position
    /// at the moment Grab Pose Studio is entered. Fixed offset — not
    /// collision detection. Change this single value if needed.
    /// </summary>
    private static readonly Vector3 StudioOffset = new Vector3(50f, 0f, 50f);

    /// <summary>
    /// Runs on every domain reload (startup, recompile, Play Mode enter/exit).
    /// Unconditionally re-subscribes to playModeStateChanged so the callback
    /// survives the Edit Mode → Play Mode domain reload.
    /// </summary>
    static GrabPoseStudioLauncher()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    // ── Build Settings guard ─────────────────────────────────────────────────

    /// <summary>
    /// Checks whether GrabPoseStudio.unity is present in the current Build
    /// Settings scene list using EditorBuildSettings.scenes. Does not check
    /// whether the scene is enabled — presence alone is sufficient for
    /// SceneManager.LoadScene to work at runtime.
    /// </summary>
    private static bool IsStudioSceneInBuildSettings()
    {
        foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
        {
            if (scene.path == StudioScenePath)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Shows a blocking Editor dialog when GrabPoseStudio.unity is missing
    /// from Build Settings. If the developer clicks "Open Build Settings",
    /// the Build Settings window opens so they can add the scene manually.
    /// Does NOT modify Build Settings automatically.
    /// </summary>
    private static void ShowMissingSceneDialog()
    {
        bool fix = EditorUtility.DisplayDialog(
            title:   "Grab Pose Studio",
            message: "GrabPoseStudio.unity is not in Build Settings.\nDid you even read the docs?",
            ok:      "Add It For Me",
            cancel:  "My Bad");

        if (!fix) return;

        // Add the Studio scene to Build Settings without touching any
        // existing entries.
        var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(
            EditorBuildSettings.scenes);

        scenes.Add(new EditorBuildSettingsScene(StudioScenePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();

        Debug.Log("[GrabPoseStudio] GrabPoseStudio.unity added to Build Settings. You're welcome.");
    }

    // ── Launch Studio ────────────────────────────────────────────────────────

    [MenuItem(LaunchStudioMenuPath)]
    private static void LaunchStudio()
    {
        if (!IsStudioSceneInBuildSettings())
        {
            ShowMissingSceneDialog();
            return;
        }

        if (EditorApplication.isPlaying)
        {
            Debug.Log("[GrabPoseStudio] Launch requested while already in Play Mode.");
            EnterStudio();
            return;
        }

        Debug.Log("[GrabPoseStudio] Launch requested. Entering Play Mode...");
        SessionState.SetBool(PendingLaunchSessionKey, true);
        EditorApplication.isPlaying = true;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        // Safety net: clear pending flag if Play Mode entry was cancelled
        // or the developer exited before EnteredPlayMode fired.
        if (state == PlayModeStateChange.EnteredEditMode)
        {
            SessionState.SetBool(PendingLaunchSessionKey, false);
            return;
        }

        if (state != PlayModeStateChange.EnteredPlayMode)
            return;

        if (!SessionState.GetBool(PendingLaunchSessionKey, false))
            return;

        SessionState.SetBool(PendingLaunchSessionKey, false);
        Debug.Log("[GrabPoseStudio] Play Mode ready. Launching Studio...");
        EnterStudio();
    }

    // ── Enter Studio ─────────────────────────────────────────────────────────

    [MenuItem(EnterStudioMenuPath)]
    private static void EnterStudio()
    {
        if (!IsStudioSceneInBuildSettings())
        {
            ShowMissingSceneDialog();
            return;
        }

        if (!EditorApplication.isPlaying)
        {
            Debug.LogWarning("[GrabPoseStudioLauncher] Grab Pose Studio can only be entered while in Play Mode.");
            return;
        }

        Scene existing = SceneManager.GetSceneByName(StudioSceneName);
        if (existing.IsValid() && existing.isLoaded)
        {
            Debug.Log("[GrabPoseStudioLauncher] Grab Pose Studio is already loaded.");
            return;
        }

        SceneManager.sceneLoaded += OnStudioSceneLoaded;
        SceneManager.LoadScene(StudioScenePath, LoadSceneMode.Additive);
    }

    private static void OnStudioSceneLoaded(Scene loadedScene, LoadSceneMode mode)
    {
        if (loadedScene.name != StudioSceneName)
            return;

        SceneManager.sceneLoaded -= OnStudioSceneLoaded;

        Debug.Log("[GrabPoseStudio] Studio loaded.");

        // Capture XR rig position BEFORE anything moves.
        GameObject xrRigObject = GameObject.Find(XrRigName);
        if (xrRigObject == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: XR Rig 'XR Origin Hands (XR Rig)' not found.");
            return;
        }
        Transform xrRig = xrRigObject.transform;
        Vector3 originalRigPosition = xrRig.position;
        Debug.Log($"[GrabPoseStudio] Original XR Rig position: {originalRigPosition}");

        // Hide BOT so it does not obstruct the Studio status panel.
        HideBot();

        // Position Studio root relative to where the rig currently is.
        PositionStudioRoot(loadedScene, originalRigPosition);

        // Move the rig to PlayerPoint (which has now moved with the root).
        MoveExistingRigToPlayerPoint(loadedScene, xrRig);

        GrabInteraction[] grabbables = DiscoverActiveGrabbables();
        Transform recordingPoint = FindChildByNameInScene(loadedScene, RecordingPointName);
        if (recordingPoint == null)
            Debug.LogError("[GrabPoseStudio] ERROR: RecordingPoint not found in GrabPoseStudio scene.");

        ActivateStudioInput(loadedScene, grabbables, recordingPoint);
    }

    // ── BOT ──────────────────────────────────────────────────────────────────

    private static void HideBot()
    {
        GameObject bot = GameObject.Find(BotGameObjectName);
        if (bot != null)
        {
            bot.SetActive(false);
            Debug.Log("[GrabPoseStudio] BOT hidden for Studio session.");
        }
        else
        {
            Debug.LogWarning("[GrabPoseStudio] BOT GameObject not found — skipping hide.");
        }
    }

    // ── Studio root positioning ───────────────────────────────────────────────

    private static void PositionStudioRoot(Scene studioScene, Vector3 originalRigPosition)
    {
        GameObject[] roots = studioScene.GetRootGameObjects();
        GameObject studioRootObject = null;
        foreach (GameObject root in roots)
        {
            if (root.name == StudioRootName)
            {
                studioRootObject = root;
                break;
            }
        }

        if (studioRootObject == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: GrabPoseStudio root GameObject not found in Studio scene.");
            return;
        }

        Vector3 studioPosition = originalRigPosition + StudioOffset;
        studioRootObject.transform.position = studioPosition;
        Debug.Log($"[GrabPoseStudio] Studio positioned at: {studioRootObject.transform.position}");
    }

    private static void MoveExistingRigToPlayerPoint(Scene studioScene, Transform xrRig)
    {
        Transform playerPoint = FindChildByNameInScene(studioScene, PlayerPointName);
        if (playerPoint == null)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: PlayerPoint not found in GrabPoseStudio scene.");
            return;
        }
        Debug.Log($"[GrabPoseStudio] PlayerPoint found: {playerPoint.position}");

        Debug.Log("[GrabPoseStudio] Moving XR Rig to PlayerPoint...");

        // Disable CharacterController before a direct position set — otherwise
        // it retains stale collision state and can read as "stuck" on the
        // rig's next move. Same pattern as TeleportManager.SyncPlayerPosition.
        CharacterController cc = xrRig.GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;

        xrRig.SetPositionAndRotation(playerPoint.position, playerPoint.rotation);

        if (cc != null) cc.enabled = true;

        Debug.Log($"[GrabPoseStudio] XR Rig moved to PlayerPoint: {xrRig.position}");
    }

    // ── Grabbable discovery ───────────────────────────────────────────────────

    private static GrabInteraction[] DiscoverActiveGrabbables()
    {
        GrabInteraction[] grabbables =
            Object.FindObjectsByType<GrabInteraction>(FindObjectsSortMode.None);
        Debug.Log($"[GrabPoseStudio] Found {grabbables.Length} active grabbables.");
        return grabbables;
    }

    // ── Studio input activation ───────────────────────────────────────────────

    private static void ActivateStudioInput(Scene studioScene, GrabInteraction[] grabbables, Transform recordingPoint)
    {
        GameObject[] roots = studioScene.GetRootGameObjects();
        if (roots.Length == 0)
        {
            Debug.LogError("[GrabPoseStudio] ERROR: Studio scene has no root GameObjects; cannot attach input listener.");
            return;
        }

        GrabPoseStudioInput studioInput = roots[0].AddComponent<GrabPoseStudioInput>();
        studioInput.SetGrabbables(grabbables, recordingPoint);
    }

    // ── Scene search helpers ──────────────────────────────────────────────────

    private static Transform FindChildByNameInScene(Scene scene, string targetName)
    {
        if (!scene.IsValid() || !scene.isLoaded) return null;

        foreach (GameObject root in scene.GetRootGameObjects())
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

    [MenuItem(EnterStudioMenuPath, true)]
    private static bool ValidateEnterStudio()
    {
        return EditorApplication.isPlaying;
    }
}
#endif
