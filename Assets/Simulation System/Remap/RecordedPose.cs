using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR.Hands;
[CreateAssetMenu(menuName = "XR Hands/Recorded Pose", fileName = "NewRecordedPose")]
public class RecordedPose : ScriptableObject, IPoseSource
{
    [Header("Identity")]
    public PoseType poseType;
    public Handedness handedness;

    // ── Hand-tracking joints ─────────────────────────────────────────────────

    [Header("Hand Tracking Data")]
    [Tooltip("Joint local-pose snapshots (HandTracking mode). Applied by HandPoseLock.")]
    public JointPoseSnapshot[] jointSnapshots = System.Array.Empty<JointPoseSnapshot>();

    // ── Controller blend tree values ─────────────────────────────────────────

    [Header("Controller Data")]
    [Tooltip("Blend tree 'Trigger' parameter value at record time (0–1).")]
    [Range(0f, 1f)]
    public float triggerValue;

    [Tooltip("Blend tree 'Grip' parameter value at record time (0–1).")]
    [Range(0f, 1f)]
    public float gripValue;

    // ── Anchor data (shared) ─────────────────────────────────────────────────

    [Header("Anchor Data")]
    [Tooltip("True when anchor data was recorded against a GrabInteraction.")]
    public bool hasAnchorData;

    [Tooltip("Inverse(trackingRoot.rotation) * object.rotation at record time.\n" +
             "For hands: trackingRoot = wrist (skeletonDriver.rootTransform).\n" +
             "For controllers: trackingRoot = controllerRoot.\n" +
             "At grab time: desiredRot = liveRoot.rotation * handToObjectRotOffset")]
    public Quaternion handToObjectRotOffset;

    [Tooltip("(pinchPoint.position - object.position) in object rotation frame.\n" +
             "Scale-immune. Identical formula for both input modes.")]
    public Vector3 objToContactLS;

    [Header("Recorded Object Transform")]
    [Tooltip("World rotation of the object at record time.\n" +
             "Used at grab time: desiredRot = liveRoot.rotation * handToObjectRotOffset")]
    public Quaternion recordedObjectWorldRotation = Quaternion.identity;

    [Tooltip("Distance from pinch point to object center at record time.\n" +
             "Used at grab time to position the object at the exact recorded distance.")]
    public float recordedPinchToObjectDistance;

    [Tooltip("Direction from pinch point to object center in tracking-root-local space.\n" +
             "At grab time: objectPos = pinchPos + liveRoot.rotation * recordedPinchToObjectDirLS * recordedPinchToObjectDistance")]
    public Vector3 recordedPinchToObjectDirLS;

    // ── IPoseSource ──────────────────────────────────────────────────────────

    PoseType IPoseSource.PoseType => poseType;
    Handedness IPoseSource.Handedness => handedness;
    JointPoseSnapshot[] IPoseSource.JointSnapshots => jointSnapshots;
    float IPoseSource.TriggerValue => triggerValue;
    float IPoseSource.GripValue => gripValue;
    bool IPoseSource.HasAnchorData => hasAnchorData;
    Quaternion IPoseSource.HandToObjectRotOffset => handToObjectRotOffset;
    Vector3 IPoseSource.ObjToContactLS => objToContactLS;
    Quaternion IPoseSource.RecordedObjectWorldRotation => recordedObjectWorldRotation;
    float IPoseSource.RecordedPinchToObjectDistance => recordedPinchToObjectDistance;
    Vector3 IPoseSource.RecordedPinchToObjectDirLS => recordedPinchToObjectDirLS;

    // ── Editor: Hand-Tracking Capture ────────────────────────────────────────
#if UNITY_EDITOR

    public static RecordedPose CaptureHandTracking(
        XRHandSkeletonDriver driver,
        Transform handRoot,
        Handedness handedness,
        Transform pinchPoint = null,
        GrabInteraction targetGrabbable = null,
        string assetName = "RecordedPose",
        string saveFolder = null)
    {
        if (driver == null) { return null; }
        if (handRoot == null) { return null; }

        var refs = driver.jointTransformReferences;
        var snapshots = new List<JointPoseSnapshot>(refs.Count);
        foreach (var item in refs)
        {
            if (item.jointTransform == null) continue;
            snapshots.Add(new JointPoseSnapshot
            {
                jointID = item.xrHandJointID,
                localPosition = item.jointTransform.localPosition,
                localRotation = item.jointTransform.localRotation,
            });
        }

        var asset = CreateInstance<RecordedPose>();
        asset.poseType = PoseType.HandTracking;
        asset.handedness = handedness;
        asset.jointSnapshots = snapshots.ToArray();

        RecordAnchorData(asset, handRoot, pinchPoint, targetGrabbable);
        return SaveAsset(asset, assetName, saveFolder);
    }

    /// <summary>
    /// Capture a controller-driven hand pose by reading the current Animator
    /// blend tree parameters. That's it — two floats define the whole pose.
    /// </summary>
    public static RecordedPose CaptureController(
        Animator animator,
        Transform trackingRoot,
        Handedness handedness,
        Transform pinchPoint = null,
        GrabInteraction targetGrabbable = null,
        string assetName = "RecordedPose",
        string saveFolder = null)
    {
        if (animator == null) { return null; }
        if (trackingRoot == null) { return null; }

        var asset = CreateInstance<RecordedPose>();
        asset.poseType = PoseType.Controller;
        asset.handedness = handedness;
        asset.triggerValue = animator.GetFloat("Trigger");
        asset.gripValue = animator.GetFloat("Grip");


        RecordAnchorData(asset, trackingRoot, pinchPoint, targetGrabbable);
        return SaveAsset(asset, assetName, saveFolder);
    }

    // ── Shared helpers ───────────────────────────────────────────────────────

    private static void RecordAnchorData(
        RecordedPose asset, Transform trackingRoot,
        Transform pinchPoint, GrabInteraction targetGrabbable)
    {
        if (pinchPoint != null && targetGrabbable != null)
        {
            Transform obj = targetGrabbable.transform;

            // Relative rotation: root → object
            asset.handToObjectRotOffset =
                Quaternion.Inverse(trackingRoot.rotation) * obj.rotation;

            // Pinch-to-object vector in object-local space (existing field)
            asset.objToContactLS =
                Quaternion.Inverse(obj.rotation) * (pinchPoint.position - obj.position);

            // ── NEW: explicit object rotation and distance ───────────────
            asset.recordedObjectWorldRotation = obj.rotation;

            Vector3 pinchToObj = obj.position - pinchPoint.position;
            asset.recordedPinchToObjectDistance = pinchToObj.magnitude;

            // Direction from pinch to object in tracking-root-local space.
            // At grab time: objPos = pinchPos + liveRoot.rotation * dirLS * distance
            if (asset.recordedPinchToObjectDistance > 0.0001f)
            {
                asset.recordedPinchToObjectDirLS =
                    Quaternion.Inverse(trackingRoot.rotation) * pinchToObj.normalized;
            }
            else
            {
                asset.recordedPinchToObjectDirLS = Vector3.zero;
            }

            asset.hasAnchorData = true;

        }
        else
        {
            asset.hasAnchorData = false;
        }
    }

    private static RecordedPose SaveAsset(RecordedPose asset, string assetName, string saveFolder)
    {
        if (string.IsNullOrEmpty(saveFolder))
        {
            saveFolder = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (string.IsNullOrEmpty(saveFolder))
                saveFolder = "Assets";
            else if (!Directory.Exists(saveFolder))
                saveFolder = Path.GetDirectoryName(saveFolder);
        }

        string path = AssetDatabase.GenerateUniqueAssetPath($"{saveFolder}/{assetName}.asset");
        AssetDatabase.CreateAsset(asset, path);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        EditorUtility.FocusProjectWindow();
        Selection.activeObject = asset;

        string info = asset.poseType == PoseType.HandTracking
            ? $"{asset.jointSnapshots.Length} joints"
            : $"Trigger={asset.triggerValue:F2}, Grip={asset.gripValue:F2}";

        return asset;
    }
#endif
}
