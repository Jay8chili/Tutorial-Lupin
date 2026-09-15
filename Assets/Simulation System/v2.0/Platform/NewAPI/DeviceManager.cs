using LightSide;
using SimulationSystem.V02.Utility;
using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

public class DeviceManager : MonoBehaviour
{
    [SerializeField] private TMP_InputField pairCode;
    [SerializeField] private Button register;
    [SerializeField] private TextMeshProUGUI versionText;
    [SerializeField] private UniText versionTextUni;
    [SerializeField] private TextMeshProUGUI versionTextOnLogin;
    [SerializeField] private UniText versionTextOnLoginUni;
    [SerializeField] private UnityEvent OnValidateSuccess;


    private NewAPICollections collections;

    // Cache system info to avoid repeated native calls
    private string cachedDeviceType;
    private string cachedDeviceId;
    private string cachedBundleId;

    private string deviceIdentifier;

    private void Start()
    {

        TextCompat.ResolveUniText(ref versionTextUni, versionText);
        TextCompat.ResolveUniText(ref versionTextOnLoginUni, versionTextOnLogin);
        TextCompat.SetText(versionTextUni, versionText, "v " + Application.version);
        TextCompat.SetText(versionTextOnLoginUni, versionTextOnLogin, "v " + Application.version);

        collections = NewAPIManager.Instance.GetAPICollections();
        register.onClick.AddListener(RegisterDevice);
        //PlayerPrefs.SetString("device_identifier", "a0f3e5d2142c187e9cb90c04522509f0c840fd5d526f306fff3ffbbe7ae95907");

        if (NewAPIManager.Instance == null)
        {
            return;
        }

        deviceIdentifier = PlayerPrefs.GetString("device_identifier");

        // Pre-cache values
        cachedDeviceType = SystemInfo.deviceType.ToString();
        cachedDeviceId = SystemInfo.deviceUniqueIdentifier;
        cachedBundleId = Application.identifier;

        if (!string.IsNullOrEmpty(deviceIdentifier))
        {
            Revalidate();
        }
    }

    public void RegisterDevice()
    {
        if (pairCode.text.Length != 8)
        {
            MessageHandleManager.Instance.ShowMessage(MessageHandleManager.OperationStatus.failure, "Code Must be 8 Characters");
            return;
        }

        DeviceRegistrationRequest requestData = new()
        {
            device_type = cachedDeviceType,
            device_id = cachedDeviceId,
            bundle_id = cachedBundleId,
            org_code = pairCode.text
        };
        string requestString = JsonUtility.ToJson(requestData);
        
        StartCoroutine(NewAPIManager.Instance.PostWebRequest(
            collections.DeviceRegister(),
            requestString,
            (response) => { DeviceAuthProcess(response); },
            false)
        );
        pairCode.text = "";
    }

    public void Revalidate()
    {
        DeviceReValidate requestData = new()
        {
            identifier = cachedDeviceId
        };

        string requestString = JsonUtility.ToJson(requestData);

        
        StartCoroutine(NewAPIManager.Instance.PostWebRequest(
            collections.DeviceValidation(),
            requestString,
            (response) => { DeviceAuthProcess(response); },
            false)
        );
    }

    private void DeviceAuthProcess(string res)
    {
        if (string.IsNullOrEmpty(res)) return;
        try
        {
            DeviceAuthResponse response = JsonUtility.FromJson<DeviceAuthResponse>(res);

            PlayerPrefs.SetString("device_identifier", response.device_identifier);
            PlayerPrefs.SetString("device_auth_code", response.auth_code);
            PlayerPrefs.SetString("org_name", response.org_name);
            PlayerPrefs.SetInt("org_id", response.org_id);
            PlayerPrefs.SetString("site_name", response.site_name);
            PlayerPrefs.Save(); // Ensure data is written to disk

            PlatformGameManager.Instance.SetOrgDetails(response.org_name, response.site_name,response.org_id,response.auth_code);
            PlatformGameManager.Instance.SetDeviceID(response.device_id);

            OnValidateSuccess?.Invoke();
        }
        catch (Exception e)
        {
        }
    }
}

[Serializable]
public class DeviceRegistrationRequest
{
    public string device_id;
    public string device_type;
    public string org_code;
    public string bundle_id;
}

[Serializable]
public class DeviceAuthResponse
{
    public string device_identifier;
    public string auth_code;
    public int org_id;
    public string org_name;
    public string org_icon;
    public string site_name;
    public int device_id;
}
[Serializable]
public class DeviceReValidate
{
    public string identifier;
}