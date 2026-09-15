using UnityEditor;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;

public class FixMetaQuestFeature
{
    [MenuItem("Tools/Fix MetaQuest Feature Asset")]
    static void Fix()
    {
        // Use GetSettingsForBuildTargetGroup — works across all recent OpenXR versions
        var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(
            BuildTargetGroup.Android
        );

        if (settings == null)
        {
            return;
        }

        // Access features via SerializedObject to avoid API version mismatches
        var so = new SerializedObject(settings);
        var featuresProp = so.FindProperty("features");

        if (featuresProp == null)
        {
        }
        else
        {
            for (int i = 0; i < featuresProp.arraySize; i++)
            {
                var element = featuresProp.GetArrayElementAtIndex(i);
                if (element.objectReferenceValue == null)
                {
                }
                else
                {
                    var feature = element.objectReferenceValue as OpenXRFeature;
                }
            }
        }

        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }
}