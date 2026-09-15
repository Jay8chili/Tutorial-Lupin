
using LightSide;
using SimulationSystem.V02.Utility;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public class ContentManager : MonoBehaviour
{
    public enum SimType
    {
        oculus,
        xri,
        version2
    };

    private int userId;
    [SerializeField] private SimType simType;
    [SerializeField] private TextMeshProUGUI userName;
    [SerializeField] private UniText userNameUni;
    [SerializeField] private List<GameObject> contentUIList = new();
    [SerializeField] private List<Button> sideBarButtonList = new();

    [SerializeField] private Color selectedColor, defaultColor;

    // Module Section Ref
    [SerializeField] private Transform moduleParent;
    [SerializeField] private GameObject modulePrefab;

    // Simulation Section Ref
    [SerializeField] private Transform simulationParent;
    [SerializeField] private GameObject simulationPrefab;

    // Leaderboard Section Ref
    [SerializeField] private Transform leaderboardParent;
    [SerializeField] private GameObject leaderboardPrefab;

    // User Progress Section Ref
    [SerializeField] private Transform progressCardParent;
    [SerializeField] private GameObject progressCardPrefab;
    [SerializeField] private ProgressDataContainer overallDataContainer;

   
    private enum SimulationStatus {
        Launch,
        Download
    }
    private SimulationStatus downloadStatus;

    private NewAPICollections collections;

    private string modulefilter;

    private void Start()
    {
        foreach (Button button in sideBarButtonList)
        {
            button.onClick.AddListener(() =>
            {
                SideBarButtonDefault();
                button.GetComponent<Image>().color = selectedColor;
            });
        }
        if (simType == SimType.oculus)
        {
            modulefilter = "oculus";
        }
        else if (simType == SimType.xri)
        {
            modulefilter = "xri";
        }
        else
        {
            modulefilter = "2.0";
        }
    }

    private void SideBarButtonDefault()
    {
        foreach (Button button in sideBarButtonList)
        {
            button.GetComponent<Image>().color = defaultColor;
        }
    } 
    public void UpdateUserName()
    {
        collections = NewAPIManager.Instance.GetAPICollections();
        TextCompat.ResolveUniText(ref userNameUni, userName);
        TextCompat.SetText(userNameUni, userName,LocalizationManager.Instance.GetText("ContentManager_Hello") + PlatformGameManager.Instance.GetUserName());
        userId = PlatformGameManager.Instance.GetUserID();
        LoadModuleList();

    }

    public void DisableAlltheUI()
    {
        foreach (GameObject go in contentUIList)
        {
            go.SetActive(false);
        }
    }
    public void EnableUI(int index)
    {
        if(contentUIList.Count > index)
        {
            contentUIList[index].SetActive(true);
        }
    }

    //Modules
    private void LoadModuleList()
    {
        StartCoroutine(NewAPIManager.Instance.GetWebRequest(collections.GetModules(userId.ToString()), false, (res) =>
        {
            ProcessModules(res);
        }));
    }

    private void ProcessModules(string res)
    {
        if (string.IsNullOrEmpty(res)) return;
        foreach(Transform go in moduleParent)
        {
            Destroy(go.gameObject);
        }
        string wrappedJson = "{\"modules\":" + res + "}";
        ModuleList response =JsonUtility.FromJson<ModuleList>(wrappedJson);
        if (response.modules.Count > 0)
        {
            foreach (Module module in response.modules)
            {
                if (!string.IsNullOrEmpty(module.type) && module.type != modulefilter)
                {
                    continue;
                }
                GameObject mod = Instantiate(modulePrefab,moduleParent);
                mod.GetComponent<DataContainer>().SetDetails(module.module_id, module.title, module.description, 
                    module.course_img, ((data) =>
                {
                    
                    LoadSimulationList(data.GetID());

                }));
            }
        }
    }
    private void LoadSimulationList(string module)
    {
        string platform;
#if UNITY_EDITOR || UNITY_STANDALONE_WIN
        platform = "windows";
#else
        platform ="android";
#endif
        Debug.Log("Language Code " + LocalizationManager.Instance.CurrentLanguage);
        StartCoroutine(NewAPIManager.Instance.GetWebRequest(collections.GetSimulations(module, LocalizationManager.Instance.CurrentLanguage), false, (res) =>
        {
            Debug.Log(res);
            ProcessSimulations(res);
        },true, platform));
    }
    private void ProcessSimulations(string res)
    {
        DisableAlltheUI();
        EnableUI(1);
        if (string.IsNullOrEmpty(res)) return;
        foreach (Transform go in simulationParent)
        {
            Destroy(go.gameObject);
        }
        string wrappedJson = "{\"simulations\":" + res + "}";
        SimulationList response = JsonUtility.FromJson<SimulationList>(wrappedJson);
        if (response.simulations.Count > 0)
        {
            foreach (Simulation sim in response.simulations)
            {
                GameObject mod = Instantiate(simulationPrefab, simulationParent);
                string existingBundle = AssetBundleManager.Instance.DownloadValidator(sim.bundle_code, sim.bundle_uuid);
                if (!string.IsNullOrEmpty(existingBundle))
                {
                    mod.GetComponent<DataContainer>().UpdateButton(LocalizationManager.Instance.GetText("ContentManager_Launch"));
                    string path = Path.Combine(Application.persistentDataPath, sim.bundle_code);
                    Debug.Log(path);
                    mod.GetComponent<DataContainer>().UpdateLocalPath(path);
                    downloadStatus = SimulationStatus.Launch;
                }
                else
                {
                    mod.GetComponent<DataContainer>().UpdateButton(LocalizationManager.Instance.GetText("ContentManager_Download"));
                    downloadStatus= SimulationStatus.Download;
                }

                mod.GetComponent<DataContainer>().SetDetails(sim.id, sim.sim_name, sim.description,
                    sim.sim_icon, (data =>
                    {
                        string state =data.GetButtonText();
                        if (downloadStatus == SimulationStatus.Launch)
                        {
                            if (!ProgressBarManager.Instance.IsProgressBarAvailable())
                            {
                                string progName = LocalizationManager.Instance.GetText("ContentManager_Launching") + " " + data.GetCardName();
                                ProgressBarManager.Instance.UpdateProgressBar(0, progName, LocalizationManager.Instance.GetText("ContentManager_Launching"));
                                // Load Assetbundle from the Disk
                                data.UpdateButton(LocalizationManager.Instance.GetText("ContentManager_Launching"));
                                SessionManager.Instance.SetActiveSimulationID(data.GetSimulationID());

                                // Ensure this simulation's language asset is downloaded/cached before the
                                // bundle loads. SimulationLocalizationInjector reads it directly from disk
                                // once the bundle's own scene is running (see SimulationManager.Start()) -
                                // nothing here touches LocalizationManager, so the platform UI's own text
                                // is never disturbed while still browsing.
                                if (!string.IsNullOrEmpty(sim.lang_asset_path) &&
                                    string.Equals(sim.lang_asset_code, LocalizationManager.Instance.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
                                {
                                    LocalizationDownloader.Instance.DownloadSimulationLanguageAsset(sim.lang_asset_path, sim.bundle_code, sim.lang_asset_code);
                                }

                                AssetBundleManager.Instance.LoadAssetBundle(data.GetLocalBundlePath(),data.GetBundleCode());
                            }
                            else
                            {
                                MessageHandleManager.Instance.ShowMessage(MessageHandleManager.OperationStatus.hint, LocalizationManager.Instance.GetText("ContentManager_WaitResponse"));
                            }
                            
                        }
                        else if(downloadStatus == SimulationStatus.Download)
                        {
                            if (!ProgressBarManager.Instance.IsProgressBarAvailable())
                            {
                                string progName = LocalizationManager.Instance.GetText("ContentManager_Downloading") + data.GetCardName();
                                ProgressBarManager.Instance.UpdateProgressBar(0, progName, LocalizationManager.Instance.GetText("ContentManager_Downloading"));
                                // Load Assetbundle from the Disk
                                data.UpdateButton(LocalizationManager.Instance.GetText("ContentManager_Downloading"));
                                AssetBundleManager.Instance.DownloadAssetBundle(sim.bundle_path, sim.bundle_uuid, data);
                                // Pre-fetch this simulation's language asset alongside the bundle, if it
                                // targets the currently active language. Download+extract only - it is
                                // not loaded into LocalizationManager until Launch (see above).
                                if (!string.IsNullOrEmpty(sim.lang_asset_path) &&
                                    string.Equals(sim.lang_asset_code, LocalizationManager.Instance.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
                                {
                                    LocalizationDownloader.Instance.DownloadSimulationLanguageAsset(sim.lang_asset_path, sim.bundle_code, sim.lang_asset_code);
                                }

                                downloadStatus = SimulationStatus.Launch;
                            }
                            else
                            {
                                MessageHandleManager.Instance.ShowMessage(MessageHandleManager.OperationStatus.hint, LocalizationManager.Instance.GetText("ContentManager_WaitResponse"));
                            }
                            
                        }

                    }),sim.bundle_code,sim.bundle_path,sim.bundle_uuid,true);
            }
        }
    }

    public void LoadLeaderboard()
    {
        StartCoroutine(NewAPIManager.Instance.GetWebRequest(collections.GetLeaderboard(), false, (res) =>
        {
            ProcessLeaderBoard(res);
        }));
    }

    private void ProcessLeaderBoard(string res)
    {
        if (string.IsNullOrEmpty(res)) return;
        foreach (Transform go in leaderboardParent)
        {
            Destroy(go.gameObject);
        }
        string wrappedJson = "{\"leaderboards\":" + res + "}";
        LeaderboardList response = JsonUtility.FromJson<LeaderboardList>(wrappedJson);

        if (response.leaderboards.Count > 0) 
        {

            foreach(Leaderboard leaderboard in response.leaderboards)
            {

                GameObject leaderboartTile = Instantiate(leaderboardPrefab,leaderboardParent);
                LeaderBoardCard card = leaderboartTile.GetComponent<LeaderBoardCard>();

                string playerName= leaderboard.name;
                if (card != null) 
                {
                    if(leaderboard.id == PlatformGameManager.Instance.GetUserID())
                    {
                        playerName = "You";
                    }
                    card.SetRankDetails(playerName, leaderboard.rank, leaderboard.exp);
                }
            }
        }
    }

    public void LoadUserProgress()
    {
        StartCoroutine(NewAPIManager.Instance.GetWebRequest(collections.GetuserProgress(), false, (res) =>
        {
            ProcessUserProgress(res);
        }));
    }

    private void ProcessUserProgress(string res)
    {
        if (string.IsNullOrEmpty(res)) return;
        foreach (Transform go in progressCardParent)
        {
            Destroy(go.gameObject);
        }
        UserProgress response = JsonUtility.FromJson<UserProgress>(res);

        if (response != null) 
        {
            overallDataContainer.SetData(response.summary.total_attempts, response.summary.guided_attempts, response.summary.assessment_attempts,
                response.summary.total_score, response.summary.total_time_spent);

            if(response.simulations.Count > 0)
            {
                foreach(var  sim in response.simulations)
                {
                    GameObject go = Instantiate(progressCardPrefab, progressCardParent);
                    ProgressDataContainer card = go.GetComponent<ProgressDataContainer>();
                    card.SetData(sim.total_attempts,sim.guided_attempts,sim.assessment_attempts,sim.total_score,sim.total_time_spent,sim.simulation_name);
                }
            }
        }
    }
}


// Module Classes
[Serializable]
public class Module
{
    public int module_id;
    public string title;
    public string description;
    public string type;
    public string course_img;
}

[Serializable]
public class ModuleList
{
    public List<Module> modules;
}

// Simulation Classes
[Serializable]
public class Simulation
{
    public int id;
    public string sim_name;
    public string sim_icon;
    public string description;
    public string bundle_code;
    public string bundle_path;
    public string bundle_uuid;
    public string lang_asset_path;
    public string lang_asset_code;
}

[Serializable]
public class SimulationList
{
    public List<Simulation> simulations;
}

// Leadeboard Classes
[Serializable]
public class Leaderboard
{
    public int id;
    public string name;
    public int exp;
    public int rank;
}

[Serializable]
public class LeaderboardList
{
    public List<Leaderboard> leaderboards;
}

// User Progress Classes

[Serializable]
public class Summary
{
    public int total_attempts;
    public int guided_attempts;
    public int assessment_attempts;
    public int total_score;
    public int total_time_spent;
}

[Serializable]
public class SimulationProgress:Summary
{
    public int simulation_id;
    public string simulation_name;
}

[Serializable]
public class UserProgress
{
    public Summary summary;
    public List<SimulationProgress> simulations;
}
