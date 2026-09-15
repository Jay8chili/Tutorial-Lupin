// Assets/Editor/UniText/UniTextConversionSettings.cs
// Configuration for migrating TextMeshProUGUI to LightSide.UniText.

using System;
using System.Collections.Generic;
using LightSide;
using TMPro;
using UnityEngine;

namespace UniTextTools.EditorTools
{
    [CreateAssetMenu(
        fileName = "UniTextConversionSettings",
        menuName = "UniText/Conversion Settings")]
    public class UniTextConversionSettings : ScriptableObject
    {
        /// <summary>
        /// Pairs a TMP font asset with the UniText font stack and appearance that should
        /// replace it. There is no automatic equivalence between the two systems, so this
        /// has to be stated explicitly.
        /// </summary>
        [Serializable]
        public class FontMapping
        {
            [Tooltip("The TMP font asset used by your existing components.")]
            public TMP_FontAsset SourceFont;

            [Tooltip("Font stack to assign when a component uses the font above.")]
            public UniTextFontStack FontStack;

            [Tooltip("Appearance to assign alongside it. Leave empty to use the default.")]
            public UniTextAppearance Appearance;
        }

        [Header("Fonts")]
        [Tooltip("Used when a component's font asset has no explicit mapping below.")]
        public UniTextFontStack DefaultFontStack;

        [Tooltip("Used when a mapping does not specify its own appearance.")]
        public UniTextAppearance DefaultAppearance;

        [Tooltip("Per-font overrides. Anything not listed falls back to the defaults above.")]
        public List<FontMapping> FontMappings = new List<FontMapping>();

        [Tooltip("Skip components whose font asset is not listed above, instead of " +
                 "falling back to the default stack.")]
        public bool RequireExplicitFontMapping;

        [Header("Values to Migrate")]
        public bool CopyText = true;
        public bool CopyFontSize = true;
        public bool CopyAutoSize = true;
        public bool CopyAlignment = true;
        public bool CopyWordWrap = true;
        public bool CopyDirection = true;
        public bool CopyColor = true;
        public bool CopyRaycastTarget = true;
        public bool CopyMaskable = true;

        [Header("Defaults for Values TMP Has No Equivalent For")]
        [Tooltip("Applied to every converted component. TMP has no matching concept.")]
        public TextOverEdge OverEdge = TextOverEdge.Ascent;

        public TextUnderEdge UnderEdge = TextUnderEdge.Descent;

        public LeadingDistribution LeadingDistribution = LeadingDistribution.HalfLeading;

        [Header("Rich Text")]
        [Tooltip("TMP markup is not UniText markup. When a component has rich text enabled " +
                 "and its text contains tags, log it so you can review the markup by hand.")]
        public bool WarnOnRichTextMarkup = true;

        // ------------------------------------------------------------------

        /// <summary>
        /// Resolves the font stack and appearance for a given TMP font asset.
        /// </summary>
        public bool TryResolveFont(
            TMP_FontAsset sourceFont,
            out UniTextFontStack fontStack,
            out UniTextAppearance appearance)
        {
            foreach (var mapping in FontMappings)
            {
                if (mapping == null) continue;
                if (mapping.SourceFont != sourceFont) continue;

                fontStack = mapping.FontStack;
                appearance = mapping.Appearance ?? DefaultAppearance;
                return fontStack != null;
            }

            if (RequireExplicitFontMapping)
            {
                fontStack = null;
                appearance = null;
                return false;
            }

            fontStack = DefaultFontStack;
            appearance = DefaultAppearance;
            return fontStack != null;
        }

        public bool IsConfigured =>
            DefaultFontStack != null || FontMappings.Exists(m => m?.FontStack != null);
    }
}
