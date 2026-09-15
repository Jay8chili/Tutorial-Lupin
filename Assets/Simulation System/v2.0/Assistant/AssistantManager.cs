using LightSide;
using SimulationSystem.V02.Simulation.Managers;
using SimulationSystem.V02.StateInteractions;
using SimulationSystem.V02.Utility;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using static UnityEngine.Rendering.DebugUI;

namespace SimulationSystem.V02.Assistant
{
    public class AssistantManager : MonoBehaviour
    {
        // ─────────────────────────────────────────────
        // SINGLETON
        // ─────────────────────────────────────────────

        public static AssistantManager Instance { get; private set; }

        public static Action PromptCompleted;

        // ─────────────────────────────────────────────
        // PANEL STATE MACHINE — TYPES & FIELDS
        // ─────────────────────────────────────────────

        // NOTE: BotFollowMe is a full member of this enum and participates in the interrupt/stash
        // flow exactly like Help, BotUI, etc.
        // Contamination and SpeedAlert are intentionally kept separate — any number of zones can
        // be triggered at once, so each overlays independently and never interrupts or stashes
        // the current panel (see the CONTAMINATION and HAND SPEED TRACKING regions below).
        private enum ActivePanel { None, Prompt, BotUI, Help, BotFollowMe, Assist }

        /// <summary>The panel currently popped in and visible. Only one panel is active at a time.</summary>
        private ActivePanel _currentPanel = ActivePanel.None;

        /// <summary>Panel that was interrupted by a higher-priority panel; restored when the interrupter closes.</summary>
        private ActivePanel _stashedPanel = ActivePanel.None;

        /// <summary>True when this manager paused the simulation to show a panel; used to auto-resume on dismiss.</summary>
        private bool _wasSimPausedByPanel = false;

        // One CTS per panel to cancel in-flight DoScale tweens
        private CancellationTokenSource _promptCts;
        private CancellationTokenSource _botUICts;
        private CancellationTokenSource _helpCts;
        private CancellationTokenSource _progressCts;
        private CancellationTokenSource _assistCts;
        private CancellationTokenSource _botFollowMeCts;
        private CancellationTokenSource _guideUnavailableCts;
        private Coroutine _guideUnavailableCoroutine;

        // Cached original scene scales — panels may be world-space canvases
        // with tiny scales like 0.001. We pop to these, not to Vector3.one.
        private Dictionary<GameObject, Vector3> _panelOriginalScales = new Dictionary<GameObject, Vector3>();

        // ─────────────────────────────────────────────
        // RUNTIME STATE
        // ─────────────────────────────────────────────

        /// <summary>True while the prompt panel is animating or waiting for its audio to finish.</summary>
        private bool _promptActive = false;

        /// <summary>Reference to the active HidePromptAfter coroutine; stopped early if a new state starts.</summary>
        private Coroutine _promptCoroutine = null;

        /// <summary>BotUI text queued while the prompt is still active; flushed when the prompt closes.</summary>
        private string _pendingUIContent = null;

        /// <summary>Callback queued alongside <see cref="_pendingUIContent"/>; invoked when the deferred BotUI is dismissed.</summary>
        private UnityAction _pendingOnComplete = null;

        /// <summary>Callback for the currently displayed BotUI panel; invoked when the dismiss button is pressed.</summary>
        private UnityAction _uiOnComplete = null;

        // ── Static UI panel tracking (Prompt / Clause) ───────────────────────
        // Prompt and Clause each resolve to either botPromptPanel (default,
        // bot-following) or a state-assigned static panel/text pair. These
        // track which one is currently the visible one, and which CTS field
        // owns its fade tween — no reparenting involved.

        private enum PromptPanelKind { Bot, StaticPrompt, StaticClause, Clause }
        private PromptPanelKind _activePromptPanelKind = PromptPanelKind.Bot;
        private GameObject _activePromptPanelObj;

        private CancellationTokenSource _staticPromptCts;
        private CancellationTokenSource _staticClauseCts;
        private CancellationTokenSource _clauseCts;

        /// <summary>Listener + state pair for "Keep Clause Till State End" — popped when the state completes.</summary>
        private UnityAction _clauseKeepUpHandler;
        private SimulationState _clauseKeepUpState;

        /// <summary>Tracks whether the help panel is currently visible.</summary>
        private bool _isHelpVisible = false;

        /// <summary>Tracks whether the settings panel is currently visible.</summary>
        private bool _isSettingsVisible = false;

        // ─────────────────────────────────────────────
        // SERIALIZED REFERENCES — BOT CORE
        // ─────────────────────────────────────────────

        [Header("Bot Controller")]
        [SerializeField] private BotController botController;

        // ─────────────────────────────────────────────
        // SERIALIZED REFERENCES — PANELS
        // ─────────────────────────────────────────────

        [Header("Bot Prompt Panel (shown on state start)")]
        [Tooltip("Root GameObject of the prompt panel shown at the start of each simulation state.")]
        [SerializeField] private GameObject botPromptPanel;
        [Tooltip("Text component that displays the prompt message inside the prompt panel.")]
        [SerializeField] private TMP_Text botPromptText;
        [Tooltip("UniText component that displays the prompt message instead, checked ahead of the TMP_Text field above.")]
        [SerializeField] private UniText botPromptTextUni;

        [Header("Pre/Post Prompt Panel (clause default)")]
        [Tooltip("Default panel used for pre-prompt / post-prompt (clause) text when a state does not assign its own " +
                 "static clause UI. Falls back to Bot Prompt Panel if left empty.")]
        [SerializeField] private GameObject prePostPromptPanel;
        [Tooltip("Text component that displays the pre/post prompt message inside the Pre/Post Prompt Panel.")]
        [SerializeField] private TMP_Text prePostPromptText;
        [Tooltip("UniText component that displays the pre/post prompt message instead, checked ahead of the TMP_Text field above.")]
        [SerializeField] private UniText prePostPromptTextUni;

        [Header("Bot UI Panel (shown per-interaction, not at state start)")]
        [Tooltip("Root GameObject of the BotUI panel shown during individual interactions.")]
        [SerializeField] private GameObject botUIPanel;
        [Tooltip("Text component that displays the interaction message inside the BotUI panel.")]
        [SerializeField] private TMP_Text botUIText;
        [Tooltip("UniText component that displays the interaction message instead, checked ahead of the TMP_Text field above.")]
        [SerializeField] private UniText botUITextUni;

        [Header("Bot Button (UI panel dismiss only)")]
        [Tooltip("Button shown on the BotUI panel; pressing it dismisses the panel and fires the onComplete callback.")]
        [SerializeField] private CustomButton botButton;

        [Header("Bot Progress Panel")]
        [Tooltip("Root GameObject of the progress panel that displays simulation completion.")]
        [SerializeField] private GameObject botProgressPanel;
        [Tooltip("All Progress components to update simultaneously. Each manages its own slider and text.")]
        [SerializeField] private Progress[] progressTrackers;
        [Tooltip("Text component that displays the progress percentage alongside the slider.")]
        [SerializeField] private TMP_Text botProgressText;
        [Tooltip("UniText component that displays the progress percentage instead, checked ahead of the TMP_Text field above.")]
        [SerializeField] private UniText botProgressTextUni;
        [Tooltip("Duration in seconds for the slider to animate from its current value to the target progress.")]
        [SerializeField] private float progressAnimDuration = 0.6f;

        [Header("Bot Follow Me Panel")]
        [Tooltip("Root GameObject of the follow-me panel shown when the bot is guiding the user. " +
                 "Participates in full interrupt/stash flow identical to Help and BotUI.")]
        [SerializeField] private GameObject botFollowMePanel;

        [Header("Settings Panel")]
        [Tooltip("Root GameObject of the settings panel; toggled via OpenSettings / CloseSettings.")]
        [SerializeField] private GameObject settingsPanel;

        // ─────────────────────────────────────────────
        // SERIALIZED REFERENCES — POP ANIMATION
        // ─────────────────────────────────────────────

        [Header("Panel Pop Animation")]
        [Tooltip("Duration of pop-in / pop-out scale animation (seconds).")]
        [SerializeField] private float popDuration = 0.2f;

        [Tooltip("Ease for pop-in (scale 0→1). OutBack gives a bouncy overshoot.")]
        [SerializeField] private Ease popInEase = Ease.OutBack;

        [Tooltip("Ease for pop-out (scale 1→0). InBack gives a tuck-in feel.")]
        [SerializeField] private Ease popOutEase = Ease.InBack;

        // ─────────────────────────────────────────────
        // SERIALIZED REFERENCES — HELP PANEL
        // ─────────────────────────────────────────────

        [Header("[HELP PANEL]")]
        [Header("Refrences")]
        [Tooltip("Root GameObject of the help panel shown when the user requests assistance.")]
        [SerializeField] private GameObject helpPanel;
        [Tooltip("Button that opens the help panel; also closes the assist panel automatically.")]
        [SerializeField] private CustomButton helpButton;
        [Tooltip("Button inside the help panel that closes it and re-opens the assist panel.")]
        [SerializeField] private CustomButton helpCloseButton;

        // ─────────────────────────────────────────────
        // SERIALIZED REFERENCES — ASSIST PANEL
        // ─────────────────────────────────────────────

        [Header("[ ASSIST PANEL ]")]
        [Header("Refrences")]
        [Tooltip("Root GameObject of the assist panel toggled by the Y button.")]
        [SerializeField] private GameObject assistPanel;
        [Tooltip("Input action reference for the controller Y button that toggles the assist panel.")]
        [SerializeField] private InputActionReference buttonYAction;
        public static Action HelpEnabled;
        public static Action HelpDisabled;

        /// <summary>Tracks whether the assist panel is currently popped in.</summary>
        private bool isAssistantPanelVisible = false;

        // ─────────────────────────────────────────────
        // SERIALIZED REFERENCES — PATHFINDING
        // ─────────────────────────────────────────────

        [Header(" [ PATHFINDING ] ")]
        [Header("Refrences")]
        [Tooltip("Button that triggers bot guidance toward the current interaction target.")]
        [SerializeField] CustomButton pathfindingButton;

        [Tooltip("Panel shown when the guide button is pressed but the current interaction does not support guidance.")]
        [SerializeField] private GameObject guideUnavailablePanel;

        [Tooltip("How long (seconds) the guide-unavailable panel stays visible before auto-dismissing.")]
        [SerializeField] private float guideUnavailableDuration = 3f;

        // ─────────────────────────────────────────────
        // SERIALIZED REFERENCES — BOT DISCARD
        // ─────────────────────────────────────────────

        [Header("Bot Discard")]
        [Tooltip("Total time in seconds for the bot to dissolve-out, teleport, and dissolve-in a discarded object.")]
        [SerializeField] private float discardMoveDuration = 0.8f;

        [Tooltip("Zone GameObject that is enabled while a discard operation is in progress.")]
        [SerializeField] private GameObject discardZone;

        /// <summary>Queue of objects waiting to be moved to their discard destination; processed one at a time.</summary>
        private readonly Queue<(GameObject obj, Transform destination)> _discardQueue
            = new Queue<(GameObject, Transform)>();

        /// <summary>True while RunDiscardQueue is processing; prevents multiple coroutines running in parallel.</summary>
        private bool _discardRunning = false;

        // ─────────────────────────────────────────────
        // SERIALIZED REFERENCES — CONTAMINATION
        // ─────────────────────────────────────────────

        [Header("[ CONTAMINATION ]")]
        [Header("Refrences")]
        [Tooltip("Reference to the ContaminationManager that fires contamination triggered/resolved events.")]
        [SerializeField] private ContaminationManager contaminationManager;

        // Any number of zones can be contaminated or over-speed at once, each with its own panel —
        // these track every currently-open panel and its independent fade-tween CTS by panel instance.
        private HashSet<GameObject> _activeContaminationPanels = new HashSet<GameObject>();
        private Dictionary<GameObject, CancellationTokenSource> _contaminationCts = new Dictionary<GameObject, CancellationTokenSource>();

        private HashSet<GameObject> _activeSpeedAlertPanels = new HashSet<GameObject>();
        private Dictionary<GameObject, CancellationTokenSource> _speedAlertCts = new Dictionary<GameObject, CancellationTokenSource>();

        [Header("[ FIRST AIR ]")]
        [Header("Refrences")]
        [Tooltip("Reference to the FirstAirManager that fires first air triggered/resolved events.")]
        [SerializeField] private FirstAirManager firstAirManager;

        // Any number of zones can have first air broken at once, each with its own panel —
        // these track every currently-open panel and its independent fade-tween CTS by panel instance.
        private HashSet<GameObject> _activeFirstAirPanels = new HashSet<GameObject>();
        private Dictionary<GameObject, CancellationTokenSource> _firstAirCts = new Dictionary<GameObject, CancellationTokenSource>();

        // ─────────────────────────────────────────────
        // SERIALIZED REFERENCES — LEGACY PROMPT
        // ─────────────────────────────────────────────

        [Header("Scene Prompt (legacy / non-state use)")]
        [Tooltip("Legacy prompt component used outside the state machine; prefer EvaluateStateInteractions for state-driven prompts.")]
        [SerializeField] private PromptInteraction prompt;

        // ─────────────────────────────────────────────
        // PROGRESS — PRIVATE STATE
        // ─────────────────────────────────────────────

        /// <summary>The progress value (0–1) the slider is currently animating toward.</summary>
        private float _progressTarget = 0f;

        /// <summary>Reference to the active AnimateProgress coroutine; stopped before starting a new animation.</summary>
        private Coroutine _progressCoroutine = null;

        // ═════════════════════════════════════════════
        // UNITY LIFECYCLE
        // ═════════════════════════════════════════════

        #region Unity Methods

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            TextCompat.ResolveUniText(ref botPromptTextUni, botPromptText);
            TextCompat.ResolveUniText(ref prePostPromptTextUni, prePostPromptText);
            TextCompat.ResolveUniText(ref botUITextUni, botUIText);
            TextCompat.ResolveUniText(ref botProgressTextUni, botProgressText);

            // Cache each panel's original scene scale (may be tiny for world-space canvases),
            // then zero it out and disable. PopIn will scale to the cached value.
            CacheAndHidePanel(botPromptPanel);
            CacheAndHidePanel(prePostPromptPanel);
            CacheAndHidePanel(botUIPanel);
            CacheAndHidePanel(helpPanel);
            CacheAndHidePanel(assistPanel);
            CacheAndHidePanel(botFollowMePanel);
            CacheAndHidePanel(guideUnavailablePanel);
            CacheAndHideContaminationZonePanels();
            CacheAndHideFirstAirZonePanels();

            if (settingsPanel)
            {
                settingsPanel.SetActive(false);
            }
            if (discardZone)
            {
                discardZone?.SetActive(false);
            }
            //botButton?.gameObject.SetActive(false);

            if (progressTrackers != null)
                foreach (var t in progressTrackers)
                    if (t != null) t.SetSliderValue(0f);

            // Warm up TMP font atlases so text dimensions are correct on first show.
            // See WarmUpTMPAtlases for full explanation.
            WarmUpTMPAtlases();

            AddListnersPathfinding();
            AddListnersForHelpButtonPanel();
        }

        private void OnEnable()
        {
            BotController.BotSummonedWithKey += OnBotSummonedWithKey;
            BotController.BotDismissedWithKey += OnBotDismissedWithKey;

            buttonYAction.action.actionMap.Enable(); // enables the whole map
            buttonYAction.action.performed += OnYButtonPressed;

            TeleportManager.TeleportStarted += OnTeleoprtStarted;
            TeleportManager.TeleportCompleted += OnTeleportCompleted;

            AddListnersContamination();
            AddListnersHandSpeedTracking();
            AddListnersFirstAir();
        }

        private void OnDisable()
        {
            BotController.BotSummonedWithKey -= OnBotSummonedWithKey;
            BotController.BotDismissedWithKey -= OnBotDismissedWithKey;

            buttonYAction.action.performed -= OnYButtonPressed;

            TeleportManager.TeleportStarted -= OnTeleoprtStarted;
            TeleportManager.TeleportCompleted -= OnTeleportCompleted;

            RemoveListnersPathfinding();
            RemoveListnersContamination();
            RemoveListnersHandSpeedTracking();
            RemoveListnersFirstAir();

            _promptCts?.Cancel();
            _botUICts?.Cancel();
            _helpCts?.Cancel();
            _progressCts?.Cancel();
            _assistCts?.Cancel();
            _botFollowMeCts?.Cancel();
            _guideUnavailableCts?.Cancel();

            foreach (CancellationTokenSource cts in _contaminationCts.Values) cts?.Cancel();
            foreach (CancellationTokenSource cts in _speedAlertCts.Values) cts?.Cancel();
            foreach (CancellationTokenSource cts in _firstAirCts.Values) cts?.Cancel();
        }

        #endregion


        // ═════════════════════════════════════════════
        // POP ANIMATION HELPERS
        // ═════════════════════════════════════════════
        // Panels start DISABLED. PopIn enables then scales 0→1.
        // PopOut scales 1→0 then disables. One panel visible at a time.

        /// <summary>
        /// Stores the panel's current localScale, zeroes it, and disables the GameObject.
        /// SetActive(false) is used intentionally here — it ensures the Canvas layout system
        /// runs a full rebuild cycle on the next SetActive(true), which is the only reliable
        /// way to get ContentSizeFitter and LayoutGroup to recalculate correctly in World Space.
        /// Also disables all CustomButton colliders via PanelButtonController if present.
        /// Called once in Awake for every managed panel.
        /// </summary>
        private void CacheAndHidePanel(GameObject panel)
        {
            if (panel == null) return;
            /* _panelOriginalScales[panel] = panel.transform.localScale;
             panel.transform.localScale = Vector3.zero;
             panel.SetActive(false);*/
            /* panel.SetActive(false);*/
            panel.GetComponent<CanvasGroup>().alpha = 0;
            // Disable button colliders — enabled only when this panel becomes active.
            panel.GetComponent<PanelButtonController>()?.SetCollidersEnabled(false);
        }

        /// <summary>Caches/hides the uiPanel and handSpeedUIPanel of every contamination zone so each is ready for its own PopIn.</summary>
        private void CacheAndHideContaminationZonePanels()
        {
            if (contaminationManager == null) return;

            foreach (ContaminationZone zone in contaminationManager.zones)
            {
                if (zone.uiPanel != null)
                    CacheAndHidePanel(zone.uiPanel);
                if (zone.handSpeedUIPanel != null)
                    CacheAndHidePanel(zone.handSpeedUIPanel);
            }
        }

        /// <summary>Caches/hides the uiPanel of every first air zone so each is ready for its own PopIn.</summary>
        private void CacheAndHideFirstAirZonePanels()
        {
            if (firstAirManager == null) return;

            foreach (FirstAirZone zone in firstAirManager.zones)
            {
                if (zone.uiPanel != null)
                    CacheAndHidePanel(zone.uiPanel);
            }
        }

        /// <summary>
        /// Returns the scale cached in Awake for the given panel.
        /// Falls back to Vector3.one if the panel was never cached (should not happen in normal use).
        /// </summary>
        private Vector3 GetCachedScale(GameObject panel)
        {
            /*if (panel != null && _panelOriginalScales.TryGetValue(panel, out Vector3 scale))
                return scale;*/
            return Vector3.one; // fallback
        }

        /// <summary>
        /// Forces TMP to measure every text element across all managed panels and fully
        /// populate the font atlas before any panel is shown for the first time.
        /// Without this, TMP loads atlas textures on demand — the first time a panel shows,
        /// TMP may report zero or wrong text dimensions, causing ContentSizeFitter to
        /// calculate wrong child sizes and misalign the layout.
        /// Called once at the end of Awake. All panels are invisible at this point
        /// so ForceMeshUpdate runs with no visual side effects.
        /// </summary>
        private void WarmUpTMPAtlases()
        {
            GameObject[] panels =
            {
                botPromptPanel, prePostPromptPanel, botUIPanel, botProgressPanel,
                helpPanel, assistPanel, botFollowMePanel
            };

            foreach (GameObject panel in panels)
            {
                if (panel == null) continue;
                foreach (TMP_Text text in panel.GetComponentsInChildren<TMP_Text>(includeInactive: true))
                    text.ForceMeshUpdate();
            }

            if (contaminationManager != null)
            {
                foreach (ContaminationZone zone in contaminationManager.zones)
                {
                    if (zone.uiPanel != null)
                        foreach (TMP_Text text in zone.uiPanel.GetComponentsInChildren<TMP_Text>(includeInactive: true))
                            text.ForceMeshUpdate();

                    if (zone.handSpeedUIPanel != null)
                        foreach (TMP_Text text in zone.handSpeedUIPanel.GetComponentsInChildren<TMP_Text>(includeInactive: true))
                            text.ForceMeshUpdate();
                }
            }

            if (firstAirManager != null)
            {
                foreach (FirstAirZone zone in firstAirManager.zones)
                {
                    if (zone.uiPanel != null)
                        foreach (TMP_Text text in zone.uiPanel.GetComponentsInChildren<TMP_Text>(includeInactive: true))
                            text.ForceMeshUpdate();
                }
            }
        }

        /// <summary>
        /// Forces an immediate synchronous layout rebuild on the Canvas inside the given panel.
        /// Called after setting dynamic text and before PopIn so ContentSizeFitter / LayoutGroup
        /// settle at the correct size before the scale tween starts — prevents mid-tween
        /// element repositioning.
        /// The panel root is a plain Transform wrapper so GetComponentInChildren reaches the Canvas.
        /// </summary>
        private void ForceRebuildLayout(GameObject panel)
        {
            if (panel == null) return;
            Canvas canvas = panel.GetComponentInChildren<Canvas>(includeInactive: true);
            if (canvas == null) return;
            RectTransform canvasRect = canvas.GetComponent<RectTransform>();
            if (canvasRect != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(canvasRect);
        }

        /// <summary>
        /// Enables the panel, forces an immediate layout rebuild, then scales from zero
        /// to its cached original scale using the pop-in ease.
        /// SetActive(true) triggers the Canvas layout rebuild cycle that ContentSizeFitter
        /// and LayoutGroup need to recalculate correctly in World Space.
        /// ForceRebuildLayoutImmediate is called immediately after activation so the layout
        /// is fully settled before the scale tween begins — no mid-tween repositioning.
        /// Enables CustomButton colliders only after the tween completes.
        /// Cancels any in-flight tween on the same panel before starting a new one.
        /// </summary>
        private void PopIn(GameObject panel, ref CancellationTokenSource cts)
        {
            if (panel == null) return;
            cts?.Cancel();
            cts = new CancellationTokenSource();

            var cG = panel.GetComponent<CanvasGroup>();
            cG.alpha = 0f;

            // SetActive(true) triggers the full Canvas layout rebuild cycle.
            panel.SetActive(true);

            ForceRebuildLayout(panel);

            _ = cG.DoFade(1f, popDuration, popInEase, cts.Token,
                onComplete: () =>
                {
                    // Enable colliders only once fully scaled up — prevents accidental
                    // button triggers while the panel is still animating in.
                    panel?.GetComponent<PanelButtonController>()?.SetCollidersEnabled(true);
                });
            // Force an immediate synchronous rebuild right after activation so
            // ContentSizeFitter / LayoutGroup settle before the tween starts.

            /*_ = panel.transform.DoScale(targetScale, popDuration, popInEase, cts.Token,
                onComplete: () =>
                {
                    // Enable colliders only once fully scaled up — prevents accidental
                    // button triggers while the panel is still animating in.
                    panel?.GetComponent<PanelButtonController>()?.SetCollidersEnabled(true);
                });*/
        }

        /// <summary>
        /// Scales the panel to zero using the pop-out ease, then disables it.
        /// Disables CustomButton colliders immediately at tween start.
        /// SetActive(false) is called on completion so the next PopIn gets a full
        /// layout rebuild cycle via SetActive(true).
        /// If the panel is already inactive the onComplete callback fires immediately.
        /// </summary>
        private void PopOut(GameObject panel, ref CancellationTokenSource cts, Action onComplete = null)
        {
            if (panel == null) { onComplete?.Invoke(); return; }

            // Already inactive — nothing to animate.
            if (!panel.activeSelf)
            {
                onComplete?.Invoke();
                return;
            }

            // Disable colliders immediately — panel is shrinking, should not accept input.
            panel.GetComponent<PanelButtonController>()?.SetCollidersEnabled(false);

            cts?.Cancel();
            cts = new CancellationTokenSource();

            _ = panel.GetComponent<CanvasGroup>().DoFade(0, 0.1f, popOutEase, cts.Token,

                onComplete: () =>
                {
                    // Disable after tween so next PopIn triggers a full layout rebuild cycle.
                    if (panel != null) panel.SetActive(false);
                    onComplete?.Invoke();
                });
            /* _ = panel.transform.DoScale(Vector3.zero, popDuration, popOutEase, cts.Token,
                 onComplete: () =>
                 {
                     // Disable after tween so next PopIn triggers a full layout rebuild cycle.
                     if (panel != null) panel.SetActive(false);
                     onComplete?.Invoke();
                 });*/
        }

        /// <summary>
        /// Instantly cancels any tween, forces scale to zero, and disables the panel — no animation.
        /// SetActive(false) is restored so the next PopIn gets a full layout rebuild cycle.
        /// Disables CustomButton colliders immediately via PanelButtonController.
        /// Used when panels must disappear instantly (e.g. on teleport or state reset).
        /// </summary>
        private void SnapHidden(GameObject panel, ref CancellationTokenSource cts)
        {
            if (panel == null) return;
            cts?.Cancel();
            cts = new CancellationTokenSource();

            panel.GetComponent<CanvasGroup>().alpha = 0f;
            panel.SetActive(false);
            // Disable colliders immediately — panel is now inactive.

            panel.GetComponent<PanelButtonController>()?.SetCollidersEnabled(false);
        }

        /// <summary>Pops in a panel tracked by instance in a multi-panel CTS map (contamination / speed alert — many can be open at once).</summary>
        private void PopInTracked(GameObject panel, Dictionary<GameObject, CancellationTokenSource> ctsMap)
        {
            CancellationTokenSource cts = ctsMap.TryGetValue(panel, out var existing) ? existing : null;
            PopIn(panel, ref cts);
            ctsMap[panel] = cts;
        }

        /// <summary>Pops out a panel tracked by instance in a multi-panel CTS map (contamination / speed alert — many can be open at once).</summary>
        private void PopOutTracked(GameObject panel, Dictionary<GameObject, CancellationTokenSource> ctsMap)
        {
            CancellationTokenSource cts = ctsMap.TryGetValue(panel, out var existing) ? existing : null;
            PopOut(panel, ref cts);
            ctsMap[panel] = cts;
        }

        /// <summary>Snap-hides a panel tracked by instance in a multi-panel CTS map (contamination / speed alert — many can be open at once).</summary>
        private void SnapHiddenTracked(GameObject panel, Dictionary<GameObject, CancellationTokenSource> ctsMap)
        {
            CancellationTokenSource cts = ctsMap.TryGetValue(panel, out var existing) ? existing : null;
            SnapHidden(panel, ref cts);
            ctsMap[panel] = cts;
        }


        // ═════════════════════════════════════════════
        // STATIC UI PANEL RESOLUTION (Prompt / Clause)
        // ═════════════════════════════════════════════
        // Prompt and Clause are two independent scene panel/text slots.
        // Resolving which one to use for a given segment is just a lookup —
        // no reparenting. Sequencing between two different physical panels
        // is ordinary PopOut/PopIn, exactly like every other panel pair in
        // this file.

        /// <summary>Maps a <see cref="PromptPanelKind"/> to its own CancellationTokenSource by ref.</summary>
        private ref CancellationTokenSource CtsForKind(PromptPanelKind kind)
        {
            switch (kind)
            {
                case PromptPanelKind.StaticPrompt: return ref _staticPromptCts;
                case PromptPanelKind.StaticClause: return ref _staticClauseCts;
                case PromptPanelKind.Clause: return ref _clauseCts;
                default: return ref _promptCts;
            }
        }

        /// <summary>Resolves the panel/text/kind to use for the pre/post ("clause") prompt segments.</summary>
        private (GameObject panel, TMP_Text text, UniText textUni, PromptPanelKind kind) ResolveClauseSlot(SimulationState state)
        {
            if (state.useStaticUIForClauses && state.clauseStaticUIPanel != null
                && TextCompat.HasTarget(state.clauseStaticUITextUni, state.clauseStaticUIText))
                return (state.clauseStaticUIPanel, state.clauseStaticUIText, state.clauseStaticUITextUni, PromptPanelKind.StaticClause);
            if (prePostPromptPanel != null && TextCompat.HasTarget(prePostPromptTextUni, prePostPromptText))
                return (prePostPromptPanel, prePostPromptText, prePostPromptTextUni, PromptPanelKind.Clause);
            return (botPromptPanel, botPromptText, botPromptTextUni, PromptPanelKind.Bot);
        }

        /// <summary>Resolves the panel/text/kind to use for the main prompt segment.</summary>
        private (GameObject panel, TMP_Text text, UniText textUni, PromptPanelKind kind) ResolvePromptSlot(SimulationState state)
        {
            if (state.useStaticUIForPrompt && state.promptStaticUIPanel != null
                && TextCompat.HasTarget(state.promptStaticUITextUni, state.promptStaticUIText))
                return (state.promptStaticUIPanel, state.promptStaticUIText, state.promptStaticUITextUni, PromptPanelKind.StaticPrompt);
            return (botPromptPanel, botPromptText, botPromptTextUni, PromptPanelKind.Bot);
        }

        /// <summary>Removes any pending "Keep Clause Till State End" listener from a previous, possibly-abandoned run.</summary>
        private void ClearClauseKeepUp()
        {
            if (_clauseKeepUpHandler != null && _clauseKeepUpState != null)
                _clauseKeepUpState.onStateComplete.RemoveListener(_clauseKeepUpHandler);
            _clauseKeepUpHandler = null;
            _clauseKeepUpState = null;
        }

        // ═════════════════════════════════════════════
        // PANEL STATE MACHINE — ORCHESTRATION
        // ═════════════════════════════════════════════

        /// <summary>Maps an <see cref="ActivePanel"/> enum value to its corresponding scene GameObject.</summary>
        private GameObject GetPanelObject(ActivePanel panel)
        {
            switch (panel)
            {
                case ActivePanel.Prompt: return botPromptPanel;
                case ActivePanel.BotUI: return botUIPanel;
                case ActivePanel.Help: return helpPanel;
                case ActivePanel.BotFollowMe: return botFollowMePanel;
                case ActivePanel.Assist: return assistPanel;
                default: return null;
            }
        }

        /// <summary>Maps an <see cref="ActivePanel"/> enum value to its corresponding CancellationTokenSource by ref.</summary>
        private ref CancellationTokenSource GetPanelCts(ActivePanel panel)
        {
            switch (panel)
            {
                case ActivePanel.Prompt: return ref _promptCts;
                case ActivePanel.BotUI: return ref _botUICts;
                case ActivePanel.Help: return ref _helpCts;
                case ActivePanel.BotFollowMe: return ref _botFollowMeCts;
                case ActivePanel.Assist: return ref _assistCts;
                default: return ref _promptCts;
            }
        }

        /// <summary>
        /// Pops out the currently active panel and stashes it so it can be restored later.
        /// Also pauses the simulation if it was running, so it resumes when the stash is restored.
        /// Does nothing if <paramref name="newPanel"/> is already the active panel.
        /// </summary>
        private void InterruptCurrentPanel(ActivePanel newPanel)
        {
            if (_currentPanel != ActivePanel.None && _currentPanel != newPanel)
            {
                _stashedPanel = _currentPanel;

                ref var cts = ref GetPanelCts(_currentPanel);
                PopOut(GetPanelObject(_currentPanel), ref cts);

                if (SimulationManager.Instance != null && !SimulationManager.Instance.isPaused)
                {
                    SimulationManager.Instance.PauseSimulation();
                    _wasSimPausedByPanel = true;
                }
            }

            _currentPanel = newPanel;
        }

        /// <summary>
        /// Pops the stashed panel back in after the interrupting panel has been dismissed.
        /// Also resumes the simulation if this manager was the one that paused it.
        /// </summary>
        private void RestoreStashedPanel()
        {
            _currentPanel = ActivePanel.None;

            if (_stashedPanel != ActivePanel.None)
            {
                ActivePanel toRestore = _stashedPanel;
                _stashedPanel = ActivePanel.None;

                ref var cts = ref GetPanelCts(toRestore);
                PopIn(GetPanelObject(toRestore), ref cts);
                _currentPanel = toRestore;
            }

            if (_wasSimPausedByPanel)
            {
                _wasSimPausedByPanel = false;
                SimulationManager.Instance?.ResumeSimulation();
            }
        }

        /// <summary>
        /// Instantly hides both the active panel and any stashed panel with no animation.
        /// Also resumes the simulation if it was paused by a panel interrupt.
        /// </summary>
        private void DismissAllPanelsImmediate()
        {
            if (_currentPanel != ActivePanel.None)
            {
                ref var cts = ref GetPanelCts(_currentPanel);
                SnapHidden(GetPanelObject(_currentPanel), ref cts);
            }

            if (_stashedPanel != ActivePanel.None)
            {
                ref var cts2 = ref GetPanelCts(_stashedPanel);
                SnapHidden(GetPanelObject(_stashedPanel), ref cts2);
            }

            _currentPanel = ActivePanel.None;
            _stashedPanel = ActivePanel.None;

            if (_wasSimPausedByPanel)
            {
                _wasSimPausedByPanel = false;
                SimulationManager.Instance?.ResumeSimulation();
            }
        }

        /// <summary>
        /// Nuclear reset: stops all prompt coroutines, snap-hides all managed panels,
        /// clears all pending callbacks, and resets panel tracking state.
        /// Called on teleport start and at the beginning of every new state prompt.
        /// </summary>
        private void HideAllImmediate()
        {
            if (_promptCoroutine != null)
            {
                StopCoroutine(_promptCoroutine);
                _promptCoroutine = null;
            }

            SnapHidden(_activePromptPanelObj != null ? _activePromptPanelObj : botPromptPanel, ref CtsForKind(_activePromptPanelKind));
            SnapHidden(botUIPanel, ref _botUICts);
            SnapHidden(botFollowMePanel, ref _botFollowMeCts);

            _activePromptPanelKind = PromptPanelKind.Bot;
            _activePromptPanelObj = null;
            ClearClauseKeepUp();

            // Contamination and speedAlert are independent of the ActivePanel flow — reset them separately.
            // Any number of zone panels can be open at once, so snap-hide every tracked one.
            foreach (GameObject panel in _activeContaminationPanels) SnapHiddenTracked(panel, _contaminationCts);
            _activeContaminationPanels.Clear();

            foreach (GameObject panel in _activeSpeedAlertPanels) SnapHiddenTracked(panel, _speedAlertCts);
            _activeSpeedAlertPanels.Clear();

            foreach (GameObject panel in _activeFirstAirPanels) SnapHiddenTracked(panel, _firstAirCts);
            _activeFirstAirPanels.Clear();

            _promptActive = false;
            _pendingUIContent = null;
            _pendingOnComplete = null;
            _uiOnComplete = null;
            _currentPanel = ActivePanel.None;
            _stashedPanel = ActivePanel.None;

            DisableBotButton();
        }


        // ═════════════════════════════════════════════
        // STATE ENTRY POINT
        // ═════════════════════════════════════════════

        /// <summary>
        /// Main entry point called by the SimulationManager when a new state begins.
        /// Extracts prompt data from the state and starts the prompt flow.
        /// </summary>
        public async Task EvaluateStateInteractions(SimulationState state)
        {
            if (state == null) return;

            if (state.teleportOnStart)
            {
                await WaitForActionAsync();
            }

            StartPrompt(state);

        }

        private static Task WaitForActionAsync()
        {
            var tcs = new TaskCompletionSource<bool>();

            void Handler()
            {
                TeleportManager.TeleportCompleted -= Handler; // Unsubscribe after first call
                tcs.SetResult(true);
            }

            TeleportManager.TeleportCompleted += Handler;
            return tcs.Task;
        }

        // ═════════════════════════════════════════════
        // PROMPT
        // ═════════════════════════════════════════════

        #region Prompt

        /// <summary>
        /// Resets all panels, shows the prompt panel with the given text, plays the audio clip,
        /// then auto-hides after the clip duration (or immediately if no clip).
        /// </summary>
        private void StartPrompt(SimulationState state)
        {
            HideAllImmediate();

            _promptActive = true;
            _currentPanel = ActivePanel.Prompt;

            _promptCoroutine = StartCoroutine(RunPromptSequence(state));
        }
        /// <summary>
        /// Plays the pre-prompt (if enabled), main prompt, and post-prompt (if enabled) in order,
        /// each waiting for its own audio clip to finish before moving on. Mirrors the original
        /// assessment-mode gating in StartPrompt: in Assessment mode, prompts are only shown
        /// (with visuals/audio) for prompt-only or single-UI-interaction states.
        /// </summary>
        private IEnumerator RunPromptSequence(SimulationState state)
        {
            bool isAssessment = SimulationManager.Instance != null &&
                                SimulationManager.Instance.simulationMode == SimulationMode.Assessment;

            bool playFullSequence = true;

            if (isAssessment)
            {
                var simState = SimulationManager.Instance?.currentState;
                bool onlyUI = simState != null && simState.listOfInteractions != null &&
                              simState.listOfInteractions.Count == 1 &&
                              simState.listOfInteractions[0] is UIInteraction;

                bool isPromptOnly = state.MoveToNextStepAfterAudio;

                playFullSequence = onlyUI || isPromptOnly;
            }

            if (playFullSequence)
            {
                bool isFirst = true;
                var pre = ResolveClauseSlot(state);
                var main = ResolvePromptSlot(state);
                var post = ResolveClauseSlot(state);

                if (state.hasPrePrompt)
                {
                    _activePromptPanelKind = pre.kind;
                    _activePromptPanelObj = pre.panel;

                    yield return ShowPromptAndWait(pre.panel, pre.text, pre.textUni, state.prePromptText, state.prePromptAudio, isFirst,
                        () => PopIn(pre.panel, ref CtsForKind(pre.kind)));
                    isFirst = false;

                    // "Disappear When Prompt Plays" — close the clause as a distinct beat
                    // before the main prompt pops back in fresh, even if it's the same panel.
                    bool sameAsMain = pre.panel == main.panel;
                    if (!sameAsMain || state.clauseLifetime == SimulationState.ClauseLifetime.DisappearWhenPromptPlays)
                    {
                        PopOut(pre.panel, ref CtsForKind(pre.kind));
                        isFirst = true;
                    }
                }

                _activePromptPanelKind = main.kind;
                _activePromptPanelObj = main.panel;

                yield return ShowPromptAndWait(main.panel, main.text, main.textUni, state.promptText, state.promptAudio, isFirst,
                    () => PopIn(main.panel, ref CtsForKind(main.kind)));
                isFirst = false;

                if (state.hasPostPrompt)
                {
                    if (main.panel != post.panel)
                    {
                        PopOut(main.panel, ref CtsForKind(main.kind));
                        isFirst = true;
                    }

                    _activePromptPanelKind = post.kind;
                    _activePromptPanelObj = post.panel;

                    yield return ShowPromptAndWait(post.panel, post.text, post.textUni, state.postPromptText, state.postPromptAudio, isFirst,
                        () => PopIn(post.panel, ref CtsForKind(post.kind)));
                }
            }
            else
            {
                // In Assessment mode, if we are skipping prompt playback, yield one frame
                // to let the state complete its initialization and subscribe to PromptCompleted.
                yield return null;
            }

            PromptCompleted?.Invoke();

            _promptActive = false;

            GameObject finalPanel = _activePromptPanelObj != null ? _activePromptPanelObj : botPromptPanel;
            PromptPanelKind finalKind = _activePromptPanelKind;

            // "Keep Clause Till State End" — leave the panel visible and defer closing it
            // until this state actually completes, instead of right after the prompt
            // sequence ends. The existing interrupt/stash machinery already handles BotUI
            // etc. popping in over it and restoring it afterward for free, since
            // _currentPanel stays ActivePanel.Prompt in the meantime.
            bool keepClauseUp = (state.hasPrePrompt || state.hasPostPrompt)
                                 && state.clauseLifetime == SimulationState.ClauseLifetime.KeepClauseTillStateEnd;

            if (keepClauseUp)
            {
                ClearClauseKeepUp(); // safety: drop any stale listener from an earlier, abandoned run

                _clauseKeepUpState = state;
                _clauseKeepUpHandler = () =>
                {
                    ClearClauseKeepUp();
                    PopOut(finalPanel, ref CtsForKind(finalKind));
                    _activePromptPanelKind = PromptPanelKind.Bot;
                    _activePromptPanelObj = botPromptPanel;
                    if (_currentPanel == ActivePanel.Prompt)
                        _currentPanel = ActivePanel.None;
                };
                state.onStateComplete.AddListener(_clauseKeepUpHandler);
            }
            else
            {
                PopOut(finalPanel, ref CtsForKind(finalKind));
                _activePromptPanelKind = PromptPanelKind.Bot;
                _activePromptPanelObj = botPromptPanel;

                if (_currentPanel == ActivePanel.Prompt)
                    _currentPanel = ActivePanel.None;
            }

            _promptCoroutine = null;

            if (state.MoveToNextStepAfterAudio)
            {
                // Route through the real completion path (sets stateIsDone, invokes onStateComplete
                // exactly once, closes out the assessment record, then advances via NotifyStateComplete)
                // instead of hand-duplicating it — that duplicate used to call
                // AssessmentManager.Instance.CloseState() with no null-check, so a null instance in
                // Assessment mode would throw, kill this coroutine, and leave the step stuck without
                // ever reaching MoveToNextState().
                state.CompleteState();
                yield break;
            }

            if (_pendingUIContent != null)
            {
                string content = _pendingUIContent;
                UnityAction callback = _pendingOnComplete;
                _pendingUIContent = null;
                _pendingOnComplete = null;
                ShowBotUI(content, callback);
            }
        }


        /// <summary>
        /// Shows a single prompt segment on <paramref name="panel"/>/<paramref name="textComp"/>:
        /// sets text, optionally pops the panel in (only needed the first time a given panel
        /// becomes visible in a sequence — later segments on the SAME panel just swap the text
        /// while it stays visible), plays the given audio clip, and waits for it to finish (or
        /// one frame if there is no audio). The actual PopIn call is supplied by the caller as a
        /// delegate since this method is an iterator and can't take a `ref CancellationTokenSource`
        /// parameter directly.
        /// </summary>
        private IEnumerator ShowPromptAndWait(GameObject panel, TMP_Text textComp, UniText textCompUni, string text, AudioClip audio, bool popInPanel, Action popIn)
        {
            TextCompat.SetText(textCompUni, textComp, text);
            ForceRebuildLayout(panel);

            if (popInPanel)
                popIn();

            botController?.Summon();
            botController?.OnPromptShown();

            var audioSrc = SoundManager.Instance.promptSource;

            if (audio != null && audioSrc != null)
            {
                audioSrc.Stop();
                audioSrc.clip = audio;
                audioSrc.clip.LoadAudioData();
                audioSrc.Play();

                // Wait until the audio source actually starts playing (with a 0.5s timeout)
                float timeout = 0.5f;
                while (!audioSrc.isPlaying && timeout > 0f)
                {
                    timeout -= Time.unscaledDeltaTime;
                    yield return null;
                }

                // Poll until the audio source is no longer playing
                while (audioSrc.isPlaying)
                {
                    yield return null;
                }
            }
            else
            {
                yield return null;
            }
        }


        public void ShowHintPrompt(SimulationState state)
        {
            if (state == null) return;

            _promptActive = true;

            TextCompat.SetText(botPromptTextUni, botPromptText, state.promptText);
            ForceRebuildLayout(botPromptPanel);
            PopIn(botPromptPanel, ref _promptCts);

            botController?.Summon();
            botController?.OnPromptShown();

            float duration = 0f;
            var audioSrc = SoundManager.Instance?.promptSource;
            if (state.promptAudio != null && audioSrc != null)
            {
                audioSrc.Stop();
                audioSrc.clip = state.promptAudio;
                audioSrc.clip.LoadAudioData();
                audioSrc.Play();
                duration = state.promptAudio.length;
            }

            StartCoroutine(HidePromptAfter(duration, false));
        }

        /// <summary>
        /// Coroutine that waits for the prompt audio to finish, then hides the prompt panel.
        /// If <paramref name="moveToNextState"/> is true, advances the simulation automatically.
        /// Otherwise flushes any pending BotUI that was queued while the prompt was active.
        /// </summary>
        private IEnumerator HidePromptAfter(float delay, bool moveToNextState)
        {
            if (delay > 0f) yield return new WaitForSeconds(delay);
            else yield return null;

            PromptCompleted?.Invoke();

            _promptActive = false;
            PopOut(botPromptPanel, ref _promptCts);
            _promptCoroutine = null;

            if (_currentPanel == ActivePanel.Prompt)
                _currentPanel = ActivePanel.None;

            if (moveToNextState)
            {
                if (SimulationManager.Instance.simulationMode == SimulationMode.Assessment)
                {
                    AssessmentManager.Instance.CloseState();
                }
                SimulationManager.Instance?.currentState?.onStateComplete?.Invoke();
                SimulationManager.Instance?.MoveToNextState();
                yield break;
            }

            if (_pendingUIContent != null)
            {
                string content = _pendingUIContent;
                UnityAction callback = _pendingOnComplete;
                _pendingUIContent = null;
                _pendingOnComplete = null;
                ShowBotUI(content, callback);
            }
            SimulationManager.Instance.TogglePause();
            AssessmentManager.Instance.EnableVisualsAfterHint(SimulationManager.Instance.currentState.currentInteraction);
        }

        /// <summary>
        /// Called when BotSummonedWithKey fires (X button press).
        /// Stashes whatever panel is currently active (if any) via InterruptCurrentPanel,
        /// then pops in the prompt panel as the new active panel.
        /// Normal state-start prompts (StartPrompt) do NOT go through this path —
        /// they call PopIn directly and are unaffected.
        /// </summary>
        private void OnBotSummonedWithKey()
        {
            // InterruptCurrentPanel pops out and stashes whatever is currently active,
            // then sets _currentPanel = Prompt. If nothing is active it is a no-op on the stash.
            InterruptCurrentPanel(ActivePanel.Prompt);
            PopIn(botPromptPanel, ref _promptCts);
        }

        /// <summary>
        /// Called when BotDismissedWithKey fires (X button press).
        /// Pops out the prompt panel, then restores whatever panel was stashed on summon.
        /// If nothing was stashed, RestoreStashedPanel is a no-op.
        /// </summary>
        private void OnBotDismissedWithKey()
        {
            PopOut(botPromptPanel, ref _promptCts);

            if (_currentPanel == ActivePanel.Prompt)
            {
                _currentPanel = ActivePanel.None;
                // Restores the panel that was active before X was pressed.
                // If nothing was stashed this does nothing.
                RestoreStashedPanel();
            }
        }

        #endregion

        // ─────────────────────────────────────────────
        // LEGACY PROMPT API
        // ─────────────────────────────────────────────

        #region Prompt API

        public void ShowPrompt(string message)
        {
            prompt?.SetPrompt(message);
            botController?.OnPromptShown();
        }

        public void HidePrompt() => prompt?.HidePrompt();

        #endregion


        // ═════════════════════════════════════════════
        // BOT UI
        // ═════════════════════════════════════════════

        #region BotUI

        /// <summary>
        /// Public entry point for showing the BotUI panel.
        /// If a prompt is still active the request is queued and flushed automatically once the prompt closes.
        /// </summary>
        public void TriggerBotUI(string text, UnityAction onComplete)
        {
            if (botController == null) return;

            if (_promptActive)
            {
                _pendingUIContent = text;
                _pendingOnComplete = onComplete;
                return;
            }

            ShowBotUI(text, onComplete);
        }

        /// <summary>Interrupts any current panel, pops in the BotUI panel, and wires the dismiss button.</summary>
        private void ShowBotUI(string text, UnityAction onComplete)
        {
            InterruptCurrentPanel(ActivePanel.BotUI);

            _uiOnComplete = onComplete;

            if (TextCompat.HasTarget(botUITextUni, botUIText))
            {
                botUIPanel.SetActive(true);
                TextCompat.SetText(botUITextUni, botUIText, text);
                botUIText?.ForceMeshUpdate();
                Canvas.ForceUpdateCanvases();
                botUIPanel.SetActive(false);

            }
            // Force layout rebuild so ContentSizeFitter settles before the tween starts.
            ForceRebuildLayout(botUIPanel);
            PopIn(botUIPanel, ref _botUICts);
            _currentPanel = ActivePanel.BotUI;

            WireBotButton(() =>
            {
                HideBotUI();
                _uiOnComplete?.Invoke();
                _uiOnComplete = null;
            });

            botController?.Summon();
            botController?.OnUIOpen();
        }

        /// <summary>Pops out the BotUI panel and restores any previously stashed panel.</summary>
        public void HideBotUI()
        {
            PopOut(botUIPanel, ref _botUICts);
            DisableBotButton();
            botController?.OnUIClose();
            botController?.Dismiss();

            if (_currentPanel == ActivePanel.BotUI)
            {
                _currentPanel = ActivePanel.None;
                RestoreStashedPanel();
            }
        }

        #endregion

        // ═════════════════════════════════════════════
        // Simulation Progress
        // ═════════════════════════════════════════════
        // Progress values are always written to the Slider/Text components
        // even when the panel is disabled. Unity stores the values in the
        // component — when the panel is enabled + popped in, it shows
        // the correct value immediately with no flash of stale data.

        #region Simulation Progress

        public void ShowBotProgress(float progress)
        {
            _progressTarget = Mathf.Clamp01(progress);
            int stepIndex = GetCurrentStepIndex();
            if (progressTrackers != null)
                foreach (var t in progressTrackers)
                    if (t != null) t.UpdateValues(_progressTarget, stepIndex);

            if (_progressCoroutine != null) StopCoroutine(_progressCoroutine);
            _progressCoroutine = StartCoroutine(AnimateProgress(_progressTarget, stepIndex));
        }

        public void UpdateProgressSilent(float progress)
        {
            _progressTarget = Mathf.Clamp01(progress);
            int stepIndex = GetCurrentStepIndex();
            UpdateProgressInternal(_progressTarget, stepIndex);
        }

        public void SetProgressFromSimulation()
        {
            if (SimulationManager.Instance == null) return;

            var states = SimulationManager.Instance.states;
            var current = SimulationManager.Instance.currentState;
            if (states == null || states.Count == 0 || current == null) return;

            int index = states.IndexOf(current.gameObject);
            if (index < 0) return;

            float progress = (float)(index + 1) / states.Count;
            ShowBotProgress(progress);
        }

        public void HideBotProgress()
        {
            if (_progressCoroutine != null)
            {
                StopCoroutine(_progressCoroutine);
                _progressCoroutine = null;
            }
        }

        private int GetCurrentStepIndex()
        {
            if (SimulationManager.Instance == null) return 1;
            var states = SimulationManager.Instance.states;
            var current = SimulationManager.Instance.currentState;
            if (states == null || states.Count == 0 || current == null) return 1;
            int index = states.IndexOf(current.gameObject);
            return index < 0 ? 1 : index + 1;
        }

        private void UpdateProgressInternal(float progress, int stepIndex)
        {
            if (progressTrackers == null) return;
            foreach (var t in progressTrackers)
                if (t != null) t.UpdateValues(progress, stepIndex);
        }

        private IEnumerator AnimateProgress(float target, int stepIndex)
        {
            if (progressTrackers == null || progressTrackers.Length == 0) yield break;

            float[] starts = new float[progressTrackers.Length];
            for (int i = 0; i < progressTrackers.Length; i++)
                starts[i] = progressTrackers[i] != null ? progressTrackers[i].GetSliderValue() : 0f;

            float elapsed = 0f;
            while (elapsed < progressAnimDuration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / progressAnimDuration);
                float eased = 1f - (1f - t) * (1f - t);
                for (int i = 0; i < progressTrackers.Length; i++)
                    if (progressTrackers[i] != null)
                        progressTrackers[i].SetSliderValue(Mathf.Lerp(starts[i], target, eased));
                yield return null;
            }

            // Snap to final value and re-apply text (guards against first-step text being stale)
            for (int i = 0; i < progressTrackers.Length; i++)
                if (progressTrackers[i] != null)
                    progressTrackers[i].SnapToFinal(target);

            _progressCoroutine = null;
        }

        #endregion


        // ═════════════════════════════════════════════
        // BOT FOLLOW ME PANEL
        // ═════════════════════════════════════════════

        #region BotFollowMe

        /// <summary>
        /// Pops in the follow-me panel through the full interrupt/stash flow.
        /// Intended to be called when the bot enters guide mode to inform the user to follow it.
        /// </summary>
        public void ShowBotFollowMe()
        {
            InterruptCurrentPanel(ActivePanel.BotFollowMe);
            PopIn(botFollowMePanel, ref _botFollowMeCts);
            _currentPanel = ActivePanel.BotFollowMe;
        }

        /// <summary>Pops out the follow-me panel and restores any previously stashed panel.</summary>
        public void HideBotFollowMe()
        {
            PopOut(botFollowMePanel, ref _botFollowMeCts);

            if (_currentPanel == ActivePanel.BotFollowMe)
            {
                _currentPanel = ActivePanel.None;
                RestoreStashedPanel();
            }
        }

        #endregion


        // ═════════════════════════════════════════════
        // HELP PANEL
        // ═════════════════════════════════════════════

        #region Help Panel

        private void AddListnersForHelpButtonPanel()
        {
            if (helpButton == null)
            {
                return;
            }
            if (helpCloseButton == null)
            {
                return;
            }

            helpButton.OnButtonClicked.AddListener(() =>
            {
                // Pop out the assist panel directly — do NOT use InterruptCurrentPanel here
                // because we don't want to overwrite _stashedPanel. The original panel
                // (e.g. BotUI) is already stashed from when Y was pressed; we preserve that.
                PopOut(assistPanel, ref _assistCts);
                _currentPanel = ActivePanel.None;

                // Pop in help as the new active panel. No stash involved — assist is gone.
                PopIn(helpPanel, ref _helpCts);
                _isHelpVisible = true;
                _currentPanel = ActivePanel.Help;
                botController.OnHelpCalled();
                HelpEnabled?.Invoke();
            });

            helpCloseButton.OnButtonClicked.AddListener(() =>
            {
                // Pop out help, then pop assist back in directly.
                // Again, do NOT call RestoreStashedPanel — the original stashed panel
                // (e.g. BotUI) must stay stashed until Y is pressed or guide is chosen.
                PopOut(helpPanel, ref _helpCts);
                _isHelpVisible = false;

                _currentPanel = ActivePanel.None;

                PopIn(assistPanel, ref _assistCts);
                _currentPanel = ActivePanel.Assist;
                HelpDisabled?.Invoke();
            });
        }

        public void ShowHelp()
        {
            if (helpPanel == null) return;

            InterruptCurrentPanel(ActivePanel.Help);
            PopIn(helpPanel, ref _helpCts);
            _isHelpVisible = true;
            _currentPanel = ActivePanel.Help;
            botController.OnHelpCalled();
        }

        public void HideHelp()
        {
            if (helpPanel == null) return;

            PopOut(helpPanel, ref _helpCts);
            _isHelpVisible = false;

            if (_currentPanel == ActivePanel.Help)
            {
                _currentPanel = ActivePanel.None;
                RestoreStashedPanel();
            }
        }

        public void ToggleHelp() { if (_isHelpVisible) HideHelp(); else ShowHelp(); }

        #endregion


        // ═════════════════════════════════════════════
        // ASSIST PANEL — Y BUTTON
        // ═════════════════════════════════════════════

        #region Assist Panel

        /// <summary>
        /// Y button pressed.
        /// - If assist or help is currently visible: close whichever is showing,
        ///   restore any stashed panel, and fully reset the assist flow.
        /// - Otherwise: stash the current active panel (if any) and pop in the assist panel.
        /// </summary>
        private void OnYButtonPressed(InputAction.CallbackContext context)
        {
            if (_currentPanel == ActivePanel.Assist)
            {
                HelpDisabled?.Invoke();
                // Assist is open — close it and restore the stashed panel (if any).
                PopOut(assistPanel, ref _assistCts);
                _currentPanel = ActivePanel.None;
                RestoreStashedPanel();

            }
            else if (_currentPanel == ActivePanel.Help)
            {
                // Help is open (reached from assist) — close help and restore the original
                // stashed panel. This fully resets the assist flow so Y opens fresh next time.
                PopOut(helpPanel, ref _helpCts);
                _isHelpVisible = false;
                _currentPanel = ActivePanel.None;
                RestoreStashedPanel();
                HelpDisabled?.Invoke();
            }
            else
            {
                HelpDisabled?.Invoke();
                // No assist/help visible — stash whatever is currently active and open assist.
                InterruptCurrentPanel(ActivePanel.Assist);
                PopIn(assistPanel, ref _assistCts);

            }
        }

        /// <summary>
        /// Called by the Guide button inside the assist panel.
        /// Closes the assist panel and restores the stashed panel (if any).
        /// No assist/help re-opening — guidance takes over.
        /// </summary>
        public void OnGuidButtonPressedInAssistPanel()
        {
            if (_currentPanel == ActivePanel.Assist)
            {
                PopOut(assistPanel, ref _assistCts);
                _currentPanel = ActivePanel.None;
                RestoreStashedPanel();
            }
        }

        private async void PopInAssistPanel()
        {
            /*if (assistPanel == null) return;
            isAssistantPanelVisible = true;

            _assistCts?.Cancel();
            _assistCts = new CancellationTokenSource();

            Vector3 targetScale = GetCachedScale(assistPanel);
            assistPanel.transform.localScale = Vector3.zero;

            // SetActive(true) triggers the full Canvas layout rebuild cycle.
            assistPanel.SetActive(true);

            // Force immediate rebuild so layout settles before tween starts.
            ForceRebuildLayout(assistPanel);

            await assistPanel.transform.DoScale(targetScale, popDuration, popInEase, _assistCts.Token);

            // Enable colliders only after fully scaled up.
            assistPanel.GetComponent<PanelButtonController>()?.SetCollidersEnabled(true);*/

            if (assistPanel == null) return;
            isAssistantPanelVisible = true;

            _assistCts?.Cancel();
            _assistCts = new CancellationTokenSource();

            var cG = assistPanel.GetComponent<CanvasGroup>();
            cG.alpha = 0f;

            // SetActive(true) triggers the full Canvas layout rebuild cycle.
            assistPanel.SetActive(true);

            // Force immediate rebuild so layout settles before tween starts.
            ForceRebuildLayout(assistPanel);

            await cG.DoFade(1f, popDuration, popInEase, _assistCts.Token);

            // Enable colliders only after fully scaled up.
            assistPanel.GetComponent<PanelButtonController>()?.SetCollidersEnabled(true);
        }

        private async void PopOutAssistPanel()
        {
            if (assistPanel == null) return;
            if (!assistPanel.activeSelf) { isAssistantPanelVisible = false; return; }

            isAssistantPanelVisible = false;
            // Disable colliders immediately — panel is shrinking.
            assistPanel.GetComponent<PanelButtonController>()?.SetCollidersEnabled(false);

            _assistCts?.Cancel();
            _assistCts = new CancellationTokenSource();

            await assistPanel.GetComponent<CanvasGroup>().DoFade(0, 0.1f, popOutEase, _assistCts.Token,
                onComplete: () =>
                {
                    // Disable after tween so next PopIn gets a full layout rebuild cycle.
                    if (assistPanel != null) assistPanel.SetActive(false);
                });
        }

        /// <summary>For external callers that need to set visibility directly.</summary>
        private void ToggleVisiblityOfAssistantPanel(bool isVisible)
        {
            if (isVisible)
                PopInAssistPanel();
            else
                PopOutAssistPanel();
        }

        #endregion


        // ═════════════════════════════════════════════
        // PATHFINDING
        // ═════════════════════════════════════════════

        #region Pathfinding

        private void AddListnersPathfinding()
        {
            pathfindingButton.OnButtonClicked.AddListener(() =>
            {
                StartCoroutine(WaitAndStartGuidance());
            });

            BotGuideBehaviour.GuideFinished += HideBotFollowMe;
        }

        private void RemoveListnersPathfinding()
        {
            pathfindingButton.OnButtonClicked.RemoveListener(() =>
            {
                StartCoroutine(WaitAndStartGuidance());
            });

            BotGuideBehaviour.GuideFinished -= HideBotFollowMe;
        }

        private IEnumerator WaitAndStartGuidance()
        {
            // ── GUARD: only supported interaction types can be guided ──
            if (!CanGuide())
            {
                PopOut(assistPanel, ref _assistCts);        // Dismiss assist panel before showing feedback
                _currentPanel = ActivePanel.None;

                ShowGuideUnavailablePanel();
                yield break;                 // Bot stays in Companion mode
            }

            // Close help first if open so bot exits HelpCalled state
            if (_isHelpVisible)
                HideHelp();

            // Dismiss all managed panels immediately — clean slate before guidance
            DismissAllPanelsImmediate();

            // Pop out the assist panel too
            PopOutAssistPanel();

            yield return new WaitForSeconds(1f);

            botController.SwitchMode(BotController.BotMode.Guide);

            ShowBotFollowMe();

            StartGuidance();
        }

        /// <summary>
        /// Returns true only when the current interaction is a type that supports guidance
        /// (GrabInteraction, DetectInteraction, or GazeInteraction).
        /// </summary>
        private bool CanGuide()
        {
            Interactions currentInteraction = SimulationManager.Instance?.currentState?.currentInteraction;
            return currentInteraction is GrabInteraction
                || currentInteraction is DetectInteraction
                || currentInteraction is GazeInteraction;
        }

        /// <summary>
        /// Pops in the guide-unavailable panel independently (no stash/interrupt),
        /// then auto-dismisses it after <see cref="guideUnavailableDuration"/> seconds.
        /// Re-triggering while already visible cancels the previous auto-dismiss timer
        /// and restarts it cleanly.
        /// </summary>
        private void ShowGuideUnavailablePanel()
        {
            if (guideUnavailablePanel == null) return;

            // Cancel any in-flight auto-dismiss
            if (_guideUnavailableCoroutine != null)
            {
                StopCoroutine(_guideUnavailableCoroutine);
                _guideUnavailableCoroutine = null;
            }

            PopIn(guideUnavailablePanel, ref _guideUnavailableCts);
            _guideUnavailableCoroutine = StartCoroutine(HideGuideUnavailableAfter(guideUnavailableDuration));
        }

        private IEnumerator HideGuideUnavailableAfter(float delay)
        {
            yield return new WaitForSeconds(delay);
            PopOut(guideUnavailablePanel, ref _guideUnavailableCts);
            _guideUnavailableCoroutine = null;
            RestoreStashedPanel();
        }

        private void StartGuidance()
        {
            Interactions currentInteraction = SimulationManager.Instance.currentState.currentInteraction;

            if (currentInteraction is GrabInteraction)
            {
                if (GrabManager.Instance.leftPinchDetector.currentGrab == null &&
                    GrabManager.Instance.rightPinchDetector.currentGrab == null)
                {
                    botController.guideBehaviour.GuideTo(currentInteraction.transform.position);
                }
            }
            else if (currentInteraction is DetectInteraction)
            {
                if (currentInteraction.GetComponent<DetectInteraction>().ObjectsToBeDetectedList[0]
                    .GetComponent<GrabInteraction>() != null)
                {
                    if (GrabManager.Instance.leftPinchDetector.currentGrab == null &&
                        GrabManager.Instance.rightPinchDetector.currentGrab == null)
                    {
                        botController.guideBehaviour.GuideTo(currentInteraction.GetComponent<DetectInteraction>().
                            ObjectsToBeDetectedList[0].transform.position);
                    }
                    else
                    {
                        botController.guideBehaviour.GuideTo(currentInteraction.transform.position);
                    }
                }
                else
                {
                    botController.guideBehaviour.GuideTo(currentInteraction.transform.position);
                }
            }
            else if (currentInteraction is GazeInteraction)
            {
                botController.guideBehaviour.GuideTo(currentInteraction.transform.position);
            }
        }

        #endregion


        // ═════════════════════════════════════════════
        // BOT DISCARD
        // ═════════════════════════════════════════════

        #region BotDiscard

        /// <summary>Enqueues an object to be dissolved-out, teleported to destination, and dissolved-in.</summary>
        public void BotDiscard(GameObject obj, Transform destination)
        {
            if (obj == null) return;
            _discardQueue.Enqueue((obj, destination));

            if (!_discardRunning)
                StartCoroutine(RunDiscardQueue());
        }

        private IEnumerator RunDiscardQueue()
        {
            _discardRunning = true;
            botController?.Summon();
            botController?.OnDiscard();

            while (_discardQueue.Count > 0)
            {
                var (obj, dest) = _discardQueue.Dequeue();
                if (obj == null) continue;
                if (dest != null)
                    yield return StartCoroutine(MoveObjectToDestination(obj, dest));
            }

            botController?.OnDiscardComplete();
            _discardRunning = false;
        }

        private IEnumerator MoveObjectToDestination(GameObject obj, Transform dest)
        {
            if (obj == null) yield break;

            var rb = obj.GetComponent<Rigidbody>();
            if (rb != null) rb.isKinematic = true;

            var dissolve = obj.GetComponent<DissolveController>();
            if (dissolve == null) dissolve = obj.AddComponent<DissolveController>();

            bool dissolvedOut = false;
            dissolve.DissolveOut(discardMoveDuration * 0.5f, () => dissolvedOut = true);
            yield return new WaitUntil(() => dissolvedOut || obj == null);
            if (obj == null) yield break;

            obj.transform.position = dest.position;
            obj.transform.rotation = dest.rotation;

            bool dissolvedIn = false;
            dissolve.DissolveIn(discardMoveDuration * 0.5f, () => dissolvedIn = true);
            yield return new WaitUntil(() => dissolvedIn || obj == null);
            if (obj == null) yield break;

            if (rb != null) rb.isKinematic = false;
        }

        #endregion


        // ═════════════════════════════════════════════
        // BUTTON HELPERS
        // ═════════════════════════════════════════════

        /// <summary>Clears all listeners on the bot button, wires the given action, and enables the button.</summary>
        private void WireBotButton(UnityAction action)
        {
            if (botButton == null) return;
            botButton.OnButtonClicked.RemoveAllListeners();
            botButton.OnButtonClicked.AddListener(action);
            //botButton.gameObject.SetActive(true);
        }

        /// <summary>Clears all listeners on the bot button and disables it.</summary>
        private void DisableBotButton()
        {
            if (botButton == null) return;
            botButton.OnButtonClicked.RemoveAllListeners();
            //botButton.gameObject.SetActive(false);
        }


        // ═════════════════════════════════════════════
        // SETTINGS
        // ═════════════════════════════════════════════

        public void OpenSettings()
        {
            if (settingsPanel != null)
            {
                settingsPanel.SetActive(true);
                _isSettingsVisible = true;
            }
        }

        public void CloseSettings()
        {
            if (settingsPanel != null)
            {
                settingsPanel.SetActive(false);
                _isSettingsVisible = false;
            }
        }


        // ═════════════════════════════════════════════
        // TELEPORT EVENTS
        // ═════════════════════════════════════════════

        #region TeleportEventFunctions

        private void OnTeleoprtStarted() => HideAllImmediate();
        private void OnTeleportCompleted()
        { }


        #endregion


        // ═════════════════════════════════════════════
        // CONTAMINATION
        // ═════════════════════════════════════════════

        #region Contamination

        private void AddListnersContamination()
        {
            contaminationManager.onContaminationTriggered.AddListener(OnContaminationTriggered);
            contaminationManager.onContaminationResolved.AddListener(OnContaminationResolved);
        }

        private void RemoveListnersContamination()
        {
            contaminationManager.onContaminationTriggered.RemoveListener(OnContaminationTriggered);
            contaminationManager.onContaminationResolved.RemoveListener(OnContaminationResolved);
        }

        /// <summary>
        /// Called by ContaminationManager when a zone's contamination is triggered. Any number of
        /// zones can be triggered at once — each pops in its own uiPanel independently, as an
        /// overlay that never touches _currentPanel. The simulation is paused once, on the first
        /// concurrently-open contamination panel.
        /// </summary>
        private void OnContaminationTriggered(String areaName, GameObject zonePanel)
        {
            if (zonePanel == null)
            {
                return;
            }

            bool wasEmpty = _activeContaminationPanels.Count == 0;
            _activeContaminationPanels.Add(zonePanel);
            PopInTracked(zonePanel, _contaminationCts);
            /*  botController.audioSource.Stop();



              if (wasEmpty)
                  SimulationManager.Instance.PauseCurrentState();

              */

            AlertEffect.AlertStarted?.Invoke();
        }

        /// <summary>
        /// Called by ContaminationManager when a zone's contamination resolves. Pops out that
        /// zone's panel only. The simulation state is restarted once every concurrently-open
        /// contamination panel has resolved.
        /// </summary>
        private void OnContaminationResolved(GameObject zonePanel)
        {
            if (zonePanel == null) return;

            PopOutTracked(zonePanel, _contaminationCts);
            _activeContaminationPanels.Remove(zonePanel);

          /*  if (_activeContaminationPanels.Count == 0)
                SimulationManager.Instance.RestartCurrentState();*/

            AlertEffect.AlertFinished?.Invoke();
        }

        #endregion


        // ═════════════════════════════════════════════
        // FIRST AIR
        // ═════════════════════════════════════════════

        #region FirstAir

        private void AddListnersFirstAir()
        {
            firstAirManager.onFirstAirTriggered.AddListener(OnFirstAirTriggered);
            firstAirManager.onFirstAirResolved.AddListener(OnFirstAirResolved);
        }

        private void RemoveListnersFirstAir()
        {
            firstAirManager.onFirstAirTriggered.RemoveListener(OnFirstAirTriggered);
            firstAirManager.onFirstAirResolved.RemoveListener(OnFirstAirResolved);
        }

        /// <summary>
        /// Called by FirstAirManager when a zone's first air is broken. Any number of
        /// zones can be triggered at once — each pops in its own uiPanel independently, as an
        /// overlay that never touches _currentPanel. The simulation is paused once, on the first
        /// concurrently-open first air panel.
        /// </summary>
        private void OnFirstAirTriggered(String areaName, GameObject zonePanel)
        {
            if (zonePanel == null)
            {
                return;
            }
            bool wasEmpty = _activeFirstAirPanels.Count == 0;
            _activeFirstAirPanels.Add(zonePanel);
            PopInTracked(zonePanel, _firstAirCts);
            /*   botController.audioSource.Stop();


               if (wasEmpty)
                   SimulationManager.Instance.PauseCurrentState();

               */

            AlertEffect.AlertStarted?.Invoke();
        }

        /// <summary>
        /// Called by FirstAirManager when a zone's first air resolves. Pops out that
        /// zone's panel only. The simulation state is restarted once every concurrently-open
        /// first air panel has resolved.
        /// </summary>
        private void OnFirstAirResolved(GameObject zonePanel)
        {
            if (zonePanel == null) return;

            PopOutTracked(zonePanel, _firstAirCts);
            _activeFirstAirPanels.Remove(zonePanel);

          /*  if (_activeFirstAirPanels.Count == 0)
                SimulationManager.Instance.RestartCurrentState();*/

            AlertEffect.AlertFinished?.Invoke();
        }

        #endregion


        // ═════════════════════════════════════════════
        // HAND SPEED TRACKING
        // ═════════════════════════════════════════════
        //
        // Per-zone speedAlert panels are FULLY INDEPENDENT of the ActivePanel state machine.
        // They never read or write _currentPanel, never stash anything, and never interrupt
        // any other panel. Left and right hands are tracked independently by VRHandSpeedTracker,
        // so two different zones' panels can be open at once — each overlays on top of whatever
        // is showing, using that zone's own handSpeedUIPanel (routed via ContaminationManager,
        // which owns the VRHandSpeedTracker subscription and resolves the triggering zone).

        #region HandSpeedTracking

        private void AddListnersHandSpeedTracking()
        {
            contaminationManager.onSpeedViolationTriggered.AddListener(OnSpeedViolationTriggered);
            contaminationManager.onSpeedViolationResolved.AddListener(OnSpeedViolationResolved);
        }

        private void RemoveListnersHandSpeedTracking()
        {
            contaminationManager.onSpeedViolationTriggered.RemoveListener(OnSpeedViolationTriggered);
            contaminationManager.onSpeedViolationResolved.RemoveListener(OnSpeedViolationResolved);
        }

        /// <summary>
        /// Called by ContaminationManager when a speed violation starts in a zone.
        /// Never touches _currentPanel — the existing panel flow is completely unaffected.
        /// Pops in the violating zone's own handSpeedUIPanel.
        /// </summary>
        private void OnSpeedViolationTriggered(GameObject zonePanel)
        {
            if (zonePanel == null)
            {
                return;
            }

            _activeSpeedAlertPanels.Add(zonePanel);
            PopInTracked(zonePanel, _speedAlertCts);

            AlertEffect.AlertStarted?.Invoke();
        }

        /// <summary>
        /// Called by ContaminationManager when a zone's speed violation ends.
        /// Pops out that zone's speed-alert panel only — existing panel flow is unaffected.
        /// </summary>
        private void OnSpeedViolationResolved(GameObject zonePanel)
        {
            if (zonePanel == null) return;

            PopOutTracked(zonePanel, _speedAlertCts);
            _activeSpeedAlertPanels.Remove(zonePanel);

            AlertEffect.AlertFinished?.Invoke();
        }

        #endregion
    }
}