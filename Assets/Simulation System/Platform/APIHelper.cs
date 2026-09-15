using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace Platform
{
    public static class APIHelper
    {
        public static IEnumerator GetWebRequest(string url, Action<long, string>callback)
        {
            string recorderToken = PlayerPrefsManager.Instance.GetAuthtoken();
            using (UnityWebRequest uwr = UnityWebRequest.Get(url))
            {
                uwr.SetRequestHeader("Authorization", "Bearer " + recorderToken);
                uwr.downloadHandler = new DownloadHandlerBuffer();
            
                yield return uwr.SendWebRequest();
            
                string responseString = uwr.downloadHandler.text;
                long responseCode = uwr.responseCode;
                callback(responseCode, responseString);
            }
        }
    
        public static IEnumerator PostWebRequest(string url, string jsonString, Action<long, string> callback)
        {
            string token = PlayerPrefsManager.Instance.GetAuthtoken();
            var uwr = new UnityWebRequest(url, "POST");
            byte[] jsonToSend = new System.Text.UTF8Encoding().GetBytes(jsonString);
            uwr.uploadHandler = (UploadHandler)new UploadHandlerRaw(jsonToSend);
            uwr.downloadHandler = (DownloadHandler)new DownloadHandlerBuffer();
            uwr.SetRequestHeader("Authorization", "Bearer " + token);
            uwr.SetRequestHeader("Content-Type", "application/json");
        
            yield return uwr.SendWebRequest();
        
            if (uwr.result == UnityWebRequest.Result.ConnectionError)
            {
            }

            string responseString = uwr.downloadHandler.text;
            long responseCode = uwr.responseCode;

            callback(responseCode, responseString);
        }
        
        public static IEnumerator GetTexture(string url, Action<Texture2D> callback, bool mipMapRequired)
        {
            using (UnityWebRequest uwr = UnityWebRequestTexture.GetTexture(url))
            {
                yield return uwr.SendWebRequest();

                if (uwr.result != UnityWebRequest.Result.Success)
                {
                    callback(null);
                }
                else
                {
                    // Get downloaded asset bundle
                    Texture2D Tex2D;
                    Tex2D = DownloadHandlerTexture.GetContent(uwr);

                    if (Tex2D == null)
                    {
                        callback(null);
                    }
                    else if (mipMapRequired)
                    {
                        var mmTexture = new Texture2D(Tex2D.width, Tex2D.height, Tex2D.format, true);
                        mmTexture.SetPixelData(Tex2D.GetRawTextureData<byte>(), 0);
                        mmTexture.Apply(true, true);
                        // Tex2D.Apply(false);
                        callback(mmTexture);
                    }
                    else
                    {
                        callback(Tex2D);
                    }
                }
            }
        }
    }
}