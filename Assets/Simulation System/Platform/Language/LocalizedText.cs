using LightSide;   // UniText
using UnityEngine;

#if LOCALIZATION_TMP
using TMPro;
#endif

/// <summary>
/// Put this on any UniText (or TMP_Text) that should follow the active language.
///
///   STATIC  - a label already in the scene. Awake captures the authored text
///             as its fallback, OnEnable applies the language, and it stays
///             subscribed so language buttons update it automatically.
///
///   DYNAMIC - a label on a prefab you Instantiate. OnEnable fires on
///             instantiate, so it localizes itself even when spawned long after
///             the language was chosen. If the key is computed at runtime, call
///             Bind() right after Instantiate.
///
/// TextMeshPro support is behind the LOCALIZATION_TMP scripting define
/// (Project Settings -> Player -> Scripting Define Symbols). It is off by
/// default so the system compiles in projects without the TMP package.
/// </summary>
[AddComponentMenu("Localization/Localized Text")]
[DisallowMultipleComponent]
public class LocalizedText : MonoBehaviour
{
    [Tooltip("Lookup key, e.g. 'MainMenu_Play'. Leave empty if Bind() will supply it.")]
    [SerializeField] private string key;

    [Tooltip("Authored text, used when the key is missing. Filled in automatically by the scanner.")]
    [TextArea(1, 3)]
    [SerializeField] private string fallback;

    [Tooltip("Target label. Auto-found on this GameObject when left empty.")]
    [SerializeField] private UniText uniText;

#if LOCALIZATION_TMP
    [SerializeField] private TMP_Text tmpText;
#endif

    private object[] formatArgs;
    private bool subscribed;

    /// <summary>Lookup key. Settable from editor tooling.</summary>
    public string Key
    {
        get => key;
        set => key = value;
    }

    /// <summary>Authored source string. Settable from editor tooling.</summary>
    public string Fallback
    {
        get => fallback;
        set => fallback = value;
    }

    private void Awake()
    {
        ResolveTarget();

        if (!HasTarget)
        {
            Debug.LogWarning($"[LocalizedText] No text component on '{name}'.", this);
            enabled = false;
            return;
        }

        // Safety net for components added by hand rather than by the scanner.
        // Captured once, before anything overwrites it: the fallback must always
        // be the AUTHORED string, never the currently displayed one, or
        // switching es -> en would leave Spanish stranded.
        if (string.IsNullOrEmpty(fallback)) fallback = ReadTarget();
    }

    private void OnEnable()
    {
        Subscribe();
        Apply();
    }

    private void OnDisable() => Unsubscribe();

    private void Subscribe()
    {
        if (subscribed) return;

        LocalizationManager.Instance.OnLanguageChanged += Apply;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed) return;

        // The manager may already be gone during shutdown.
        if (LocalizationManager.Instance != null)
            LocalizationManager.Instance.OnLanguageChanged -= Apply;

        subscribed = false;
    }

    // ---------------------------------------------------------------------
    // Target abstraction
    // ---------------------------------------------------------------------

    /// <summary>Finds the text component. Public so editor tooling can call it.</summary>
    public void ResolveTarget()
    {
        if (uniText == null) uniText = GetComponent<UniText>();

#if LOCALIZATION_TMP
        if (uniText == null && tmpText == null) tmpText = GetComponent<TMP_Text>();
#endif
    }

    public bool HasTarget
    {
        get
        {
#if LOCALIZATION_TMP
            return uniText != null || tmpText != null;
#else
            return uniText != null;
#endif
        }
    }

    /// <summary>
    /// Reads the current label text. Uses UniText.Text rather than CleanText so
    /// authored markup survives - capturing the stripped version as a fallback
    /// would silently drop &lt;color&gt; and &lt;b&gt; tags on the first switch.
    /// </summary>
    public string ReadTarget()
    {
        if (uniText != null) return uniText.Text;

#if LOCALIZATION_TMP
        if (tmpText != null) return tmpText.text;
#endif
        return string.Empty;
    }

    /// <summary>Writes to the label. Public so the editor preview can drive it.</summary>
    public void WriteTarget(string value)
    {
        // Both setters early-out on an unchanged value, so redundant calls are free.
        if (uniText != null) { uniText.Text = value; return; }

#if LOCALIZATION_TMP
        if (tmpText != null) tmpText.text = value;
#endif
    }

    // ---------------------------------------------------------------------
    // Runtime API
    // ---------------------------------------------------------------------

    /// <summary>
    /// Assigns a key and fallback after instantiation, then refreshes:
    ///
    ///     var row = Instantiate(rowPrefab, container);
    ///     row.GetComponentInChildren&lt;LocalizedText&gt;()
    ///        .Bind($"Item_{item.id}_Name", item.defaultName);
    /// </summary>
    public void Bind(string newKey, string newFallback)
    {
        key = newKey;
        fallback = newFallback;

        Subscribe();   // Covers Bind() being called while still disabled.
        Apply();
    }

    /// <summary>
    /// For values containing runtime numbers. Author the string with
    /// placeholders ("Score: {0}") and pass the arguments here. They are kept,
    /// so a later language switch re-formats instead of showing a bare "{0}".
    /// </summary>
    public void SetFormatArgs(params object[] args)
    {
        formatArgs = args;
        Apply();
    }

    /// <summary>Resolved string without touching the label.</summary>
    public string Resolve() => LocalizationManager.Instance.GetText(key, fallback);

    private void Apply()
    {
        if (!HasTarget) return;

        WriteTarget(Format(Resolve()));
    }

    /// <summary>Applies an explicit value. Used by the editor preview, which has no live manager.</summary>
    public void ApplyPreview(string value)
    {
        ResolveTarget();
        if (!HasTarget) return;

        WriteTarget(Format(value));
    }

    private string Format(string value)
    {
        if (formatArgs == null || formatArgs.Length == 0) return value;

        // A translator typo in the placeholders must not throw at runtime.
        try { return string.Format(value, formatArgs); }
        catch (System.FormatException)
        {
            Debug.LogWarning($"[LocalizedText] Bad placeholders for key '{key}'.", this);
            return value;
        }
    }
}