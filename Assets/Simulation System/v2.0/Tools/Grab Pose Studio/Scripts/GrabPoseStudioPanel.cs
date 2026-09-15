using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Events;
using TMPro;
using UnityEngine.UI;
using SimulationSystem.V02.Simulation.Managers;

/// <summary>
/// Grab Pose Studio V1 — World Space status panel.
///
/// Created by GrabPoseStudioInput and placed inside the GrabPoseStudio
/// scene (not the simulation scene) via SceneManager.MoveGameObjectToScene.
/// This keeps the simulation scene completely clean — all Studio runtime
/// objects live and die with the Studio scene.
///
/// Display only. No business logic, no pose calculations, no recording.
/// GrabPoseStudioInput drives all updates via UpdateDisplay() and
/// SetFeedback().
/// </summary>
public class GrabPoseStudioPanel : MonoBehaviour
{
    // ── Tunable offset ───────────────────────────────────────────────────────
    // Adjust live in the Inspector during Play Mode, then copy the values
    // back here as the new defaults.
    [Header("Position Offset from RecordingPoint")]
    public Vector3 PanelOffset = new Vector3(0f, 0.3f, 0.4f);

    // ── Layout ───────────────────────────────────────────────────────────────

    private const float CanvasWorldWidth    = 0.42f;   // → sizeDelta.x = 210
    private const float CanvasWorldHeight   = 0.4158f; // → sizeDelta.y ≈ 207.9
    private const float CanvasPixelsPerUnit = 500f;

    // Padding inside the panel in canvas pixels
    private const float PadH = 24f;  // horizontal (left + right)
    private const float PadV = 20f;  // vertical   (top  + bottom)

    private const float FeedbackDuration = 2f;

    // ── Internal refs ─────────────────────────────────────────────────────────

    private Transform       _recordingPoint;
    private Camera          _camera;

    private TextMeshProUGUI _indexText;
    private TextMeshProUGUI _nameText;
    private TextMeshProUGUI _leftStatusText;
    private TextMeshProUGUI _rightStatusText;
    private TextMeshProUGUI _feedbackText;
    private GameObject      _exitButtonGo;

    private float _feedbackExpiry;

    /// <summary>Called directly when the Exit Play Mode button is pressed.
    /// Set by Initialise() before the button is built, so there is no
    /// separate subscription step that could run after a click. Panel has
    /// no UnityEditor dependency — GrabPoseStudioInput owns the actual
    /// exit-Play-Mode call.</summary>
    private System.Action _onExitRequested;

    // ── Colours ──────────────────────────────────────────────────────────────

    private static readonly Color BgColor        = new Color(0.06f, 0.06f, 0.06f, 0.88f);
    private static readonly Color ColorRecorded  = new Color(0.27f, 1f,    0.53f);
    private static readonly Color ColorMissing   = new Color(1f,    0.40f, 0.40f);
    private static readonly Color ColorWhite     = Color.white;
    private static readonly Color ColorGrey      = new Color(0.70f, 0.70f, 0.70f);

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Call once immediately after the panel GameObject is moved into the
    /// Studio scene. Builds the canvas hierarchy and anchors to RecordingPoint.
    /// </summary>
    public void Initialise(Transform recordingPoint, System.Action onExitRequested)
    {
        _recordingPoint = recordingPoint;
        _onExitRequested = onExitRequested;
        _camera = Camera.main;
        BuildCanvas();
        SnapToRecordingPoint();
    }

    public void UpdateDisplay(string objectName, int index, int total,
                              bool hasLeft, bool hasRight, bool allRecorded)
    {
        if (_exitButtonGo != null)
            _exitButtonGo.SetActive(allRecorded);

        if (_indexText != null)
            _indexText.text = $"[ {index} / {total} ]";

        if (_nameText != null)
            _nameText.text = objectName;

        if (_leftStatusText != null)
        {
            _leftStatusText.text  = hasLeft  ? "\u2705  Recorded" : "\u274c  Missing";
            _leftStatusText.color = hasLeft  ? ColorRecorded : ColorMissing;
        }

        if (_rightStatusText != null)
        {
            _rightStatusText.text  = hasRight ? "\u2705  Recorded" : "\u274c  Missing";
            _rightStatusText.color = hasRight ? ColorRecorded : ColorMissing;
        }

        if (_feedbackText != null) _feedbackText.text = "";
        _feedbackExpiry = 0f;
    }

    public void SetFeedback(string message)
    {
        if (_feedbackText == null) return;
        _feedbackText.text = message;
        _feedbackExpiry = Time.realtimeSinceStartup + FeedbackDuration;
    }

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void LateUpdate()
    {
        SnapToRecordingPoint();
        BillboardTowardCamera();

        if (_feedbackText != null &&
            !string.IsNullOrEmpty(_feedbackText.text) &&
            Time.realtimeSinceStartup >= _feedbackExpiry)
        {
            _feedbackText.text = "";
        }
    }

    private void SnapToRecordingPoint()
    {
        if (_recordingPoint == null) return;
        transform.position = _recordingPoint.position + PanelOffset;
    }

    private void BillboardTowardCamera()
    {
        if (_camera == null)
        {
            _camera = Camera.main;
            if (_camera == null) return;
        }

        Vector3 dir = transform.position - _camera.transform.position;
        dir.y = 0f; // lock to world-up so the panel never rolls/tilts as the head moves
        if (dir.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
    }

    // ── Canvas construction ───────────────────────────────────────────────────

    private void BuildCanvas()
    {
        // Canvas
        Canvas canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;


        CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = CanvasPixelsPerUnit;

        RectTransform rt = GetComponent<RectTransform>();
        rt.sizeDelta   = new Vector2(CanvasWorldWidth  * CanvasPixelsPerUnit,
                                     CanvasWorldHeight * CanvasPixelsPerUnit);
        rt.localScale  = Vector3.one / CanvasPixelsPerUnit;
        // Match the Inspector layout from the screenshot exactly:
        // pivot + anchors centred at 0.5,0.5 so the panel pivots from its centre
        rt.pivot       = new Vector2(0.5f, 0.5f);
        rt.anchorMin   = new Vector2(0.5f, 0.5f);
        rt.anchorMax   = new Vector2(0.5f, 0.5f);

        // Background — fills the whole canvas
        GameObject bg = new GameObject("Background");
        bg.transform.SetParent(transform, false);
        Image bgImg = bg.AddComponent<Image>();
        bgImg.color = BgColor;
        StretchFull(bg.GetComponent<RectTransform>());

        // Content area — inset from the background edges by PadH / PadV
        GameObject content = new GameObject("Content");
        content.transform.SetParent(transform, false);
        RectTransform contentRt = content.AddComponent<RectTransform>();
        contentRt.anchorMin = Vector2.zero;
        contentRt.anchorMax = Vector2.one;
        // offsetMin = (left, bottom)  offsetMax = (-right, -top)
        contentRt.offsetMin = new Vector2( PadH,  PadV);
        contentRt.offsetMax = new Vector2(-PadH, -PadV);

        VerticalLayoutGroup vlg = content.AddComponent<VerticalLayoutGroup>();
        vlg.childAlignment         = TextAnchor.UpperLeft;
        vlg.spacing                = 8f;
        vlg.childControlHeight     = false;
        vlg.childControlWidth      = true;
        vlg.childForceExpandHeight = false;
        vlg.childForceExpandWidth  = true;
        vlg.padding                = new RectOffset(0, 0, 0, 0);

        // ── Rows ─────────────────────────────────────────────────────────────
        MakeLabel(content, "GRAB POSE STUDIO", 14f, FontStyles.Bold,   ColorWhite,    22f);
        _indexText       = MakeLabel(content, "[ \u2014 / \u2014 ]",   11f, FontStyles.Normal, ColorGrey,     17f);
        _nameText        = MakeLabel(content, "",                       13f, FontStyles.Bold,   ColorWhite,    20f);

        MakeLabel(content, "Left",                                      10f, FontStyles.Normal, ColorGrey,     15f);
        _leftStatusText  = MakeLabel(content, "\u274c  Missing",        12f, FontStyles.Normal, ColorMissing,  18f);

        MakeLabel(content, "Right",                                     10f, FontStyles.Normal, ColorGrey,     15f);
        _rightStatusText = MakeLabel(content, "\u274c  Missing",        12f, FontStyles.Normal, ColorMissing,  18f);

        _feedbackText    = MakeLabel(content, "",                       12f, FontStyles.Bold,   ColorWhite,    18f);

        _exitButtonGo = MakeExitButton(content);
        _exitButtonGo.SetActive(false);
    }

    /// <summary>
    /// Same touch-button pattern as the SDK's "Hint button" prefab
    /// (Assets/Simulation Resources/Prefab/UI/Hint button.prefab):
    /// CustomButton + a trigger BoxCollider on the button GameObject,
    /// detecting the "IndexFinger"-tagged hand collider — NOT a UGUI
    /// Button/EventSystem/raycaster click, which this project's XR rig
    /// does not drive.
    /// </summary>
    private const float ExitButtonHeight = 26f;
    private const float ExitButtonWidth  = CanvasWorldWidth * CanvasPixelsPerUnit - 2f * PadH;

    private GameObject MakeExitButton(GameObject parent)
    {
        GameObject go = new GameObject("ExitPlayModeButton");
        // Build inactive: CustomButton.Awake() runs synchronously the instant
        // AddComponent<CustomButton>() executes on an active GameObject, and
        // it bakes `holdTime` into a readonly Timer right there — setting
        // holdTime after that point has no effect. Staying inactive during
        // construction defers Awake() until UpdateDisplay() later activates
        // the button, by which point holdTime is already set correctly.
        go.SetActive(false);
        go.transform.SetParent(parent.transform, false);

        RectTransform rt = go.AddComponent<RectTransform>();
        rt.sizeDelta = new Vector2(0f, ExitButtonHeight);

        Image img = go.AddComponent<Image>();
        img.color = new Color(0.85f, 0.25f, 0.25f);

        BoxCollider collider = go.AddComponent<BoxCollider>();
        collider.isTrigger = true;
        collider.size = new Vector3(ExitButtonWidth, ExitButtonHeight, 10f);

        CustomButton customButton = go.AddComponent<CustomButton>();
        customButton.holdTime = 0.15f; // near-instant tap, not a 1s hold-to-confirm
        // CustomButton's UnityEvent fields have no field initializer, so
        // Unity's Editor-time serialization (which auto-constructs them when
        // a component is added via a prefab, e.g. the SDK's "Hint button")
        // never runs for a component created via pure runtime AddComponent —
        // they stay null and AddListener throws a NullReferenceException.
        if (customButton.OnButtonClicked == null)
            customButton.OnButtonClicked = new UnityEvent();
        if (customButton.OnStartHolding == null)
            customButton.OnStartHolding = new UnityEvent();
        if (customButton.OnSuspendHolding == null)
            customButton.OnSuspendHolding = new UnityEvent();

        Transform btnTransform = go.transform;

        // Press-in: shrink slightly the moment the finger touches the button.
        customButton.OnStartHolding.AddListener(() =>
        {
            _ = btnTransform.DoScale(0.9f, 0.08f, Ease.OutQuad);
        });

        // Finger pulled out before the hold completed: spring back to rest.
        customButton.OnSuspendHolding.AddListener(() =>
        {
            _ = btnTransform.DoScale(1f, 0.12f, Ease.OutBack);
        });

        // Confirmed click: haptic buzz + a quick overshoot bounce back to rest.
        customButton.OnButtonClicked.AddListener(() =>
        {
            Debug.Log("[GrabPoseStudioPanel] Exit Play Mode button fired — invoking callback.");
            _ = HapticManager.UIClick(HapticHand.Both);
            _ = btnTransform.DoScale(1f, 0.18f, Ease.OutBack);
            _onExitRequested?.Invoke();
        });

        TextMeshProUGUI label = MakeLabel(go, "Exit Play Mode", 12f, FontStyles.Bold, ColorWhite, ExitButtonHeight);
        RectTransform labelRt = label.GetComponent<RectTransform>();
        labelRt.anchorMin = Vector2.zero;
        labelRt.anchorMax = Vector2.one;
        labelRt.offsetMin = Vector2.zero;
        labelRt.offsetMax = Vector2.zero;
        label.alignment = TextAlignmentOptions.Center;

        return go;
    }

    private static void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private static TextMeshProUGUI MakeLabel(
        GameObject parent, string text, float size,
        FontStyles style, Color color, float height)
    {
        GameObject go = new GameObject("Label");
        go.transform.SetParent(parent.transform, false);

        RectTransform rt = go.AddComponent<RectTransform>();
        rt.sizeDelta = new Vector2(0f, height);

        TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text               = text;
        tmp.fontSize           = size;
        tmp.fontStyle          = style;
        tmp.color              = color;
        tmp.overflowMode       = TextOverflowModes.Ellipsis;
        tmp.enableWordWrapping = false;

        return tmp;
    }
}
