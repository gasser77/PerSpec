#!/usr/bin/env python3
"""Shared result-provenance helpers for the PerSpec Python tools.

Two questions live here, both of which used to be answered by file timestamps alone:

1. Does this TestResults XML actually belong to the request that asked for it?
   Picking by modification time cannot tell one run's output from another's, which is
   how `quick_test.py class B` came to report class A's green results.

2. Which Unity AppData folder is *this* project's? The old scan preferred a folder
   literally named "TestFramework" in every project, so an unrelated project's stale
   results could outrank the real ones.

Mirrors the C# rules in Editor/Coordination/Core/TestResultVerifier.cs. Keep the two
in step - Python is the last line of defence when the installed package is older than
the scripts.
"""

# Prevent Python from creating .pyc files
import sys
import os
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'

import re
import time
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import List, Optional, Tuple

# Deliberately a leaf module: test_results.py imports this one, so importing it back
# would be circular. get_project_root is duplicated here for the same reason it is
# duplicated in test_coordinator.py - these scripts are run individually, not as a package.

# Results older than this are almost certainly from a previous session rather than the
# run you just asked for. Used to refuse silent imports, not to hide files you ask for.
DEFAULT_MAX_AGE_HOURS = 24.0

# Verdicts, mirroring TestResultMatch in the C# verifier.
UNREADABLE = "unreadable"
EMPTY = "empty"
NONE = "none"
PARTIAL = "partial"
EXACT = "exact"
UNVERIFIABLE = "unverifiable"

ADOPTABLE = (EXACT, UNVERIFIABLE)
ADOPTABLE_AS_LAST_RESORT = (EXACT, UNVERIFIABLE, PARTIAL)


def get_project_root() -> Path:
    """Find the Unity project root by walking up to the folder containing Assets."""
    current = Path.cwd()
    while current != current.parent:
        if (current / "Assets").exists():
            return current
        current = current.parent
    return Path.cwd()


def read_test_case_names(xml_path) -> Optional[List[str]]:
    """Fully qualified names of the tests a results XML says were executed.

    Returns None when the file cannot be read or is not an NUnit document. Only executed
    leaves are <test-case> elements - containers are <test-suite> - so this is exactly
    the set of tests that ran.
    """
    try:
        root = ET.parse(str(xml_path)).getroot()
    except Exception:
        return None

    if root.tag != 'test-run':
        return None

    names = []
    for test_case in root.findall('.//test-case'):
        full_name = test_case.get('fullname')
        if not full_name:
            # Older exports omit fullname; rebuild it the way the viewer does.
            class_name = test_case.get('classname')
            name = test_case.get('name')
            full_name = "{0}.{1}".format(class_name, name) if class_name and name else name
        names.append(full_name or '')

    return names


class XmlVerification:
    """Outcome of checking one results XML against one request."""

    def __init__(self, path, verdict, reason, matched=0, total=0,
                 matched_names=None, all_names=None, suggested_filter=None):
        self.path = path
        self.verdict = verdict
        self.reason = reason
        self.matched = matched
        self.total = total
        self.matched_names = matched_names or []
        self.all_names = all_names or []
        self.suggested_filter = suggested_filter

    @property
    def can_adopt(self) -> bool:
        return self.verdict in ADOPTABLE

    @property
    def can_adopt_as_last_resort(self) -> bool:
        return self.verdict in ADOPTABLE_AS_LAST_RESORT

    @property
    def is_definitive_miss(self) -> bool:
        """The run demonstrably executed nothing for this filter."""
        return self.verdict in (EMPTY, NONE)

    @property
    def is_filter_miss(self) -> bool:
        """Tests ran and not one belongs to this filter - so the filter names nothing.

        Distinct from EMPTY, where nothing ran at all and a broken run explains it just
        as well as a wrong name.
        """
        return self.verdict == NONE

    @property
    def miss_status(self) -> str:
        """Terminal status to record for a definitive miss."""
        return 'no_match' if self.is_filter_miss else 'inconclusive'

    def __bool__(self):
        return self.can_adopt


def name_matches(full_name: str, request_type: str, test_filter: str) -> bool:
    """Whether one NUnit full name satisfies a request filter.

    Nested classes are written Outer+Inner.Method by NUnit, so a dotted filter will not
    match them - which is honest, because Unity's own groupNames regex would not have
    selected them either.
    """
    if not full_name or not test_filter:
        return False

    if request_type == "class":
        return (full_name == test_filter
                or full_name.startswith(test_filter + ".")
                or full_name.startswith(test_filter + "("))

    if request_type == "method":
        # The paren form covers parameterised tests: Ns.Class.Method(1,2)
        return full_name == test_filter or full_name.startswith(test_filter + "(")

    return True


def _suggest_qualified_name(names: List[str], test_filter: str) -> Optional[str]:
    """Guess the fully qualified name when the caller omitted the namespace.

    Unity's anchored groupNames regex silently matches zero tests for an unqualified
    class name, so these runs executed nothing and only looked green by adopting
    somebody else's results.
    """
    needle = "." + test_filter + "."
    for name in names:
        index = name.find(needle)
        if index >= 0:
            return name[:index + 1 + len(test_filter)]
        if name.endswith("." + test_filter):
            return name
    return None


def verify_xml(xml_path, request_type: str, test_filter: Optional[str]) -> XmlVerification:
    """Check a results XML against a request. Never raises."""
    xml_path = Path(xml_path)

    if not xml_path.exists():
        return XmlVerification(xml_path, UNREADABLE, "Result XML does not exist")

    names = read_test_case_names(xml_path)
    if names is None:
        return XmlVerification(xml_path, UNREADABLE,
                               "Result XML unreadable or not an NUnit <test-run> document")

    total = len(names)

    if total == 0:
        return XmlVerification(xml_path, EMPTY,
                               "Result XML contains zero test-cases - nothing executed",
                               all_names=names)

    request_type = request_type or "all"

    if request_type == "category":
        # Categories are not written per test-case, so this can only ever be accepted
        # on its timestamp. Say so rather than claiming a match.
        return XmlVerification(
            xml_path, UNVERIFIABLE,
            "Category '{0}' cannot be verified from NUnit XML - accepted on timestamp "
            "alone ({1} test-case(s))".format(test_filter, total),
            matched=total, total=total, matched_names=names, all_names=names)

    if request_type == "all" or not test_filter:
        return XmlVerification(
            xml_path, EXACT,
            "{0} test-case(s), no filter to verify against".format(total),
            matched=total, total=total, matched_names=names, all_names=names)

    matched_names = [n for n in names if name_matches(n, request_type, test_filter)]
    matched = len(matched_names)

    if matched == 0:
        suggestion = _suggest_qualified_name(names, test_filter)
        reason = ("None of the {0} test-case(s) in {1} match {2} filter '{3}'. "
                  "Found: {4}".format(total, xml_path.name, request_type, test_filter,
                                      describe_names(names)))
        if suggestion:
            reason += ". Did you mean '{0}'?".format(suggestion)

        return XmlVerification(xml_path, NONE, reason, matched=0, total=total,
                               all_names=names, suggested_filter=suggestion)

    if matched < total:
        return XmlVerification(
            xml_path, PARTIAL,
            "Only {0} of {1} test-case(s) in {2} match '{3}' - the file is from a "
            "broader run".format(matched, total, xml_path.name, test_filter),
            matched=matched, total=total, matched_names=matched_names, all_names=names)

    return XmlVerification(
        xml_path, EXACT,
        "{0}/{1} test-case(s) match '{2}'".format(matched, total, test_filter),
        matched=matched, total=total, matched_names=matched_names, all_names=names)


def describe_names(names: List[str], limit: int = 3) -> str:
    """A short, readable sample of test names for an error message."""
    if not names:
        return "(no test cases)"
    sample = ", ".join(names[:limit])
    return sample + (", ..." if len(names) > limit else "")


def distinct_classes(names: List[str]) -> List[str]:
    """Owning classes present in a set of NUnit full names, in first-seen order.

    One place for the class-derivation rule, so the results viewer and the coordinator's
    summary can never disagree about whose run a file is.
    """
    classes = []
    for name in names:
        cls = name.rsplit(".", 1)[0] if "." in name else name
        if cls and cls not in classes:
            classes.append(cls)
    return classes


def describe_classes(names: List[str], limit: int = 4) -> str:
    """The distinct classes present in a set of NUnit full names."""
    classes = distinct_classes(names)

    if not classes:
        return "(none)"

    sample = ", ".join(classes[:limit])
    return sample + (", ..." if len(classes) > limit else "")


def file_age_seconds(path) -> float:
    """Seconds since the file was last written."""
    return max(0.0, time.time() - Path(path).stat().st_mtime)


def format_age(seconds: float) -> str:
    """Human-readable age, e.g. '6 days' or '4 minutes'."""
    if seconds < 90:
        return "{0:.0f} seconds".format(seconds)
    if seconds < 5400:
        return "{0:.0f} minutes".format(seconds / 60)
    if seconds < 172800:
        return "{0:.1f} hours".format(seconds / 3600)
    return "{0:.1f} days".format(seconds / 86400)


def read_unity_product_identity() -> Tuple[Optional[str], Optional[str]]:
    """Read companyName / productName from ProjectSettings.asset.

    Unity writes its TestResults.xml to %LocalAppData%Low/<company>/<product>, so this
    is what tells us which AppData folder is ours. Falls back to (None, None) - callers
    then fall back to the project directory name.
    """
    settings = get_project_root() / "ProjectSettings" / "ProjectSettings.asset"
    if not settings.exists():
        return None, None

    company = product = None
    try:
        with settings.open('r', encoding='utf-8', errors='replace') as handle:
            for line in handle:
                match = re.match(r"\s*companyName:\s*(.+?)\s*$", line)
                if match:
                    company = match.group(1)
                    continue
                match = re.match(r"\s*productName:\s*(.+?)\s*$", line)
                if match:
                    product = match.group(1)
                if company and product:
                    break
    except OSError:
        return None, None

    return company, product


def unity_appdata_candidates() -> List[Path]:
    """Directories under %LocalAppData%Low that may hold this project's TestResults.xml.

    Ranked most-likely first. The previous version hard-coded "TestFramework" as a
    preferred product name for every project, which ranked an unrelated project's
    week-old results above the real ones.
    """
    local_app = os.environ.get("LOCALAPPDATA")
    if not local_app:
        return []

    appdata_low = Path(local_app + "Low")
    if not appdata_low.exists():
        return []

    company, product = read_unity_product_identity()
    project_name = get_project_root().name

    candidates = []
    try:
        for company_dir in appdata_low.iterdir():
            if not company_dir.is_dir():
                continue
            for product_dir in company_dir.iterdir():
                if not product_dir.is_dir():
                    continue
                if not (product_dir / "TestResults.xml").exists():
                    continue

                # An exact company+product match from ProjectSettings is definitive.
                score = 0
                if product and product_dir.name == product:
                    score += 4
                if company and company_dir.name == company:
                    score += 2
                if product_dir.name == project_name:
                    score += 1

                candidates.append((score, product_dir))
    except OSError:
        return []

    if not candidates:
        return []

    candidates.sort(key=lambda t: t[0], reverse=True)

    # If anything actually looks like this project, do not fall through to folders that
    # do not. Every other Unity project on the machine writes a TestResults.xml too, and
    # returning them as fallbacks is how an unrelated project's results got imported.
    best_score = candidates[0][0]
    if best_score > 0:
        return [path for score, path in candidates if score == best_score]

    return [path for _, path in candidates]


def pick_best_xml(candidates_newest_first, request_type: str, test_filter: Optional[str],
                  allow_partial: bool = False) -> Tuple[Optional[Path], Optional[XmlVerification]]:
    """First candidate that can be attributed to the request.

    Candidates must be newest first. Returns (path, verification); when nothing
    qualifies the path is None and the verification describes the newest candidate so
    the caller can explain itself.
    """
    first_verification = None

    for candidate in candidates_newest_first:
        verification = verify_xml(candidate, request_type, test_filter)

        if first_verification is None:
            first_verification = verification

        acceptable = (verification.can_adopt_as_last_resort if allow_partial
                      else verification.can_adopt)
        if acceptable:
            return Path(candidate), verification

    return None, first_verification
