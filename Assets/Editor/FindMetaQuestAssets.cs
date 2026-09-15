// Assets/Editor/FindMetaQuestAssets.cs
using UnityEditor;
using UnityEngine;

public class FindMetaQuestAssets
{
    [MenuItem("Tools/Find All MetaQuestFeature Assets")]
    static void Find()
    {
        var guids = AssetDatabase.FindAssets("t:MetaQuestFeature");
        foreach (var guid in guids)
        {
        }
    }
}