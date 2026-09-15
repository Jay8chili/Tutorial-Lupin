using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Window -> Localization -> Auto Translate.
///
/// Machine-translates every untranslated row in a LocalizationTable and writes
/// the results back, flagged as machine output so they can be reviewed later.
///
/// WHY A BRIDGE: google-translate-api is a Node package. C# cannot call it, so
/// a tiny local Node server wraps it and this window talks to that over HTTP.
/// The server script is in translate-bridge/server.js.
///
/// Two backends, switchable in the window:
///
///   GTranslator  - the default. Calls Google's free translate_a/single
///                  endpoint directly via UnityWebRequest, the same approach
///                  GTranslatorAPI uses, ported so no DLLs or Node are needed.
///                  No key, no setup. Unofficial, so it rate-limits on volume.
///
///   LocalBridge  - a Node server wrapping google-translate-api. Only worth it
///                  if you already have that running.
///
///   GoogleCloud  - the official Cloud Translation v2 REST API with a key.
///                  Costs money, but does not break.
///
/// Everything runs in the editor. No translation code ships in your build.
/// </summary>
public class LocalizationAutoTranslate : EditorWindow
{
    private enum Backend { GTranslator, LocalBridge, GoogleCloud }

    private LocalizationTable table;
    private Backend backend = Backend.GTranslator;

    private string bridgeUrl = "http://localhost:3000/translate";
    private string apiKey = "";
    private string sourceLanguage = "en";
    private string targetLanguage = "es";

    private bool overwriteExisting;
    private bool retranslateStale = true;
    private int delayMs = 400;

    // Running state for the pumped request loop.
    private bool running;
    private int cursor;
    private int translated;
    private int failed;
    private List<int> queue;
    private UnityWebRequest inFlight;
    private double nextRequestTime;
    private string status = "";

    [MenuItem("Window/Localization/Auto Translate")]
    public static void Open()
    {
        LocalizationAutoTranslate window = GetWindow<LocalizationAutoTranslate>("Auto Translate");
        window.minSize = new Vector2(520, 420);
    }

    // ---------------------------------------------------------------------

    private void OnGUI()
    {
        table = (LocalizationTable)EditorGUILayout.ObjectField("Table", table, typeof(LocalizationTable), false);

        if (table == null)
        {
            EditorGUILayout.HelpBox("Assign a LocalizationTable.", MessageType.Info);
            return;
        }

        EditorGUILayout.Space(6);
        DrawBackendSettings();
        EditorGUILayout.Space(6);
        DrawOptions();
        EditorGUILayout.Space(6);
        DrawActions();

        if (!string.IsNullOrEmpty(status))
            EditorGUILayout.HelpBox(status, running ? MessageType.Info : MessageType.None);

        EditorGUILayout.Space(6);
        EditorGUILayout.HelpBox(
            "Machine translation is a first draft. Results are flagged 'machineTranslated' " +
            "so they can be reviewed in the table window before shipping.",
            MessageType.Warning);
    }

    private void DrawBackendSettings()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            backend = (Backend)EditorGUILayout.EnumPopup("Backend", backend);

            if (backend == Backend.GTranslator)
            {
                EditorGUILayout.LabelField("No key or setup needed. Unofficial endpoint - keep the delay up.",
                                           EditorStyles.miniLabel);
            }
            else if (backend == Backend.LocalBridge)
            {
                bridgeUrl = EditorGUILayout.TextField("Bridge URL", bridgeUrl);
                EditorGUILayout.LabelField("Run: node server.js (see translate-bridge/)", EditorStyles.miniLabel);
            }
            else
            {
                apiKey = EditorGUILayout.PasswordField("API key", apiKey);
                EditorGUILayout.LabelField("Cloud Translation v2. Key is editor-only, never built.", EditorStyles.miniLabel);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                sourceLanguage = EditorGUILayout.TextField("From", sourceLanguage);
                targetLanguage = EditorGUILayout.TextField("To", targetLanguage);
            }
        }
    }

    private void DrawOptions()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            overwriteExisting = EditorGUILayout.ToggleLeft(
                "Overwrite existing translations (including hand-written ones)", overwriteExisting);

            retranslateStale = EditorGUILayout.ToggleLeft(
                "Re-translate rows whose English changed since translating", retranslateStale);

            // The unofficial endpoint bans aggressively without a gap between calls.
            delayMs = EditorGUILayout.IntSlider("Delay between calls (ms)", delayMs, 0, 2000);
        }
    }

    private void DrawActions()
    {
        int pending = CountPending();

        EditorGUILayout.LabelField($"Rows needing translation: {pending} of {table.entries.Length}");

        using (new EditorGUI.DisabledScope(running || pending == 0))
        {
            if (GUILayout.Button($"Translate {pending} rows to '{targetLanguage}'", GUILayout.Height(28)))
                BeginRun();
        }

        if (running)
        {
            Rect r = EditorGUILayout.GetControlRect(false, 18);
            float progress = queue.Count == 0 ? 1f : (float)cursor / queue.Count;
            EditorGUI.ProgressBar(r, progress, $"{cursor}/{queue.Count}");

            if (GUILayout.Button("Cancel")) EndRun("Cancelled.");
        }
    }

    // ---------------------------------------------------------------------
    // Queue
    // ---------------------------------------------------------------------

    private int CountPending()
    {
        int count = 0;
        for (int i = 0; i < table.entries.Length; i++)
            if (NeedsTranslation(i)) count++;

        return count;
    }

    private bool NeedsTranslation(int index)
    {
        if (string.IsNullOrWhiteSpace(table.entries[index].sourceText)) return false;

        string existing = table.GetTranslation(index, targetLanguage);

        if (string.IsNullOrEmpty(existing)) return true;
        if (overwriteExisting) return true;

        // A machine row whose English has since changed is stale - and only a
        // machine row: never silently replace a human's work.
        if (retranslateStale
            && table.IsMachineTranslated(index, targetLanguage)
            && table.IsStale(index, targetLanguage)) return true;

        return false;
    }

    private void BeginRun()
    {
        queue = new List<int>();
        for (int i = 0; i < table.entries.Length; i++)
            if (NeedsTranslation(i)) queue.Add(i);

        cursor = 0;
        translated = 0;
        failed = 0;
        running = true;
        nextRequestTime = 0;
        status = "Translating...";

        Undo.RecordObject(table, "Auto Translate");

        // Editor coroutines need a package; pumping update is dependency-free.
        EditorApplication.update += Pump;
    }

    private void EndRun(string message)
    {
        EditorApplication.update -= Pump;

        if (inFlight != null) { inFlight.Dispose(); inFlight = null; }

        running = false;
        status = $"{message} Translated {translated}, failed {failed}.";

        EditorUtility.SetDirty(table);
        AssetDatabase.SaveAssets();
        Repaint();
    }

    // ---------------------------------------------------------------------
    // Request pump
    // ---------------------------------------------------------------------

    private void Pump()
    {
        if (!running) return;

        if (inFlight == null)
        {
            if (cursor >= queue.Count) { EndRun("Done."); return; }

            // Spacing calls keeps the unofficial endpoint from rate-limiting.
            if (EditorApplication.timeSinceStartup < nextRequestTime) return;

            string source = table.entries[queue[cursor]].sourceText;
            inFlight = BuildRequest(source);
            inFlight.SendWebRequest();
            return;
        }

        if (!inFlight.isDone) return;

        int entryIndex = queue[cursor];
        string sourceText = table.entries[entryIndex].sourceText;

        if (inFlight.result == UnityWebRequest.Result.Success)
        {
            string result = ParseResponse(inFlight.downloadHandler.text);

            if (!string.IsNullOrEmpty(result))
            {
                table.SetTranslation(entryIndex, targetLanguage, result,
                                     machineTranslated: true, translatedFrom: sourceText);
                translated++;
            }
            else
            {
                Debug.LogWarning($"[AutoTranslate] Empty result for '{table.entries[entryIndex].key}'.");
                failed++;
            }
        }
        else
        {
            Debug.LogWarning($"[AutoTranslate] '{table.entries[entryIndex].key}' failed: {inFlight.error}");
            failed++;
        }

        inFlight.Dispose();
        inFlight = null;

        cursor++;
        nextRequestTime = EditorApplication.timeSinceStartup + (delayMs / 1000.0);

        Repaint();
    }

    private UnityWebRequest BuildRequest(string text)
    {
        if (backend == Backend.GTranslator)
            return GTranslatorClient.CreateRequest(text, sourceLanguage, targetLanguage);

        if (backend == Backend.LocalBridge)
        {
            BridgeRequest payload = new BridgeRequest
            {
                text = text,
                from = sourceLanguage,
                to = targetLanguage
            };

            UnityWebRequest request = new UnityWebRequest(bridgeUrl, "POST");
            byte[] body = Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload));

            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = 20;

            return request;
        }

        // Cloud Translation v2 takes query parameters, so everything is escaped.
        string url = "https://translation.googleapis.com/language/translate/v2"
                   + $"?key={UnityWebRequest.EscapeURL(apiKey)}"
                   + $"&q={UnityWebRequest.EscapeURL(text)}"
                   + $"&source={sourceLanguage}&target={targetLanguage}&format=text";

        UnityWebRequest cloud = UnityWebRequest.Get(url);
        cloud.timeout = 20;
        return cloud;
    }

    private string ParseResponse(string json)
    {
        try
        {
            if (backend == Backend.GTranslator)
                return GTranslatorClient.ParseResponse(json);

            if (backend == Backend.LocalBridge)
                return JsonUtility.FromJson<BridgeResponse>(json).text;

            CloudResponse cloud = JsonUtility.FromJson<CloudResponse>(json);
            if (cloud.data.translations == null || cloud.data.translations.Length == 0) return null;

            // format=text still returns HTML-escaped entities.
            return Unescape(cloud.data.translations[0].translatedText);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[AutoTranslate] Could not parse response: {e.Message}");
            return null;
        }
    }

    private static string Unescape(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        return value.Replace("&#39;", "'")
                    .Replace("&quot;", "\"")
                    .Replace("&amp;", "&")
                    .Replace("&lt;", "<")
                    .Replace("&gt;", ">");
    }

    // ---- JSON shapes -----------------------------------------------------

    [Serializable] private struct BridgeRequest { public string text; public string from; public string to; }
    [Serializable] private struct BridgeResponse { public string text; }

    [Serializable] private struct CloudTranslation { public string translatedText; }
    [Serializable] private struct CloudData { public CloudTranslation[] translations; }
    [Serializable] private struct CloudResponse { public CloudData data; }
}
