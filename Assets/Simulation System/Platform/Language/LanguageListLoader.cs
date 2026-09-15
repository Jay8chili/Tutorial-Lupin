using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Fetches the language list and hands it to the selector.
/// Response shape: { "default_language": "en", "languages": ["en","es"] }
///
/// If the request fails the app still runs: no buttons appear, and everything
/// stays in the authored default language.
/// </summary>
public class LanguageListLoader : MonoBehaviour
{
    [SerializeField] private string languageListUrl = "https://example.com/api/languages";
    [SerializeField] private int timeoutSeconds = 15;
    [SerializeField] private LanguageSelector selector;

    private NewAPICollections collections;
    private void Start()
    {
        collections = NewAPIManager.Instance.GetAPICollections();

    }
    public void ListLanguages() {
        StartCoroutine(NewAPIManager.Instance.GetWebRequest(collections.GetLeaderboard(), false, (res) =>
        {
            LanguageList(res);
        }));
    }

    private void LanguageList(string response)
    {



        var result = JsonUtility.FromJson<LanguageListResponse>(response);

        Debug.LogError("Ressssss" + result);
        if (selector != null) selector.Build(result);

    }
}
