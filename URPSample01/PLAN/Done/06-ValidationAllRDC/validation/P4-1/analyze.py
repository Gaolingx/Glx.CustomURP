"""P4-1 (F-05) evidence reducer.

Reads the pre-fix and post-fix player reports plus the EditMode XML and writes Metrics.json.
The leak surface encodes one piece of refraction state per channel, so a cleared camera reads
(0, 0, 0) with alpha 128 on the drawn surface; an inherited state reads 255 in the leaked channel.
"""

import json
import os
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))

CHANNELS = ("keyword", "parameters", "probeCount", "texture")


def load_report(path):
    with open(os.path.join(HERE, path), "r", encoding="utf-8") as handle:
        return json.load(handle)


def case_metrics(report):
    metrics = {}
    for case in report["cases"]:
        leak = case.get("refractionLeak") or {}
        metrics[case["id"]] = {
            "passed": case["passed"],
            "controlVisible": leak.get("controlVisible", False),
            "control": {name: leak.get("control" + name[0].upper() + name[1:], -1) for name in CHANNELS},
            "visible": leak.get("visible", False),
            "injected": {name: leak.get(name, -1) for name in CHANNELS},
            "issues": case["issues"],
        }
    return metrics


def editmode_counts(path):
    root = ET.parse(os.path.join(HERE, path)).getroot()
    return {
        "result": root.attrib.get("result"),
        "total": int(root.attrib.get("total", 0)),
        "passed": int(root.attrib.get("passed", 0)),
        "failed": int(root.attrib.get("failed", 0)),
        "skipped": int(root.attrib.get("skipped", 0)),
    }


def main():
    before = case_metrics(load_report(os.path.join("Before", "Report.json")))
    after = case_metrics(load_report(os.path.join("Player", "Report.json")))

    cases = []
    all_passed = True
    for case_id, post in after.items():
        pre = before.get(case_id)
        if pre is None:
            all_passed = False
            continue

        # Pre-fix: the injected state must reach the reflection camera, otherwise the probe cannot
        # observe the leak at all and the post-fix zeros would be meaningless.
        before_leaked = pre["visible"] and all(pre["injected"][name] == 255 for name in CHANNELS)
        before_control_clean = pre["controlVisible"] and all(pre["control"][name] == 0 for name in CHANNELS)
        after_cleared = (
            post["passed"]
            and post["visible"]
            and all(post["injected"][name] == 0 for name in CHANNELS)
            and post["controlVisible"]
            and all(post["control"][name] == 0 for name in CHANNELS)
        )

        passed = before_leaked and before_control_clean and after_cleared
        all_passed &= passed
        cases.append(
            {
                "id": case_id,
                "beforeInjected": pre["injected"],
                "beforeControl": pre["control"],
                "afterInjected": post["injected"],
                "afterControl": post["control"],
                "beforeLeaked": before_leaked,
                "afterCleared": after_cleared,
                "passed": passed,
            }
        )

    metrics = {
        "task": "P4-1",
        "defect": "F-05",
        "channels": list(CHANNELS),
        "cases": cases,
        "editMode": editmode_counts("EditModeFull.xml"),
        "allPassed": all_passed,
    }

    with open(os.path.join(HERE, "Metrics.json"), "w", encoding="utf-8") as handle:
        json.dump(metrics, handle, indent=2)
        handle.write("\n")

    print(json.dumps(metrics, indent=2))
    return 0 if all_passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
