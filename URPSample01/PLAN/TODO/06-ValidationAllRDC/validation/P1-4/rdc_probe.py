#!/usr/bin/env python3
"""P1-4 GPU evidence probe: which draw consumes the material facing AO in each GTAO path.

Compares the Forward opaque draws, the Deferred directional lighting draws and the Deferred
AmbientOcclusionOnly draw. Writes Probe.json next to this script.
"""

import glob
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, *([".."] * 6)))
sys.path.insert(0, os.path.join(REPO, "MCP", "tools"))

from rdmcp import DEFAULT_SERVER, McpClient  # noqa: E402

CLI = (
    r"E:\GitHub\renderdoc-mcp\_dist\package"
    r"\renderdoc-mcp-windows-x64-v0.3.0\bin\renderdoc-cli.exe"
)
PLAYER = os.path.join(HERE, "Player")


def find_capture(pattern):
    matches = sorted(glob.glob(os.path.join(PLAYER, pattern)))
    return matches[-1] if matches else None


def parse_events(capture):
    output = subprocess.run([CLI, capture, "events"], capture_output=True, text=True, check=True).stdout
    rows = []
    for line in output.splitlines()[1:]:
        parts = line.split("\t")
        if len(parts) < 3:
            continue
        try:
            eid = int(parts[0])
        except ValueError:
            continue
        rows.append((eid, parts[1], int(parts[2])))
    return rows


def marker(rows, name):
    for eid, label, flags in rows:
        if flags == 64 and label == name:
            return eid
    return None


def dispatches_under(rows, name):
    stack = []
    found = []
    for eid, label, flags in rows:
        if flags == 64:
            stack.append(label)
        elif flags == 128:
            if stack:
                stack.pop()
        elif flags == 4 and stack and stack[-1] == name:
            found.append(eid)
    return found


def scopes_by_event(rows):
    stack = []
    mapping = {}
    for eid, label, flags in rows:
        if flags == 64:
            stack.append(label)
        elif flags == 128:
            if stack:
                stack.pop()
        else:
            mapping[eid] = list(stack)
    return mapping


def shader_hash(disassembly):
    for line in disassembly.splitlines():
        if line.startswith("Shader hash "):
            return line[len("Shader hash "):].strip()
    return ""


def texture_declarations(disassembly):
    return disassembly.count("dcl_resource_texture2d")


class Capture:
    def __init__(self, client, path):
        self.path = path
        self.rows = parse_events(path)
        self.scopes = scopes_by_event(self.rows)
        self.info = client.call_tool("open_capture", path=path)

    def resource_by_prefix(self, client, prefix):
        result = client.call_tool("list_resources")
        resources = result.get("resources", result) if isinstance(result, dict) else result
        for resource in resources:
            if resource.get("name", "").startswith(prefix):
                return resource
        return None


def collect(client, capture_name, pattern):
    path = find_capture(pattern)
    if not path:
        raise SystemExit("Missing capture input: " + pattern)
    capture = Capture(client, path)
    occlusion = capture.resource_by_prefix(client, "GtaoOcclusion_")
    if occlusion is None:
        raise SystemExit("No GtaoOcclusion resource in " + path)
    usage = client.call_tool("get_resource_usage", resourceId=occlusion["resourceId"])["entries"]

    writes = [entry for entry in usage if entry["usage"] == "CS_RWResource"]
    reads = [entry for entry in usage if entry["usage"] == "PS_Resource"]
    readers = []
    for entry in reads:
        scopes = capture.scopes.get(entry["eventId"], [])
        shader = client.call_tool("get_shader", eventId=entry["eventId"], stage="ps", mode="disasm")
        readers.append({
            "eventId": entry["eventId"],
            "scopes": scopes,
            "shader": shader["resourceId"],
            "hash": shader_hash(shader["disassembly"]),
            "texture2d": texture_declarations(shader["disassembly"]),
        })

    return {
        "name": capture_name,
        "capture": os.path.basename(path),
        "gtaoMarker": marker(capture.rows, "Ground Truth Ambient Occlusion"),
        "gtaoDispatches": dispatches_under(capture.rows, "Ground Truth Ambient Occlusion"),
        "depthPyramidDispatches": len(dispatches_under(capture.rows, "Depth Pyramid")),
        "deferredPassMarker": marker(capture.rows, "Deferred Pass"),
        "drawOpaqueMarker": marker(capture.rows, "DrawOpaqueObjects"),
        "forwardOnlyMarker": marker(capture.rows, "Render Opaques Forward Only"),
        "occlusion": occlusion["resourceId"],
        "writes": writes,
        "reads": readers,
    }


def main():
    client = McpClient(DEFAULT_SERVER)
    client.request("initialize", {
        "protocolVersion": "2024-11-05",
        "capabilities": {},
        "clientInfo": {"name": "p1-4-probe", "version": "1"},
    })
    client.notify("notifications/initialized")

    result = {}
    try:
        forward = collect(client, "forward", "forward_coverage_on_*.rdc")
        deferred = collect(client, "deferred", "deferred_coverage_on_*.rdc")
        deferred_nolight = collect(client, "deferredNoLight", "deferred_nolight_on_*.rdc")

        def in_scope(report, label):
            return [reader for reader in report["reads"] if label in reader["scopes"]]

        result = {
            "forward": {
                "capture": forward["capture"],
                "gtaoMarker": forward["gtaoMarker"],
                "gtaoDispatches": forward["gtaoDispatches"],
                "depthPyramidDispatches": forward["depthPyramidDispatches"],
                "occlusion": forward["occlusion"],
                "occlusionWrites": forward["writes"],
                "opaqueReaders": in_scope(forward, "DrawOpaqueObjects"),
                "readerCount": len(forward["reads"]),
            },
            "deferred": {
                "capture": deferred["capture"],
                "gtaoMarker": deferred["gtaoMarker"],
                "gtaoDispatches": deferred["gtaoDispatches"],
                "depthPyramidDispatches": deferred["depthPyramidDispatches"],
                "deferredPassMarker": deferred["deferredPassMarker"],
                "occlusion": deferred["occlusion"],
                "occlusionWrites": deferred["writes"],
                "lightingReaders": in_scope(deferred, "Deferred Pass"),
                "forwardOnlyReaders": in_scope(deferred, "Render Opaques Forward Only"),
                "readerCount": len(deferred["reads"]),
            },
            "deferredNoLight": {
                "capture": deferred_nolight["capture"],
                "gtaoMarker": deferred_nolight["gtaoMarker"],
                "gtaoDispatches": deferred_nolight["gtaoDispatches"],
                "depthPyramidDispatches": deferred_nolight["depthPyramidDispatches"],
                "deferredPassMarker": deferred_nolight["deferredPassMarker"],
                "occlusion": deferred_nolight["occlusion"],
                "occlusionWrites": deferred_nolight["writes"],
                "lightingReaders": in_scope(deferred_nolight, "Deferred Pass"),
                "forwardOnlyReaders": in_scope(deferred_nolight, "Render Opaques Forward Only"),
                "readerCount": len(deferred_nolight["reads"]),
            },
        }

        with_light_hashes = sorted({reader["hash"] for reader in result["deferred"]["lightingReaders"]})
        no_light_hashes = sorted({reader["hash"] for reader in result["deferredNoLight"]["lightingReaders"]})
        result["pathShaders"] = {
            "withLight": with_light_hashes,
            "noLight": no_light_hashes,
            "distinctPaths": not bool(set(with_light_hashes) & set(no_light_hashes)),
            "withLightTexture2D": [reader["texture2d"] for reader in result["deferred"]["lightingReaders"]],
            "noLightTexture2D": [reader["texture2d"] for reader in result["deferredNoLight"]["lightingReaders"]],
        }
        result["passed"] = (
            len(result["forward"]["opaqueReaders"]) >= 6 and
            len(result["deferred"]["lightingReaders"]) >= 1 and
            len(result["deferredNoLight"]["lightingReaders"]) == 1 and
            result["pathShaders"]["distinctPaths"]
        )
    finally:
        client.close()

    with open(os.path.join(HERE, "Probe.json"), "w", encoding="utf-8") as handle:
        json.dump(result, handle, indent=2)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
