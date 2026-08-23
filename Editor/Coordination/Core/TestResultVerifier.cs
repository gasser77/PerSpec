using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEngine;

namespace PerSpec.Editor.Coordination
{
    /// <summary>
    /// How well a results XML matches the request it is about to be attributed to.
    /// </summary>
    public enum TestResultMatch
    {
        /// <summary>Missing, still being written, or not an NUnit test-run document.</summary>
        Unreadable,

        /// <summary>Parsed fine but contains zero test-case elements - nothing executed.</summary>
        Empty,

        /// <summary>Has test cases, none of them match this request's filter - foreign file.</summary>
        None,

        /// <summary>Some cases match - the file belongs to a broader run than this request.</summary>
        Partial,

        /// <summary>Every case in the file matches this request's filter.</summary>
        Exact,

        /// <summary>Cannot be verified from the XML (category runs) - accepted on mtime alone.</summary>
        Unverifiable
    }

    /// <summary>
    /// Outcome of checking a results XML against a test request.
    /// </summary>
    public struct TestResultVerification
    {
        public string XmlPath;
        public TestResultMatch Match;

        /// <summary>Number of test-case elements whose full name matches the request filter.</summary>
        public int MatchedCases;

        /// <summary>Total number of test-case elements in the document.</summary>
        public int TotalCases;

        // Counts recomputed from the MATCHING test-case leaves only. Never taken from the
        // root attributes - those were double-counted by TestResultXMLExporter before 1.9.0
        // and are wrong in every XML written by an older version.
        public int Passed;
        public int Failed;
        public int Skipped;
        public int Inconclusive;

        public float Duration;

        /// <summary>True when the file was produced by SingleTestXMLGenerator rather than a real run.</summary>
        public bool IsSynthetic;

        /// <summary>Human readable explanation, suitable for test_requests.error_message.</summary>
        public string Reason;

        /// <summary>Fully qualified name the caller probably meant, when the filter matched nothing.</summary>
        public string SuggestedFilter;

        /// <summary>Safe to mark the request terminal from this file while a run may still be live.</summary>
        public bool CanAdopt =>
            Match == TestResultMatch.Exact || Match == TestResultMatch.Unverifiable;

        /// <summary>
        /// Safe to adopt at the true end of a run, when there is nothing left to wait for.
        /// Partial is allowed here so a recoverable run is reported with its matched subset
        /// rather than being thrown away as failed.
        /// </summary>
        public bool CanAdoptAsLastResort =>
            CanAdopt || Match == TestResultMatch.Partial;

        /// <summary>The run demonstrably executed nothing for this filter.</summary>
        public bool IsDefinitiveMiss =>
            Match == TestResultMatch.Empty || Match == TestResultMatch.None;

        /// <summary>
        /// Tests ran, and not one of them belongs to this filter - so the filter names
        /// something that does not exist. That is a caller mistake ('no_match'), unlike
        /// <see cref="TestResultMatch.Empty"/>, where nothing ran at all and a broken run
        /// is just as likely an explanation ('inconclusive').
        /// </summary>
        public bool IsFilterMiss => Match == TestResultMatch.None;

        /// <summary>Terminal status to record for a definitive miss.</summary>
        public string MissStatus => IsFilterMiss ? "no_match" : "inconclusive";
    }

    /// <summary>
    /// Decides whether a TestResults XML actually belongs to a given test request.
    ///
    /// Every path that marks a request terminal used to pick its XML by file modification
    /// time alone, which cannot tell one run's output from another's. That is how a request
    /// for class B ended up reporting class A's green results. This is the single content
    /// based gate all of those paths now go through.
    ///
    /// The check is exact and cheap because of two properties of Unity's NUnit output:
    /// only executed leaves are &lt;test-case&gt; elements (containers are &lt;test-suite&gt;), and
    /// test-case/@fullname is fully namespace qualified.
    /// </summary>
    public static class TestResultVerifier
    {
        private const int MaxSampleNames = 3;

        /// <summary>
        /// Checks an XML against a request. Never throws - an unreadable file simply
        /// reports <see cref="TestResultMatch.Unreadable"/> so callers can keep waiting.
        /// </summary>
        public static TestResultVerification Verify(string xmlPath, TestRequest request)
        {
            var verification = new TestResultVerification
            {
                XmlPath = xmlPath,
                Match = TestResultMatch.Unreadable
            };

            if (string.IsNullOrEmpty(xmlPath) || !File.Exists(xmlPath))
            {
                verification.Reason = "Result XML does not exist";
                return verification;
            }

            XDocument doc;
            try
            {
                doc = XDocument.Load(xmlPath);
            }
            catch (Exception e)
            {
                // Almost always a half-written file caught mid-flush. The caller polls again.
                verification.Reason = $"Result XML unreadable ({e.Message})";
                return verification;
            }

            var root = doc.Root;
            if (root == null || root.Name != "test-run")
            {
                verification.Reason = "Not an NUnit <test-run> document";
                return verification;
            }

            var cases = root.Descendants("test-case").ToList();
            verification.TotalCases = cases.Count;
            verification.Duration = ParseFloat(root.Attribute("duration")?.Value);
            verification.IsSynthetic = root.Descendants("property").Any(p =>
                string.Equals((string)p.Attribute("name"), "generated", StringComparison.Ordinal) &&
                string.Equals((string)p.Attribute("value"), "true", StringComparison.Ordinal));

            if (verification.TotalCases == 0)
            {
                verification.Match = TestResultMatch.Empty;
                verification.Reason = "Result XML contains zero test-cases - nothing executed";
                return verification;
            }

            string requestType = request?.RequestType ?? "all";
            string filter = request?.TestFilter;

            // Categories are not emitted per test-case in this XML, so a category run can
            // only ever be accepted on its timestamp. Say so rather than pretending.
            if (requestType == "category")
            {
                CountInto(ref verification, cases);
                verification.MatchedCases = verification.TotalCases;
                verification.Match = TestResultMatch.Unverifiable;
                verification.Reason =
                    $"Category '{filter}' cannot be verified from NUnit XML - accepted on timestamp alone " +
                    $"({verification.TotalCases} test-case(s))";
                return verification;
            }

            if (request == null || requestType == "all" || string.IsNullOrEmpty(filter))
            {
                CountInto(ref verification, cases);
                verification.MatchedCases = verification.TotalCases;
                verification.Match = TestResultMatch.Exact;
                verification.Reason = $"{verification.TotalCases} test-case(s), no filter to verify against";
                return verification;
            }

            var matched = cases
                .Where(c => IsMatch(GetFullName(c), requestType, filter))
                .ToList();

            verification.MatchedCases = matched.Count;
            CountInto(ref verification, matched);

            if (matched.Count == 0)
            {
                verification.Match = TestResultMatch.None;
                verification.SuggestedFilter = SuggestQualifiedName(cases, filter);

                string suggestion = verification.SuggestedFilter != null
                    ? $" Did you mean '{verification.SuggestedFilter}'?"
                    : string.Empty;

                verification.Reason =
                    $"None of the {verification.TotalCases} test-case(s) in " +
                    $"{Path.GetFileName(xmlPath)} match {requestType} filter '{filter}'. " +
                    $"Found: {DescribeSample(cases)}.{suggestion}";
            }
            else if (matched.Count < verification.TotalCases)
            {
                verification.Match = TestResultMatch.Partial;
                verification.Reason =
                    $"Only {matched.Count} of {verification.TotalCases} test-case(s) in " +
                    $"{Path.GetFileName(xmlPath)} match '{filter}' - the file is from a broader run; " +
                    "counts reported are the matching subset";
            }
            else
            {
                verification.Match = TestResultMatch.Exact;
                verification.Reason = $"{matched.Count}/{verification.TotalCases} test-case(s) match '{filter}'";
            }

            return verification;
        }

        /// <summary>
        /// Picks the first candidate that can be attributed to the request. Candidates must be
        /// supplied newest first; the first acceptable one wins so a stale decoy cannot mask a
        /// good file sitting behind it.
        /// </summary>
        /// <param name="allowPartial">
        /// True only at the true end of a run, where a broader run's file is better than nothing.
        /// </param>
        public static string PickBest(IEnumerable<string> candidatesNewestFirst,
                                      TestRequest request,
                                      bool allowPartial,
                                      out TestResultVerification best)
        {
            best = default;
            best.Match = TestResultMatch.Unreadable;
            best.Reason = "No candidate result files were available";

            if (candidatesNewestFirst == null)
            {
                return null;
            }

            bool haveAnyCandidate = false;

            foreach (var candidate in candidatesNewestFirst)
            {
                var verification = Verify(candidate, request);

                if (!haveAnyCandidate)
                {
                    // Remember the newest file even if it loses, so the caller can explain itself.
                    best = verification;
                    haveAnyCandidate = true;
                }

                bool acceptable = allowPartial ? verification.CanAdoptAsLastResort : verification.CanAdopt;
                if (acceptable)
                {
                    best = verification;
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// Matches an NUnit full name against a request filter.
        ///
        /// Note on nested classes: NUnit writes them as Outer+Inner.Method, so a filter of
        /// Namespace.Outer.Inner will not match. That is honest - Unity's own groupNames regex
        /// would not have selected those tests either.
        /// </summary>
        public static bool IsMatch(string fullName, string requestType, string filter)
        {
            if (string.IsNullOrEmpty(fullName) || string.IsNullOrEmpty(filter))
            {
                return false;
            }

            switch (requestType)
            {
                case "class":
                    return fullName.Equals(filter, StringComparison.Ordinal)
                        || fullName.StartsWith(filter + ".", StringComparison.Ordinal)
                        || fullName.StartsWith(filter + "(", StringComparison.Ordinal);

                case "method":
                    // The paren form covers parameterised tests: Ns.Class.Method(1,2)
                    return fullName.Equals(filter, StringComparison.Ordinal)
                        || fullName.StartsWith(filter + "(", StringComparison.Ordinal);

                default:
                    return true;
            }
        }

        /// <summary>
        /// Best guess at the fully qualified name the caller meant, given the names that
        /// actually exist. Returns null when nothing is close enough to be worth printing.
        ///
        /// Two passes, exact before fuzzy, first hit wins. Feed names in a stable order
        /// (tree or document order) so the answer is deterministic.
        /// </summary>
        public static string SuggestQualifiedName(IEnumerable<string> fullNames, string filter)
        {
            if (fullNames == null || string.IsNullOrEmpty(filter))
            {
                return null;
            }

            var candidates = fullNames.Where(n => !string.IsNullOrEmpty(n)).ToList();

            // Pass 1 - the filter is a suffix of a real full name: the "forgot the
            // namespace" mistake, which Unity's anchored groupNames regex silently
            // matches zero tests for.
            foreach (var fullName in candidates)
            {
                int index = fullName.IndexOf("." + filter + ".", StringComparison.Ordinal);
                if (index >= 0)
                {
                    return fullName.Substring(0, index + 1 + filter.Length);
                }

                if (fullName.EndsWith("." + filter, StringComparison.Ordinal))
                {
                    return fullName;
                }
            }

            // Pass 2 - same last segment, different namespace. Pass 1 cannot see this
            // because a MIDDLE segment is wrong, so the filter is a suffix of nothing.
            // This is the shape of the reported bug: TestProj.Modules.Tests.XTests
            // asked for, TestProj.Core.Tests.XTests real.
            // A nested-class filter may already use the '+' spelling, so split on both.
            string leaf = LastSegment(filter);
            if (string.IsNullOrEmpty(leaf))
            {
                return null;
            }

            foreach (var fullName in candidates)
            {
                int index = IndexOfSegment(fullName, leaf);
                if (index >= 0)
                {
                    return fullName.Substring(0, index + leaf.Length);
                }
            }

            return null;
        }

        #region Helpers

        /// <summary>The part of a name after its last '.' or '+' separator.</summary>
        private static string LastSegment(string dottedName)
        {
            int lastSeparator = dottedName.LastIndexOfAny(new[] { '.', '+' });
            return lastSeparator >= 0 ? dottedName.Substring(lastSeparator + 1) : dottedName;
        }

        /// <summary>
        /// Finds <paramref name="segment"/> inside <paramref name="fullName"/> only where it
        /// occupies a whole segment, so "Tests" never matches inside "MyTests".
        /// Case-insensitive, so a casing typo is caught too.
        ///
        /// '+' is a boundary as well as '.', because NUnit writes a nested class as
        /// Ns.Outer+Inner - and a caller who writes Ns.Outer.Inner is exactly the person who
        /// needs to be told the real spelling. A trailing '(' is a boundary too, for
        /// parameterised cases written Ns.Class.Method(1,2).
        /// </summary>
        private static int IndexOfSegment(string fullName, string segment)
        {
            int from = 0;
            while (from <= fullName.Length - segment.Length)
            {
                int index = fullName.IndexOf(segment, from, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    return -1;
                }

                bool startIsBoundary = index == 0
                                       || fullName[index - 1] == '.'
                                       || fullName[index - 1] == '+';

                int after = index + segment.Length;
                bool endIsBoundary = after == fullName.Length
                                     || fullName[after] == '.'
                                     || fullName[after] == '+'
                                     || fullName[after] == '(';

                if (startIsBoundary && endIsBoundary)
                {
                    return index;
                }

                from = index + 1;
            }

            return -1;
        }

        private static string GetFullName(XElement testCase)
        {
            string fullName = (string)testCase.Attribute("fullname");
            if (!string.IsNullOrEmpty(fullName))
            {
                return fullName;
            }

            // Older exports omit fullname; rebuild it the way the viewer does.
            string className = (string)testCase.Attribute("classname");
            string name = (string)testCase.Attribute("name");

            if (!string.IsNullOrEmpty(className) && !string.IsNullOrEmpty(name))
            {
                return className + "." + name;
            }

            return name;
        }

        private static void CountInto(ref TestResultVerification verification, List<XElement> cases)
        {
            foreach (var testCase in cases)
            {
                switch ((string)testCase.Attribute("result"))
                {
                    case "Passed":
                        verification.Passed++;
                        break;
                    case "Failed":
                        verification.Failed++;
                        break;
                    case "Skipped":
                        verification.Skipped++;
                        break;
                    case "Inconclusive":
                        verification.Inconclusive++;
                        break;
                }
            }
        }

        private static string SuggestQualifiedName(List<XElement> cases, string filter)
        {
            return SuggestQualifiedName(cases.Select(GetFullName), filter);
        }

        private static string DescribeSample(List<XElement> cases)
        {
            var names = cases
                .Select(GetFullName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Take(MaxSampleNames)
                .ToList();

            if (names.Count == 0)
            {
                return "(unnamed test cases)";
            }

            string sample = string.Join(", ", names);
            return cases.Count > names.Count ? sample + ", ..." : sample;
        }

        private static float ParseFloat(string value)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? parsed
                : 0f;
        }

        #endregion

        /// <summary>
        /// Convenience logger so every call site reports rejections the same way.
        /// </summary>
        public static void LogRejection(string context, TestResultVerification verification)
        {
            Debug.LogWarning($"[{context}] Not adopting " +
                             $"{(string.IsNullOrEmpty(verification.XmlPath) ? "(no file)" : Path.GetFileName(verification.XmlPath))}: " +
                             verification.Reason);
        }
    }
}
