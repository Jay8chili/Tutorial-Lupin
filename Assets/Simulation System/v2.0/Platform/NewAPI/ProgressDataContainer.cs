using LightSide;
using SimulationSystem.V02.Utility;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SocialPlatforms.Impl;

public class ProgressDataContainer : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI totalAttempts;
    [SerializeField] private UniText totalAttemptsUni;
    [SerializeField] private TextMeshProUGUI guidedAttempts;
    [SerializeField] private UniText guidedAttemptsUni;
    [SerializeField] private TextMeshProUGUI assessmentAttempts;
    [SerializeField] private UniText assessmentAttemptsUni;
    [SerializeField] private TextMeshProUGUI score;
    [SerializeField] private UniText scoreUni;
    [SerializeField] private TextMeshProUGUI timeSpent;
    [SerializeField] private UniText timeSpentUni;
    [SerializeField] private TextMeshProUGUI simName;
    [SerializeField] private UniText simNameUni;

    private void Awake()
    {
        TextCompat.ResolveUniText(ref totalAttemptsUni, totalAttempts);
        TextCompat.ResolveUniText(ref guidedAttemptsUni, guidedAttempts);
        TextCompat.ResolveUniText(ref assessmentAttemptsUni, assessmentAttempts);
        TextCompat.ResolveUniText(ref scoreUni, score);
        TextCompat.ResolveUniText(ref timeSpentUni, timeSpent);
        TextCompat.ResolveUniText(ref simNameUni, simName);
    }

    public void SetData(int totalAttempts,int guidedAttempts,int assessmentAttempts, int score, int timeSpent, string simName=null)
    {
        TextCompat.SetText(totalAttemptsUni, this.totalAttempts, totalAttempts.ToString());
        TextCompat.SetText(guidedAttemptsUni, this.guidedAttempts, guidedAttempts.ToString());
        TextCompat.SetText(assessmentAttemptsUni, this.assessmentAttempts, assessmentAttempts.ToString());
        TextCompat.SetText(scoreUni, this.score, score.ToString());
        TextCompat.SetText(timeSpentUni, this.timeSpent, (timeSpent/60).ToString());
        if (!string.IsNullOrEmpty(simName))
        {
            TextCompat.SetText(simNameUni, this.simName, simName);
        }
    }

}
