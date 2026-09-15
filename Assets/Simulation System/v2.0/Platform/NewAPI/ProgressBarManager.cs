
using LightSide;
using SimulationSystem.V02.Utility;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ProgressBarManager: MonoBehaviour
{
    public static ProgressBarManager Instance;
    [SerializeField] private CanvasGroup progressUI;
    [SerializeField] private TextMeshProUGUI title;
    [SerializeField] private UniText titleUni;
    [SerializeField] private TextMeshProUGUI status;
    [SerializeField] private UniText statusUni;
    [SerializeField] private Slider progressSlider;

    [SerializeField] private Color successColor, failColor;

    private Color activeColor;

    private bool isProgressUsed;
    private void Awake()
    {
        Instance = this;
        TextCompat.ResolveUniText(ref titleUni, title);
        TextCompat.ResolveUniText(ref statusUni, status);
    }
    public void UpdateProgressBar(float progress,string title=null, string status = "Downloading")
    {
        if(progressUI.alpha != 1)
        {
            progressUI.alpha = 1;
        }
        if (!string.IsNullOrEmpty(title))
        {
            isProgressUsed = true;
            TextCompat.SetText(titleUni, this.title, title);
        }
        if (TextCompat.GetText(statusUni, this.status) != status)
        {
            TextCompat.SetText(statusUni, this.status, status);
        }
        progressSlider.value = progress;
    }

    public void CloseProgress(string status = "Success",float delay=3f)
    {
        string statusText;
        if(status == "Success")
        {
            activeColor = successColor;
            statusText = LocalizationManager.Instance.GetText("ContentManager_Completed");
        }
        else
        {
            activeColor = failColor;
            statusText = LocalizationManager.Instance.GetText("ContentManager_Failed");
        }
        StartCoroutine(CloseProgressRoutine(activeColor, statusText,delay));

    }

    private IEnumerator CloseProgressRoutine(Color color, string msg,float delay)
    {
        isProgressUsed = false;
        TextCompat.SetText(statusUni, this.status, msg);
        TextCompat.SetColor(statusUni, this.status, color);
        yield return new WaitForSeconds(delay);
        progressUI.alpha = 0;
    }

    public bool IsProgressBarAvailable()
    {
        return isProgressUsed;
    }
}
