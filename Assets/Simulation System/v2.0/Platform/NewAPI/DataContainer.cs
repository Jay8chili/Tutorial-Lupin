using LightSide;
using SimulationSystem.V02.Utility;
using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class DataContainer : MonoBehaviour
{
    [SerializeField] private int id;
    [SerializeField] private TextMeshProUGUI cardName;
    [SerializeField] private UniText cardNameUni;
    [SerializeField] private TextMeshProUGUI description;
    [SerializeField] private UniText descriptionUni;
    [SerializeField] private Image icon;
    [SerializeField] private Sprite defaultIcon;
    [SerializeField] private Button selectButton;
    [SerializeField] private TextMeshProUGUI buttonText;
    [SerializeField] private UniText buttonTextUni;

    private void Awake()
    {
        TextCompat.ResolveUniText(ref cardNameUni, cardName);
        TextCompat.ResolveUniText(ref descriptionUni, description);
        TextCompat.ResolveUniText(ref buttonTextUni, buttonText);
    }

    private string bundleCode;
    private string bundleUrl;
    private string bundleUUID;
    private string localPath;

    public void SetDetails(int id, string cardName, string descption, string iconUrl, 
        Action<DataContainer> onClickAction,string code=null,string url=null,string uuid=null,bool bundleData=false)
    {
        this.id = id;
        TextCompat.SetText(cardNameUni, this.cardName, cardName);
        TextCompat.SetText(descriptionUni, this.description, descption);
        if (iconUrl != null)
        {
            StartCoroutine(NewAPIManager.Instance.DownloadTexture(iconUrl, icon, defaultIcon));

        }
        else
        {
            this.icon.sprite = defaultIcon;
        }

        if (bundleData)
        {
            bundleCode = code;
            bundleUrl = url;
            bundleUUID = uuid;
        }
        var text = selectButton.GetComponentInChildren<UniText>().Text;
        selectButton.GetComponentInChildren<UniText>().Text = LocalizationManager.Instance.GetTextBySource(text);
        this.selectButton.onClick.AddListener(() => {
            onClickAction(this);
            
        });
    }

    public string GetBundleCode()
    {
        return this.bundleCode;
    }
    public string GetBundleURL() 
    {
        return this.bundleUrl;
    }
    public string GetBundleUUID() 
    { 
        return this.bundleUUID;
    }
    public string GetID()
    {
        return this.id.ToString();
    }

    public int GetSimulationID()
    {
        return this.id;
    }

    public void UpdateButton(string val)
    {
        TextCompat.SetText(buttonTextUni, buttonText, val);
    }

    public void UpdateLocalPath(string path)
    {
        localPath = path;
    }

    public string GetButtonText()
    {
        return TextCompat.GetText(buttonTextUni, buttonText);
    }

    public string GetLocalBundlePath()
    {
        return localPath;
    }

    public string GetCardName()
    {
        return TextCompat.GetText(cardNameUni, cardName);
    }
}
