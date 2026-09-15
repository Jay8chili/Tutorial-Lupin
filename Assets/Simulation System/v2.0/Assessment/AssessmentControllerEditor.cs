#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(AssessmentController))]
public class AssessmentControllerEditor : UnityEditor.Editor
{
    private SerializedProperty _interactionConfigs;
    private SerializedProperty _contaminationAssessed;
    private SerializedProperty _firstAirAssessed;
    private SerializedProperty _fastHandAssessed;
    private SerializedProperty _contaminationMessage;
    private SerializedProperty _firstAirMessage;
    private SerializedProperty _hintTakenMessage;

    private void OnEnable()
    {
        _interactionConfigs = serializedObject.FindProperty("interactionConfigs");
        _contaminationAssessed = serializedObject.FindProperty("contaminationAssessed");
        _firstAirAssessed = serializedObject.FindProperty("firstAirAssessed");
        _fastHandAssessed = serializedObject.FindProperty("fastHandAssessed");
        _contaminationMessage = serializedObject.FindProperty("contaminationMessage");
        _firstAirMessage = serializedObject.FindProperty("firstAirMessage");
        _hintTakenMessage = serializedObject.FindProperty("hintTakenMessage");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.LabelField("Assessment Controller", EditorStyles.boldLabel);
        EditorGUILayout.Space(4);

        EditorGUILayout.LabelField("Step-Level Assessed Errors", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(_contaminationAssessed, new GUIContent("Contamination Is Assessed"));
        EditorGUILayout.PropertyField(_firstAirAssessed, new GUIContent("First Air Is Assessed"));
        EditorGUILayout.PropertyField(_fastHandAssessed, new GUIContent("Fast Hand Is Assessed"));

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Step Result Messages (sent to the API as error_message)", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(_contaminationMessage, new GUIContent("Contamination Message"));
        EditorGUILayout.PropertyField(_firstAirMessage, new GUIContent("First Air Message"));
        EditorGUILayout.PropertyField(_hintTakenMessage, new GUIContent("Hint Taken Message"));

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Interaction Configs", EditorStyles.boldLabel);
        EditorGUILayout.Space(4);

        for (int i = 0; i < _interactionConfigs.arraySize; i++)
        {
            var config = _interactionConfigs.GetArrayElementAtIndex(i);
            var interactionProp = config.FindPropertyRelative("interaction");
            var maxScoreProp = config.FindPropertyRelative("maxScore");
            var hintPenaltyProp = config.FindPropertyRelative("hintPenalty");
            var wrongDetectPenProp = config.FindPropertyRelative("wrongDetectPenalty");
            var wrongDetectsProp = config.FindPropertyRelative("wrongDetects");
            var wotdProp = config.FindPropertyRelative("WOTD");
            var wrongGrabPenProp = config.FindPropertyRelative("wrongGrabPenalty");
            var wrongGrabsProp = config.FindPropertyRelative("wrongGrabs");

            bool isDetect = interactionProp.objectReferenceValue is DetectInteraction;
            bool isGrab = interactionProp.objectReferenceValue is GrabInteraction;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Header + remove button
            EditorGUILayout.BeginHorizontal();
            string label = interactionProp.objectReferenceValue != null
                ? interactionProp.objectReferenceValue.name
                : $"Interaction {i}";
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            if (GUILayout.Button("✕", GUILayout.Width(22)))
            {
                _interactionConfigs.DeleteArrayElementAtIndex(i);
                serializedObject.ApplyModifiedProperties();
                return;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.PropertyField(interactionProp, new GUIContent("Interaction"));
            EditorGUILayout.PropertyField(maxScoreProp, new GUIContent("Max Score"));
            EditorGUILayout.PropertyField(hintPenaltyProp, new GUIContent("Hint Penalty"));

            // Only show wrong detect fields for DetectInteraction
            if (isDetect)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Wrong Detect Assessment", EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(wrongDetectPenProp, new GUIContent("Wrong Detect Penalty"));
                EditorGUILayout.PropertyField(wrongDetectsProp, new GUIContent("Wrong Detects"), true);
                EditorGUILayout.PropertyField(wotdProp, new GUIContent("Wrong Objects To Detect (WOTD)"), true);
            }

            // Only show wrong grab fields for GrabInteraction
            if (isGrab)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Wrong Grab Assessment", EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(wrongGrabPenProp, new GUIContent("Wrong Grab Penalty"));
                EditorGUILayout.PropertyField(wrongGrabsProp, new GUIContent("Wrong Grabs"), true);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
        }

        if (GUILayout.Button("+ Add Interaction Config"))
            _interactionConfigs.InsertArrayElementAtIndex(_interactionConfigs.arraySize);

        serializedObject.ApplyModifiedProperties();
    }
}
#endif