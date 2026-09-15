using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

#if UNITY_EDITOR
using UnityEditor;
using System.IO;
#endif

// ─────────────────────────────────────────────────────────────────────────────
// Data
// ─────────────────────────────────────────────────────────────────────────────

public enum PoseType
{
    HandTracking,
    Controller
}

public enum Handedness
{
    Left,
    Right
}

[System.Serializable]
public struct JointPoseSnapshot
{
    public XRHandJointID jointID;
    public Vector3 localPosition;
    public Quaternion localRotation;
}

/// <summary>
/// Common read surface shared by RecordedPose (ScriptableObject asset) and
/// BakedPose (plain serialized class embedded on GrabInteraction).
///
/// WHY THIS EXISTS
/// ─────────────────
/// GrabInteraction.ResolvePose() needs to hand back either a RecordedPose
/// asset or a baked-in-place BakedPose without caring which. Previously this
/// was done by copying a BakedPose into a freshly `new`-ed RecordedPose —
/// but RecordedPose is a ScriptableObject, and instances created with `new`
/// have no native backing, so Unity's overloaded null checks silently treat
/// them as null. This interface lets ResolvePose return either type directly
/// by reference, with zero runtime instantiation.
/// </summary>
public interface IPoseSource
{
    PoseType PoseType { get; }
    Handedness Handedness { get; }
    JointPoseSnapshot[] JointSnapshots { get; }
    float TriggerValue { get; }
    float GripValue { get; }
    bool HasAnchorData { get; }
    Quaternion HandToObjectRotOffset { get; }
    Vector3 ObjToContactLS { get; }
    Quaternion RecordedObjectWorldRotation { get; }
    float RecordedPinchToObjectDistance { get; }
    Vector3 RecordedPinchToObjectDirLS { get; }
}

/// <summary>
/// Stores a hand-pose snapshot and everything needed to re-place the object
/// at grab time so the recorded grip point lands exactly at the live finger.
///
/// TWO RECORDING MODES
/// ────────────────────
///   HandTracking — JointPoseSnapshot[] keyed by XRHandJointID.
///                  Applied by HandPoseLock via skeleton driver list swap.
///
///   Controller   — Two floats: triggerValue and gripValue.
///                  These are the Blend Tree parameters that fully define the
///                  hand pose. At lock time ControllerPoseLock sets these on
///                  the Animator, forces one evaluation so the bones settle,
///                  then disables the Animator. The blend tree IS the pose —
///                  no bone snapshots needed.
///
/// Anchor data (handToObjectRotOffset, objToContactLS) is identical for both
/// modes — always recorded relative to the wrist / controller root.
/// </summary>

// ─────────────────────────────────────────────────────────────────────────────
// Editor recorder
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Place anywhere in the scene. In Play Mode, hold the object in the desired
/// grip, then right-click → context menu to record.
///
/// Supports BOTH input modes:
///   HandTracking  — uses handPoseLock → skeletonDriver + rootTransform
///   Controller    — reads Trigger/Grip floats from the hand Animator
/// </summary>
public class PoseRef : MonoBehaviour
{
    [Header("Mode")]
    public InputMode recordMode = InputMode.HandTracking;
    public Handedness handedness = Handedness.Right;

    [Header("Contact & Target")]
    public Transform pinchPoint;
    public GrabInteraction targetGrabbable;

    [Header("Hand Tracking (when recordMode = HandTracking)")]
    [Tooltip("Provides driver and wrist root.")]
    public HandPoseLock handPoseLock;

    [Header("Controller (when recordMode = Controller)")]
    [Tooltip("Provides Animator reference for reading blend tree values.")]
    public ControllerPoseLock controllerPoseLock;

    [Tooltip("Stable tracking reference for controller mode.")]
    public Transform controllerRoot;

#if UNITY_EDITOR
    [ContextMenu("Save Hand Tracking Pose")]
    public void RecordHandTrackingPose()
    {
        if (handPoseLock == null)
        {
            return;
        }

        XRHandSkeletonDriver driver = handPoseLock.skeletonDriver;
        if (driver == null)
        {
            return;
        }

        Transform handRoot = driver.rootTransform;
        if (handRoot == null)
        {
            return;
        }

        string label = handedness == Handedness.Right ? "RightHand" : "LeftHand";
        RecordedPose.CaptureHandTracking(
            driver, handRoot, handedness,
            pinchPoint, targetGrabbable,
            assetName: $"{gameObject.name}_{label}_HandTracking");
    }

    [ContextMenu("Save Controller Pose")]
    public void RecordControllerPose()
    {
        if (controllerPoseLock == null)
        {
            return;
        }
        if (controllerPoseLock.handAnimator == null)
        {
            return;
        }
        if (controllerRoot == null)
        {
            return;
        }

        string label = handedness == Handedness.Right ? "RightHand" : "LeftHand";
        RecordedPose.CaptureController(
            controllerPoseLock.handAnimator,
            controllerRoot,
            handedness,
            pinchPoint, targetGrabbable,
            assetName: $"{gameObject.name}_{label}_Controller");
    }
#endif
}

