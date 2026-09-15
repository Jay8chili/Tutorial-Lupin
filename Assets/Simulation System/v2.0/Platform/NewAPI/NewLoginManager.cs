using LightSide;
using SimulationSystem.V02.Utility;
using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

public class NewLoginManager : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI welcomeText;
    [SerializeField] private UniText welcomeTextUni;
    [SerializeField] private TMP_InputField loginPinInput;
    [SerializeField] private Button submitButton;
    [SerializeField] private Animator loginPageAnimation;

    [SerializeField] private UnityEvent onLoginSucess;
    
    private NewAPICollections collections;


    private void Start()
    {
        collections = NewAPIManager.Instance.GetAPICollections();
        submitButton.onClick.AddListener(LoginWithPin);
        TextCompat.ResolveUniText(ref welcomeTextUni, welcomeText);
    }

    public void SetPlatformName()
    {
        TextCompat.SetText(welcomeTextUni, welcomeText, LocalizationManager.Instance.GetText("NewLoginManager_WelcomeTo") + PlatformGameManager.Instance.GetOrgName() + ", " + PlatformGameManager.Instance.GetSiteName());
        StartCoroutine(AnimateLoginpage(2f));
            
        

    }

    IEnumerator AnimateLoginpage(float delay=0f)
    {
        yield return new WaitForSeconds(delay);
        loginPageAnimation.enabled = true;

        yield return new WaitForSeconds(3f);
        if (PlatformGameManager.Instance.GetIsLoggedIn())
        {
            MessageHandleManager.Instance.ShowMessage(MessageHandleManager.OperationStatus.success, LocalizationManager.Instance.GetText("NewLoginManager_AutoLogin"));
            onLoginSucess?.Invoke();
        }
    }

    private void LoginWithPin()
    {
        if(loginPinInput.text.Length != 6)
        {
            MessageHandleManager.Instance.ShowMessage(MessageHandleManager.OperationStatus.failure, LocalizationManager.Instance.GetText("NewLoginManager_InvalidCode"));
            return;
        }
        string url = collections.PinLogin(loginPinInput.text);
        StartCoroutine(NewAPIManager.Instance.GetWebRequest(url, true, (res) =>
        {
            ProcessLogin(res);
        }));
    }

    private void ProcessLogin(string res)
    {
        if (string.IsNullOrEmpty(res)) return;

        try
        {
            LoginData response = JsonUtility.FromJson<LoginData>(res);
            PlatformGameManager.Instance.SetUserDetails(response.name, response.user_auth_code, response.user_id);
            PlatformGameManager.Instance.SetIsLoggedIn(true);
            onLoginSucess?.Invoke();
            MessageHandleManager.Instance.ShowMessage(MessageHandleManager.OperationStatus.success, LocalizationManager.Instance.GetText("NewLoginManager_LoginSucessfully"));
        }
        catch (Exception e)
        {
            MessageHandleManager.Instance.ShowMessage(MessageHandleManager.OperationStatus.failure, LocalizationManager.Instance.GetText("NewLoginManager_FailedToLogin"));

        }
    }

}

[Serializable]
public class LoginData
{
    public string user_auth_code;
    public string name;
    public int user_id;
}
