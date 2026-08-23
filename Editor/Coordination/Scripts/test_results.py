#!/usr/bin/env python3
"""
Test Results Viewer - View and analyze Unity test results from XML files
"""

# Prevent Python from creating .pyc files
import sys
import os
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'

import argparse
import re
import xml.etree.ElementTree as ET
from pathlib import Path
from datetime import datetime, timedelta
from typing import Optional, List, Dict
import json

def get_project_root():
    """Find Unity project root by looking for Assets folder"""
    current = Path.cwd()
    while current != current.parent:
        if (current / "Assets").exists():
            return current
        current = current.parent
    return Path.cwd()

# These scripts are run individually rather than as a package, so a sibling import only
# resolves when the script's own directory is on sys.path. It normally is (sys.path[0]),
# but not under runpy or `python -m` - and this module is now also the entry point for
# database-backed verification, where a silent ImportError would read as "no results".
_SCRIPT_DIR = str(Path(__file__).resolve().parent)
if _SCRIPT_DIR not in sys.path:
    sys.path.insert(0, _SCRIPT_DIR)

import results_verification as rv


# Exit codes. Only the assertive flags (--for-request / --newer-than) ever return a
# non-zero one; plain `latest` has always exited 0 and still does. The numbering matches
# quick_test.py so a caller can read both tools against one table.
EXIT_OK = 0
EXIT_FAILED = 1           # the run failed or was cancelled, or its results are red
EXIT_UNATTRIBUTABLE = 3   # results exist but are not this request's
EXIT_NO_DB = 4            # the coordination database could not be consulted
EXIT_UNFINISHED = 5       # the request never finished; NO results were produced


def get_test_results_path():
    """Get the primary TestResults directory path (PerSpec/TestResults/)."""
    project_root = get_project_root()
    return project_root / "PerSpec" / "TestResults"


def _load_coordinator():
    """Open the coordination database. Returns (module, coordinator, error_message).

    Imported lazily and never at module scope: reading a results XML needs no database at
    all, and test_results.py has to keep working in a project where PerSpec was never
    initialised. A failure here means "verdict unknown", not "no results".
    """
    try:
        import test_coordinator as tc
    except Exception as e:                  # ImportError, or a syntax error in a sibling
        return None, None, f"test_coordinator.py could not be imported: {e}"

    try:
        return tc, tc.TestCoordinator(), None
    except FileNotFoundError as e:          # _ensure_database_exists
        return tc, None, (f"{e} PerSpec has not been initialised in this project, so there "
                          f"is nothing to verify a request against.")
    except Exception as e:
        # _get_connection runs PRAGMA journal_mode=WAL, which takes a brief write lock and
        # WILL raise while Unity is mid-write. That must not fall through to printing a
        # summary from a file we have not attributed.
        return tc, None, f"Could not open the coordination database: {e}"


# Slack for clock skew between Unity's row timestamps and the filesystem. Deliberately
# generous: a run that produced nothing leaves nothing to parse, so a wide net still
# cannot invent a match. The window only orders candidates - verify_xml decides.
_CLOCK_SKEW_SECONDS = 5.0
_MAX_CANDIDATES = 25


def request_dispatch_cutoff(tc, row: Dict, slack_seconds: float = _CLOCK_SKEW_SECONDS):
    """Earliest mtime an XML may carry and still belong to this request.

    Mirrors the C# ladder (PlayModeTestCompletionChecker: StartedAt ?? CreatedAt, minus a
    clock-skew buffer) and TestCoordinator._await_results_xml. Returns None when the row
    carries no usable timestamp - in which case NOTHING is attributable, which is the
    honest answer rather than "accept the newest file on disk".
    """
    for key in ('started_at', 'created_at'):
        value = row.get(key)
        if value in (None, '', 0):
            continue

        parsed = tc.parse_db_timestamp(value)
        # sqlite-net writes an unset DateTime as ticks 0, which parses to 0001-01-01.
        # Treating that as a cutoff would mean "accept anything ever written".
        if parsed is None or parsed.year < 2000:
            continue

        return parsed - timedelta(seconds=slack_seconds)

    return None


def candidates_since(cutoff, max_age_hours=None) -> List[Path]:
    """Result files that could plausibly come from a run dispatched at `cutoff`.

    Routed through list_result_files so the Unity AppData import and its staleness guard
    still apply, and so --max-age / --allow-stale keep composing. Newest first.
    """
    files = list_result_files(0, max_age_hours)

    if cutoff is not None:
        files = [f for f in files
                 if datetime.fromtimestamp(f.stat().st_mtime) >= cutoff]

    return files[:_MAX_CANDIDATES]


_RELATIVE_AGE = re.compile(r'^(\d+(?:\.\d+)?)\s*([smhd])$', re.IGNORECASE)
_UNIT_SECONDS = {'s': 1, 'm': 60, 'h': 3600, 'd': 86400}


def parse_newer_than(text: str) -> datetime:
    """Parse a --newer-than value into a naive local datetime.

    Accepts, in order: a relative age ('90s', '15m', '2h', '1d'), epoch seconds, or
    ISO 8601 ('2026-08-21T10:15:00', trailing 'Z' tolerated).
    """
    text = (text or '').strip()

    match = _RELATIVE_AGE.match(text)
    if match:
        seconds = float(match.group(1)) * _UNIT_SECONDS[match.group(2).lower()]
        return datetime.now() - timedelta(seconds=seconds)

    try:
        return datetime.fromtimestamp(float(text))
    except ValueError:
        pass

    try:
        parsed = datetime.fromisoformat(text.replace('Z', ''))
    except ValueError:
        raise ValueError(f"Cannot read --newer-than '{text}'. Use a relative age "
                         f"(15m, 2h, 1d), epoch seconds, or ISO 8601 "
                         f"(2026-08-21T10:15:00).")

    # Everything here compares against naive datetime.fromtimestamp(mtime); an aware
    # datetime would raise TypeError on the comparison.
    return parsed.astimezone().replace(tzinfo=None) if parsed.tzinfo else parsed


def _import_appdata_xml_into_perspec(source_xml: Path, dest_dir: Path) -> Optional[Path]:
    """Copy Unity's AppData TestResults.xml into PerSpec/TestResults with a timestamp."""
    try:
        import shutil
        mtime = datetime.fromtimestamp(source_xml.stat().st_mtime)
        dest = dest_dir / f"TestResults_{mtime.strftime('%Y%m%d_%H%M%S')}.xml"
        dest_dir.mkdir(parents=True, exist_ok=True)
        shutil.copy2(str(source_xml), str(dest))
        print(f"[INFO] Imported {source_xml} -> {dest}")
        return dest
    except OSError as e:
        print(f"[WARN] Failed to import {source_xml}: {e}")
        return None


def parse_xml_file(xml_path: Path) -> Dict:
    """Parse a test results XML file"""
    try:
        tree = ET.parse(xml_path)
        root = tree.getroot()
        
        # Extract summary from root attributes
        summary = {
            'file': xml_path.name,
            'timestamp': datetime.fromtimestamp(xml_path.stat().st_mtime).strftime('%Y-%m-%d %H:%M:%S'),
            'total': int(root.get('total', 0)),
            'passed': int(root.get('passed', 0)),
            'failed': int(root.get('failed', 0)),
            'inconclusive': int(root.get('inconclusive', 0)),
            'skipped': int(root.get('skipped', 0)),
            'duration': float(root.get('duration', 0))
        }
        
        # Extract individual test results
        tests = []
        for test_case in root.findall('.//test-case'):
            test_info = {
                'name': test_case.get('fullname', test_case.get('name', 'Unknown')),
                'result': test_case.get('result', 'Unknown'),
                'duration': float(test_case.get('duration', 0)),
                'classname': test_case.get('classname', ''),
                'methodname': test_case.get('methodname', '')
            }
            
            # Get failure message if present
            failure = test_case.find('failure')
            if failure is not None:
                test_info['message'] = failure.find('message').text if failure.find('message') is not None else ''
                test_info['stack_trace'] = failure.find('stack-trace').text if failure.find('stack-trace') is not None else ''
            
            tests.append(test_info)
        
        summary['tests'] = tests

        # Provenance, derived from the loop above rather than a second parse of the same
        # document. The test-case names are the only ground truth about whose run this is;
        # the root counts and the file mtime cannot tell one run's output from another's.
        summary['classes'] = rv.distinct_classes([t['name'] for t in tests])
        summary['test_count'] = len(tests)
        summary['path'] = str(xml_path)
        # 'timestamp' reads like it came from the XML. It does not.
        summary['timestamp_source'] = 'file-mtime'
        return summary
        
    except Exception as e:
        return {
            'file': xml_path.name,
            'path': str(xml_path),
            'error': str(e),
            'timestamp': datetime.fromtimestamp(xml_path.stat().st_mtime).strftime('%Y-%m-%d %H:%M:%S')
        }

def list_result_files(limit: int = 10, max_age_hours=None) -> List[Path]:
    """List available test result files, newest first.

    Looks in PerSpec/TestResults/ first. If Unity's own AppData copy is newer than
    anything we have, it is imported so subsequent reads are consistent - but only if it
    is recent enough to plausibly be the current run.

    The age guard matters because the coordinator trims PerSpec/TestResults, so "newer
    than anything local" can be true of an arbitrarily old file. That is how a six-day-old
    green run came to be printed as the current result.
    """
    if max_age_hours is None:
        max_age_hours = rv.DEFAULT_MAX_AGE_HOURS

    results_path = get_test_results_path()
    results_path.mkdir(parents=True, exist_ok=True)

    perspec_xmls = list(results_path.glob("*.xml"))
    perspec_latest_mtime = max(
        (x.stat().st_mtime for x in perspec_xmls),
        default=0.0,
    )

    max_age_seconds = None if max_age_hours <= 0 else max_age_hours * 3600.0

    for appdata_dir in rv.unity_appdata_candidates():
        source = appdata_dir / "TestResults.xml"
        if not source.exists() or source.stat().st_mtime <= perspec_latest_mtime:
            continue

        age = rv.file_age_seconds(source)
        if max_age_seconds is not None and age > max_age_seconds:
            print(f"[WARN] Not importing {source}: it is {rv.format_age(age)} old, "
                  f"far older than the current run. Use --allow-stale to import it anyway.")
            break

        imported = _import_appdata_xml_into_perspec(source, results_path)
        if imported is not None:
            perspec_xmls.append(imported)
        break  # only consider the highest-priority candidate

    xml_files = sorted(
        perspec_xmls,
        key=lambda x: x.stat().st_mtime,
        reverse=True,
    )

    return xml_files[:limit] if limit > 0 else xml_files


def warn_if_stale(xml_path: Path, warn_after_seconds: float = 3600.0):
    """Print the result's age, loudly when it is old enough to be from a previous session."""
    try:
        age = rv.file_age_seconds(xml_path)
    except OSError:
        return

    if age > warn_after_seconds:
        print(f"\n[WARN] These results are {rv.format_age(age)} old - they are almost "
              f"certainly NOT from a run you just started.")
    else:
        print(f"\nAge: {rv.format_age(age)}")


def _format_names_summary(names: List[str], class_limit: int = 4) -> str:
    """e.g. '7 test(s) in PerSpec.Tests.FooTests' - which run a results file is from."""
    if not names:
        return "0 test(s) - NOTHING was executed"

    classes = rv.distinct_classes(names)
    head = ", ".join(classes[:class_limit])
    more = f", +{len(classes) - class_limit} more" if len(classes) > class_limit else ""
    return f"{len(names)} test(s) in {head}{more}"


def format_contents(data: Dict, class_limit: int = 4) -> str:
    """Provenance line for an already-parsed summary. Costs no extra file read."""
    return _format_names_summary([t.get('name', '') for t in data.get('tests', [])],
                                 class_limit)


def describe_file_contents(xml_path: Path, class_limit: int = 2) -> str:
    """Provenance line for a file we have not fully parsed - used by the `list` view.

    read_test_case_names builds no per-test dictionaries, so this is the cheap parse.
    """
    names = rv.read_test_case_names(xml_path)
    if names is None:
        return "unreadable / not an NUnit <test-run> document"
    return _format_names_summary(names, class_limit)


def display_summary(data: Dict, verbose: bool = False):
    """Display test results summary"""
    if 'error' in data:
        print(f"\n[ERROR] Failed to parse {data['file']}: {data['error']}")
        return
    
    # Header
    print(f"\n{'='*60}")
    print(f"Test Results: {data['file']}")
    print(f"Timestamp: {data['timestamp']} (file mtime, not from the XML)")
    print(f"Contains: {format_contents(data)}")
    print(f"{'='*60}")
    
    # Summary stats
    print(f"\nSummary:")
    print(f"  Total:        {data['total']}")
    print(f"  Passed:       {data['passed']}")
    print(f"  Failed:       {data['failed']}")
    print(f"  Inconclusive: {data['inconclusive']}")
    print(f"  Skipped:      {data['skipped']}")
    print(f"  Duration:     {data['duration']:.2f} seconds")
    
    # Show failed tests
    if data.get('tests'):
        failed_tests = [t for t in data['tests'] if t['result'] in ['Failed', 'Error']]
        if failed_tests:
            print(f"\nFailed Tests ({len(failed_tests)}):")
            for test in failed_tests:
                print(f"  [FAILED] {test['name']}")
                if verbose and test.get('message'):
                    print(f"     Message: {test['message']}")
                if verbose and test.get('stack_trace'):
                    print(f"     Stack: {test['stack_trace'][:200]}...")
        
        # Show inconclusive tests
        inconclusive_tests = [t for t in data['tests'] if t['result'] == 'Inconclusive']
        if inconclusive_tests:
            print(f"\nInconclusive Tests ({len(inconclusive_tests)}):")
            for test in inconclusive_tests:
                print(f"  [INCONCLUSIVE] {test['name']}")
        
        # Show passed tests if verbose
        if verbose:
            passed_tests = [t for t in data['tests'] if t['result'] == 'Passed']
            if passed_tests:
                print(f"\nPassed Tests ({len(passed_tests)}):")
                for test in passed_tests:
                    print(f"  [PASSED] {test['name']} ({test['duration']:.3f}s)")


def _verdict(outcome, exit_code, **extra) -> Dict:
    """Uniform verdict shape, so the human printer and --json share one truth."""
    verdict = {
        'outcome': outcome,
        'exit_code': exit_code,
        'request': None,
        'cutoff': None,
        'xml': None,
        'data': None,
        'verification': None,
        'considered': [],
        'messages': [],
    }
    verdict.update(extra)
    return verdict


def resolve_for_request(request_id: Optional[int], newer_than: Optional[datetime],
                        max_age_hours=None, allow_partial: bool = False) -> Dict:
    """Find the results XML that provably belongs to `request_id` and/or `newer_than`.

    Never writes to the database. print_summary corrects a lying row via
    _mark_inconclusive; a viewer must not mutate rows behind a read command.
    """
    # --- window only: no request to check against, just "has anything run since?" ---
    if request_id is None:
        candidates = candidates_since(newer_than, max_age_hours)
        if not candidates:
            newest = list_result_files(1, max_age_hours)
            detail = ""
            if newest:
                age = rv.format_age(rv.file_age_seconds(newest[0]))
                detail = (f" The newest file on disk is {newest[0].name} ({age} old) - "
                          f"it is NOT from the window you asked about.")
            return _verdict('unattributable', EXIT_UNATTRIBUTABLE, cutoff=newer_than,
                            messages=[f"No results have been written since "
                                      f"{newer_than:%Y-%m-%d %H:%M:%S}.{detail}"])

        chosen = candidates[0]
        return _verdict('ok', EXIT_OK, cutoff=newer_than, xml=chosen,
                        data=parse_xml_file(chosen))

    # --- a real request: the database is the only thing that knows what it asked for ---
    tc, coordinator, error = _load_coordinator()
    if coordinator is None:
        return _verdict('no-db', EXIT_NO_DB,
                        messages=[error, "The verdict is UNKNOWN - no results are shown."])

    try:
        row = coordinator.get_request_status(request_id)
    except Exception as e:
        return _verdict('no-db', EXIT_NO_DB,
                        messages=[f"Could not read request {request_id}: {e}",
                                  "The verdict is UNKNOWN - no results are shown."])

    if row is None:
        return _verdict('no-request', EXIT_FAILED,
                        messages=[f"Request {request_id} does not exist in the "
                                  f"coordination database.",
                                  "Run 'quick_test.py stuck' to see in-flight requests."])

    cutoff = request_dispatch_cutoff(tc, row)
    if newer_than is not None:
        cutoff = newer_than if cutoff is None else max(cutoff, newer_than)

    if cutoff is None:
        return _verdict('no-window', EXIT_UNATTRIBUTABLE, request=row,
                        messages=[f"Request {request_id} has no usable started_at or "
                                  f"created_at, so no file on disk can be attributed to "
                                  f"it.",
                                  "Pass --newer-than to supply the window yourself."])

    candidates = candidates_since(cutoff, max_age_hours)
    status = row.get('status')

    # --- the run never finished: this is the timeout case, and the point of the flag ---
    if status not in tc.TERMINAL_STATUSES:
        if candidates:
            message = (f"An XML was written at "
                       f"{datetime.fromtimestamp(candidates[0].stat().st_mtime):%H:%M:%S} "
                       f"but request {request_id} is still '{status}' - the coordinator "
                       f"never recorded this run as finished, so those results are not "
                       f"confirmed to be its output.")
        else:
            message = (f"Nothing has been written to PerSpec/TestResults since this "
                       f"request was dispatched at {cutoff:%Y-%m-%d %H:%M:%S}.")

        return _verdict('unfinished', EXIT_UNFINISHED, request=row, cutoff=cutoff,
                        considered=[(c, 'not attributable - the run never finished')
                                    for c in candidates[:5]],
                        messages=[f"Request {request_id} never finished. It is still "
                                  f"'{status}'.", message])

    # --- terminal: does anything on disk actually contain the tests we asked for? ---
    chosen, verification = rv.pick_best_xml(candidates, row.get('request_type'),
                                            row.get('test_filter'),
                                            allow_partial=allow_partial)

    if chosen is None:
        if not candidates:
            reason = (f"The request reached status '{status}' but no results file was "
                      f"written after {cutoff:%Y-%m-%d %H:%M:%S}.")
        else:
            reason = verification.reason if verification is not None else \
                "No candidate could be attributed to this request."

        messages = [f"No results can be attributed to request {request_id}.", reason]

        if verification is not None and verification.all_names:
            messages.append(f"The newest candidate contains: "
                            f"{rv.describe_classes(verification.all_names)}")
        if verification is not None and verification.suggested_filter:
            messages.append(f"Did you mean '{verification.suggested_filter}'? Unity's "
                            f"anchored filter regex matches zero tests for an "
                            f"unqualified class name.")

        return _verdict('unattributable',
                        EXIT_UNATTRIBUTABLE if status == 'completed' else EXIT_FAILED,
                        request=row, cutoff=cutoff, verification=verification,
                        considered=[(c, 'rejected') for c in candidates[:5]],
                        messages=messages)

    # --- attributed. Score the MATCHED subset, never the whole file's totals ---
    data = parse_xml_file(chosen)
    request_type = row.get('request_type')
    test_filter = row.get('test_filter')

    tests = data.get('tests', [])
    if test_filter and request_type in ('class', 'method'):
        matched = [t for t in tests
                   if rv.name_matches(t.get('name', ''), request_type, test_filter)]
    else:
        matched = tests

    red = [t for t in matched if t.get('result') in ('Failed', 'Error')]
    exit_code = EXIT_FAILED if (red or status in ('failed', 'timeout', 'cancelled')) \
        else EXIT_OK

    return _verdict('ok', exit_code, request=row, cutoff=cutoff, xml=chosen, data=data,
                    verification=verification,
                    considered=[(c, 'considered') for c in candidates[:5]])


def _verdict_to_json(verdict: Dict) -> Dict:
    """JSON shape with one load-bearing rule: `results` is null unless outcome == 'ok'.

    A scripted consumer must be physically unable to read another run's counts.
    """
    row = verdict.get('request') or {}
    verification = verdict.get('verification')

    payload = {
        'outcome': verdict['outcome'],
        'exit_code': verdict['exit_code'],
        'request': ({
            'id': row.get('id'),
            'status': row.get('status'),
            'request_type': row.get('request_type'),
            'test_filter': row.get('test_filter'),
            'test_platform': row.get('test_platform'),
            'terminal': row.get('status') in _terminal_names(),
        } if row else None),
        'cutoff': verdict['cutoff'].isoformat() if verdict['cutoff'] else None,
        'xml': str(verdict['xml']) if verdict['xml'] else None,
        'results': verdict['data'] if verdict['outcome'] == 'ok' else None,
        'verification': ({
            'verdict': verification.verdict,
            'reason': verification.reason,
            'matched': verification.matched,
            'total': verification.total,
            'classes': rv.distinct_classes(verification.all_names),
            'suggested_filter': verification.suggested_filter,
        } if verification is not None else None),
        'considered': [{'file': path.name,
                        'mtime': datetime.fromtimestamp(path.stat().st_mtime).isoformat(),
                        'note': note}
                       for path, note in verdict['considered']],
        'messages': verdict['messages'],
    }
    return payload


def _terminal_names():
    """Terminal status names, without forcing a database connection to ask."""
    try:
        import test_coordinator as tc
        return tc.TERMINAL_STATUSES
    except Exception:
        return frozenset({'completed', 'failed', 'cancelled', 'timeout', 'inconclusive'})


def print_request_verdict(verdict: Dict, verbose: bool = False) -> None:
    """Human output for an assertive `latest`.

    Prints a results summary ONLY when the file was attributed. Printing somebody else's
    counts under a caveat is exactly the failure this flag exists to prevent.
    """
    row = verdict.get('request') or {}

    if row:
        print(f"\nRequest #{row.get('id')}: {row.get('request_type')} "
              f"'{row.get('test_filter')}' on {row.get('test_platform')} "
              f"- status '{row.get('status')}'")
    if verdict['cutoff'] is not None:
        print(f"Only results written after "
              f"{verdict['cutoff']:%Y-%m-%d %H:%M:%S} can belong to it.")

    if verdict['outcome'] == 'ok':
        display_summary(verdict['data'], verbose)
        warn_if_stale(verdict['xml'])
        if verdict['verification'] is not None:
            print(f"Attributed: {verdict['verification'].reason}")
        if verdict['exit_code'] == EXIT_FAILED:
            print("\n[FAIL] The requested tests ran and some of them failed.")
        if verbose and verdict['considered']:
            _print_ledger(verdict['considered'])
        return

    print("\n" + "!" * 60)
    for line in verdict['messages']:
        if line:
            print(f"[ERROR] {line}")
    print("[ERROR] No results are shown, because none can be attributed to this request.")
    print("[HINT] 'test_results.py latest' without --for-request WILL show an older run.")
    if verdict['outcome'] == 'unfinished':
        print(f"[HINT] python quick_test.py stuck --repair   # clears request "
              f"{row.get('id')}, still '{row.get('status')}'")
    print("!" * 60)

    if verdict['considered']:
        _print_ledger(verdict['considered'])


def _print_ledger(considered) -> None:
    """The candidate files and why each was or was not used."""
    print("\nFiles considered:")
    for path, note in considered:
        stamp = datetime.fromtimestamp(path.stat().st_mtime)
        print(f"  {path.name:<44} {stamp:%Y-%m-%d %H:%M:%S}  {note}")


def run_assertive_latest(args, max_age) -> int:
    """`latest` with --for-request / --newer-than. Returns a process exit code."""
    try:
        newer_than = parse_newer_than(args.newer_than) if args.newer_than else None
    except ValueError as e:
        print(f"[ERROR] {e}")
        return EXIT_FAILED

    verdict = resolve_for_request(args.for_request, newer_than, max_age,
                                  args.allow_partial)

    if args.json:
        print(json.dumps(_verdict_to_json(verdict), indent=2, default=str))
    else:
        print_request_verdict(verdict, args.verbose)

    return verdict['exit_code']


def main():
    # Ensure UTF-8 encoding for emoji/Unicode characters
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')

    parser = argparse.ArgumentParser(description='View Unity test results')
    
    # Subcommands
    subparsers = parser.add_subparsers(dest='command', help='Commands')
    
    # Latest command
    latest_parser = subparsers.add_parser('latest', help='Show latest test results')
    latest_parser.add_argument('-v', '--verbose', action='store_true', help='Show detailed output')
    latest_parser.add_argument('--json', action='store_true', help='Output as JSON')
    latest_parser.add_argument('--max-age', type=float, default=None,
                               help='Refuse to import Unity results older than N hours '
                                    f'(default: {rv.DEFAULT_MAX_AGE_HOURS:.0f})')
    latest_parser.add_argument('--allow-stale', action='store_true',
                               help='Import Unity results no matter how old they are')
    latest_parser.add_argument('--for-request', type=int, metavar='ID', default=None,
                               help='Only report results that provably belong to test '
                                    'request ID. Exits non-zero (1/3/4/5) when none can '
                                    'be attributed, instead of showing an older run.')
    latest_parser.add_argument('--newer-than', metavar='WHEN', default=None,
                               help='Only consider results written since WHEN: a relative '
                                    'age (15m, 2h, 1d), epoch seconds, or ISO 8601.')
    latest_parser.add_argument('--allow-partial', action='store_true',
                               help='With --for-request: accept a file from a broader run '
                                    'that contains the requested tests.')
    
    # List command
    list_parser = subparsers.add_parser('list', help='List available test result files')
    list_parser.add_argument('-n', '--number', type=int, default=10, help='Number of files to list')
    list_parser.add_argument('--max-age', type=float, default=None,
                             help='Refuse to import Unity results older than N hours')
    list_parser.add_argument('--allow-stale', action='store_true',
                             help='Import Unity results no matter how old they are')
    
    # Show command (specific file)
    show_parser = subparsers.add_parser('show', help='Show specific test result file')
    show_parser.add_argument('filename', help='Name of the XML file to show')
    show_parser.add_argument('-v', '--verbose', action='store_true', help='Show detailed output')
    show_parser.add_argument('--json', action='store_true', help='Output as JSON')
    
    # Failed command (show only failed tests)
    failed_parser = subparsers.add_parser('failed', help='Show failed tests from recent runs')
    failed_parser.add_argument('-n', '--number', type=int, default=5, help='Number of recent files to check')
    failed_parser.add_argument('-v', '--verbose', action='store_true', help='Show error messages')
    
    # Stats command
    stats_parser = subparsers.add_parser('stats', help='Show statistics from recent test runs')
    stats_parser.add_argument('-n', '--number', type=int, default=10, help='Number of recent runs to analyze')
    
    # Clean command
    clean_parser = subparsers.add_parser('clean', help='Clean old test result files')
    clean_parser.add_argument('--keep', type=int, default=10, help='Number of recent files to keep')
    clean_parser.add_argument('--confirm', action='store_true', help='Confirm deletion')
    
    # A bare invocation means `latest`. Let argparse fill the defaults rather than
    # hand-listing them: the hand-written list went stale every time a flag was added to
    # the latest subparser, and a missing attribute crashes the most common command.
    args = parser.parse_args(sys.argv[1:] or ['latest'])
    
    # Default to 'latest' if no command specified
    # 0 disables the age guard entirely.
    max_age = 0.0 if getattr(args, 'allow_stale', False) else getattr(args, 'max_age', None)
    
    # Execute commands
    if args.command == 'latest':
        # Assertive mode: prove the results belong to the run you asked about, or say so
        # and exit non-zero. Plain `latest` below is a viewer and has always exited 0.
        if args.for_request is not None or args.newer_than is not None:
            sys.exit(run_assertive_latest(args, max_age))

        files = list_result_files(1, max_age)
        if files:
            data = parse_xml_file(files[0])
            if args.json:
                print(json.dumps(data, indent=2, default=str))
            else:
                display_summary(data, args.verbose)
                warn_if_stale(files[0])
        else:
            print("No test result files found")
    
    elif args.command == 'list':
        files = list_result_files(args.number, max_age)
        if files:
            print(f"\nFound {len(files)} test result files:")
            print("-" * 60)
            for i, file in enumerate(files, 1):
                stat = file.stat()
                mtime = datetime.fromtimestamp(stat.st_mtime)
                size_kb = stat.st_size / 1024
                print(f"{i:3}. {file.name:<40} {mtime.strftime('%Y-%m-%d %H:%M:%S')} ({size_kb:.1f} KB)")
                # Which run each file is from, so a stale one is obvious on sight.
                print(f"     {describe_file_contents(file)}")
        else:
            print("No test result files found")
    
    elif args.command == 'show':
        results_path = get_test_results_path()
        file_path = results_path / args.filename
        if file_path.exists():
            data = parse_xml_file(file_path)
            if args.json:
                print(json.dumps(data, indent=2, default=str))
            else:
                display_summary(data, args.verbose)
        else:
            print(f"File not found: {args.filename}")
            print(f"Looking in: {results_path}")
    
    elif args.command == 'failed':
        files = list_result_files(args.number)
        all_failed = []
        
        for file in files:
            data = parse_xml_file(file)
            if 'tests' in data:
                failed = [t for t in data['tests'] if t['result'] in ['Failed', 'Error']]
                for test in failed:
                    test['file'] = file.name
                    test['timestamp'] = data['timestamp']
                # Once per file. This used to sit inside the loop above, so each file's
                # failures were appended once per failure and printed N times over.
                all_failed.extend(failed)
        
        if all_failed:
            print(f"\nFailed tests from {len(files)} recent runs:")
            print("=" * 60)
            
            # Group by file
            current_file = None
            for test in all_failed:
                if test['file'] != current_file:
                    current_file = test['file']
                    print(f"\n{test['file']} ({test['timestamp']}):")
                
                print(f"  [FAILED] {test['name']}")
                if args.verbose and test.get('message'):
                    print(f"     {test['message']}")
        else:
            print("No failed tests found in recent runs")
    
    elif args.command == 'stats':
        files = list_result_files(args.number)
        
        if files:
            total_stats = {
                'runs': len(files),
                'total_tests': 0,
                'total_passed': 0,
                'total_failed': 0,
                'total_duration': 0,
                'pass_rate': 0
            }
            
            print(f"\nStatistics from {len(files)} test runs:")
            print("=" * 60)
            
            for file in files:
                data = parse_xml_file(file)
                if 'error' not in data:
                    total_stats['total_tests'] += data['total']
                    total_stats['total_passed'] += data['passed']
                    total_stats['total_failed'] += data['failed']
                    total_stats['total_duration'] += data['duration']
            
            if total_stats['total_tests'] > 0:
                total_stats['pass_rate'] = (total_stats['total_passed'] / total_stats['total_tests']) * 100
            
            print(f"Total Runs:     {total_stats['runs']}")
            print(f"Total Tests:    {total_stats['total_tests']}")
            print(f"Total Passed:   {total_stats['total_passed']}")
            print(f"Total Failed:   {total_stats['total_failed']}")
            print(f"Pass Rate:      {total_stats['pass_rate']:.1f}%")
            print(f"Total Duration: {total_stats['total_duration']:.1f} seconds")
            print(f"Avg Duration:   {total_stats['total_duration']/total_stats['runs']:.1f} seconds per run")
        else:
            print("No test result files found")
    
    elif args.command == 'clean':
        files = list_result_files(0)  # Get all files
        
        if len(files) > args.keep:
            files_to_delete = files[args.keep:]
            
            print(f"\nFiles to delete ({len(files_to_delete)}):")
            for file in files_to_delete:
                print(f"  - {file.name}")
            
            if args.confirm or input("\nDelete these files? (y/N): ").lower() == 'y':
                for file in files_to_delete:
                    try:
                        file.unlink()
                        print(f"Deleted: {file.name}")
                    except Exception as e:
                        print(f"Error deleting {file.name}: {e}")
                print(f"\nDeleted {len(files_to_delete)} files, kept {args.keep} most recent")
            else:
                print("Deletion cancelled")
        else:
            print(f"Only {len(files)} files found, keeping all (threshold: {args.keep})")

if __name__ == "__main__":
    main()