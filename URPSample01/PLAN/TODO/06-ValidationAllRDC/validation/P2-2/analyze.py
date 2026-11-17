#!/usr/bin/env python3
"""P2-2 SSGI settings-stamp metrics (F-07).

Reads the validation player report and checks, per case, the dispatch snapshot recorded before and
after the post-warmup settings switch. A stamped field must reset the SSGI history exactly once
(validity 0 on the next frame, stamp changed); an unstamped field must leave it untouched.

Usage: python analyze.py [PlayerDir]
"""

import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PLAYER = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "Player")

STAMPED_CASES = {
    "forward_ssgi_probe_atlas_switch",
    "forward_ssgi_probe_atlas_switch_back",
    "forward_ssgi_raysteps_switch",
    "forward_ssgi_thickness_switch",
    "deferred_ssgi_probe_atlas_switch",
}
UNSTAMPED_CASES = {
    "forward_ssgi_denoiser_radius_switch",
    "forward_ssgi_intensity_switch",
}


def main():
    report_path = os.path.join(PLAYER, "Report.json")
    with open(report_path, encoding="utf-8") as handle:
        report = json.load(handle)

    cases = {}
    for case in report.get("cases", []):
        case_id = case.get("id")
        switch = case.get("ssgiSwitch")
        if switch is None:
            cases[case_id] = {"present": False, "passed": False}
            continue

        expected_reset = case_id in STAMPED_CASES or switch.get("expectReset", False)
        reset_once = switch["afterResetCount"] == switch["beforeResetCount"] + 1
        no_reset = switch["afterResetCount"] == switch["beforeResetCount"]
        stamp_changed = switch["afterStamp"] != switch["beforeStamp"]
        frame_advanced = switch["afterFrameIndex"] > switch["beforeFrameIndex"]

        checks = {
            "frameAdvanced": frame_advanced,
            "historyValidBefore": switch["beforeHistoryValid"],
        }
        if expected_reset:
            checks["historyInvalidAfter"] = not switch["afterHistoryValid"]
            checks["resetExactlyOnce"] = reset_once
            checks["stampChanged"] = stamp_changed
        else:
            checks["historyValidAfter"] = switch["afterHistoryValid"]
            checks["noReset"] = no_reset
            checks["stampUnchanged"] = not stamp_changed

        passed = all(checks.values()) and switch.get("passed", False) and not case.get("issues")
        cases[case_id] = {
            "present": True,
            "expectReset": expected_reset,
            "switch": switch,
            "checks": checks,
            "issues": case.get("issues", []),
            "passed": passed,
        }

    missing = (STAMPED_CASES | UNSTAMPED_CASES) - set(cases)
    result = {
        "reportPassed": report.get("passed", False),
        "completed": report.get("completed", False),
        "cases": cases,
        "missingCases": sorted(missing),
        "passed": report.get("passed", False) and not missing and all(c["passed"] for c in cases.values()),
    }

    out = os.path.join(HERE, "Metrics.json")
    with open(out, "w", encoding="utf-8") as handle:
        json.dump(result, handle, indent=2)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
