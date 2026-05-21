// TmproTaskExecutor — TYPE 2 TMPRO module.
//
// Package-specific module per rules.md §"UnityHelper Modification Rules" Rule 2 — TextMeshPro
// is the runtime owner of fonts in this project. SCENE (type 0) intentionally does NOT carry
// font-creation verbs because they only make sense when TMP is present. The Localization (type 1)
// module is the precedent for this layout.
//
// Verbs:
//   CreateFontAsset    — TTF/OTF → TMP_FontAsset SDF (.asset).
//   AddFallbackFont    — append a font to a primary font's fallback table.
//   RemoveFallbackFont — remove a font from a primary font's fallback table.
//   ListFontAssets     — diagnostics: dump every TMP_FontAsset under a folder + fallback chain.
//
// All edits go through TMP's editor APIs and SetDirty + SaveAssets — no hand YAML editing
// (rules.md §"Edit Prefabs/Scenes via UnityHelper, Not YAML").

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace PerSpec.UnityHelper.Editor
{
    public class TmproTaskExecutor : BaseTaskExecutor
    {
        public override ExecutorType Type => ExecutorType.TMPRO;

        public override bool Execute(Task task)
        {
            try
            {
                switch (task.action)
                {
                    case "CreateFontAsset":     return CreateFontAsset(task);
                    case "AddFallbackFont":     return AddFallbackFont(task);
                    case "RemoveFallbackFont":  return RemoveFallbackFont(task);
                    case "ListFontAssets":      return ListFontAssets(task);
                    default:
                        task.error = $"Unknown typography action: {task.action}";
                        return false;
                }
            }
            catch (Exception ex)
            {
                task.error = ex.Message;
                return false;
            }
        }

        // -------------------------------------------------------------------
        // CreateFontAsset
        // -------------------------------------------------------------------
        // Required:
        //   sourceFont        — path to TTF/OTF (e.g. Assets/Fonts/NotoSansArabic-Regular.ttf)
        //   outputPath        — path to save the .asset (e.g. Assets/Fonts/NotoSansArabic SDF.asset)
        // Optional:
        //   samplingPointSize — default 90
        //   atlasPadding      — default 9
        //   atlasWidth        — default 2048
        //   atlasHeight       — default 2048
        //   renderMode        — SDFAA (default), SDFAA_HINTED, RASTER_HINTED, …
        //   populationMode    — "Dynamic" (default — recommended for fallbacks) or "Static"
        // -------------------------------------------------------------------
        private bool CreateFontAsset(Task task)
        {
            string sourceFont = GetParam(task, "sourceFont");
            string outputPath = GetParam(task, "outputPath");
            if (string.IsNullOrEmpty(sourceFont))
            {
                task.error = "CreateFontAsset failed: 'sourceFont' parameter is required (path to TTF/OTF).";
                return false;
            }
            if (string.IsNullOrEmpty(outputPath))
            {
                task.error = "CreateFontAsset failed: 'outputPath' parameter is required (path to .asset).";
                return false;
            }
            if (!outputPath.EndsWith(".asset"))
            {
                task.error = "CreateFontAsset failed: 'outputPath' must end with .asset.";
                return false;
            }

            int samplingPointSize = ParseInt(GetOptionalParam(task, "samplingPointSize", "90"), 90);
            int atlasPadding      = ParseInt(GetOptionalParam(task, "atlasPadding", "9"), 9);
            int atlasWidth        = ParseInt(GetOptionalParam(task, "atlasWidth", "2048"), 2048);
            int atlasHeight       = ParseInt(GetOptionalParam(task, "atlasHeight", "2048"), 2048);

            GlyphRenderMode renderMode = ParseEnum(GetOptionalParam(task, "renderMode", "SDFAA"),
                GlyphRenderMode.SDFAA);

            AtlasPopulationMode populationMode = ParseEnum(GetOptionalParam(task, "populationMode", "Dynamic"),
                AtlasPopulationMode.Dynamic);

            var sourceFontAsset = AssetDatabase.LoadAssetAtPath<Font>(sourceFont);
            if (sourceFontAsset == null)
            {
                task.error = $"CreateFontAsset failed: source font not found at '{sourceFont}'.";
                return false;
            }

            // Idempotent — if the output already exists, return success without overwriting.
            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(outputPath);
            if (existing != null)
            {
                task.result = $"Font asset already exists: {outputPath}";
                return true;
            }

            string outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir) && !AssetDatabase.IsValidFolder(outputDir))
            {
                Directory.CreateDirectory(outputDir);
                AssetDatabase.Refresh();
            }

            var fontAsset = TMP_FontAsset.CreateFontAsset(
                sourceFontAsset,
                samplingPointSize,
                atlasPadding,
                renderMode,
                atlasWidth,
                atlasHeight,
                populationMode,
                enableMultiAtlasSupport: true);

            if (fontAsset == null)
            {
                task.error = $"CreateFontAsset failed: TMP_FontAsset.CreateFontAsset returned null for '{sourceFont}'.";
                return false;
            }

            AssetDatabase.CreateAsset(fontAsset, outputPath);

            // CRITICAL: TMP_FontAsset creates the atlas Texture2D and the rendering Material
            // in-memory; without AddObjectToAsset they're transient and disappear on save —
            // the saved .asset has m_Material: {fileID: 0} and won't render any glyphs.
            // Mirrors what TMP_FontAsset_CreatorWindow does after manual font generation.
            string baseName = Path.GetFileNameWithoutExtension(outputPath);
            if (fontAsset.atlasTexture != null)
            {
                fontAsset.atlasTexture.name = $"{baseName} Atlas";
                AssetDatabase.AddObjectToAsset(fontAsset.atlasTexture, fontAsset);
            }
            if (fontAsset.material != null)
            {
                fontAsset.material.name = $"{baseName} Material";
                AssetDatabase.AddObjectToAsset(fontAsset.material, fontAsset);
            }

            EditorUtility.SetDirty(fontAsset);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[TmproTaskExecutor] CreateFontAsset: {sourceFont} → {outputPath} ({populationMode}, {samplingPointSize}px, {atlasWidth}x{atlasHeight}) ✓");
            task.result = $"Created font asset: {outputPath}";
            return true;
        }

        // -------------------------------------------------------------------
        // AddFallbackFont — append `fallback` to `font.fallbackFontAssetTable`.
        // -------------------------------------------------------------------
        // Required:
        //   font     — path to the primary TMP_FontAsset (.asset)
        //   fallback — path to the fallback TMP_FontAsset (.asset). Pipe-separated for multiple.
        // Idempotent: skips fallbacks already present.
        // -------------------------------------------------------------------
        private bool AddFallbackFont(Task task)
        {
            string fontPath = GetParam(task, "font");
            string fallbackParam = GetParam(task, "fallback");
            if (string.IsNullOrEmpty(fontPath) || string.IsNullOrEmpty(fallbackParam))
            {
                task.error = "AddFallbackFont failed: 'font' and 'fallback' parameters are required.";
                return false;
            }

            var primary = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(fontPath);
            if (primary == null)
            {
                task.error = $"AddFallbackFont failed: primary font asset not found at '{fontPath}'.";
                return false;
            }
            if (primary.fallbackFontAssetTable == null)
                primary.fallbackFontAssetTable = new List<TMP_FontAsset>();

            int added = 0;
            int skipped = 0;
            foreach (string fb in fallbackParam.Split('|').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)))
            {
                var fbAsset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(fb);
                if (fbAsset == null)
                {
                    Debug.LogWarning($"[TmproTaskExecutor] AddFallbackFont: fallback not found at '{fb}', skipping.");
                    continue;
                }
                if (fbAsset == primary)
                {
                    Debug.LogWarning($"[TmproTaskExecutor] AddFallbackFont: refused to add font as its own fallback ({fb}).");
                    continue;
                }
                if (primary.fallbackFontAssetTable.Contains(fbAsset))
                {
                    skipped++;
                    continue;
                }
                primary.fallbackFontAssetTable.Add(fbAsset);
                added++;
            }

            EditorUtility.SetDirty(primary);
            AssetDatabase.SaveAssets();
            Debug.Log($"[TmproTaskExecutor] AddFallbackFont: {fontPath} += {added} fallback(s), {skipped} already present ✓");
            task.result = $"Added {added} fallback(s), {skipped} already present";
            return true;
        }

        // -------------------------------------------------------------------
        // RemoveFallbackFont — remove `fallback` from `font.fallbackFontAssetTable`.
        // -------------------------------------------------------------------
        private bool RemoveFallbackFont(Task task)
        {
            string fontPath = GetParam(task, "font");
            string fallbackParam = GetParam(task, "fallback");
            if (string.IsNullOrEmpty(fontPath) || string.IsNullOrEmpty(fallbackParam))
            {
                task.error = "RemoveFallbackFont failed: 'font' and 'fallback' parameters are required.";
                return false;
            }

            var primary = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(fontPath);
            if (primary == null)
            {
                task.error = $"RemoveFallbackFont failed: primary font asset not found at '{fontPath}'.";
                return false;
            }
            if (primary.fallbackFontAssetTable == null || primary.fallbackFontAssetTable.Count == 0)
            {
                task.result = "No fallbacks to remove.";
                return true;
            }

            int removed = 0;
            foreach (string fb in fallbackParam.Split('|').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)))
            {
                var fbAsset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(fb);
                if (fbAsset == null) continue;
                removed += primary.fallbackFontAssetTable.RemoveAll(f => f == fbAsset);
            }

            EditorUtility.SetDirty(primary);
            AssetDatabase.SaveAssets();
            Debug.Log($"[TmproTaskExecutor] RemoveFallbackFont: {fontPath} -= {removed} fallback(s) ✓");
            task.result = $"Removed {removed} fallback(s)";
            return true;
        }

        // -------------------------------------------------------------------
        // ListFontAssets — dump every TMP_FontAsset and its fallback chain.
        // -------------------------------------------------------------------
        // Optional:
        //   folder — restrict to a folder. Default: Assets/.
        // -------------------------------------------------------------------
        private bool ListFontAssets(Task task)
        {
            string folder = GetOptionalParam(task, "folder", "Assets");
            string[] guids = AssetDatabase.FindAssets("t:TMP_FontAsset", new[] { folder });
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== TMP_FontAsset under '{folder}' ===");
            sb.AppendLine($"Count: {guids.Length}");

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var fa = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                if (fa == null) continue;
                sb.AppendLine($"- {path}");
                sb.AppendLine($"    population: {fa.atlasPopulationMode}, samplingPx: {fa.creationSettings.pointSize}, atlas: {fa.atlasWidth}x{fa.atlasHeight}");
                if (fa.fallbackFontAssetTable != null && fa.fallbackFontAssetTable.Count > 0)
                {
                    sb.AppendLine($"    fallbacks ({fa.fallbackFontAssetTable.Count}):");
                    foreach (var fb in fa.fallbackFontAssetTable)
                    {
                        if (fb == null) continue;
                        sb.AppendLine($"      • {AssetDatabase.GetAssetPath(fb)}");
                    }
                }
            }

            task.result = sb.ToString();
            Debug.Log($"[TmproTaskExecutor] ListFontAssets: {guids.Length} font(s) under '{folder}' ✓");
            return true;
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------
        private static int ParseInt(string s, int defaultValue)
        {
            return int.TryParse(s, out int v) ? v : defaultValue;
        }

        private static T ParseEnum<T>(string s, T defaultValue) where T : struct
        {
            if (string.IsNullOrEmpty(s)) return defaultValue;
            return Enum.TryParse<T>(s, ignoreCase: true, out T result) ? result : defaultValue;
        }
    }
}
