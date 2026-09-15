using LightSide;
using SimulationSystem.V02.Assistant;
using SimulationSystem.V02.StateInteractions;
using SimulationSystem.V02.Utility;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

public class SimulationState : MonoBehaviour
{
    public int sessionLogID;
    [SerializeField] public List<Interactions> listOfInteractions = new List<Interactions>();

    [HideInInspector] public Interactions currentInteraction ;
    private int currentInteractionIndex;

    [Header("Prompt")]
    public string promptText;
    public AudioClip promptAudio;
    public bool MoveToNextStepAfterAudio;


    [Header("Secondary Prompts")]
    [Tooltip("If true, an additional prompt (with its own audio) is shown BEFORE the main prompt above.")]
    public bool hasPrePrompt = false;
    [Tooltip("Text shown for the pre-prompt. Only used if Has Pre Prompt is true.")]
    public string prePromptText;
    [Tooltip("Audio clip played for the pre-prompt. Only used if Has Pre Prompt is true.")]
    public AudioClip prePromptAudio;

    [Tooltip("If true, an additional prompt (with its own audio) is shown AFTER the main prompt above, before interactions begin.")]
    public bool hasPostPrompt = false;
    [Tooltip("Text shown for the post-prompt. Only used if Has Post Prompt is true.")]
    public string postPromptText;
    [Tooltip("Audio clip played for the post-prompt. Only used if Has Post Prompt is true.")]
    public AudioClip postPromptAudio;


    [Header("Static UI — Prompt")]
    [Tooltip("If true, the main prompt uses Prompt Static UI Panel/Text for this step " +
             "instead of the bot's floating prompt panel.")]
    public bool useStaticUIForPrompt = false;
    [Tooltip("Scene panel popped in/out for the main prompt when Use Static UI For Prompt is enabled.")]
    public GameObject promptStaticUIPanel;
    [Tooltip("Text component the main prompt is written to when Use Static UI For Prompt is enabled.")]
    public TMP_Text promptStaticUIText;
    [Tooltip("UniText component the main prompt is written to instead, checked ahead of the TMP_Text field above.")]
    public UniText promptStaticUITextUni;

    [Header("Static UI — Clauses (Pre/Post Prompt)")]
    [Tooltip("If true, the pre/post prompt (\"clause\") uses Clause Static UI Panel/Text for this " +
             "step instead of the bot's floating prompt panel. Assign the same panel used for the " +
             "prompt above to share one, or a different one to keep them visually separate.")]
    public bool useStaticUIForClauses = false;
    [Tooltip("Scene panel popped in/out for the clause when Use Static UI For Clauses is enabled.")]
    public GameObject clauseStaticUIPanel;
    [Tooltip("Text component the clause is written to when Use Static UI For Clauses is enabled.")]
    public TMP_Text clauseStaticUIText;
    [Tooltip("UniText component the clause is written to instead, checked ahead of the TMP_Text field above.")]
    public UniText clauseStaticUITextUni;

    public enum ClauseLifetime { DisappearWhenPromptPlays, KeepClauseTillStateEnd }
    [Tooltip("Disappear When Prompt Plays: the pre-prompt clause pops out right before the main " +
             "prompt starts playing.\n" +
             "Keep Clause Till State End: the clause panel stays visible for this entire step " +
             "(through interactions) instead of closing when the prompt sequence ends.")]
    public ClauseLifetime clauseLifetime = ClauseLifetime.DisappearWhenPromptPlays;


    [Header("Teleport")]
    [Tooltip("If true, the player is teleported to Teleport Target before interactions begin.")]
    public bool teleportOnStart = false;
    [Tooltip("The transform the player will be teleported to when this state starts.")]
    public Transform teleportTarget;

    [Header("Timing")]
    [Tooltip("Seconds to wait after this state completes before moving to the next state.")]
    [Min(0f)]
    public float delayAfterState = 0f;

    [Tooltip("Seconds to wait between each interaction in the sequence.")]
    [Min(0f)]
    public float delayBetweenInteractions = 0f;

    [Header("Step Save State")]
    [Tooltip("Extra state hooks (besides interactions) captured when this step starts and restored on Restart Step / Restart Interaction. Drop a SceneObjectSnapshot on props that get enabled/disabled, moved, or animated, or a StepStateHook wired to custom script reset methods.")]
    public List<StepStateComponent> extraStepStateHooks = new();

    public UnityEvent onStateStart = new UnityEvent();
    public UnityEvent onStateComplete = new UnityEvent();
    public List<DelayedEvent> OnStateStartDelayedEvent = new();
    public List<DelayedEvent> OnStateCompleteDelayedEvent = new();
    private SimulationManager simulationManager;
    private bool stateIsDone = false;
    private Coroutine sequenceCoroutine;

    // Set to true by OnTeleportDone once the teleport coroutine fully completes.
    private bool _teleportDone = false;

    // ─────────────────────────────────────────────
    // START STATE
    // ─────────────────────────────────────────────
    private void OnEnable()
    {
         onStateStart.AddListener(StateStartDelayedEvents);
        onStateComplete.AddListener(StateCompleteDelayedEvents);
    }
    private void OnDisable()
    {
        onStateStart.RemoveListener(StateStartDelayedEvents);
        onStateComplete.RemoveListener(StateCompleteDelayedEvents);
    }
    public void StartState(SimulationManager stateMachine, bool isRestore = false)
    {
        simulationManager = stateMachine;
        stateIsDone = false;
        _teleportDone = false;

        // Restart path: put objects/scripts back exactly as they were at the top of this
        // step before anything re-runs. Fresh entry: note down that baseline for later.
        if (isRestore)
            RestoreState();
        else
            CaptureState();

        //Checks what type/s of interaction this state has
        //This also handles Prompt Audio
        AssistantManager.Instance?.EvaluateStateInteractions(this);

        AssistantManager.Instance?.SetProgressFromSimulation(); // set the assistant progress bar based on the current state index in the SimulationManager

        AssessmentManager.Instance?.BeginState(this); // Notify the AssessmentManager that this state has begun, so it can start tracking for any assessments linked to this state

        onStateStart.Invoke();
       
        if (MoveToNextStepAfterAudio)
            return;     // AssistantManager.HidePromptAfter handles MoveToNextState

        if (listOfInteractions == null || listOfInteractions.Count == 0)
        {
            CompleteState();
            return;
        }

        // Reset every interaction up-front so none carry stale flags
        foreach (var i in listOfInteractions)
            if (i != null) i.ResetInteraction();

        // Wait for prompt to complete before starting sequence
        AssistantManager.PromptCompleted += OnPromptDone;

        // Kick off teleport before the sequence so RunSequence can wait on it.
        if (teleportOnStart && teleportTarget != null && TeleportManager.Instance != null)
        {
            TeleportManager.TeleportCompleted += OnTeleportDone;
            TeleportManager.Instance.UpdatePlayerPos(teleportTarget);
        }
        else
        {
            _teleportDone = true;
            //sequenceCoroutine = StartCoroutine(RunSequence());
        }
    }

    private void OnPromptDone()
    {
        AssistantManager.PromptCompleted -= OnPromptDone; // Unsubscribe immediately
        sequenceCoroutine = StartCoroutine(RunSequence());
    }

    private void OnTeleportDone()
    {
        _teleportDone = true;
        //sequenceCoroutine = StartCoroutine(RunSequence());
        TeleportManager.TeleportCompleted -= OnTeleportDone;
    }

    // ─────────────────────────────────────────────
    // STEP SAVE STATE
    // ─────────────────────────────────────────────

    /// <summary>Notes down the current state of every interaction + extra hook. Called once at the true top of this step.</summary>
    public void CaptureState()
    {
        foreach (var i in listOfInteractions)
            i?.CaptureStepState();
        foreach (var h in extraStepStateHooks)
            h?.CaptureStepState();
    }

    /// <summary>Restores every interaction + extra hook back to its last captured baseline.</summary>
    public void RestoreState()
    {
        foreach (var i in listOfInteractions)
            i?.RestoreStepState();
        foreach (var h in extraStepStateHooks)
            h?.RestoreStepState();
    }

    /// <summary>
    /// Restarts only the currently-running interaction from its own baseline — captured right
    /// before this interaction's turn began in RunSequence — without touching anything earlier
    /// interactions in this step already legitimately did.
    /// </summary>
    public void RestartCurrentInteraction()
    {
        if (currentInteraction == null) return;

        currentInteraction.StopInteraction();
        currentInteraction.RestoreStepState();
        currentInteraction.ResetInteraction();
        currentInteraction.CaptureStepState(); // re-baseline so a second retry still has a valid snapshot
        currentInteraction.StartInteraction();
        currentInteraction.OnActivateInteraction?.Invoke();
    }

    // ─────────────────────────────────────────────
    // STOP STATE
    // ─────────────────────────────────────────────

    public void StopState()
    {
        if (sequenceCoroutine != null)
        {
            StopCoroutine(sequenceCoroutine);
            sequenceCoroutine = null;
        }

        foreach (var interaction in listOfInteractions)
        {
            if (interaction == null) continue;
            interaction.StopInteraction();
            interaction.ResetInteraction();
        }

        stateIsDone = true;
    }

    // ─────────────────────────────────────────────
    // SEQUENCE COROUTINE
    // ─────────────────────────────────────────────

    private IEnumerator RunSequence()
    {
        for (int i = 0; i < listOfInteractions.Count; i++)
        {
            // ── Delay between interactions (skip before the first one) ───
            if (i > 0 && delayBetweenInteractions > 0f)
            {
                yield return new WaitForSeconds(delayBetweenInteractions);
            }
            currentInteraction = listOfInteractions[i];

            var interaction = listOfInteractions[i];
            if (interaction == null) continue;

            // Re-baseline right before this interaction's own turn — this is what
            // RestartCurrentInteraction restores to, independent of whatever earlier
            // interactions in this step legitimately already changed.
            interaction.CaptureStepState();

            if (interaction is DetectInteraction || interaction is GazeInteraction)
            {
                interaction.gameObject.SetActive(true);
            }

            AssessmentManager.Instance?.OnInteractionStarted(interaction);  // Notify the AssessmentManager that an interaction has started, so it can track for any assessments linked to this interaction

            // Call StartInteraction directly (guaranteed virtual dispatch to the override).
            // Then fire the Inspector event so any external listeners also run.
            interaction.StartInteraction();
            interaction.OnActivateInteraction?.Invoke();

            AssessmentManager.Instance?.DisableVisualsAfterStart(interaction);

            int safeIndex = i;
            yield return new WaitUntil(() => listOfInteractions[safeIndex].HasInteractionEnded());

            AssessmentManager.Instance?.OnInteractionCompleted(interaction);    // Notify the AssessmentManager that an interaction has completed, so it can track for any assessments linked to this interaction


            // Same pattern on deactivate: call the method, then fire the event.
            interaction.StopInteraction();
            interaction.OnDeactivateInteraction?.Invoke();

            interaction.ResetInteraction();
        }
        if (listOfInteractions.Count == 0&&(simulationManager.simulationMode== SimulationMode.Assessment))
        {
            AssessmentManager.Instance._currentStateRecord = new StateAssessmentRecord
            {
                stateName = this.name,
                stateMaxScore = 0f,
                stateFinalScore = 0f,
                hintTaken = false,
                hintPenaltyApplied = 0f,
                interactions = new List<InteractionAssessmentRecord>()
            };
        }
        CompleteState();
    }

    // ─────────────────────────────────────────────
    // COMPLETE STATE
    // ─────────────────────────────────────────────

    public void CompleteState()
    {
        if (stateIsDone) return;
        stateIsDone = true;
        onStateComplete.Invoke();

        AssessmentManager.Instance?.CloseState();   // Notify the AssessmentManager that this state has completed, so it can close out any assessments linked to this state

        //AssistantManager.Instance.SetProgressFromSimulation();

        // Wait before notifying SimulationManager to advance
        if (delayAfterState > 0f)
            StartCoroutine(DelayedNotify());
        else
            simulationManager.NotifyStateComplete(this);
    }
    public void StateStartDelayedEvents()
    {
        foreach (DelayedEvent StateStartDelayedEvents in OnStateStartDelayedEvent)
        {
            StateStartDelayedEvents.InvokeDelayedEvent();
        }
    }
    public void StateCompleteDelayedEvents()
    {
        foreach (DelayedEvent StateCompleteDelayedEvents in OnStateCompleteDelayedEvent)
        {
            StateCompleteDelayedEvents.InvokeDelayedEvent();
        }
    }
    private IEnumerator DelayedNotify()
    {
        yield return new WaitForSeconds(delayAfterState);
        simulationManager.NotifyStateComplete(this);
    }
}
