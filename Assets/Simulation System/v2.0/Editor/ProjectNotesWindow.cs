using UnityEditor;
using UnityEngine;
using System.IO;

/// <summary>
/// A lightweight, Notepad-like editor window for taking project-specific notes directly inside the Unity Editor.
/// Features include basic file operations (New, Open, Save, Save As), word wrapping, standard shortcut keys,
/// and proper Undo/Redo integration via Unity's native Undo system.
/// </summary>
public class ProjectNotesWindow : EditorWindow
{
    // Serialize these fields so they survive assembly reloads and integrate natively with Unity's Undo system
    [SerializeField] private string noteText = "";
    [SerializeField] private string currentFilePath = "";
    
    // Non-serialized state variables
    private Vector2 scroll;
    private bool isDirty = false;
    private string previousFocus = "";
    private GUIStyle textAreaStyle;

    /// <summary>
    /// Computes the path to the "ProjectNotes" folder located next to the Assets folder (in the project root).
    /// This ensures notes are kept outside of source control if desired, or at least separate from project assets.
    /// </summary>
    private string NotesFolder =>
        Path.Combine(Directory.GetParent(Application.dataPath).FullName, "ProjectNotes");

    /// <summary>
    /// Opens the Project Notes window from the Unity toolbar.
    /// </summary>
    [MenuItem("Tools/Project Notes")]
    public static void Open()
    {
        GetWindow<ProjectNotesWindow>("Project Notes");
    }

    private void OnGUI()
    {
        // 1. Intercept global keyboard shortcuts (e.g. Ctrl + S) before other controls consume them.
        HandleKeyboardShortcuts();

        // 2. Initialize styles safely (needs to be done in OnGUI where EditorStyles are guaranteed to be loaded)
        InitializeStyles();

        // 3. Draw the top toolbar (New, Open, Save, etc.)
        DrawToolbar();

        EditorGUI.BeginChangeCheck();

        scroll = EditorGUILayout.BeginScrollView(scroll);

        // Assign a specific control name so we can detect when this text area gains focus
        GUI.SetNextControlName("NoteEditor");

        // Use the custom word-wrap style and expand to fill available window space
        string newText = EditorGUILayout.TextArea(
            noteText,
            textAreaStyle,
            GUILayout.ExpandHeight(true),
            GUILayout.ExpandWidth(true));

        EditorGUILayout.EndScrollView();

        // 4. Handle text area specific keyboard inputs (Enter / Shift+Enter) safely without losing focus or selecting all
        HandleTextAreaInput();

        // 5. Handle Unity's auto-select-all bug when the text area gains focus
        HandleFocusSelectionBug();

        if (EditorGUI.EndChangeCheck())
        {
            // Record the current state *before* applying the new text so Ctrl+Z (Undo) works properly
            Undo.RecordObject(this, "Edit Note");
            
            noteText = newText;
            isDirty = true;
        }

        // 6. Draw the bottom status bar (Save status, Character count)
        DrawStatusBar();
    }

    /// <summary>
    /// Initializes the custom GUIStyle for the text area to enforce word wrapping and Notepad-like appearance.
    /// </summary>
    private void InitializeStyles()
    {
        if (textAreaStyle == null)
        {
            // Create a new style based on Unity's default text area but force wordWrap
            textAreaStyle = new GUIStyle(EditorStyles.textArea)
            {
                wordWrap = true,
                richText = false
            };
        }
    }

    /// <summary>
    /// Handles global window shortcuts like Ctrl+S for saving.
    /// </summary>
    private void HandleKeyboardShortcuts()
    {
        Event e = Event.current;

        // Check for Save shortcut: Ctrl + S (or Cmd + S on macOS)
        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.S && (e.control || e.command))
        {
            SaveFile();
            
            // Consume the event so it doesn't trigger anything else in the editor
            e.Use();
        }
    }

    /// <summary>
    /// Safely intercepts Enter and Shift+Enter within the text area to prevent Unity from 
    /// triggering unwanted "select all" behavior or ignoring the input entirely.
    /// </summary>
    private void HandleTextAreaInput()
    {
        Event e = Event.current;
        
        // Only apply this fix if our text area is currently the focused control
        if (GUI.GetNameOfFocusedControl() == "NoteEditor")
        {
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
            {
                // Retrieve the underlying TextEditor instance handling IMGUI text input
                TextEditor textEditor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
                if (textEditor != null)
                {
                    Undo.RecordObject(this, "Edit Note");
                    
                    // Manually replace the current selection (or insert at caret) with a newline
                    textEditor.ReplaceSelection("\n");
                    noteText = textEditor.text;
                    isDirty = true;
                    
                    // Consume the event to stop Unity's default Enter key processing
                    e.Use();
                }
            }
        }
    }

    /// <summary>
    /// Prevents Unity from automatically selecting the entire document when the user clicks 
    /// into the text area for the first time or when it regains focus.
    /// </summary>
    private void HandleFocusSelectionBug()
    {
        string currentFocus = GUI.GetNameOfFocusedControl();
        
        // Detect if the NoteEditor just gained focus this frame
        if (currentFocus == "NoteEditor" && previousFocus != "NoteEditor")
        {
            TextEditor textEditor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
            if (textEditor != null)
            {
                // Clear the selection by setting the select index to match the cursor index.
                // This ensures the caret simply blinks where the user clicked, behaving like Notepad.
                textEditor.selectIndex = textEditor.cursorIndex;
            }
        }
        
        previousFocus = currentFocus;
    }

    /// <summary>
    /// Draws the top toolbar containing file management buttons and the current filename.
    /// </summary>
    private void DrawToolbar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

        if (GUILayout.Button("New", EditorStyles.toolbarButton))
            NewFile();

        if (GUILayout.Button("Open", EditorStyles.toolbarButton))
            OpenFile();

        if (GUILayout.Button("Save", EditorStyles.toolbarButton))
            SaveFile();

        if (GUILayout.Button("Save As", EditorStyles.toolbarButton))
            SaveFileAs();

        GUILayout.FlexibleSpace();

        // Display current filename in the toolbar
        string fileName = string.IsNullOrEmpty(currentFilePath)
            ? "Untitled"
            : Path.GetFileName(currentFilePath);

        GUILayout.Label(fileName, EditorStyles.miniLabel);

        EditorGUILayout.EndHorizontal();
    }

    /// <summary>
    /// Draws the bottom status bar showing the dirty state and the document's character count.
    /// </summary>
    private void DrawStatusBar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

        // If the file has been edited (isDirty) OR it has never been saved to disk (no filepath), mark it as unsaved
        bool isUnsaved = isDirty || string.IsNullOrEmpty(currentFilePath);
        GUILayout.Label(isUnsaved ? "Unsaved ●" : "Saved ✓");

        GUILayout.FlexibleSpace();

        GUILayout.Label($"Characters : {noteText.Length}");

        EditorGUILayout.EndHorizontal();
    }

    /// <summary>
    /// Ensures the target output folder exists, creating it if it doesn't.
    /// </summary>
    private void EnsureNotesFolder()
    {
        if (!Directory.Exists(NotesFolder))
            Directory.CreateDirectory(NotesFolder);
    }

    /// <summary>
    /// Initializes a blank new file. Prompts to save if there are unsaved changes.
    /// </summary>
    private void NewFile()
    {
        if (isDirty)
        {
            int option = EditorUtility.DisplayDialogComplex(
                "Unsaved Changes",
                "Do you want to save the current note?",
                "Save",
                "Cancel",
                "Discard");

            if (option == 1) // Cancel
                return;
                
            if (option == 0) // Save
                SaveFile();
        }

        // Use Undo so the user can undo the "New File" action if they clicked it by mistake
        Undo.RecordObject(this, "New Note");
        noteText = "";
        currentFilePath = "";
        isDirty = false;
        
        // Remove focus so the text area drops its internal buffer and properly clears the screen
        GUI.FocusControl(null);
    }

    /// <summary>
    /// Opens an existing text file from disk into the editor.
    /// </summary>
    private void OpenFile()
    {
        EnsureNotesFolder();

        if (isDirty)
        {
            int option = EditorUtility.DisplayDialogComplex(
                "Unsaved Changes",
                "Do you want to save the current note before opening another?",
                "Save",
                "Cancel",
                "Discard");

            if (option == 1) // Cancel
                return;
                
            if (option == 0) // Save
                SaveFile();
        }

        string path = EditorUtility.OpenFilePanel(
            "Open Note",
            NotesFolder,
            "txt");

        if (string.IsNullOrEmpty(path))
            return;

        Undo.RecordObject(this, "Open Note");
        noteText = File.ReadAllText(path);
        currentFilePath = path;
        isDirty = false;
        
        // Remove focus so the new file isn't instantly auto-selected and clears the cache
        GUI.FocusControl(null);
    }

    /// <summary>
    /// Saves the current note. Triggers Save As if the file has never been saved before.
    /// </summary>
    private void SaveFile()
    {
        if (string.IsNullOrEmpty(currentFilePath))
        {
            SaveFileAs();
            return;
        }

        File.WriteAllText(currentFilePath, noteText);
        isDirty = false;

        // Force a UI repaint to immediately update the "Saved" status
        Repaint();
        
        AssetDatabase.Refresh();
    }

    /// <summary>
    /// Opens the Save file dialog to save the note to a specific path.
    /// </summary>
    private void SaveFileAs()
    {
        EnsureNotesFolder();

        string path = EditorUtility.SaveFilePanel(
            "Save Note",
            NotesFolder,
            "New Note",
            "txt");

        if (string.IsNullOrEmpty(path))
            return;

        currentFilePath = path;

        File.WriteAllText(currentFilePath, noteText);
        isDirty = false;

        Repaint();
        
        AssetDatabase.Refresh();
    }
}