using System;
using UnityEngine;
using UnityEngine.Events;
using System.Collections;

public class AlertEffect : MonoBehaviour
{
    public static Action AlertStarted;
    public static Action AlertFinished;
    public float VinneteTimeForFadingOutTint = 1f;
    public AudioClip AudioClipForAlert;

    [SerializeField] private AudioSource audioSource;
    [SerializeField] private CanvasGroup canvasGroup;
    [SerializeField] private float AlertEffectTime;


    [ContextMenu("StartAlertTest")]
  public void TestEffect()
    {
        AlertStarted?.Invoke();

        Invoke("TestFinishEffect", 5f);
    }
   
    public void TestFinishEffect()
    {
        AlertFinished?.Invoke();
    }

    private void Awake()
    {
        audioSource.GetComponent<AudioSource>().loop = false;
    }

    private void OnEnable()
    {
        AlertStarted+= StartAlert;
        AlertFinished+= FinishAlert;
    }

    private void OnDisable()
    {
        AlertStarted -= StartAlert;
        AlertFinished -= FinishAlert;
    }
    private IEnumerator AnimateCanvasGroup()
    {
        audioSource.clip = AudioClipForAlert;
        audioSource.Play();
        canvasGroup.alpha = 0f;
        float Elapsedtime = 0f;
        while (true)
        {
            if (Elapsedtime <= VinneteTimeForFadingOutTint/2)
            {
                Elapsedtime += Time.deltaTime;
                canvasGroup.alpha = Mathf.Lerp(0, 1f, Elapsedtime / (VinneteTimeForFadingOutTint/2));
            }
            else if(Elapsedtime>VinneteTimeForFadingOutTint/2 && Elapsedtime<=VinneteTimeForFadingOutTint)
            {
                Elapsedtime += Time.deltaTime;
                canvasGroup.alpha = Mathf.Lerp( 1f,0f, Elapsedtime / VinneteTimeForFadingOutTint);
            }
            else if(Elapsedtime> VinneteTimeForFadingOutTint)
            {
                Elapsedtime = 0f;
            }
            yield return null;
        }
  
     
   
    }


    public void StartAlert()
    {
        StartCoroutine(nameof(AnimateCanvasGroup));
    }

    public void FinishAlert()
    {
        StopCoroutine(nameof(AnimateCanvasGroup));
        audioSource.Stop();
        audioSource.clip = null;
        canvasGroup.alpha = 0f;
        
    }
}
