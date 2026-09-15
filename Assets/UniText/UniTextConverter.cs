// Assets/Editor/UniText/UniTextConverter.cs
// Replaces TextMeshProUGUI components with LightSide.UniText.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LightSide;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace UniTextTools.EditorTools
{
    public static class UniTextConverter
    {
        public class Report
        {
            public int Converted;
            public int AlreadyConverted;
            public int Skipped;

            public readonly List<string> Messages = new List<string>();
            public readonly HashSet<string> UnmappedFonts = new HashSet<string>();
            public readonly List<string> MarkupWarnings = new List<string>();

            public void Log(string message) => Messages.Add(message);

            public override string ToString() =>
                $"Converted: {Converted}   Already UniText: {AlreadyConverted}   Skipped: {Skipped}";
        }

        /// <summary>Values read off a TMP component before it is destroyed.</summary>
        private class Snapshot
        {
            public string Text;
            public float FontSize;
            public bool AutoSize;
            public float MinFontSize;
            public float MaxFontSize;
            public bool WordWrap;
            public bool RightToLeft;
            public Color Color;
            public bool RaycastTarget;
            public bool Maskable;
            public bool RichText;
            public string HorizontalName;
            public string VerticalName;
            public TMP_FontAsset Font;
            public string SourceTypeName;
        }

        // ------------------------------------------------------------------
        // Conversion
        // ------------------------------------------------------------------

        public static bool ConvertComponent(
            TMP_Text component,
            UniTextConversionSettings settings,
            bool registerUndo,
            Report report)
        {
            if (component == null || settings == null) return false;

            var gameObject = component.gameObject;
            var path = GetHierarchyPath(component.transform);

            // UniText is a MaskableGraphic, so it only replaces the Canvas-based TMP type.
            if (!(component is TextMeshProUGUI))
            {
                report.Skipped++;
                report.Log($"[Skip] {path}: {component.GetType().Name} is world-space text. " +
                           "UniText derives from MaskableGraphic and needs a Canvas.");
                return false;
            }

            if (gameObject.GetComponent<UniText>() != null)
            {
                report.Skipped++;
                report.Log($"[Skip] {path}: already has a UniText.");
                return false;
            }

            var snapshot = TakeSnapshot(component);

            if (!settings.TryResolveFont(snapshot.Font, out var fontStack, out var appearance))
            {
                report.Skipped++;
                report.UnmappedFonts.Add(snapshot.Font != null ? snapshot.Font.name : "<none>");
                report.Log($"[Skip] {path}: no font stack for " +
                           $"'{(snapshot.Font != null ? snapshot.Font.name : "no font")}'.");
                return false;
            }

            if (settings.WarnOnRichTextMarkup && snapshot.RichText && ContainsMarkup(snapshot.Text))
            {
                report.MarkupWarnings.Add(path);
                report.Log($"[Markup] {path}: contains TMP tags. UniText parses markup through " +
                           "its own IParseRule set — review this string by hand.");
            }

            var siblingIndex = GetComponentIndex(component);

            if (registerUndo) Undo.DestroyObjectImmediate(component);
            else UnityEngine.Object.DestroyImmediate(component, true);

            var uniText = registerUndo
                ? Undo.AddComponent<UniText>(gameObject)
                : gameObject.AddComponent<UniText>();

            if (uniText == null)
            {
                report.Skipped++;
                report.Log($"[Fail] {path}: could not add UniText. The object now has no text component.");
                return false;
            }

            try
            {
                Apply(uniText, snapshot, settings, fontStack, appearance, report, path);
            }
            catch (Exception exception)
            {
                report.Skipped++;
                report.Log($"[Fail] {path}: {exception.GetBaseException().Message}");
                return false;
            }
            RestoreComponentIndex(uniText, siblingIndex);

            EditorUtility.SetDirty(gameObject);
            report.Converted++;
            report.Log($"[Replace] {path}: {snapshot.SourceTypeName} -> UniText");
            return true;
        }

        // ------------------------------------------------------------------
        // Reading TMP
        // ------------------------------------------------------------------

        private static Snapshot TakeSnapshot(TMP_Text tmp)
        {
            var snapshot = new Snapshot
            {
                SourceTypeName = tmp.GetType().Name,
                Text = tmp.text,
                FontSize = tmp.fontSize,
                AutoSize = tmp.enableAutoSizing,
                MinFontSize = tmp.fontSizeMin,
                MaxFontSize = tmp.fontSizeMax,
                RightToLeft = tmp.isRightToLeftText,
                Color = tmp.color,
                RaycastTarget = tmp.raycastTarget,
                Maskable = tmp.maskable,
                RichText = tmp.richText,
                Font = tmp.font,
                WordWrap = ReadWordWrap(tmp)
            };

            ReadAlignment(tmp, out snapshot.HorizontalName, out snapshot.VerticalName);
            return snapshot;
        }

        /// <summary>
        /// TMP renamed word wrapping partway through its life: older versions expose
        /// enableWordWrapping, newer ones textWrappingMode. Read whichever exists.
        /// </summary>
        private static bool ReadWordWrap(TMP_Text tmp)
        {
            if (TryReadProperty(tmp, "enableWordWrapping", out var legacy) && legacy is bool flag)
                return flag;

            if (TryReadProperty(tmp, "textWrappingMode", out var mode) && mode != null)
                return !string.Equals(mode.ToString(), "NoWrap", StringComparison.OrdinalIgnoreCase);

            return true;
        }

        /// <summary>
        /// Prefers TMP's split horizontalAlignment/verticalAlignment properties. Falls back
        /// to decomposing the combined TextAlignmentOptions enum by name.
        /// </summary>
        private static void ReadAlignment(TMP_Text tmp, out string horizontal, out string vertical)
        {
            if (TryReadProperty(tmp, "horizontalAlignment", out var h) && h != null &&
                TryReadProperty(tmp, "verticalAlignment", out var v) && v != null)
            {
                horizontal = h.ToString();
                vertical = v.ToString();
                return;
            }

            var name = tmp.alignment.ToString();

            if (name.IndexOf("Left", StringComparison.Ordinal) >= 0) horizontal = "Left";
            else if (name.IndexOf("Right", StringComparison.Ordinal) >= 0) horizontal = "Right";
            else if (name.IndexOf("Justified", StringComparison.Ordinal) >= 0) horizontal = "Justified";
            else if (name.IndexOf("Flush", StringComparison.Ordinal) >= 0) horizontal = "Flush";
            else horizontal = "Center";

            if (name.StartsWith("Top", StringComparison.Ordinal)) vertical = "Top";
            else if (name.StartsWith("Bottom", StringComparison.Ordinal)) vertical = "Bottom";
            else if (name.StartsWith("Baseline", StringComparison.Ordinal)) vertical = "Baseline";
            else if (name.StartsWith("Capline", StringComparison.Ordinal)) vertical = "Capline";
            else vertical = "Middle"; // bare Left/Center/Right are vertically centred in TMP
        }

        private static bool TryReadProperty(object target, string name, out object value)
        {
            value = null;
            var property = target.GetType().GetProperty(
                name, BindingFlags.Public | BindingFlags.Instance);

            if (property == null || !property.CanRead) return false;

            try
            {
                value = property.GetValue(target);
                return true;
            }
            catch { return false; }
        }

        private static bool ContainsMarkup(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var open = text.IndexOf('<');
            return open >= 0 && text.IndexOf('>', open) > open;
        }

        // ------------------------------------------------------------------
        // Writing UniText
        // ------------------------------------------------------------------

        /// <summary>
        /// Writes values into the new component's serialized fields rather than through its
        /// public property setters.
        /// </summary>
        /// <remarks>
        /// A component added this frame has not built its runtime state yet — fontProvider,
        /// textProcessor and friends are still null — so setters that touch them throw.
        /// Serialization side-steps that entirely: Unity deserialises the values and the
        /// component initialises itself normally on enable.
        /// </remarks>
        private static void Apply(
            UniText uniText,
            Snapshot snapshot,
            UniTextConversionSettings settings,
            UniTextFontStack fontStack,
            UniTextAppearance appearance,
            Report report,
            string path)
        {
            var serialized = new SerializedObject(uniText);

            SetReference(serialized, "fontStack", fontStack, report, path);
            if (appearance != null) SetReference(serialized, "appearance", appearance, report, path);

            if (settings.CopyText) SetString(serialized, "text", snapshot.Text);
            if (settings.CopyFontSize) SetFloat(serialized, "fontSize", snapshot.FontSize);

            if (settings.CopyAutoSize)
            {
                SetFloat(serialized, "minFontSize", snapshot.MinFontSize);
                SetFloat(serialized, "maxFontSize", snapshot.MaxFontSize);
                SetBool(serialized, "autoSize", snapshot.AutoSize);
            }

            if (settings.CopyWordWrap) SetBool(serialized, "wordWrap", snapshot.WordWrap);

            if (settings.CopyAlignment)
            {
                SetEnumByName(serialized, "horizontalAlignment", snapshot.HorizontalName);
                SetEnumByName(serialized, "verticalAlignment", snapshot.VerticalName);
            }

            if (settings.CopyDirection)
            {
                SetEnumByName(serialized, "baseDirection",
                    snapshot.RightToLeft ? "RightToLeft" : "Auto");
            }

            if (settings.CopyColor) SetColor(serialized, "m_Color", snapshot.Color);
            if (settings.CopyRaycastTarget) SetBool(serialized, "m_RaycastTarget", snapshot.RaycastTarget);
            if (settings.CopyMaskable) SetBool(serialized, "m_Maskable", snapshot.Maskable);

            SetEnumByName(serialized, "overEdge", settings.OverEdge.ToString());
            SetEnumByName(serialized, "underEdge", settings.UnderEdge.ToString());
            SetEnumByName(serialized, "leadingDistribution", settings.LeadingDistribution.ToString());

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        // ---- serialized field writers ------------------------------------

        private static SerializedProperty Find(SerializedObject serialized, string name) =>
            serialized.FindProperty(name);

        private static void SetString(SerializedObject so, string name, string value)
        {
            var property = Find(so, name);
            if (property != null && property.propertyType == SerializedPropertyType.String)
                property.stringValue = value ?? string.Empty;
        }

        private static void SetFloat(SerializedObject so, string name, float value)
        {
            var property = Find(so, name);
            if (property != null && property.propertyType == SerializedPropertyType.Float)
                property.floatValue = value;
        }

        private static void SetBool(SerializedObject so, string name, bool value)
        {
            var property = Find(so, name);
            if (property != null && property.propertyType == SerializedPropertyType.Boolean)
                property.boolValue = value;
        }

        private static void SetColor(SerializedObject so, string name, Color value)
        {
            var property = Find(so, name);
            if (property != null && property.propertyType == SerializedPropertyType.Color)
                property.colorValue = value;
        }

        /// <summary>
        /// Sets an enum field by member name, trying the usual vocabulary differences
        /// before giving up. Matching on names means the serialised integer never has to
        /// be guessed at.
        /// </summary>
        private static void SetEnumByName(SerializedObject so, string name, string memberName)
        {
            var property = Find(so, name);
            if (property == null || property.propertyType != SerializedPropertyType.Enum) return;
            if (string.IsNullOrEmpty(memberName)) return;

            var names = property.enumNames;

            var index = Array.FindIndex(names,
                n => string.Equals(n, memberName, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                foreach (var synonym in Synonyms(memberName))
                {
                    index = Array.FindIndex(names,
                        n => string.Equals(n, synonym, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0) break;
                }
            }

            if (index >= 0) property.enumValueIndex = index;
        }

        /// <summary>
        /// Assigns a font stack or appearance without knowing whether the package models
        /// it as a ScriptableObject asset or a [SerializeReference] instance.
        /// </summary>
        private static void SetReference(
            SerializedObject so, string name, object value, Report report, string path)
        {
            var property = Find(so, name);
            if (property == null)
            {
                report.Log($"[Warn] {path}: no serialized field '{name}' on UniText.");
                return;
            }

            switch (property.propertyType)
            {
                case SerializedPropertyType.ObjectReference:
                    property.objectReferenceValue = value as UnityEngine.Object;
                    return;

                case SerializedPropertyType.ManagedReference:
                    property.managedReferenceValue = value;
                    return;

                default:
                    report.Log($"[Warn] {path}: '{name}' is a {property.propertyType} field. " +
                               "It has to be assigned by hand after conversion.");
                    return;
            }
        }

        private static IEnumerable<string> Synonyms(string name)
        {
            switch (name)
            {
                case "Middle": yield return "Center"; yield return "Centre"; break;
                case "Center": yield return "Middle"; break;
                case "Justified": yield return "Justify"; break;
                case "Flush": yield return "Justified"; yield return "Justify"; break;
                case "Baseline": yield return "Bottom"; break;
                case "Capline": yield return "Top"; break;
                case "Geometry": yield return "Center"; yield return "Middle"; break;
            }
        }

        // ------------------------------------------------------------------
        // Component ordering
        // ------------------------------------------------------------------

        private static int GetComponentIndex(Component component)
        {
            var all = component.gameObject.GetComponents<Component>();
            return Array.IndexOf(all, component);
        }

        private static void RestoreComponentIndex(Component component, int desiredIndex)
        {
            if (desiredIndex < 0) return;

            var guard = 0;
            while (GetComponentIndex(component) > desiredIndex && guard++ < 64)
            {
                if (!UnityEditorInternal.ComponentUtility.MoveComponentUp(component)) break;
            }
        }

        // ------------------------------------------------------------------
        // Batch
        // ------------------------------------------------------------------

        public static void ConvertHierarchy(
            IEnumerable<GameObject> roots,
            UniTextConversionSettings settings,
            bool includeInactive,
            bool registerUndo,
            Report report)
        {
            foreach (var root in roots)
            {
                if (root == null) continue;

                // Materialise first: components are destroyed as we iterate.
                var components = root.GetComponentsInChildren<TMP_Text>(includeInactive).ToList();

                foreach (var component in components)
                {
                    if (component == null) continue;

                    if (PrefabUtility.IsPartOfPrefabInstance(component) &&
                        !PrefabUtility.IsPartOfPrefabAsset(component))
                    {
                        var source = PrefabUtility.GetCorrespondingObjectFromSource(component);
                        var assetPath = AssetDatabase.GetAssetPath(source);

                        report.Skipped++;
                        report.Log($"[Skip] {GetHierarchyPath(component.transform)}: prefab instance. " +
                                   "Convert the prefab asset instead" +
                                   (string.IsNullOrEmpty(assetPath) ? "." : $": {assetPath}"));
                        continue;
                    }

                    ConvertComponent(component, settings, registerUndo, report);
                }
            }
        }

        // ------------------------------------------------------------------
        // Reference scanning
        // ------------------------------------------------------------------

        public class ReferenceHit
        {
            public string DeclaringType;
            public string FieldName;
            public string FieldType;
            public string ScriptPath;
        }

        /// <summary>
        /// Finds serialized fields typed against TMP components. UniText is not a TMP type,
        /// so these cannot survive the migration and must be retyped by hand.
        /// </summary>
        public static List<ReferenceHit> ScanForBrokenReferences()
        {
            var hits = new List<ReferenceHit>();

            var types = TypeCache.GetTypesDerivedFrom<MonoBehaviour>()
                .Concat(TypeCache.GetTypesDerivedFrom<ScriptableObject>());

            foreach (var type in types)
            {
                var space = type.Namespace ?? string.Empty;
                if (space.StartsWith("TMPro") ||
                    space.StartsWith("UnityEngine") ||
                    space.StartsWith("UnityEditor"))
                    continue;

                var fields = type.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.DeclaredOnly);

                foreach (var field in fields)
                {
                    if (!typeof(TMP_Text).IsAssignableFrom(GetElementType(field.FieldType))) continue;

                    var serialized = field.IsPublic ||
                                     field.GetCustomAttribute<SerializeField>() != null;
                    if (!serialized) continue;

                    hits.Add(new ReferenceHit
                    {
                        DeclaringType = type.FullName,
                        FieldName = field.Name,
                        FieldType = field.FieldType.Name,
                        ScriptPath = FindScriptPath(type)
                    });
                }
            }

            return hits.OrderBy(h => h.DeclaringType).ThenBy(h => h.FieldName).ToList();
        }

        private static Type GetElementType(Type type)
        {
            if (type.IsArray) return type.GetElementType();
            if (type.IsGenericType && type.GetGenericArguments().Length == 1)
                return type.GetGenericArguments()[0];
            return type;
        }

        private static string FindScriptPath(Type type)
        {
            foreach (var guid in AssetDatabase.FindAssets($"t:MonoScript {type.Name}"))
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(assetPath);
                if (script != null && script.GetClass() == type) return assetPath;
            }
            return string.Empty;
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// True if any object in the hierarchy is an instance of a prefab Unity cannot
        /// resolve. Saving such a prefab discards the broken instance permanently, so
        /// these must be repaired before the asset is written back.
        /// </summary>
        public static bool TryFindMissingNestedPrefab(GameObject root, out string objectName)
        {
            objectName = null;
            if (root == null) return false;

            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (transform == null) continue;
                if (!PrefabUtility.IsPrefabAssetMissing(transform.gameObject)) continue;

                objectName = GetHierarchyPath(transform);
                return true;
            }

            return false;
        }

        public static string GetHierarchyPath(Transform transform)
        {
            if (transform == null) return "<null>";
            var stack = new Stack<string>();
            while (transform != null)
            {
                stack.Push(transform.name);
                transform = transform.parent;
            }
            return string.Join("/", stack);
        }
    }
}
