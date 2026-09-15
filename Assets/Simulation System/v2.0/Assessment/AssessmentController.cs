using System;
using System.Collections.Generic;
using UnityEngine;

public class AssessmentController : MonoBehaviour
{
    [Header("Step-Level Assessed Errors")]
    [Tooltip("If checked, a Contamination trigger during this step deducts AssessmentManager's Contamination Penalty from this step's score.")]
    public bool contaminationAssessed;

    [Tooltip("If checked, a First Air break during this step deducts AssessmentManager's First Air Penalty from this step's score.")]
    public bool firstAirAssessed;

    [Tooltip("If checked, a Fast Hand (hand-speed) violation during this step deducts AssessmentManager's Fast Hand Penalty from this step's score.")]
    public bool fastHandAssessed;

    [Header("Step Result Messages (sent to the API as error_message)")]
    [Tooltip("Sent as this step's error_message if Contamination triggered during it.")]
    public string contaminationMessage = "Surface was Contaminated";

    [Tooltip("Sent as this step's error_message if First Air was disturbed during it.")]
    public string firstAirMessage = "First air was disturbed";

    [Tooltip("Sent as this step's error_message if a hint was taken during it.")]
    public string hintTakenMessage = "Hint was taken";

    /// <summary>Every assessed step is worth exactly this many marks, split evenly across interactionConfigs.</summary>
    public const float StepCompulsoryMaxScore = 1f;

    [Tooltip("Per-interaction assessment config. Each step is compulsorily worth 1 mark total — maxScore below is auto-divided evenly across these interactions (see RecalculateInteractionShares) and shouldn't be hand-edited.")]
    public List<InteractionAssessmentConfig> interactionConfigs = new();

    /// <summary>
    /// Splits the step's compulsory 1 mark evenly across interactionConfigs, rounded down to
    /// 1 decimal place. Whatever remainder that rounding leaves goes entirely to the LAST
    /// interaction so the total always sums to exactly 1 — e.g. 3 interactions → 0.3, 0.3, 0.4.
    /// Runs automatically in the Editor whenever this component's fields change, and again as
    /// a runtime safety net right before AssessmentManager reads maxScore.
    /// </summary>
    public void RecalculateInteractionShares()
    {
        int count = interactionConfigs.Count;
        if (count == 0) return;

        float share = Mathf.Floor((StepCompulsoryMaxScore / count) * 10f) / 10f;
        float runningTotal = 0f;

        for (int i = 0; i < count - 1; i++)
        {
            if (interactionConfigs[i] == null) continue;
            interactionConfigs[i].maxScore = share;
            runningTotal += share;
        }

        if (interactionConfigs[count - 1] != null)
            interactionConfigs[count - 1].maxScore = Mathf.Round((StepCompulsoryMaxScore - runningTotal) * 100f) / 100f;
    }

    private void OnValidate()
    {
        RecalculateInteractionShares();
    }

    public float GetMaxScoreForState()
    {
        float maxscore = 0;
        foreach(var intcon in interactionConfigs)
        {
           maxscore+= intcon.maxScore;
        }
        return maxscore;
    }

    public float GetFinalScoreForState(int index)
    {
        if (interactionConfigs.Count <= 0)
        {
            Debug.Log($"[Assessment] GetFinalScoreForState({index}): no interactionConfigs — returning 0");
            return 0;
        }
        else if (interactionConfigs[0].maxScore == 0)
        {
            Debug.Log($"[Assessment] GetFinalScoreForState({index}): first config has maxScore 0 — returning 0");
            return 0;
        }
        else
        {
            float score = AssessmentManager.Instance._session.states[index - 1].stateFinalScore;
            Debug.Log($"[Assessment] GetFinalScoreForState({index}): returning session.states[{index - 1}].stateFinalScore = {score:F1}");
            return score;
        }
    }

}

[System.Serializable]
public class InteractionAssessmentConfig
{
    public Interactions interaction;
    public float maxScore = 10f;
    public float hintPenalty = 5f;

    // Only shown in Inspector when interaction is DetectInteraction — managed by editor script
    public float wrongDetectPenalty = 5f;
    public List<DetectInteraction> wrongDetects = new();
    public List<GameObject> WOTD = new();

    // Only shown in Inspector when interaction is GrabInteraction — managed by editor script
    public float wrongGrabPenalty = 5f;
    public List<GrabInteraction> wrongGrabs = new();
}