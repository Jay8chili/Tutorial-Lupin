using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Inspector-only bridge for making any existing script's progress restartable, with zero code
/// changes to that script. Wire a method that notes down state to onCaptureStepState, and a
/// method that undoes it back to onRestoreStepState, then add this component to a
/// SimulationState's extraStepStateHooks list.
/// </summary>
public class StepStateHook : StepStateComponent
{
    [Tooltip("Invoked when this step's baseline is captured (state start, or right before this hook's owning interaction runs). Wire a method here that records whatever this script needs to remember.")]
    public UnityEvent onCaptureStepState;

    [Tooltip("Invoked when this step/interaction is restarted. Wire a method here that undoes this script's progress back to the captured baseline.")]
    public UnityEvent onRestoreStepState;

    public override void CaptureStepState() => onCaptureStepState?.Invoke();
    public override void RestoreStepState() => onRestoreStepState?.Invoke();
}
