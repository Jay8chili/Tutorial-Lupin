using SimulationSystem.V02.Simulation.Managers;
using SimulationSystem.V02.StateInteractions;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;

public class SimulationManager : MonoBehaviour
{
    public bool ByPassSimulationApiLogs = false;
    public bool ShowBot = true;
    [Tooltip("Assign state GameObjects (in order). Each state GameObject should have a State component.")]
    public List<GameObject> states;

    private int currentIndex = -1;
    public SimulationState currentState { get; private set; }

    public static SimulationManager Instance;

    public GameObject setModeUI;
    public GameObject simulationCompletedPanel;
    [HideInInspector] public SimulationMode simulationMode { get; private set; }

    [Header("Grab Settings")]
    [Tooltip("Global grab behaviour. Individual GrabInteraction objects can override this.")]
    public GrabBehaviour grabBehaviour = GrabBehaviour.GrabWithPinch;

    [Tooltip("Legacy: hold the trigger to hold the object, release the trigger to drop it.\n" +
             "Toggle: press the trigger once to grab, releasing it does nothing — press again to release.\n" +
             "Proximity-grabbed objects are unaffected by this setting either way.")]
    public GrabTriggerMechanism grabTriggerMechanism = GrabTriggerMechanism.Legacy;

    // ── Intro ─────────────────────────────────────────────────────────────
    [Header("Intro Settings")]
    [Tooltip("Panel to show during the intro. Starts inactive; hidden automatically when audio ends.")]
    public GameObject introPanel;

    [Tooltip("Audio clip to play during the intro. If null, intro phase is skipped.")]
    public AudioClip introClip;

    [Tooltip("None = full flow (intro → setMode → simulation).\n" +
             "SkipIntro = skip intro, show setModeUI directly.\n" +
             "SkipAll = skip intro and setMode, start simulation immediately.")]
    public StartupOverride startupOverride = StartupOverride.None;

    [Header("Simulation Events")]
    public UnityEvent SimulationIntroStarted;
    public UnityEvent SimulationIntroCompleted;
    public UnityEvent SimulationStart;
    public UnityEvent SimulationEnd;
    public UnityEvent SimulationPaused;
    public UnityEvent SimulationResumed;

    // ── Pause State ───────────────────────────────────────────────────────
    private bool _isPaused;
    public bool isPaused => _isPaused;

    private Dictionary<Interactions, bool> _pausedInteractFlags = new Dictionary<Interactions, bool>();

    private void Awake()
    {

        if (Instance != null)
        {


            // Destroy(gameObject);
        }


        Instance = this;
    }

    private void Start()
    {
        states.Clear();

        for (int i = 0; i < gameObject.transform.childCount; i++)
        {
            if (transform.GetChild(i).gameObject.activeInHierarchy &&
                transform.GetChild(i).gameObject.GetComponent<SimulationState>() != null)
            {
                states.Add(transform.GetChild(i).gameObject);
            }
        }

        // Overwrite each state's authored prompt text/audio with the active
        // language's translations, if a per-simulation language pack was pre-fetched
        // (ContentManager.DownloadSimulationLanguageAsset) before this bundle's scene
        // was activated. Auto-attached since bundle scenes don't author this
        // component themselves. No-op in the default/authored language.
        if (!LocalizationManager.Instance.IsDefaultLanguage)
        {
            SimulationLocalizationInjector injector = GetComponent<SimulationLocalizationInjector>();
            if (injector == null) injector = gameObject.AddComponent<SimulationLocalizationInjector>();
            injector.CaptureAndInject();
        }

        switch (startupOverride)
        {
            case StartupOverride.SkipAll:
                StartSimulation();
                break;

            case StartupOverride.SkipIntro:
                setModeUI.SetActive(true);
                break;

            case StartupOverride.None:
            default:
                StartCoroutine(IntroRoutine());
                break;
        }
    }

    // ─────────────────────────────────────────────
    // INTRO
    // ─────────────────────────────────────────────

    private IEnumerator IntroRoutine()
    {
        if (introClip != null)
        {
            if (introPanel != null)
                introPanel.SetActive(true);

            SimulationIntroStarted?.Invoke();

            // yield return on the IEnumerator directly — Unity waits frame-by-frame
            // until the audio finishes and SoundManager clears the clip
            yield return StartCoroutine(SoundManager.PlayIntroCoroutine(introClip));

            if (introPanel != null)
                introPanel.SetActive(false);

            SimulationIntroCompleted?.Invoke();
        }

        // Intro done (or skipped) — now show the mode selection


    }

    // ─────────────────────────────────────────────
    // PAUSE / RESUME / PLAY
    // ─────────────────────────────────────────────

    /// <summary>
    /// Pause the entire simulation. All interactions in the current state
    /// have canInteract set to false. Flags are snapshot so ResumeSimulation
    /// can restore them exactly.
    /// </summary>
    public void PauseSimulation()
    {
        if (_isPaused) return;
        _isPaused = true;

        _pausedInteractFlags.Clear();

        if (currentState != null)
        {
            foreach (var interaction in currentState.listOfInteractions)
            {
                if (interaction == null) continue;
                _pausedInteractFlags[interaction] = interaction.canInteract;
                interaction.canInteract = false;
            }
        }

        SimulationPaused?.Invoke();
    }

    /// <summary>
    /// Resume from a paused state. Restores canInteract flags to exactly
    /// what they were before PauseSimulation was called.
    /// </summary>
    public void ResumeSimulation()
    {
        if (!_isPaused) return;
        _isPaused = false;

        if (currentState != null)
        {
            foreach (var interaction in currentState.listOfInteractions)
            {
                if (interaction == null) continue;
                if (_pausedInteractFlags.TryGetValue(interaction, out bool wasActive))
                    interaction.canInteract = wasActive;
            }
        }

        _pausedInteractFlags.Clear();

        SimulationResumed?.Invoke();
    }

    /// <summary>
    /// Convenience alias — resumes from pause if paused, otherwise no-op.
    /// Wire to a "Play" button.
    /// </summary>
    public void PlaySimulation()
    {
        ResumeSimulation();
    }

    /// <summary>
    /// Toggle between paused and playing.
    /// Wire to a single pause/play toggle button.
    /// </summary>
    public void TogglePause()
    {
        if (_isPaused)
            ResumeSimulation();
        else
            PauseSimulation();
    }

    // ─────────────────────────────────────────────
    // EXISTING API
    // ─────────────────────────────────────────────

    public void PauseCurrentState()
    {
        foreach (var interaction in currentState.listOfInteractions)
        {
            interaction.canInteract = false;
        }
    }

    public void RestartCurrentState()
    {
        _isPaused = false;
        _pausedInteractFlags.Clear();
        MoveToState(currentIndex, isRestart: true);
    }

    /// <summary>
    /// Restarts only the interaction currently running in the current state — reverts its
    /// object(s)/hooked scripts back to how they were right before this interaction began,
    /// then restarts it, without touching any earlier interaction in this same step.
    /// </summary>
    public void RestartCurrentInteraction()
    {
        currentState?.RestartCurrentInteraction();
    }

    public void MoveToNextState()
    {
        _isPaused = false;
        _pausedInteractFlags.Clear();


        MoveToState(currentIndex + 1);


    }
    public float GetAssessmentFinalScore(int index)
    {

        if (SimulationManager.Instance.states[index - 1].GetComponent<AssessmentController>())
        {
            return SimulationManager.Instance.states[index - 1].GetComponent<AssessmentController>().GetFinalScoreForState(index);
        }
        else
        {
            return 0;
        }
    }

    public float GetElaspsedTime()
    {
        if(ByPassSimulationApiLogs)
        {
            return 0;
        }
        else
        {
            return SessionManager.Instance.GetElapsedTime();
        }
    }
    public void MoveToState(int index) => MoveToState(index, isRestart: false);

    private void MoveToState(int index, bool isRestart)
    {
        // Before the current state changes to the next state, log the completed
        // state's score and time spent to SessionManager (assessment mode only).
        // Skipped on restart — index == currentIndex there, so this would otherwise
        // re-log the PREVIOUS state's (states[index - 1]) score on every restart.
        if (!isRestart && index > 0)
        {
            if (simulationMode == SimulationMode.Assessment)
            {
                float scoreForStep = GetAssessmentFinalScore(index);
                string stepMessage = AssessmentManager.Instance != null
                    ? AssessmentManager.Instance.GetStepMessage(index, "")
                    : "";
                StepUpdate data = new StepUpdate(currentState.sessionLogID, "Done", GetElaspsedTime(), scoreForStep, stepMessage);
                Debug.Log($"[Assessment] Sending step score to backend — state index {index - 1} ('{currentState.name}'), sessionLogID {currentState.sessionLogID}: score={scoreForStep:F1}, error_message='{stepMessage}'");
                if (!ByPassSimulationApiLogs)
                {
                    SessionManager.Instance.UpdateSession(data);
                }
            }
            else if (simulationMode == SimulationMode.Guided)
            {
                // Guided mode has no scoring — just tell the backend the step finished so it
                // isn't left sitting at "Pending" forever.
                StepUpdate data = new StepUpdate(currentState.sessionLogID, "Done", GetElaspsedTime(), 0f);
                Debug.Log($"[Guided] Sending step completion to backend — state index {index - 1} ('{currentState.name}'), sessionLogID {currentState.sessionLogID}");
                if (!ByPassSimulationApiLogs)
                {
                    SessionManager.Instance.UpdateSession(data);
                }
            }
        }


        if (states == null || states.Count == 0)
            return;

        _isPaused = false;
        _pausedInteractFlags.Clear();

        StopCurrent();
        if (index >= states.Count)
        {
            SoundManager.PlaySimulationEnd();

            AssessmentManager.Instance?.CloseSession(); // if AssessmentManager exists in the scene, close the session
            
            if(! ByPassSimulationApiLogs)
            { 
                SessionManager.Instance.EndSession();
            }

            SimulationEnd?.Invoke();
            simulationCompletedPanel.SetActive(true);
        }
        if (index < 0 || index >= states.Count)
        {
            currentIndex = index;
            currentState = null;
            return;
        }

        GameObject go = states[index];
        if (go == null)
            return;

        currentIndex = index;
        go.SetActive(true);

        currentState = go.GetComponent<SimulationState>();
        if (currentState != null)
        {

            currentState.StartState(this, isRestart);
            if (!ByPassSimulationApiLogs)
            {
                SessionManager.Instance.ResetElapsedTime();
            }
        }
    }

    private void StopCurrent()
    {
        if (currentState != null)
        {
            currentState.StopState();
            if (currentState.gameObject != null)
                currentState.gameObject.SetActive(false);

            currentState = null;
        }
    }

    public void NotifyStateComplete(SimulationState state)
    {
        if (state != currentState)
            return;


        // Allow PartsIdentificationManager to intercept in Free Roam
        if (PartsIdentificationManager.Instance != null &&
            PartsIdentificationManager.Instance.TryInterceptStateComplete(currentIndex))
            return;

        MoveToNextState();
    }

    public void SetMode(int value)
    {
        float maxScore = 0f;
        List<Step> steps = new List<Step>();

        if (value == 0)
            SetSimulationType(SimulationMode.Guided);
        else if (value == 1)
            SetSimulationType(SimulationMode.Assessment);
        else if (value == 2)
            SetSimulationType(SimulationMode.FreeRoam); ;

        setModeUI.SetActive(false);

        string mode;
        foreach (var states in states)
        {
            Step step = new(states.GetComponent<SimulationState>().promptText, GetStateTypeString(states.GetComponent<SimulationState>()));
            steps.Add(step);
            if (value == 1)
            {
                foreach (var a in states.GetComponent<AssessmentController>().interactionConfigs)
                {
                    // Only count configs with an assigned interaction — matches
                    // AssessmentManager.BeginState(), which skips unassigned slots.
                    // Counting unassigned slots here would make this upfront total
                    // drift from the sum of per-state scores sent during the session.
                    if (a.interaction == null) continue;
                    maxScore += a.maxScore;
                }
            }
        }
        if (value == 1)
            Debug.Log($"[Assessment] Session max score computed: {maxScore:F1} across {states.Count} states.");

        if (value == 1)
        {
            mode = "Assessment";
        }
        else
        {
            mode = "Guided";
        }
        if (!ByPassSimulationApiLogs)
        {
            SessionManager.Instance.CreateSession(mode, steps, maxScore, () =>
            {
                StartSession();
            });

        }

        else
        {
            StartSession();
        }

    }
    private string GetStateTypeString(SimulationState state)
    {
        //Check the edge case of if prompt state type is considered only if AutoProgress boolean is True in State
        if (state.listOfInteractions.Count <= 0)
        {
            return "Prompt";
        }
        else
        {
            switch (state.listOfInteractions[0])
            {
                case GrabInteraction:
                    return "Grab";
                    break;
                case DetectInteraction:
                    if (state.listOfInteractions[0].GetComponent<DetectInteraction>().ObjectsToBeDetectedList[0].GetComponent<GrabInteraction>())
                    {
                        return "Detect With Grab";

                    }
                    else
                    {
                        return "Detect With Hand";
                    }
                case UIInteraction:
                    return "Ui";
                    break;
                case GazeInteraction:
                    return "Gaze";
                    break;
                case IdleInteraction:
                    return "Idle";
                    break;
                default:
                    return "Prompt";
            }
        }
    }
    public void AssignStepSessionID(List<Step> steps)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            states[i].GetComponent<SimulationState>().sessionLogID = steps[i].log_id;
        }
    }
    private async void StartSession()
    {
        await Task.Delay(2000);
        setModeUI.SetActive(false);


        //ConfigureScenarioSteps();

        StartSimulation();
    }
    private void StartSimulation()
    {
        /* Platform.APICollection.LogEvent("simulation_start", new Dictionary<string, object>
         {
             { "simulation_mode", simulationMode.ToString() }
         });*/
        currentState = states[0].GetComponent<SimulationState>();
        MoveToState(0);

        AssessmentManager.Instance?.BeginSession(); // if AssessmentManager exists in the scene, start the session

        //Sfx
        SoundManager.PlaySimulationStart();

        //event
        SimulationStart?.Invoke();
    }

    private void SetSimulationType(SimulationMode mode)
    {
        simulationMode = mode;
        if (simulationMode == SimulationMode.Assessment)
            Debug.Log("[Assessment] Simulation mode set to Assessment — score tracking is now active.");
    }
}

public enum SimulationMode
{
    Guided,
    Assessment,
    FreeRoam
}

public enum GrabBehaviour
{
    Default,
    GrabWithPinch,
    GrabWithoutPinch
}

public enum GrabTriggerMechanism
{
    Legacy,
    Toggle
}

public enum StartupOverride
{
    None,
    SkipIntro,
    SkipAll
}