using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(FirstAirManager))]
public class FirstAirManagerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        FirstAirManager manager = (FirstAirManager)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Editor Tools", EditorStyles.boldLabel);

        if (GUILayout.Button("Apply Threshold & Attach FirstAirTrigger to All Colliders"))
            ApplyFirstAirTriggers(manager);
    }

    private void ApplyFirstAirTriggers(FirstAirManager manager)
    {
        int added = 0, updated = 0;

        foreach (FirstAirZone zone in manager.zones)
        {
            foreach (Collider col in zone.colliders)
            {
                if (col == null) continue;

                FirstAirTrigger trigger = col.GetComponent<FirstAirTrigger>();

                if (trigger == null)
                {
                    trigger = col.gameObject.AddComponent<FirstAirTrigger>();
                    added++;
                }
                else updated++;

                trigger.contactThreshold = manager.contactThreshold;
                EditorUtility.SetDirty(trigger);
            }
        }
    }
}
