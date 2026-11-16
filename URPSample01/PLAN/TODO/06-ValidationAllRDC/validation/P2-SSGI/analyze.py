"""RenderDoc census for the P2-1 SSGI dispatch chain.

For every capture this script records the Screen Space Global Illumination marker span, the
dispatch order and compute shader of every kernel, and how the SSGI history, blue-noise LUT and
reflection-probe atlas resources are bound inside that span. The released renderdoc-mcp build does
not expose dispatch group counts, so sizes come from the player-side dispatch stats in Report.json;
this script covers the GPU side that the CPU cannot see.

Usage:
    python analyze.py captures/*.rdc --report out/Report.json --out out
"""

import argparse
import json
import sys
from pathlib import Path

MARKER = "Screen Space Global Illumination"


def find_repo_root(start):
    for candidate in (start, *start.parents):
        if (candidate / "MCP" / "tools" / "rdmcp.py").exists():
            return candidate
    raise RuntimeError("Could not locate the repository root (MCP/tools/rdmcp.py).")


REPO_ROOT = find_repo_root(Path(__file__).resolve())
sys.path.insert(0, str(REPO_ROOT / "MCP" / "tools"))
from rdmcp import DEFAULT_SERVER, McpClient  # noqa: E402


def marker_spans(events):
    spans = {}
    stack = []
    for event in events:
        flags = event.get("flags", "")
        if flags == "PushMarker":
            stack.append((event["eventId"], event.get("name", "")))
        elif flags == "PopMarker" and stack:
            start, name = stack.pop()
            spans.setdefault(name, []).append((start, event["eventId"]))
    return spans


def kernel_labels(stats):
    """Mirror the dispatch order in ScreenSpaceLightingSsgiPass.Dispatch."""
    if not stats:
        return None

    temporal = stats.get("temporal", False)
    denoise = stats.get("denoise", False)
    second_pass = stats.get("secondDenoiserPass", False)
    half_denoiser = stats.get("halfResolutionDenoiser", False)
    point = stats.get("pointDistributionDispatches", 0) > 0

    labels = ["trace", "reproject"]
    if temporal:
        labels += ["validate", "accumulate", "copyHistory"]
    if denoise:
        if point:
            labels.append("pointDistribution")
        labels.append("diffuseDenoise")
        if half_denoiser:
            labels.append("gather")
        if second_pass:
            labels += ["accumulate", "copyHistory", "diffuseDenoise"]
            if half_denoiser:
                labels.append("gather")
    if stats.get("expectedUpsampleDispatches", 0) > 0:
        labels.append("upsample")
    return labels


def load_case_index(report_path):
    if report_path is None or not report_path.exists():
        return {}
    report = json.loads(report_path.read_text(encoding="utf-8"))
    index = {}
    for case in report.get("cases", []):
        capture = case.get("capture") or ""
        if capture:
            index[Path(capture).name] = case
    return index


def analyze_capture(client, capture, case):
    client.call_tool("open_capture", path=str(capture))
    events = client.call_tool("list_events")
    spans = marker_spans(events).get(MARKER)
    if not spans:
        return {"capture": capture.name, "error": "SSGI marker not found."}

    start, end = spans[-1]
    dispatches = [
        event["eventId"]
        for event in events
        if start < event["eventId"] < end and "Dispatch" in event.get("flags", "")
    ]

    stats = case.get("ssgiDispatch") if case else None
    labels = kernel_labels(stats)
    if labels is not None and len(labels) != len(dispatches):
        labels = None

    rows = []
    for order, event_id in enumerate(dispatches):
        state = client.call_tool("get_pipeline_state", eventId=event_id)
        rows.append({
            "order": order,
            "eventId": event_id,
            "label": labels[order] if labels and order < len(labels) else "unlabeled",
            "csShader": (state.get("csShader") or {}).get("resourceId", ""),
        })

    textures = client.call_tool("list_resources", type="texture")["resources"]

    interesting = {"Ssgi", "OwenScrambled", "ScramblingTile", "RankingTile", "ReflProbe", "Atlas"}
    resource_rows = {}
    for texture in textures:
        name = texture["name"]
        if not any(token.lower() in name.lower() for token in interesting):
            continue
        usage = client.call_tool("get_resource_usage", resourceId=texture["resourceId"])
        entries = [
            entry for entry in usage.get("entries", [])
            if start < entry["eventId"] < end
        ]
        if entries:
            resource_rows[name] = entries

    label_by_event = {row["eventId"]: row["label"] for row in rows}
    for name, entries in resource_rows.items():
        for entry in entries:
            entry["label"] = label_by_event.get(entry["eventId"], "")

    return {
        "capture": capture.name,
        "caseId": case.get("id") if case else None,
        "ssgiMarker": {"start": start, "end": end},
        "dispatchCount": len(rows),
        "dispatches": rows,
        "resources": resource_rows,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("captures", nargs="+", type=Path)
    parser.add_argument("--report", type=Path, help="Report.json produced by the validation player")
    parser.add_argument("--out", type=Path, default=Path(__file__).parent)
    args = parser.parse_args()

    cases = load_case_index(args.report)
    results = []
    client = McpClient(DEFAULT_SERVER)
    try:
        client.request("initialize", {
            "protocolVersion": "2024-11-05",
            "capabilities": {},
            "clientInfo": {"name": "ssgi-census", "version": "1"},
        })
        client.notify("notifications/initialized")
        for capture in sorted(args.captures):
            print(f"analyzing {capture.name}", flush=True)
            results.append(analyze_capture(client, capture, cases.get(capture.name)))
    finally:
        client.close()

    args.out.mkdir(parents=True, exist_ok=True)
    output = args.out / "P2Census.json"
    output.write_text(json.dumps({"captures": results}, indent=2), encoding="utf-8")
    print(f"wrote {output}")


if __name__ == "__main__":
    main()