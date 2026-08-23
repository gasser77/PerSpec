using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace PerSpec.Editor.Coordination
{
    /// <summary>
    /// What a pre-flight filter resolution concluded.
    /// </summary>
    public enum PreflightVerdict
    {
        /// <summary>The filter selects at least one test. Run it.</summary>
        Matched,

        /// <summary>The filter demonstrably selects nothing. A caller mistake - do not run.</summary>
        NoMatch,

        /// <summary>
        /// The question could not be answered. ALWAYS run: an unverifiable pre-flight must
        /// never turn a runnable request into an error.
        /// </summary>
        CouldNotVerify
    }

    public struct PreflightResult
    {
        public PreflightVerdict Verdict;

        /// <summary>Short explanation for the editor log, whatever the verdict.</summary>
        public string Reason;

        /// <summary>Caller-facing text for test_requests.error_message. Only set for NoMatch.</summary>
        public string ErrorMessage;

        /// <summary>Fully qualified name the caller probably meant. May be null.</summary>
        public string Suggestion;

        /// <summary>Leaf tests in the retrieved tree, before filtering.</summary>
        public int TotalTests;
    }

    /// <summary>
    /// Resolves a test request's filter against Unity's test tree BEFORE anything runs.
    ///
    /// A filter that matches nothing used to cost a full PlayMode cycle and end as
    /// 'inconclusive' - the same status used for "compilation errors" and "every test
    /// skipped". That made a caller typo indistinguishable from a condition of the project,
    /// so the natural reaction was to retry, which never helps. Resolving the filter first
    /// turns that into an immediate, named error.
    ///
    /// Governing rule: a pre-flight that cannot answer must return
    /// <see cref="PreflightVerdict.CouldNotVerify"/> and let the run proceed. A false
    /// 'no match' sends the caller to rename a class that was fine - the same failure this
    /// exists to prevent, merely inverted. Proceeding only ever costs what shipping today
    /// costs.
    /// </summary>
    public static class TestFilterPreflight
    {
        /// <summary>
        /// RetrieveTestList hangs its job off EditorApplication.update, which does not
        /// survive a domain reload - so the callback can simply never arrive. Generous
        /// against a 300s --wait, and a cold assembly scan genuinely takes seconds.
        /// </summary>
        private const double TimeoutSeconds = 30.0;

        /// <summary>How many existing category names to name when a category matches nothing.</summary>
        private const int MaxCategoriesListed = 8;

        private static TestRunnerApi _api;

        /// <summary>
        /// Shared instance, mirroring TestExecutor: a TestRunnerApi ScriptableObject left
        /// without HideAndDontSave is destroyed on scene change / domain reload while the
        /// static reference survives, and every later call hits a fake-null object.
        /// </summary>
        private static TestRunnerApi Api
        {
            get
            {
                if (_api == null)
                {
                    _api = ScriptableObject.CreateInstance<TestRunnerApi>();
                    _api.hideFlags = HideFlags.HideAndDontSave;
                }

                return _api;
            }
        }

        /// <summary>
        /// Retrieves the test tree for <paramref name="testMode"/> and reports whether the
        /// request's filter selects anything. The callback is always invoked exactly once,
        /// on the main thread, including on timeout and on failure.
        /// </summary>
        /// <param name="testMode">
        /// Must be the SAME value the run will use. A PlayMode-only class does not appear in
        /// the EditMode tree, so a mismatch here false-reports every PlayMode request.
        /// </param>
        public static void Resolve(TestRequest request, TestMode testMode, Action<PreflightResult> onResolved)
        {
            if (onResolved == null)
            {
                return;
            }

            if (request == null)
            {
                onResolved(CouldNotVerify("No request to resolve a filter for"));
                return;
            }

            string requestType = request.RequestType ?? "all";
            string filter = request.TestFilter;

            // Nothing to get wrong: an unfiltered run matches by definition.
            if (requestType == "all" || string.IsNullOrEmpty(filter))
            {
                onResolved(new PreflightResult
                {
                    Verdict = PreflightVerdict.Matched,
                    Reason = "unfiltered run - nothing to resolve"
                });
                return;
            }

            bool settled = false;
            double deadline = EditorApplication.timeSinceStartup + TimeoutSeconds;
            EditorApplication.CallbackFunction watchdog = null;

            Action<PreflightResult> settle = result =>
            {
                if (settled)
                {
                    return;
                }

                settled = true;

                if (watchdog != null)
                {
                    EditorApplication.update -= watchdog;
                }

                onResolved(result);
            };

            watchdog = () =>
            {
                if (EditorApplication.timeSinceStartup >= deadline)
                {
                    settle(CouldNotVerify(
                        $"Test list retrieval did not answer within {TimeoutSeconds:F0}s - running anyway"));
                }
            };

            EditorApplication.update += watchdog;

            try
            {
                Api.RetrieveTestList(testMode, root =>
                {
                    try
                    {
                        // The job passes Current after MoveNext() returned false, so a null
                        // root is possible in principle. Treat it as "no answer", not "no tests".
                        if (root == null)
                        {
                            settle(CouldNotVerify("Test list retrieval returned no tree - running anyway"));
                            return;
                        }

                        var leaves = new List<ITestAdaptor>();
                        CollectLeaves(root, leaves);

                        settle(Evaluate(
                            leaves.Select(leaf => leaf.FullName),
                            leaves.Select(leaf => leaf.Categories),
                            requestType,
                            filter));
                    }
                    catch (Exception e)
                    {
                        settle(CouldNotVerify($"Test list could not be read ({e.Message}) - running anyway"));
                    }
                });
            }
            catch (Exception e)
            {
                settle(CouldNotVerify($"Test list retrieval failed to start ({e.Message}) - running anyway"));
            }
        }

        /// <summary>
        /// The decision, with no Unity API in sight so it can be unit tested directly.
        /// <paramref name="categoriesPerTest"/> must be in the same order as
        /// <paramref name="fullNames"/>; either may be null for the modes that ignore it.
        /// </summary>
        public static PreflightResult Evaluate(IEnumerable<string> fullNames,
                                               IEnumerable<string[]> categoriesPerTest,
                                               string requestType,
                                               string filter)
        {
            if (string.IsNullOrEmpty(filter) || requestType == "all")
            {
                return new PreflightResult
                {
                    Verdict = PreflightVerdict.Matched,
                    Reason = "unfiltered run - nothing to resolve"
                };
            }

            var names = (fullNames ?? Enumerable.Empty<string>()).ToList();

            // With nothing loaded, "the caller typed it wrong" and "no test assembly is in
            // this domain" are observationally identical. Refuse to guess.
            if (names.Count == 0)
            {
                return CouldNotVerify(
                    "The test tree is empty - cannot tell a bad filter from an unloaded assembly");
            }

            // ITestAdaptor.FullName splices " GeneratedTestCase0" INSIDE the parens of a
            // parameterised case, while Unity's own FullNameFilter matches the un-spliced
            // name. Harmless for a bare filter (the divergence is after the prefix, so the
            // StartsWith branches still fire), but an explicitly expanded Ns.C.M(1,2) would
            // be reported as a miss for something Unity would have run.
            if (filter.IndexOf('(') >= 0)
            {
                return CouldNotVerify(
                    "Parenthesised filters cannot be compared to the test tree exactly - running anyway");
            }

            if (requestType == "category")
            {
                return EvaluateCategory(names, categoriesPerTest, filter);
            }

            int matched = names.Count(name => TestResultVerifier.IsMatch(name, requestType, filter));

            if (matched > 0)
            {
                return new PreflightResult
                {
                    Verdict = PreflightVerdict.Matched,
                    TotalTests = names.Count,
                    Reason = $"{matched} of {names.Count} test(s) match {requestType} filter '{filter}'"
                };
            }

            string suggestion = TestResultVerifier.SuggestQualifiedName(names, filter);

            string message = $"Filter '{filter}' matched 0 tests. Nothing was run. " +
                             "Check the namespace/class name.";
            if (!string.IsNullOrEmpty(suggestion))
            {
                message += $" Did you mean: {suggestion}";
            }

            return new PreflightResult
            {
                Verdict = PreflightVerdict.NoMatch,
                TotalTests = names.Count,
                Suggestion = suggestion,
                ErrorMessage = message,
                Reason = message
            };
        }

        #region Helpers

        private static PreflightResult EvaluateCategory(List<string> names,
                                                        IEnumerable<string[]> categoriesPerTest,
                                                        string filter)
        {
            if (categoriesPerTest == null)
            {
                return CouldNotVerify("No category data in the test tree - running anyway");
            }

            var categories = categoriesPerTest.ToList();
            if (categories.Count != names.Count)
            {
                return CouldNotVerify("Category data does not line up with the test tree - running anyway");
            }

            // Unity builds filter.categoryNames with IsRegex = true, so the semantics are an
            // unanchored Regex.IsMatch - not string equality.
            Regex pattern;
            try
            {
                pattern = new Regex(filter, RegexOptions.CultureInvariant);
            }
            catch (ArgumentException e)
            {
                return CouldNotVerify($"Category filter is not a valid regex ({e.Message}) - running anyway");
            }

            int matched = 0;
            var known = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var testCategories in categories)
            {
                if (testCategories == null)
                {
                    continue;
                }

                bool hit = false;
                foreach (var category in testCategories)
                {
                    if (string.IsNullOrEmpty(category))
                    {
                        continue;
                    }

                    known.Add(category);

                    if (!hit && pattern.IsMatch(category))
                    {
                        hit = true;
                    }
                }

                if (hit)
                {
                    matched++;
                }
            }

            if (matched > 0)
            {
                return new PreflightResult
                {
                    Verdict = PreflightVerdict.Matched,
                    TotalTests = names.Count,
                    Reason = $"{matched} of {names.Count} test(s) match category '{filter}'"
                };
            }

            // No "did you mean" here - a suggestion over an unanchored regex is noise. The
            // categories that DO exist are the useful thing to show.
            string message = $"Category '{filter}' matched 0 of {names.Count} tests. Nothing was run.";
            if (known.Count > 0)
            {
                message += " Known categories: " + string.Join(", ", known.Take(MaxCategoriesListed).ToArray());
                if (known.Count > MaxCategoriesListed)
                {
                    message += ", ...";
                }
            }

            return new PreflightResult
            {
                Verdict = PreflightVerdict.NoMatch,
                TotalTests = names.Count,
                ErrorMessage = message,
                Reason = message
            };
        }

        /// <summary>
        /// Collects executable leaves. Uses IsSuite rather than HasChildren so this stays the
        /// exact analogue of the &lt;test-case&gt; vs &lt;test-suite&gt; split TestResultVerifier
        /// relies on. A parameterised method is a suite and is correctly walked through.
        /// </summary>
        private static void CollectLeaves(ITestAdaptor node, List<ITestAdaptor> into)
        {
            if (node == null)
            {
                return;
            }

            if (!node.IsSuite)
            {
                into.Add(node);
                return;
            }

            if (node.Children == null)
            {
                return;
            }

            foreach (var child in node.Children)
            {
                CollectLeaves(child, into);
            }
        }

        private static PreflightResult CouldNotVerify(string reason)
        {
            return new PreflightResult
            {
                Verdict = PreflightVerdict.CouldNotVerify,
                Reason = reason
            };
        }

        #endregion
    }
}
