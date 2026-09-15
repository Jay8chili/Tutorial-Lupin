using UnityEngine;

public abstract class StepStateComponent : MonoBehaviour
{
    public abstract void CaptureStepState();
    public abstract void RestoreStepState();
}
