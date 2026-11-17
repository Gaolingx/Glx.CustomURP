"""P4-3 resolution / MSAA / renderer matrix reducer.

Reads the player report and writes Metrics.json. Each case turns on every screen-space effect, so
the report already asserts the dispatch dimensions, history and non-finite pixels; this script
re-derives the target-size expectations and the F-12 terrain samples into one reviewable table.
"""

import json
import os
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
WIDTH, HEIGHT = 1280, 720


def load(path):
    with open(os.path.join(HERE, path), "r", encoding="utf-8") as handle:
        return json.load(handle)


def expected_target(scale):
    return int(WIDTH * scale), int(HEIGHT * scale)


def case_row(case):
    scale = case.get("renderScale", 1.0) or 1.0
    target = expected_target(scale)
    gtao = case.get("gtaoDispatch") or {}
    ssgi = case.get("ssgiDispatch") or {}
    ssr = case.get("ssrDispatch") or {}
    row = {
        "id": case["id"],
        "renderer": case["renderer"],
        "renderScale": scale,
        "msaa": case.get("msaaSamples", 1),
        "targetWidth": case.get("targetWidth", 0),
        "targetHeight": case.get("targetHeight", 0),
        "expectedTarget": list(target),
        "targetMatches": (case.get("targetWidth"), case.get("targetHeight")) == target,
        "nonFinitePixels": case.get("nonFinitePixels", -1),
        "passed": case["passed"],
        "issues": case["issues"],
        "gtao": {
            "aoWidth": gtao.get("aoWidth", 0),
            "aoHeight": gtao.get("aoHeight", 0),
            "groupsX": gtao.get("groupsX", 0),
            "groupsY": gtao.get("groupsY", 0),
            "produced": gtao.get("produced", False),
        },
        "ssgi": {
            "traceWidth": ssgi.get("traceWidth", 0),
            "traceHeight": ssgi.get("traceHeight", 0),
            "totalDispatches": ssgi.get("totalDispatches", 0),
        },
        "ssr": {
            "traceWidth": ssr.get("traceWidth", 0),
            "traceHeight": ssr.get("traceHeight", 0),
            "pbr": ssr.get("pbr", False),
            "traceDispatches": ssr.get("traceDispatches", 0),
        },
    }
    row["ok"] = row["passed"] and row["targetMatches"] and row["nonFinitePixels"] == 0
    return row


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
    report = load(os.path.join("Player", "Report.json"))
    rows = [case_row(case) for case in report["cases"]]

    terrain = None
    terrain_path = os.path.join(HERE, "TerrainPixels.json")
    if os.path.exists(terrain_path):
        terrain = load("TerrainPixels.json")

    metrics = {
        "task": "P4-3",
        "cases": rows,
        "caseCount": len(rows),
        "allPassed": all(row["ok"] for row in rows),
        "terrain": terrain,
        "editMode": editmode_counts("EditModeFull.xml"),
    }

    with open(os.path.join(HERE, "Metrics.json"), "w", encoding="utf-8") as handle:
        json.dump(metrics, handle, indent=2)
        handle.write("\n")

    for row in rows:
        status = "PASS" if row["ok"] else "FAIL"
        print(f"{status} {row['id']:<40} target={row['targetWidth']}x{row['targetHeight']} "
              f"ao={row['gtao']['aoWidth']}x{row['gtao']['aoHeight']} "
              f"ssgi={row['ssgi']['traceWidth']}x{row['ssgi']['traceHeight']} "
              f"ssr={row['ssr']['traceWidth']}x{row['ssr']['traceHeight']}")
        if not row["ok"]:
            for issue in row["issues"]:
                print("   ", issue)
    print("allPassed", metrics["allPassed"])
    return 0 if metrics["allPassed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
