// -----------------------------------------------------------------------------
// IMPORTANT: PerSpec.Editor.Preflight must never reference another assembly.
//
// Every other PerSpec editor assembly depends on Gilzoide.SqliteNet, directly or
// transitively:
//
//   PerSpec.Editor.Initialization -> PerSpec.Editor.Services -> PerSpec.Editor.Coordination -> Gilzoide.SqliteNet
//   PerSpec.Editor.Windows        -> PerSpec.Editor.Coordination                             -> Gilzoide.SqliteNet
//   PerSpec.Editor.UnityHelper                                                               -> Gilzoide.SqliteNet
//
// So when com.gilzoide.sqlite-net fails to resolve, all of them fail to compile,
// the Tools > PerSpec menu disappears, and no PerSpec code survives to explain
// why. This assembly exists to be the one that still compiles and still talks.
// Adding a reference here - to any assembly, PerSpec or otherwise - defeats its
// entire purpose.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// UnityEditor also defines a PackageInfo (the legacy asset-store one), so the name is
// ambiguous under a plain "using UnityEditor.PackageManager".
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace PerSpec.Editor.Preflight
{
    /// <summary>
    /// Reports missing PerSpec package dependencies with an actionable message instead of
    /// letting Unity fail with a bare "package cannot be found" and a vanished menu.
    /// </summary>
    [InitializeOnLoad]
    internal static class PerSpecDependencyDoctor
    {
        #region Constants

        private const string OpenUpmName = "package.openupm.com";
        private const string OpenUpmUrl = "https://package.openupm.com";

        /// <summary>Logged at most once per editor session, not once per domain reload.</summary>
        private const string SessionKeyReported = "PerSpec.Preflight.Reported";

        private const string MenuPathRepair = "Tools/PerSpec/Repair Package Dependencies";

        #endregion

        #region Required Packages

        /// <summary>
        /// A dependency declared in package.json, plus where it actually comes from.
        /// The registry matters: telling someone to add an OpenUPM scope for a package
        /// that lives on the Unity registry sends them the wrong way.
        /// </summary>
        private readonly struct RequiredPackage
        {
            public readonly string Id;
            public readonly bool FromOpenUpm;
            public readonly string Purpose;

            public RequiredPackage(string id, bool fromOpenUpm, string purpose)
            {
                Id = id;
                FromOpenUpm = fromOpenUpm;
                Purpose = purpose;
            }
        }

        // Keep in sync with the "dependencies" block of package.json.
        private static readonly RequiredPackage[] Required =
        {
            new RequiredPackage("com.gilzoide.sqlite-net", true,
                "the SQLite coordination database that every PerSpec editor assembly builds on"),
            new RequiredPackage("com.cysharp.unitask", true,
                "the async test patterns PerSpec is built around"),
            new RequiredPackage("com.unity.nuget.newtonsoft-json", false,
                "scene hierarchy and scenario JSON"),
            new RequiredPackage("com.unity.test-framework", false,
                "test discovery and execution"),
        };

        #endregion

        #region Startup Check

        static PerSpecDependencyDoctor()
        {
            // Defer: package registration is not settled during the static constructor,
            // and GetAllRegisteredPackages can legitimately come back empty that early.
            EditorApplication.delayCall += CheckOnStartup;
        }

        private static void CheckOnStartup()
        {
            if (SessionState.GetBool(SessionKeyReported, false))
            {
                return;
            }

            try
            {
                var missing = FindMissingPackages();

                // The overwhelmingly common case. Say nothing at all.
                if (missing.Count == 0)
                {
                    return;
                }

                SessionState.SetBool(SessionKeyReported, true);
                Debug.LogError(BuildDiagnosticMessage(missing));
            }
            catch (Exception ex)
            {
                // A broken doctor must never be the reason a project looks broken.
                Debug.LogWarning($"[PerSpec] Dependency preflight could not run: {ex.Message}");
            }
        }

        #endregion

        #region Detection

        /// <summary>
        /// Returns the required packages Unity has not registered. Empty when everything
        /// resolved, or when the package list is not available yet (nothing can be concluded
        /// from an empty list, so we stay quiet rather than cry wolf).
        /// </summary>
        private static List<RequiredPackage> FindMissingPackages()
        {
            var missing = new List<RequiredPackage>();

            // Synchronous, unlike Client.List(), which would need polling.
            var registered = PackageInfo.GetAllRegisteredPackages();
            if (registered == null || registered.Length == 0)
            {
                return missing;
            }

            var present = new HashSet<string>(registered.Select(p => p.name), StringComparer.OrdinalIgnoreCase);

            foreach (var required in Required)
            {
                if (!present.Contains(required.Id))
                {
                    missing.Add(required);
                }
            }

            return missing;
        }

        #endregion

        #region Manifest Inspection

        private enum ScopeState
        {
            /// <summary>manifest.json could not be read.</summary>
            ManifestUnreadable,

            /// <summary>No scopedRegistries block at all.</summary>
            NoScopedRegistries,

            /// <summary>A scopedRegistries block exists, but no OpenUPM registry in it.</summary>
            NoOpenUpmRegistry,

            /// <summary>OpenUPM registry exists but does not list this package as a scope.</summary>
            ScopeMissing,

            /// <summary>The scope is listed. The cause is something other than a missing scope.</summary>
            ScopePresent
        }

        private static string ManifestPath =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Packages", "manifest.json");

        /// <summary>
        /// Inspects manifest.json as raw text. Deliberately avoids a JSON library, because
        /// com.unity.nuget.newtonsoft-json is itself on the list of things that may be missing.
        /// </summary>
        private static ScopeState GetScopeState(string packageId)
        {
            string manifest;
            try
            {
                manifest = File.ReadAllText(ManifestPath);
            }
            catch
            {
                return ScopeState.ManifestUnreadable;
            }

            string scopedRegistries = ExtractScopedRegistriesArray(manifest);
            if (scopedRegistries == null)
            {
                return ScopeState.NoScopedRegistries;
            }

            if (scopedRegistries.IndexOf(OpenUpmUrl, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return ScopeState.NoOpenUpmRegistry;
            }

            // Searching only inside the scopedRegistries array is what makes a plain substring
            // test safe here: the same package id also appears in "dependencies", where it means
            // something completely different.
            return scopedRegistries.IndexOf("\"" + packageId + "\"", StringComparison.OrdinalIgnoreCase) >= 0
                ? ScopeState.ScopePresent
                : ScopeState.ScopeMissing;
        }

        /// <summary>
        /// Returns the text of the scopedRegistries array, brackets included, or null when the
        /// key is absent. Counts brackets so nested registry objects do not end the scan early.
        /// </summary>
        private static string ExtractScopedRegistriesArray(string manifest)
        {
            int key = manifest.IndexOf("\"scopedRegistries\"", StringComparison.Ordinal);
            if (key < 0)
            {
                return null;
            }

            int open = manifest.IndexOf('[', key);
            if (open < 0)
            {
                return null;
            }

            int depth = 0;
            for (int i = open; i < manifest.Length; i++)
            {
                char c = manifest[i];
                if (c == '[')
                {
                    depth++;
                }
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return manifest.Substring(open, i - open + 1);
                    }
                }
            }

            return null;
        }

        #endregion

        #region Messaging

        private static string BuildDiagnosticMessage(List<RequiredPackage> missing)
        {
            var sb = new StringBuilder();

            sb.AppendLine(missing.Count == 1
                ? $"[PerSpec] Missing required package: {missing[0].Id}"
                : $"[PerSpec] Missing {missing.Count} required packages: {string.Join(", ", missing.Select(m => m.Id))}");
            sb.AppendLine();
            sb.AppendLine("PerSpec cannot compile without these, so most of the Tools > PerSpec menu will be");
            sb.AppendLine("unavailable until this is resolved.");
            sb.AppendLine();

            foreach (var package in missing)
            {
                sb.AppendLine($"  {package.Id}");
                sb.AppendLine($"    Needed for: {package.Purpose}");

                if (!package.FromOpenUpm)
                {
                    sb.AppendLine("    Source: the Unity registry. No scoped registry needed.");
                    sb.AppendLine("    Check your network, then reopen the Package Manager window to retry.");
                    sb.AppendLine();
                    continue;
                }

                sb.AppendLine($"    Source: OpenUPM ({OpenUpmUrl})");

                switch (GetScopeState(package.Id))
                {
                    case ScopeState.ManifestUnreadable:
                        sb.AppendLine($"    Could not read {ManifestPath} to check your scoped registries.");
                        break;
                    case ScopeState.NoScopedRegistries:
                        sb.AppendLine("    Your Packages/manifest.json has no scopedRegistries block, so Unity asked");
                        sb.AppendLine("    the default Unity registry, which does not host this package.");
                        break;
                    case ScopeState.NoOpenUpmRegistry:
                        sb.AppendLine("    Your Packages/manifest.json has scoped registries, but none for OpenUPM.");
                        break;
                    case ScopeState.ScopeMissing:
                        sb.AppendLine("    Your OpenUPM registry does not list this package in its scopes, so Unity");
                        sb.AppendLine("    asked the default Unity registry instead and got nothing.");
                        break;
                    case ScopeState.ScopePresent:
                        sb.AppendLine("    The scope IS listed correctly, so this is not a manifest problem. It is");
                        sb.AppendLine("    most likely a failed or interrupted download. Reopen the Package Manager");
                        sb.AppendLine("    window to retry, and check your network or proxy.");
                        break;
                }

                sb.AppendLine();
            }

            sb.AppendLine($"Fix: {MenuPathRepair.Replace("/", " > ")}");

            return sb.ToString();
        }

        /// <summary>The manifest block a correctly configured consumer needs.</summary>
        private static string BuildScopedRegistrySnippet()
        {
            var scopes = new List<string> { "com.digitraver.perspec" };
            scopes.AddRange(Required.Where(r => r.FromOpenUpm).Select(r => r.Id));

            var sb = new StringBuilder();
            sb.AppendLine("\"scopedRegistries\": [");
            sb.AppendLine("  {");
            sb.AppendLine($"    \"name\": \"{OpenUpmName}\",");
            sb.AppendLine($"    \"url\": \"{OpenUpmUrl}\",");
            sb.AppendLine("    \"scopes\": [");
            for (int i = 0; i < scopes.Count; i++)
            {
                sb.AppendLine($"      \"{scopes[i]}\"{(i < scopes.Count - 1 ? "," : string.Empty)}");
            }
            sb.AppendLine("    ]");
            sb.AppendLine("  }");
            sb.Append("]");

            return sb.ToString();
        }

        #endregion

        #region Repair

        [MenuItem(MenuPathRepair, priority = 500)]
        private static void RepairPackageDependencies()
        {
            var missing = FindMissingPackages();

            if (missing.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "PerSpec Dependencies",
                    "All required packages resolved. Nothing to repair.",
                    "OK");
                return;
            }

            // A scoped registry snippet only helps OpenUPM packages. Handing it to someone whose
            // only missing package comes from the Unity registry would send them the wrong way.
            var openUpmMissing = missing.Where(m => m.FromOpenUpm).ToList();
            if (openUpmMissing.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "PerSpec Dependencies",
                    $"Missing: {string.Join(", ", missing.Select(m => m.Id))}\n\n" +
                    "These come from the default Unity registry, so no scoped registry is involved and " +
                    "there is nothing to repair in your manifest.\n\n" +
                    "This is almost always a failed download. Check your network or proxy, then reopen " +
                    "Window > Package Management > Package Manager to retry.",
                    "OK");
                return;
            }

            // Deliberately no automated manifest edit.
            //
            // There is no public, stable API for this: UnityEditor.PackageManager.Client has no
            // AddScopedRegistry in 6000.3 (verified by the compiler), so any automated path would
            // mean hand-editing JSON. Doing that would also require merging scopes into whatever
            // registries already exist, and a bad merge breaks the one file that gates the entire
            // project. Newtonsoft cannot be trusted to parse it either, since it is on the list of
            // packages that may be missing.
            //
            // Copying an exact, correct snippet costs the user one paste and cannot corrupt anything.
            ShowSnippetDialog(openUpmMissing);
        }

        private static void ShowSnippetDialog(List<RequiredPackage> missing)
        {
            string snippet = BuildScopedRegistrySnippet();
            EditorGUIUtility.systemCopyBuffer = snippet;

            bool reveal = EditorUtility.DisplayDialog(
                "PerSpec Dependencies",
                $"Missing: {string.Join(", ", missing.Select(m => m.Id))}\n\n" +
                "The correct scopedRegistries block has been copied to your clipboard. Paste it into " +
                "Packages/manifest.json, merging the scopes into your existing OpenUPM registry if you " +
                "already have one.\n\n" +
                "Unity re-resolves packages as soon as the file is saved.\n\n" +
                snippet,
                "Show manifest.json",
                "Close");

            if (reveal)
            {
                EditorUtility.RevealInFinder(ManifestPath);
            }
        }

        #endregion
    }
}
