#!/usr/bin/env python3
"""P1-3 GPU evidence probe: GTAO depth source, material variant and history seeding.

Reads the P1-GTAO RenderDoc captures and writes Probe.json plus the exported depth images and the
two Lit pixel-shader disassemblies. Run it after the validation player has produced Player/*.rdc.
"""

import glob
import json
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, *([".."] * 6)))
sys.path.insert(0, os.path.join(REPO, "MCP", "tools"))
sys.path.insert(0, HERE)

from rdmcp import DEFAULT_SERVER, McpClient  # noqa: E402

CLI = (
    r"E:\GitHub\renderdoc-mcp\_dist\package"
    r"\renderdoc-mcp-windows-x64-v0.3.0\bin\renderdoc-cli.exe"
)
PLAYER = os.path.join(HERE, "Player")
OUT = os.path.join(HERE, "DepthProbe")
# RenderDoc reports the intermediate camera target in its own row order: the floor pixel at
# presented (640, 510) lives at row 720 - 1 - 510 in the render target.
PROBE_X = 640
PROBE_PRESENTED_Y = 510


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


def scope_end_before_next_marker(rows, start_eid, stop_names):
    """Last scope-close event before the next top level marker after the transparent draws."""
    last = None
    for eid, label, flags in rows:
        if eid <= start_eid:
            continue
        if flags == 64 and label in stop_names:
            return last
        if flags == 128:
            last = eid
    return last


class Capture:
    def __init__(self, client, path):
        self.path = path
        self.rows = parse_events(path)
        self.info = client.call_tool("open_capture", path=path)

    def resources(self, client):
        result = client.call_tool("list_resources")
        resources = result.get("resources", result) if isinstance(result, dict) else result
        return resources

    def resource_by_prefix(self, client, prefixes):
        for resource in self.resources(client):
            name = resource.get("name", "")
            if any(name.startswith(prefix) for prefix in prefixes):
                return resource
        return None


def shader_hash(disassembly):
    for line in disassembly.splitlines():
        if line.startswith("Shader hash "):
            return line[len("Shader hash "):].strip()
    return ""


def write_shader(path, text):
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text)


def main():
    on_capture = find_capture("forward_gtao_full_temporal_*.rdc")
    off_capture = find_capture("forward_gtao_off_zero_*.rdc")
    ortho_capture = find_capture("forward_gtao_ortho_*.rdc")
    first_frame = find_capture("forward_gtao_first_frame_*.rdc")
    for capture in (on_capture, off_capture, ortho_capture, first_frame):
        if not capture:
            raise SystemExit("Missing capture input: " + str(capture))

    os.makedirs(OUT, exist_ok=True)
    client = McpClient(DEFAULT_SERVER)
    client.request("initialize", {
        "protocolVersion": "2024-11-05",
        "capabilities": {},
        "clientInfo": {"name": "p1-gtao-probe", "version": "1"},
    })
    client.notify("notifications/initialized")

    result = {}
    try:
        # --- AO on -------------------------------------------------------------------------------
        on = Capture(client, on_capture)
        on_gtao = dispatches_under(on.rows, "Ground Truth Ambient Occlusion")
        on_transparent = marker(on.rows, "DrawTransparentObjects")
        on_probe_end = scope_end_before_next_marker(
            on.rows, on_transparent, {"SSL Binding Probe", "FinalBlit", "UGUI.Rendering.RenderOverlays"})
        probe_row = 719 - PROBE_PRESENTED_Y
        history = client.call_tool("pixel_history", x=PROBE_X, y=probe_row, eventId=on_probe_end)
        writes = [m for m in history["modifications"] if m["passed"]]
        floor = writes[0]
        ps_on = client.call_tool("get_shader", eventId=floor["eventId"], stage="ps", mode="disasm")
        cs_on = client.call_tool("get_shader", eventId=on_gtao[0], stage="cs", mode="disasm")

        occlusion = on.resource_by_prefix(client, ("GtaoOcclusion_",))
        pyramid = on.resource_by_prefix(client, ("ScreenSpaceDepthPyramid_",))
        cam_depth = on.resource_by_prefix(client, ("_CameraDepthTexture_",))
        occlusion_usage = client.call_tool("get_resource_usage", resourceId=occlusion["resourceId"])
        pyramid_stats = client.call_tool("get_texture_stats", resourceId=pyramid["resourceId"], mip=0)
        cam_stats = client.call_tool("get_texture_stats", resourceId=cam_depth["resourceId"])
        pyramid_export = client.call_tool("export_texture", resourceId=pyramid["resourceId"], mip=0)
        cam_export = client.call_tool("export_texture", resourceId=cam_depth["resourceId"])

        # Compare the packed atlas mip 0 (top-left region) with the camera depth texture.
        import analyze as A
        wp, hp, cp, pyramid_rows = A.read_png(pyramid_export["path"])
        wc, hc, cc, cam_rows = A.read_png(cam_export["path"])
        compared = different = maximum = 0
        for y in range(min(hp, hc)):
            for x in range(min(wp, wc)):
                a = pyramid_rows[y][x * cp]
                b = cam_rows[y][x * cc]
                d = abs(a - b)
                compared += 1
                different += 1 if d else 0
                maximum = max(maximum, d)

        shutil.copyfile(pyramid_export["path"], os.path.join(OUT, "DepthPyramidMip0.png"))
        shutil.copyfile(cam_export["path"], os.path.join(OUT, "CameraDepthTexture.png"))
        write_shader(os.path.join(OUT, "Lit_AO_on.ps.asm.txt"), ps_on["disassembly"])

        # --- AO off ------------------------------------------------------------------------------
        off = Capture(client, off_capture)
        off_gtao = dispatches_under(off.rows, "Ground Truth Ambient Occlusion")
        off_transparent = marker(off.rows, "DrawTransparentObjects")
        off_probe_end = scope_end_before_next_marker(
            off.rows, off_transparent, {"SSL Binding Probe", "FinalBlit", "UGUI.Rendering.RenderOverlays"})
        off_history = client.call_tool("pixel_history", x=PROBE_X, y=probe_row, eventId=off_probe_end)
        off_writes = [m for m in off_history["modifications"] if m["passed"]]
        off_floor = off_writes[0]
        ps_off = client.call_tool("get_shader", eventId=off_floor["eventId"], stage="ps", mode="disasm")
        write_shader(os.path.join(OUT, "Lit_AO_off.ps.asm.txt"), ps_off["disassembly"])

        # --- Orthographic variant ----------------------------------------------------------------
        ortho = Capture(client, ortho_capture)
        ortho_gtao = dispatches_under(ortho.rows, "Ground Truth Ambient Occlusion")
        cs_ortho = client.call_tool("get_shader", eventId=ortho_gtao[0], stage="cs", mode="disasm")

        # --- First frame after the history reset --------------------------------------------------
        first = Capture(client, first_frame)
        first_gtao = dispatches_under(first.rows, "Ground Truth Ambient Occlusion")
        first_occlusion = first.resource_by_prefix(client, ("GtaoOcclusion_",))
        first_history0 = first.resource_by_prefix(client, ("GtaoHistory0_",))
        first_history1 = first.resource_by_prefix(client, ("GtaoHistory1_",))
        first_usage = {
            "occlusion": client.call_tool("get_resource_usage", resourceId=first_occlusion["resourceId"]),
            "history0": client.call_tool("get_resource_usage", resourceId=first_history0["resourceId"]),
            "history1": client.call_tool("get_resource_usage", resourceId=first_history1["resourceId"]),
        }

        result = {
            "onCapture": os.path.basename(on_capture),
            "offCapture": os.path.basename(off_capture),
            "orthoCapture": os.path.basename(ortho_capture),
            "firstFrameCapture": os.path.basename(first_frame),
            "probe": {
                "renderTargetX": PROBE_X,
                "renderTargetY": probe_row,
                "presentedY": PROBE_PRESENTED_Y,
                "onFloorDraw": floor["eventId"],
                "onDepth": floor["depth"],
                "offFloorDraw": off_floor["eventId"],
                "offDepth": off_floor["depth"],
                "depthEqual": floor["depth"] == off_floor["depth"],
            },
            "dispatch": {
                "onGtaoMarker": marker(on.rows, "Ground Truth Ambient Occlusion"),
                "onDispatches": on_gtao,
                "offGtaoMarker": marker(off.rows, "Ground Truth Ambient Occlusion"),
                "offDispatches": off_gtao,
                "orthoDispatches": ortho_gtao,
                "firstFrameDispatches": first_gtao,
                "depthPyramidDispatches": len(dispatches_under(on.rows, "Depth Pyramid")),
            },
            "materialVariant": {
                "onShader": ps_on["resourceId"],
                "onHash": shader_hash(ps_on["disassembly"]),
                "onTexture2D": ps_on["disassembly"].count("dcl_resource_texture2d"),
                "onSamplers": ps_on["disassembly"].count("dcl_sampler"),
                "offShader": ps_off["resourceId"],
                "offHash": shader_hash(ps_off["disassembly"]),
                "offTexture2D": ps_off["disassembly"].count("dcl_resource_texture2d"),
                "offSamplers": ps_off["disassembly"].count("dcl_sampler"),
            },
            "computeVariant": {
                "perspectiveShader": cs_on["resourceId"],
                "perspectiveHash": shader_hash(cs_on["disassembly"]),
                "orthographicShader": cs_ortho["resourceId"],
                "orthographicHash": shader_hash(cs_ortho["disassembly"]),
            },
            "occlusionTexture": {
                "resourceId": occlusion["resourceId"],
                "usage": occlusion_usage["entries"],
            },
            "depthSource": {
                "pyramid": pyramid["resourceId"],
                "cameraDepth": cam_depth["resourceId"],
                "pyramidMin": pyramid_stats["min"]["floatValue"],
                "pyramidMax": pyramid_stats["max"]["floatValue"],
                "cameraMin": cam_stats["min"]["floatValue"],
                "cameraMax": cam_stats["max"]["floatValue"],
                "extremesEqual": (
                    pyramid_stats["min"]["uintValue"] == cam_stats["min"]["uintValue"]
                    and pyramid_stats["max"]["uintValue"] == cam_stats["max"]["uintValue"]
                ),
                "comparedPixels": compared,
                "differentPixels": different,
                "maxExportDifference": maximum,
            },
            "firstFrame": {
                "occlusion": first_usage["occlusion"]["entries"],
                "history0": first_usage["history0"]["entries"],
                "history1": first_usage["history1"]["entries"],
            },
        }
    finally:
        client.close()

    with open(os.path.join(HERE, "Probe.json"), "w", encoding="utf-8") as handle:
        json.dump(result, handle, indent=2)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
