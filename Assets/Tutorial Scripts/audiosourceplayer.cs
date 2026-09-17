using UnityEngine;

public class audiosourceplayer : MonoBehaviour
{
   public AudioSource audioSource;

    public void OnAudioPlay()
    {
        audioSource.Play();
    }
}
