#!/usr/bin/env python3
"""Dispatch census for the P1-GTAO captures: GTAO and depth pyramid dispatches per frame."""

import glob
import json
import os
import subprocess

HERE = os.path.dirname(os.path.abspath(__file__))
PLAYER = os.path.join(HERE, "Player")
CLI = (
    r"E:\GitHub\renderdoc-mcp\_dist\package"
    r"\renderdoc-mcp-windows-x64-v0.3.0\bin\renderdoc-cli.exe"
)


def events(capture):
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


def scope_dispatches(rows, name):
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


def marker(rows, name):
    for eid, label, flags in rows:
        if flags == 64 and label == name:
            return eid
    return None


def main():
    census = {}
    for capture in sorted(glob.glob(os.path.join(PLAYER, "*.rdc"))):
        rows = events(capture)
        gtao_marker = marker(rows, "Ground Truth Ambient Occlusion")
        census[os.path.basename(capture)] = {
            "gtaoMarker": gtao_marker,
            "gtaoDispatches": scope_dispatches(rows, "Ground Truth Ambient Occlusion"),
            "depthPyramidMarker": marker(rows, "Depth Pyramid"),
            "depthPyramidDispatches": len(scope_dispatches(rows, "Depth Pyramid")),
            "drawOpaqueMarker": marker(rows, "DrawOpaqueObjects"),
            "gBufferMarker": marker(rows, "GBuffer"),
        }

    out = os.path.join(HERE, "P1Census.json")
    with open(out, "w", encoding="utf-8") as handle:
        json.dump(census, handle, indent=2)
    print(json.dumps(census, indent=2))


if __name__ == "__main__":
    main()
