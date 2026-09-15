using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using System.Text;
using System.Collections;
using UnityEngine.Networking;
using System.Collections.Generic;
using Unity.EditorCoroutines.Editor;
namespace SimulationSystem.V02.Editor
{
    public class TTSAudioGeneratorWindow : EditorWindow, ISerializationCallbackReceiver
    {
        #region Serialization Fields
        [SerializeField] private List<int> serializedSelectedKeys = new List<int>();
        [SerializeField] private List<int> serializedOverrideKeys = new List<int>();
        [SerializeField] private List<string> serializedOverrideValues = new List<string>();
        #endregion
        #region Constants & Fields
        // ================= CONFIG =================
        private const string VOICES_API = "https://p6mhete3gf.ap-south-1.awsapprunner.com/api/voices";
        private const string GENERATE_API = "https://p6mhete3gf.ap-south-1.awsapprunner.com/api/generate-steps-audio";
        private const string PREVIEW_API = "https://p6mhete3gf.ap-south-1.awsapprunner.com/api/generate-audio";
        [HideInInspector]
        private string authKey = "9c1f227e04bf6bb0fd3f0a59767976b938b21cbb619ee409e7e68b4377d674e8";
        // ================= LAYOUT METRICS =================
        private const float SPACING_SMALL = 5f;
        private const float SPACING_MEDIUM = 10f;
        private const float SPACING_LARGE = 20f;
        private const float BUTTON_WIDTH_X = 25f;
        private const float BUTTON_WIDTH_SELECT_ALL = 80f;
        private const float BUTTON_WIDTH_SELECT_NONE = 90f;
        private const float BUTTON_WIDTH_CLEAR_LIST = 80f;
        private const float BUTTON_WIDTH_USE_ORIGINAL = 125f;
        private const float BUTTON_WIDTH_USE_PREVIEW = 115f;

        private const float BUTTON_HEIGHT_GENERATE = 35f;
        private const float BUTTON_HEIGHT_CLEAR_ALL = 28f;

        private const float TOGGLE_WIDTH = 18f;
        private const float PREVIEW_TEXT_HEIGHT = 60f;
        private const float ACTIVITY_LOG_HEIGHT = 88f;

        private const float TEXT_AREA_MIN_HEIGHT_READONLY = 36f;
        private const float TEXT_AREA_MIN_HEIGHT_OVERRIDE = 42f;
        private const float DRAG_DROP_AREA_HEIGHT = 50f;
        private static readonly Color ClearButtonColor = new Color(1f, 0.45f, 0.45f);
        // ================= LANGUAGE =================
        private string[] languageOptions = new string[] { "en-US", "en-IN", "hi-IN", "es-ES" };
        private int selectedLanguageIndex = 0;
        private string SelectedLanguage => languageOptions[selectedLanguageIndex];
        // ================= VOICES =================
        private string selectedVoice = null;
        private List<string> availableVoices = new List<string>();
        // ================= PREVIEW =================
        private string previewText = "Hello, this is a test prompt.";
        private AudioClip previewClip;
        private bool isPreviewLoading = false;
        private bool isPreviewPlaying = false;
        private GameObject previewAudioObject;
        private AudioSource previewAudioSource;
        // ================= USER LIST =================
        private List<GameObject> simulationObjects = new List<GameObject>();
        // These are editor-window-only values. They intentionally never modify SimulationState.promptText.
        private readonly HashSet<int> selectedObjectIds = new HashSet<int>();
        private readonly Dictionary<int, string> promptOverrides = new Dictionary<int, string>();
        private Vector2 scroll;
        // ================= GENERATION FEEDBACK =================
        private bool isGenerating;
        private bool cancelGenerationRequested;
        private float generationProgress;
        private string generationStatus = "Ready.";
        private int generationSuccessCount;
        private int generationFailCount;
        private readonly List<string> activityLog = new List<string>();
        // Tracks which SimulationState card is in Edit mode (by instance ID). -1 = none.
        private int editingStateId = -1;
        // Tracks which card's preview is loading (by instance ID). -1 = none.
        private int previewingStateId = -1;
        // Tracks per-card Audio Text foldout state (by SimulationState instance ID).
        private readonly Dictionary<int, bool> audioTextFoldouts = new Dictionary<int, bool>();
        // Rect of the Simulation Steps panel — used for whole-panel drag-drop hit testing.
        private Rect stepsPanelRect;
        // ================= WIZARD INTEGRATION =================
        // Populated by ReceiveFromWizard() — holds per-step multi-language data.
        // Null when in manual mode (user-dragged objects only).
        private List<WizardStepAudioData> _wizardData = null;
        // Maps common TSV column header names → TTS language codes.
        private static readonly Dictionary<string, string> HeaderToLangCode =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
            { "english", "en-US" }, { "en",      "en-US" }, { "en-us", "en-US" }, { "en-in", "en-IN" },
            { "hindi",   "hi-IN" }, { "hi",      "hi-IN" }, { "hi-in", "hi-IN" },
            { "spanish", "es-ES" }, { "es",      "es-ES" }, { "es-es", "es-ES" },
            { "french",  "fr-FR" }, { "fr",      "fr-FR" },
            { "german",  "de-DE" }, { "de",      "de-DE" },
            };
        // ================= UI FOLDOUT STATES =================
        [SerializeField] private bool foldoutConfigure = false;
        [SerializeField] private bool foldoutSimulationSteps = true;
        [SerializeField] private bool foldoutGenerationProgress = false;
        #endregion
        #region Unity Lifecycle
        [MenuItem("Tools/TTS Audio Generator")]
        public static void ShowWindow()
        {
            GetWindow<TTSAudioGeneratorWindow>("TTS Audio Generator");
        }
        /// <summary>Opens the window if not already open and returns the instance.</summary>
        public static TTSAudioGeneratorWindow GetOrOpen()
        {
            var w = GetWindow<TTSAudioGeneratorWindow>("TTS Audio Generator");
            w.Focus();
            return w;
        }
        private void OnEnable()
        {
            foreach (var obj in simulationObjects)
            {
                if (obj != null)
                    selectedObjectIds.Add(obj.GetInstanceID());
            }
        }
        private void OnDisable()
        {
            if (previewAudioObject != null)
            {
                DestroyImmediate(previewAudioObject);
            }
            EditorApplication.update -= CheckPreviewPlayback;
        }
        public void OnBeforeSerialize()
        {
            serializedSelectedKeys.Clear();
            foreach (var id in selectedObjectIds)
            {
                serializedSelectedKeys.Add(id);
            }
            serializedOverrideKeys.Clear();
            serializedOverrideValues.Clear();
            foreach (var kvp in promptOverrides)
            {
                serializedOverrideKeys.Add(kvp.Key);
                serializedOverrideValues.Add(kvp.Value);
            }
        }
        public void OnAfterDeserialize()
        {
            selectedObjectIds.Clear();
            foreach (var id in serializedSelectedKeys)
            {
                selectedObjectIds.Add(id);
            }
            promptOverrides.Clear();
            for (int i = 0; i < Mathf.Min(serializedOverrideKeys.Count, serializedOverrideValues.Count); i++)
            {
                promptOverrides[serializedOverrideKeys[i]] = serializedOverrideValues[i];
            }
        }
        #endregion
        #region GUI Rendering
        private void OnGUI()
        {
            if (isGenerating)
                foldoutGenerationProgress = true;

            DrawHeader();

            // 1. Configure Foldout
            foldoutConfigure = EditorGUILayout.Foldout(foldoutConfigure, "Configure", true);
            if (foldoutConfigure)
            {
                EditorGUILayout.BeginVertical("box");
                DrawToolbar();
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(SPACING_SMALL);
            }

            // 2. Simulation Steps Foldout
            foldoutSimulationSteps = EditorGUILayout.Foldout(foldoutSimulationSteps, "Simulation Steps", true);
            if (foldoutSimulationSteps)
            {
                EditorGUILayout.BeginVertical("box", GUILayout.ExpandHeight(true));
                DrawStepList();
                EditorGUILayout.Space(SPACING_SMALL);
                if (GUILayout.Button(new GUIContent("Add Simulation Step", "Add a new empty Simulation Step slot.")))
                    simulationObjects.Add(null);
                EditorGUILayout.EndVertical();

                // Capture the full panel rect for whole-panel drag-drop.
                if (Event.current.type == EventType.Repaint)
                    stepsPanelRect = GUILayoutUtility.GetLastRect();

                HandlePanelDragDrop();
                EditorGUILayout.Space(SPACING_SMALL);
            }

            // 3. Generation Progress Foldout
            foldoutGenerationProgress = EditorGUILayout.Foldout(foldoutGenerationProgress, "Generation Progress", true);
            if (foldoutGenerationProgress)
            {
                EditorGUILayout.BeginVertical("box");
                DrawGenerationPanel();
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(SPACING_SMALL);
            }

            DrawBottomButtons();
        }
        private void DrawHeader()
        {
            GUILayout.Label("TTS Audio Generator", EditorStyles.boldLabel);
            EditorGUILayout.Space();
        }
        private void DrawToolbar()
        {
            selectedLanguageIndex = EditorGUILayout.Popup(
                new GUIContent("Language", "The language code used for audio generation and voice fetching."),
                selectedLanguageIndex,
                languageOptions
            );
            if (GUILayout.Button(new GUIContent("Fetch Voices", "Download the latest available voices for the selected language.")))
                EditorCoroutineUtility.StartCoroutineOwnerless(FetchVoicesCoroutine());
            EditorGUILayout.Space(SPACING_SMALL);
            DrawVoiceDropdown();
        }
        private void DrawPreviewSection()
        {
            EditorGUILayout.LabelField("Preview Text:");
            previewText = EditorGUILayout.TextArea(previewText, GUILayout.Height(PREVIEW_TEXT_HEIGHT));
            EditorGUILayout.Space(SPACING_SMALL);
            GUI.enabled = !isPreviewLoading;
            if (GUILayout.Button("Generate Preview"))
            {
                EditorCoroutineUtility.StartCoroutineOwnerless(PreviewAudioCoroutine());
            }
            GUI.enabled = true;
            if (isPreviewLoading)
            {
                EditorGUILayout.HelpBox("Generating preview...", MessageType.Info);
            }
            if (previewClip != null)
            {
                EditorGUILayout.BeginHorizontal();
                if (!isPreviewPlaying)
                {
                    if (GUILayout.Button("Play"))
                    {
                        PlayPreviewClip();
                    }
                }
                else
                {
                    if (GUILayout.Button("Stop"))
                    {
                        StopPreviewClip();
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
        }
        private void DrawStepList()
        {
            // ── Header row ──────────────────────────────────────────────────
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.LabelField(
                new GUIContent($"Simulation Steps ({simulationObjects.Count})",
                    "? Only checked Simulation Steps are generated.\nAudio Text is temporary and never modifies the original Simulation Prompt."),
                EditorStyles.boldLabel);

            if (GUILayout.Button(new GUIContent("All",  "Select every Simulation Step."),  GUILayout.Width(36)))
                SetAllObjectsSelected(true);
            if (GUILayout.Button(new GUIContent("None", "Deselect every Simulation Step."), GUILayout.Width(40)))
                SetAllObjectsSelected(false);
            if (GUILayout.Button(new GUIContent("Clear", "Remove all Simulation Steps from the list."), GUILayout.Width(45)))
                ClearObjectList();

            EditorGUILayout.EndHorizontal();

            // ── Scrollable card list ────────────────────────────────────────
            scroll = EditorGUILayout.BeginScrollView(scroll);

            if (simulationObjects.Count == 0)
            {
                // Friendly empty state
                GUILayout.FlexibleSpace();
                EditorGUILayout.BeginVertical();
                GUILayout.Space(20);
                GUIStyle centeredGrey = new GUIStyle(EditorStyles.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap  = true,
                    normal    = { textColor = Color.grey }
                };
                GUILayout.Label("Drag Simulation Steps here", centeredGrey);
                GUILayout.Label("or click \"Add Simulation Step\"", centeredGrey);
                GUILayout.Space(20);
                EditorGUILayout.EndVertical();
                GUILayout.FlexibleSpace();
            }
            else
            {
                int removeIndex = -1;
                for (int i = 0; i < simulationObjects.Count; i++)
                {
                    DrawStepCard(i, out bool requestRemove);
                    if (requestRemove) removeIndex = i;
                }
                if (removeIndex >= 0)
                    RemoveObjectAt(removeIndex);
            }

            EditorGUILayout.EndScrollView();
        }
        private void DrawStepCard(int i, out bool requestRemove)
        {
            requestRemove = false;
            EditorGUILayout.BeginVertical("box");

            // ── Row 1: toggle + object field + remove ───────────────────────
            EditorGUILayout.BeginHorizontal();

            bool isSelected = simulationObjects[i] != null && IsObjectSelected(simulationObjects[i]);
            bool newSelection = EditorGUILayout.Toggle(isSelected, GUILayout.Width(TOGGLE_WIDTH));
            if (simulationObjects[i] != null && newSelection != isSelected)
                SetObjectSelected(simulationObjects[i], newSelection);

            var previousObject = simulationObjects[i];
            var updatedObject = (GameObject)EditorGUILayout.ObjectField(previousObject, typeof(GameObject), true);
            if (updatedObject != previousObject)
            {
                if (previousObject != null)
                    selectedObjectIds.Remove(previousObject.GetInstanceID());
                simulationObjects[i] = updatedObject;
                SetObjectSelected(updatedObject, true);
            }

            if (GUILayout.Button(new GUIContent("X", "Remove this step from the list."), GUILayout.Width(BUTTON_WIDTH_X)))
                requestRemove = true;

            EditorGUILayout.EndHorizontal();

            // ── Body: only when a GameObject is assigned ────────────────────
            if (simulationObjects[i] != null)
            {
                SimulationState state = simulationObjects[i].GetComponent<SimulationState>();

                if (state == null)
                {
                    EditorGUILayout.HelpBox("No SimulationState component found!", MessageType.Warning);
                }
                else
                {
                    int stateId = state.GetInstanceID();

                    // Auto-populate override with original prompt on first encounter.
                    if (!promptOverrides.ContainsKey(stateId))
                        promptOverrides[stateId] = state.promptText ?? string.Empty;

                    bool isModified  = promptOverrides.TryGetValue(stateId, out string audioText)
                                       && audioText != state.promptText;
                    bool isEditing   = editingStateId == stateId;
                    bool isPreviewing = previewingStateId == stateId;

                    // ── Simulation Text (always read-only) ──────────────────
                    EditorGUILayout.LabelField(new GUIContent("Simulation Text", "Original prompt from the SimulationState. Never modified."), EditorStyles.miniBoldLabel);
                    EditorGUILayout.SelectableLabel(
                        Truncate(state.promptText, 200),
                        EditorStyles.textArea,
                        GUILayout.MinHeight(TEXT_AREA_MIN_HEIGHT_READONLY));

                    EditorGUILayout.Space(3);

                    // ── Audio Text foldout (collapsed by default) ───────────
                    if (!audioTextFoldouts.ContainsKey(stateId))
                        audioTextFoldouts[stateId] = false;

                    EditorGUILayout.BeginHorizontal();
                    audioTextFoldouts[stateId] = EditorGUILayout.Foldout(
                        audioTextFoldouts[stateId],
                        new GUIContent("Audio Text", "Text sent to the TTS engine. Defaults to the Simulation Text."),
                        true);
                    GUILayout.FlexibleSpace();
                    GUIStyle statusStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        normal = { textColor = isModified ? new Color(1f, 0.75f, 0.1f) : new Color(0.4f, 0.9f, 0.4f) }
                    };
                    GUILayout.Label(isModified ? "Modified" : "Original", statusStyle);
                    EditorGUILayout.EndHorizontal();

                    if (audioTextFoldouts[stateId])
                    {
                        // ── Audio Text field (read-only unless editing) ─────
                        GUI.enabled = isEditing;
                        string newAudio = EditorGUILayout.TextArea(
                            audioText ?? string.Empty,
                            GUILayout.MinHeight(TEXT_AREA_MIN_HEIGHT_READONLY));
                        GUI.enabled = true;

                        if (isEditing && newAudio != audioText)
                            promptOverrides[stateId] = newAudio;

                        EditorGUILayout.Space(3);

                        // ── Action buttons row ──────────────────────────────
                        EditorGUILayout.BeginHorizontal();

                        // Edit / Done
                        if (!isEditing)
                        {
                            if (GUILayout.Button(new GUIContent("Edit", "Unlock Audio Text for editing.")))
                                editingStateId = stateId;
                        }
                        else
                        {
                            if (GUILayout.Button(new GUIContent("Done", "Lock Audio Text after editing.")))
                                editingStateId = -1;
                        }

                        // Reset — force a GUI refresh immediately
                        if (GUILayout.Button(new GUIContent("Reset", "Restore Audio Text back to the original Simulation Prompt.")))
                        {
                            promptOverrides[stateId] = state.promptText ?? string.Empty;
                            if (editingStateId == stateId) editingStateId = -1;
                            GUI.FocusControl(null);
                            Repaint();
                        }

                        // ▶ Preview — single button: generate + auto-play
                        GUI.enabled = !isPreviewing && !isPreviewLoading;
                        if (GUILayout.Button(new GUIContent(
                                isPreviewing ? "Previewing..." : "\u25b6 Preview",
                                "Generate and immediately play a preview of the Audio Text.")))
                        {
                            if (CheckInternetConnection())
                            {
                                previewingStateId = stateId;
                                previewText = promptOverrides.TryGetValue(stateId, out string pt)
                                              && !string.IsNullOrWhiteSpace(pt) ? pt : state.promptText;
                                EditorCoroutineUtility.StartCoroutineOwnerless(PreviewAudioCoroutine());
                            }
                        }
                        GUI.enabled = true;

                        EditorGUILayout.EndHorizontal();
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }
        /// <summary>Handles drag-and-drop for the entire Simulation Steps panel.</summary>
        private void HandlePanelDragDrop()
        {
            Event evt = Event.current;

            // Only respond if the mouse is within the captured panel rect.
            if (!stepsPanelRect.Contains(evt.mousePosition)) return;

            switch (evt.type)
            {
                case EventType.DragUpdated:
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    // Draw highlight overlay.
                    EditorGUI.DrawRect(stepsPanelRect, new Color(0.3f, 0.6f, 1f, 0.15f));
                    GUI.Box(stepsPanelRect, string.Empty);
                    var centreStyle = new GUIStyle(EditorStyles.boldLabel)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        normal    = { textColor = new Color(0.3f, 0.6f, 1f) }
                    };
                    GUI.Label(stepsPanelRect, "Drop Simulation Steps Here", centreStyle);
                    Repaint();
                    evt.Use();
                    break;

                case EventType.DragPerform:
                    DragAndDrop.AcceptDrag();
                    foreach (var draggedObject in DragAndDrop.objectReferences)
                    {
                        if (draggedObject is GameObject go && !simulationObjects.Contains(go))
                        {
                            simulationObjects.Add(go);
                            SetObjectSelected(go, true);
                        }
                    }
                    Repaint();
                    evt.Use();
                    break;

                case EventType.DragExited:
                    Repaint();
                    break;
            }
        }
        private void DrawGenerationPanel()
        {
            // Status message
            MessageType msgType = isGenerating ? MessageType.Info
                : generationFailCount > 0    ? MessageType.Warning
                : generationSuccessCount > 0 ? MessageType.None
                : MessageType.None;
            EditorGUILayout.HelpBox(generationStatus, msgType);

            if (isGenerating || generationSuccessCount > 0 || generationFailCount > 0)
            {
                // Progress bar
                Rect progressRect = GUILayoutUtility.GetRect(18, 18, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(progressRect, generationProgress,
                    isGenerating ? $"{Mathf.RoundToInt(generationProgress * 100f)}%" : "Done");

                EditorGUILayout.Space(3);

                // Success / Fail counters
                EditorGUILayout.BeginHorizontal();
                GUIStyle successStyle = new GUIStyle(EditorStyles.miniLabel)
                    { normal = { textColor = new Color(0.4f, 0.9f, 0.4f) } };
                GUIStyle failStyle = new GUIStyle(EditorStyles.miniLabel)
                    { normal = { textColor = new Color(1f, 0.45f, 0.45f) } };
                GUILayout.Label($"\u2714 Success: {generationSuccessCount}", successStyle);
                GUILayout.Space(12);
                GUILayout.Label($"\u2716 Failed: {generationFailCount}", failStyle);
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();

                if (isGenerating)
                {
                    EditorGUILayout.Space(3);
                    if (GUILayout.Button(new GUIContent("Cancel After Current Request", "Stops generation after the current step finishes.")))
                        cancelGenerationRequested = true;
                }
            }
        }
        private void DrawBottomButtons()
        {
            EditorGUILayout.Space(SPACING_SMALL);

            GUI.enabled = simulationObjects.Count > 0 && !isGenerating;
            GUI.backgroundColor = new Color(0.4f, 0.9f, 0.4f);
            if (GUILayout.Button(
                new GUIContent("Generate Audio", "Generate audio only for checked Simulation Steps."),
                GUILayout.Height(BUTTON_HEIGHT_GENERATE)))
            {
                int selectedCount = CountSelectedValidObjects();
                if (selectedCount == 0)
                    AddLog("No selected Simulation Steps with text are ready for generation.", MessageType.Warning);
                else if (CheckInternetConnection() && EditorUtility.DisplayDialog("Generate Audio",
                    $"Generate audio for {selectedCount} selected step(s)?\n\nExisting files are preserved. New clips are versioned and assigned to the SimulationState.",
                    "Generate", "Cancel"))
                {
                    isGenerating = true;
                    cancelGenerationRequested = false;
                    foldoutGenerationProgress = true;
                    generationSuccessCount = 0;
                    generationFailCount = 0;
                    EditorCoroutineUtility.StartCoroutineOwnerless(GenerateAudioCoroutine());
                }
            }
            GUI.backgroundColor = Color.white;

            GUI.color   = ClearButtonColor;
            GUI.enabled = simulationObjects.Count > 0 && !isGenerating;
            if (GUILayout.Button(
                new GUIContent("Clear Audio from All", "Remove generated audio clips from every Simulation Step in the list."),
                GUILayout.Height(BUTTON_HEIGHT_CLEAR_ALL)))
            {
                if (EditorUtility.DisplayDialog("Clear Assigned Audio",
                    "This removes the assigned prompt audio from every Simulation Step currently in the list.\n\nGenerated files are kept on disk. You can undo this in Unity.",
                    "Clear Assigned Audio", "Cancel"))
                    ClearAudioFromAll();
            }
            GUI.color   = Color.white;
            GUI.enabled = true;
        }
        private void DrawVoiceDropdown()
        {
            List<string> options = new List<string>();
            options.Add("Default");
            options.AddRange(availableVoices);
            int selectedIndex = 0;
            if (!string.IsNullOrEmpty(selectedVoice))
            {
                int index = options.IndexOf(selectedVoice);
                if (index > 0)
                    selectedIndex = index;
            }
            selectedIndex = EditorGUILayout.Popup("Voice", selectedIndex, options.ToArray());
            selectedVoice = selectedIndex == 0 ? null : options[selectedIndex];
        }
        #endregion
        #region Audio Generation & Preview
        private bool CheckInternetConnection()
        {
            if (Application.internetReachability == NetworkReachability.NotReachable)
            {
                EditorUtility.DisplayDialog("No Internet Connection",
                    "Hey Simulation Dev 👋\n\nI think your relationship with the Internet is going through a rough patch. 💔🌐\n\nPreview and Audio Generation both require an active internet connection.\n\nPlease reconnect to Wi-Fi or your network and try again.",
                    "Reconnect & Try Again");
                return false;
            }
            return true;
        }
        
        private IEnumerator PreviewAudioCoroutine()
        {
            if (string.IsNullOrWhiteSpace(previewText))
            {
                previewingStateId = -1;
                yield break;
            }

            isPreviewLoading = true;
            Repaint();

            var requestBody = new PreviewRequest
            {
                languageCode = SelectedLanguage,
                voiceName    = selectedVoice ?? "",
                text         = previewText.Trim()
            };

            string json        = JsonUtility.ToJson(requestBody);
            byte[] jsonToSend  = Encoding.UTF8.GetBytes(json);

            using (UnityWebRequest request = new UnityWebRequest(PREVIEW_API, "POST"))
            {
                request.uploadHandler   = new UploadHandlerRaw(jsonToSend);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("x-auth-key", authKey);
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    isPreviewLoading  = false;
                    previewingStateId = -1;
                    Repaint();
                    yield break;
                }

                byte[] audioBytes = request.downloadHandler.data;
                if (audioBytes == null || audioBytes.Length == 0)
                {
                    isPreviewLoading  = false;
                    previewingStateId = -1;
                    Repaint();
                    yield break;
                }

                string folderPath = "Assets/GeneratedAudio";
                if (!Directory.Exists(folderPath))
                    Directory.CreateDirectory(folderPath);

                string filePath = Path.Combine(folderPath, "PreviewAudio.mp3");
                File.WriteAllBytes(filePath, audioBytes);
                AssetDatabase.ImportAsset(filePath, ImportAssetOptions.ForceUpdate);
                AssetDatabase.Refresh();
                AssetDatabase.ImportAsset(filePath, ImportAssetOptions.ForceSynchronousImport);

                previewClip = AssetDatabase.LoadAssetAtPath<AudioClip>(filePath);
                if (previewClip == null)
                {
                }
                else
                    PlayPreviewClip(); // auto-play after generate
            }

            isPreviewLoading  = false;
            previewingStateId = -1;
            Repaint();
        }
        public void PlayPreviewClip()
        {
            if (previewClip == null)
            {
                return;
            }
            if (previewAudioObject == null)
            {
                previewAudioObject = new GameObject("TTS Preview");
                previewAudioSource = previewAudioObject.AddComponent<AudioSource>();
            }
            previewAudioSource.Stop();
            previewAudioSource.clip = previewClip;
            previewAudioSource.playOnAwake = false;
            previewAudioSource.loop = false;
            previewAudioSource.Play();
            isPreviewPlaying = true;
            EditorApplication.update -= CheckPreviewPlayback;
            EditorApplication.update += CheckPreviewPlayback;
        }
        public void StopPreviewClip()
        {
            if (previewAudioSource != null)
            {
                previewAudioSource.Stop();
            }
            isPreviewPlaying = false;
            EditorApplication.update -= CheckPreviewPlayback;
        }
        private void CheckPreviewPlayback()
        {
            if (previewAudioSource == null) return;
            if (!previewAudioSource.isPlaying)
            {
                isPreviewPlaying = false;
                EditorApplication.update -= CheckPreviewPlayback;
                Repaint();
            }
        }
        private IEnumerator FetchVoicesCoroutine()
        {
            string url = $"{VOICES_API}?languageCode={SelectedLanguage}";
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.SetRequestHeader("x-auth-key", authKey);
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    yield break;
                }
                string json = request.downloadHandler.text;
                VoiceListResponse response =
                    JsonUtility.FromJson<VoiceListResponse>(json);
                availableVoices.Clear();
                if (response != null && response.voices != null)
                {
                    foreach (var v in response.voices)
                    {
                        if (!string.IsNullOrWhiteSpace(v.name))
                            availableVoices.Add(v.name.Trim());
                    }
                }
                availableVoices.Sort();
                selectedVoice = null;
                Repaint();
            }
        }
        private IEnumerator GenerateAudioCoroutine()
        {
            string project = new DirectoryInfo(Application.dataPath).Parent.Name;
            // Only checked, valid steps are included.
            var entries = new List<SimulationState>();
            for (int i = 0; i < simulationObjects.Count; i++)
            {
                if (simulationObjects[i] == null) continue;
                SimulationState state = simulationObjects[i].GetComponent<SimulationState>();
                if (state == null || string.IsNullOrEmpty(state.promptText) || !IsObjectSelected(simulationObjects[i])) continue;
                entries.Add(state);
            }
            if (entries.Count == 0)
            {
                AddLog("No selected SimulationState objects with prompt text found.", MessageType.Warning);
                generationStatus = "No steps selected.";
                isGenerating = false;
                yield break;
            }
            generationStatus = $"Starting generation for {entries.Count} step(s)...";
            string folder = Path.Combine("Assets", "GeneratedAudio", project, SelectedLanguage);
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            int done = 0;
            int failed = 0;
            int totalClips = 0;
            for (int entryIndex = 0; entryIndex < entries.Count; entryIndex++)
            {
                if (cancelGenerationRequested)
                {
                    AddLog("Generation cancelled. Completed clips were kept and assigned.", MessageType.Warning);
                    break;
                }
                var state = entries[entryIndex];
                int stepNo = GetStepNumber(state.gameObject);
                generationProgress = (float)entryIndex / entries.Count;
                generationStatus = $"Step {entryIndex + 1} / {entries.Count}: {state.name}";
                Repaint();
                bool ok = false;
                yield return GenerateClipForProperty(state, stepNo, GetEffectivePromptText(state), folder, "promptAudio", success => ok = success);
                totalClips++;
                if (ok) { done++; generationSuccessCount++; }
                else generationFailCount++;
                // Secondary prompts — only generated when enabled on the state and text is present.
                if (state.hasPrePrompt && !string.IsNullOrWhiteSpace(state.prePromptText))
                {
                    bool preOk = false;
                    yield return GenerateClipForProperty(state, stepNo, state.prePromptText, folder, "prePromptAudio", success => preOk = success);
                    totalClips++;
                    if (preOk) { done++; generationSuccessCount++; }
                    else generationFailCount++;
                }
                if (state.hasPostPrompt && !string.IsNullOrWhiteSpace(state.postPromptText))
                {
                    bool postOk = false;
                    yield return GenerateClipForProperty(state, stepNo, state.postPromptText, folder, "postPromptAudio", success => postOk = success);
                    totalClips++;
                    if (postOk) { done++; generationSuccessCount++; }
                    else generationFailCount++;
                }
                yield return new EditorWaitForSeconds(0.1f);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            generationProgress = 1f;
            generationStatus = cancelGenerationRequested
                ? $"Cancelled — {done}/{totalClips} clip(s) completed."
                : generationFailCount > 0
                    ? $"Done with errors — {done} succeeded, {generationFailCount} failed."
                    : $"All done! {done}/{totalClips} clip(s) generated successfully.";
            AddLog(generationStatus, MessageType.Info);
            isGenerating = false;
            Repaint();
        }
        /// <summary>
        /// Generates a single TTS clip for the given text, saves it under
        /// a step-specific versioned path, and assigns it to the
        /// named AudioClip property (e.g. "promptAudio", "prePromptAudio", "postPromptAudio")
        /// on the given SimulationState. Invokes onComplete(true) on success, onComplete(false)
        /// on any failure (empty text, request error, or import failure).
        /// </summary>
        private IEnumerator GenerateClipForProperty(SimulationState state, int stepNo, string text, string folder,
            string propName, Action<bool> onComplete)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                onComplete?.Invoke(false);
                yield break;
            }
            string assetPath = GetNextVersionedAssetPath(folder, stepNo);
            var body = JsonUtility.ToJson(new PreviewRequest
            {
                languageCode = SelectedLanguage,
                voiceName = selectedVoice ?? "",
                text = text.Trim()
            });
            using (var req = new UnityWebRequest(PREVIEW_API, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("x-auth-key", authKey);
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    AddLog($"Failed: {state.name} ({propName}) - {req.error}", MessageType.Error);
                    onComplete?.Invoke(false);
                    yield break;
                }
                File.WriteAllBytes(assetPath, req.downloadHandler.data);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
                if (clip == null)
                {
                    AddLog($"Failed to import: {assetPath}", MessageType.Error);
                    onComplete?.Invoke(false);
                    yield break;
                }
                var so = new SerializedObject(state);
                var p = so.FindProperty(propName);
                if (p != null)
                {
                    Undo.RecordObject(state, "Assign generated TTS audio");
                    p.objectReferenceValue = clip;
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(state);
                }
                AddLog($"Generated: {state.name} ({propName})", MessageType.Info);
                onComplete?.Invoke(true);
            }
        }
        private void ClearAudioFromAll()
        {
            int cleared = 0;
            foreach (var obj in simulationObjects)
            {
                if (obj == null) continue;
                SimulationState state = obj.GetComponent<SimulationState>();
                if (state == null) continue;
                var so = new SerializedObject(state);
                var p = so.FindProperty("promptAudio");
                if (p != null && p.objectReferenceValue != null)
                {
                    Undo.RecordObject(state, "Clear assigned TTS audio");
                    p.objectReferenceValue = null;
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(state);
                    cleared++;
                }
            }
            AssetDatabase.SaveAssets();
            AddLog($"Cleared promptAudio assignments from {cleared} SimulationState(s). Generated files were kept.", MessageType.Info);
            Repaint();
        }
        #endregion
        #region Wizard Integration
        /// <summary>
        /// Called by the Simulation Step Wizard after generating states.
        /// Populates simulationObjects with the new states and triggers
        /// multi-language audio generation using wizard step data.
        /// </summary>
        public void ReceiveFromWizard(List<SimToolStepData> steps, List<string> langHeaders)
        {
            if (steps == null)
            {
                return;
            }
            _wizardData = new List<WizardStepAudioData>();
            simulationObjects.Clear();
            selectedObjectIds.Clear();
            promptOverrides.Clear();
            // Resolve header → lang code for each column.
            var langs = new List<(string langCode, int colIdx)>();
            for (int c = 0; c < (langHeaders?.Count ?? 0); c++)
            {
                string h = langHeaders[c].Trim();
                string code = HeaderToLangCode.TryGetValue(h, out string lc) ? lc : h;
                langs.Add((code, c));
            }
            if (langs.Count == 0) langs.Add(("en-US", 0));
            int stepNo = 0;
            foreach (var step in steps)
            {
                stepNo++;
                string englishText = step.languageTexts.Count > 0
                    ? step.languageTexts[0].Trim() : step.stepName;
                string safeEnglish = SanitizeFileName(string.IsNullOrWhiteSpace(englishText)
                    ? step.stepName : englishText);
                string goName = $"{stepNo}_{safeEnglish}";
                SimulationState state = FindStateInScene(goName);
                if (state != null)
                {
                    simulationObjects.Add(state.gameObject);
                    SetObjectSelected(state.gameObject, true);
                }
                var langTexts = new List<(string langCode, string text)>();
                var prePromptLangTexts = new List<(string langCode, string text)>();
                var postPromptLangTexts = new List<(string langCode, string text)>();
                foreach (var (langCode, colIdx) in langs)
                {
                    string text = colIdx < step.languageTexts.Count
                        ? step.languageTexts[colIdx].Trim() : string.Empty;
                    langTexts.Add((langCode, text));
                    string preText = step.hasPrePrompt && step.prePromptLanguageTexts != null && colIdx < step.prePromptLanguageTexts.Count
                        ? step.prePromptLanguageTexts[colIdx].Trim() : string.Empty;
                    prePromptLangTexts.Add((langCode, preText));
                    string postText = step.hasPostPrompt && step.postPromptLanguageTexts != null && colIdx < step.postPromptLanguageTexts.Count
                        ? step.postPromptLanguageTexts[colIdx].Trim() : string.Empty;
                    postPromptLangTexts.Add((langCode, postText));
                }
                _wizardData.Add(new WizardStepAudioData
                {
                    stepNo = stepNo,
                    state = state,
                    langTexts = langTexts,
                    hasPrePrompt = step.hasPrePrompt,
                    prePromptLangTexts = prePromptLangTexts,
                    hasPostPrompt = step.hasPostPrompt,
                    postPromptLangTexts = postPromptLangTexts
                });
            }
            Repaint();
            EditorCoroutineUtility.StartCoroutineOwnerless(WizardGenerateCoroutine());
        }
        private IEnumerator WizardGenerateCoroutine()
        {
            if (_wizardData == null || _wizardData.Count == 0) yield break;
            isGenerating = true;
            foldoutGenerationProgress = true;
            string project = new DirectoryInfo(Application.dataPath).Parent.Name;
            int total = _wizardData.Count;
            int current = 0;
            foreach (var entry in _wizardData)
            {
                current++;
                yield return WizardGenerateForLangTexts(entry, entry.langTexts, project, suffix: null, propName: "promptAudio");
                if (entry.hasPrePrompt && entry.prePromptLangTexts != null)
                    yield return WizardGenerateForLangTexts(entry, entry.prePromptLangTexts, project, suffix: "pre", propName: "prePromptAudio");
                if (entry.hasPostPrompt && entry.postPromptLangTexts != null)
                    yield return WizardGenerateForLangTexts(entry, entry.postPromptLangTexts, project, suffix: "post", propName: "postPromptAudio");
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            _wizardData = null;
            EditorUtility.DisplayDialog("Audio Complete",
                $"Generated audio for {current} step(s) (including pre/post prompts where enabled).\n" +
                $"Saved to: Assets/GeneratedAudio/{project}/", "OK");

            isGenerating = false;
            Repaint();
        }
        /// <summary>
        /// Generates clips for every (langCode, text) pair on a wizard step entry — used for the
        /// main prompt, and optionally the pre/post prompt, each keyed by an optional file suffix
        /// (e.g. "pre"/"post"). Only the primary (first) language column is assigned to the
        /// SimulationState's AudioClip field named by propName — the rest are just saved as assets,
        /// matching the existing behaviour for the main prompt.
        /// </summary>
        private IEnumerator WizardGenerateForLangTexts(WizardStepAudioData entry,
            List<(string langCode, string text)> langTexts, string project, string suffix, string propName)
        {
            if (langTexts == null) yield break;
            foreach (var (langCode, text) in langTexts)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }
                string folder = Path.Combine("Assets", "GeneratedAudio", project, langCode);
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                string assetPath = GetNextVersionedAssetPath(folder, entry.stepNo);
                var body = JsonUtility.ToJson(new PreviewRequest
                {
                    languageCode = langCode,
                    voiceName = selectedVoice ?? "",
                    text = text
                });
                using (var req = new UnityWebRequest(PREVIEW_API, "POST"))
                {
                    req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                    req.downloadHandler = new DownloadHandlerBuffer();
                    req.SetRequestHeader("Content-Type", "application/json");
                    req.SetRequestHeader("x-auth-key", authKey);
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        continue;
                    }
                    File.WriteAllBytes(assetPath, req.downloadHandler.data);
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                    var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
                    if (clip == null)
                    {
                        continue;
                    }
                    // Only the primary (first) language column is assigned to the SimulationState field.
                    bool isPrimary = langTexts.Count > 0 && langTexts[0].langCode == langCode;
                    if (isPrimary && entry.state != null)
                    {
                        var so = new SerializedObject(entry.state);
                        var p = so.FindProperty(propName);
                        if (p != null)
                        {
                            Undo.RecordObject(entry.state, "Assign generated wizard TTS audio");
                            p.objectReferenceValue = clip;
                            so.ApplyModifiedProperties();
                            EditorUtility.SetDirty(entry.state);
                        }
                    }
                }
                yield return new EditorWaitForSeconds(0.1f);
            }
        }
        #endregion
        #region Helper Methods
        private string Truncate(string input, int length)
        {
            if (string.IsNullOrEmpty(input)) return "";
            return input.Length <= length
                ? input
                : input.Substring(0, length) + "...";
        }
        private bool IsObjectSelected(GameObject obj) =>
            obj != null && selectedObjectIds.Contains(obj.GetInstanceID());
        private void SetObjectSelected(GameObject obj, bool selected)
        {
            if (obj == null) return;
            if (selected) selectedObjectIds.Add(obj.GetInstanceID());
            else selectedObjectIds.Remove(obj.GetInstanceID());
        }
        private void SetAllObjectsSelected(bool selected)
        {
            foreach (var obj in simulationObjects)
                SetObjectSelected(obj, selected);
        }
        private void ClearObjectList()
        {
            simulationObjects.Clear();
            selectedObjectIds.Clear();
            promptOverrides.Clear();
            AddLog("Cleared the step list. No SimulationState data or audio assignments were changed.", MessageType.Info);
        }
        private void RemoveObjectAt(int index)
        {
            if (index < 0 || index >= simulationObjects.Count) return;
            var obj = simulationObjects[index];
            if (obj != null)
            {
                selectedObjectIds.Remove(obj.GetInstanceID());
                var state = obj.GetComponent<SimulationState>();
                if (state != null) promptOverrides.Remove(state.GetInstanceID());
            }
            simulationObjects.RemoveAt(index);
        }
        private int CountSelectedValidObjects()
        {
            int count = 0;
            foreach (var obj in simulationObjects)
            {
                var state = obj == null ? null : obj.GetComponent<SimulationState>();
                if (state != null && !string.IsNullOrWhiteSpace(state.promptText) && IsObjectSelected(obj)) count++;
            }
            return count;
        }
        private string GetEffectivePromptText(SimulationState state)
        {
            if (state != null && promptOverrides.TryGetValue(state.GetInstanceID(), out string overrideText)
                && !string.IsNullOrWhiteSpace(overrideText))
                return overrideText.Trim();
            return state == null ? string.Empty : state.promptText;
        }
        private int GetStepNumber(GameObject go)
        {
            if (go == null) return 1;
            // 1. Try to parse from the GameObject name (e.g., "1_WashHands" or "002_Soap")
            string name = go.name;
            int underscoreIdx = name.IndexOf('_');
            if (underscoreIdx > 0)
            {
                string prefix = name.Substring(0, underscoreIdx);
                if (int.TryParse(prefix, out int num))
                {
                    return num;
                }
            }
            int spaceIdx = name.IndexOf(' ');
            if (spaceIdx > 0)
            {
                string prefix = name.Substring(0, spaceIdx);
                if (int.TryParse(prefix, out int num))
                {
                    return num;
                }
            }
            // 2. Fallback to its 1-based index in the simulationObjects list
            int idx = simulationObjects.IndexOf(go);
            if (idx >= 0)
            {
                return idx + 1;
            }
            return 1;
        }
        private static string GetNextVersionedAssetPath(string languageFolder, int stepNo)
        {
            for (int version = 1; version < 10000; version++)
            {
                string candidate = Path.Combine(languageFolder, $"{stepNo:000}_v{version:000}.mp3");
                if (!File.Exists(candidate)) return candidate;
            }
            throw new IOException($"Could not find an available TTS filename for step {stepNo:000}.");
        }
        private void AddLog(string message, MessageType type)
        {
            string entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
            activityLog.Add(entry);
            if (activityLog.Count > 100) activityLog.RemoveAt(0);
            generationStatus = message;
            Repaint();
        }
        private static SimulationState FindStateInScene(string goName)
        {
            foreach (var root in UnityEngine.SceneManagement.SceneManager
                .GetActiveScene().GetRootGameObjects())
            {
                var s = FindRecursive(root.transform, goName);
                if (s != null) return s;
            }
            return null;
        }
        private static SimulationState FindRecursive(Transform t, string name)
        {
            if (t.name == name)
            {
                var s = t.GetComponent<SimulationState>();
                if (s != null) return s;
            }
            foreach (Transform child in t)
            {
                var s = FindRecursive(child, name);
                if (s != null) return s;
            }
            return null;
        }
        private static string SanitizeFileName(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            foreach (char c in Path.GetInvalidFileNameChars())
                text = text.Replace(c, '_');
            text = text.Trim();
            // Strip trailing dots, which cause DirectoryNotFoundException on Windows
            while (text.EndsWith("."))
            {
                text = text.Substring(0, text.Length - 1).Trim();
            }
            return text;
        }
        #endregion
    }

}
#region Data Models
// ================= WIZARD STEP AUDIO DATA =================
public class WizardStepAudioData
{
    public int stepNo;
    public SimulationState state;
    public List<(string langCode, string text)> langTexts;
    public bool hasPrePrompt;
    public List<(string langCode, string text)> prePromptLangTexts;
    public bool hasPostPrompt;
    public List<(string langCode, string text)> postPromptLangTexts;
}
// ================= RESPONSE MODELS =================
[Serializable]
public class VoiceListResponse
{
    public List<VoiceItem> voices;
    public int total;
}
[Serializable]
public class VoiceItem
{
    public string name;
    public string fullName;
    public string ssmlGender;
}
[Serializable]
public class PreviewRequest
{
    public string languageCode;
    public string voiceName;
    public string text;
}
#endregion