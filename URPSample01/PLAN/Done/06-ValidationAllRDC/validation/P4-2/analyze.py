"""P4-2 (X1) evidence reducer.

Reads the pre-fix and post-fix player reports plus the EditMode XML and writes Metrics.json.
Each probe state reports six masks: opaque/transparent keyword, texture-binding and uniform bits.
A clean camera reads all zero; the pre-fix build keeps the previous camera's GI, SSR and occlusion
result textures (bits 4|8|32 = 44) and, for the injected state, every binding and uniform (63).
"""

import json
import os
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))

MASKS = (
    "opaqueKeywords",
    "transparentKeywords",
    "opaqueBindings",
    "transparentBindings",
    "opaqueUniforms",
    "transparentUniforms",
)
STATES = ("control", "live", "overlay", "stale", "missing")
# GI | SSR | occlusion result textures.
RESIDUAL_BINDINGS = 4 | 8 | 32
# Injected GI, SSR, transparent SSR and occlusion bindings. The refraction bindings (1|2) are
# already cleared per camera by P4-1, so they never survive into the rejected camera.
STALE_BINDINGS = 4 | 8 | 16 | 32
# Injected GI, SSR and transparent SSR uniforms; refraction uniforms are reset by P4-1.
STALE_UNIFORMS = 4 | 8 | 16


def load_report(path):
    with open(os.path.join(HERE, path), "r", encoding="utf-8") as handle:
        return json.load(handle)


def state_masks(case, state):
    leak = case.get("keywordLeak") or {}
    masks = leak.get(state) or {}
    return {name: masks.get(name, -1) for name in MASKS}, leak.get(state + "Visible", False)


def case_metrics(case):
    leak = case.get("keywordLeak") or {}
    return {
        "passed": case.get("passed", False),
        "overlaySupported": leak.get("overlaySupported", False),
        **{state: {"visible": visible, "masks": masks}
           for state, (masks, visible) in
           ((state, state_masks(case, state)) for state in STATES)},
    }


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
    before = {case["id"]: case for case in load_report(os.path.join("Before", "Report.json"))["cases"]}
    after = {case["id"]: case for case in load_report(os.path.join("Player", "Report.json"))["cases"]}

    cases = []
    all_passed = True
    for case_id, post_case in after.items():
        pre_case = before.get(case_id)
        if pre_case is None:
            all_passed = False
            continue

        pre = case_metrics(pre_case)
        post = case_metrics(post_case)

        # Pre-fix: the injected state must survive into the rejected camera, otherwise the probe
        # cannot observe a leak at all.
        stale = pre["stale"]["masks"]
        before_leaked = (
            pre["stale"]["visible"]
            and stale["opaqueKeywords"] == 0
            and stale["transparentKeywords"] == 0
            and stale["opaqueBindings"] == STALE_BINDINGS
            and stale["transparentBindings"] == STALE_BINDINGS
            and stale["opaqueUniforms"] == STALE_UNIFORMS
            and stale["transparentUniforms"] == STALE_UNIFORMS
        )
        # Pre-fix: the rejected cameras also keep the last supported camera's GI/SSR result textures.
        residual_states = ["control", "missing"]
        if pre["overlaySupported"]:
            residual_states.append("overlay")
        before_residual = all(
            pre[state]["visible"]
            and pre[state]["masks"]["opaqueKeywords"] == 0
            and pre[state]["masks"]["transparentKeywords"] == 0
            and pre[state]["masks"]["opaqueBindings"] == RESIDUAL_BINDINGS
            and pre[state]["masks"]["transparentBindings"] == RESIDUAL_BINDINGS
            and pre[state]["masks"]["opaqueUniforms"] == 0
            and pre[state]["masks"]["transparentUniforms"] == 0
            for state in residual_states
        )
        # Post-fix: every rejected path reads zero in every mask.
        cleared_states = ["control", "stale", "missing"]
        if post["overlaySupported"]:
            cleared_states.append("overlay")
        after_cleared = all(
            post[state]["visible"]
            and all(value == 0 for value in post[state]["masks"].values())
            for state in cleared_states
        )
        live = post["live"]["masks"]
        live_observed = (
            post["live"]["visible"]
            and live["opaqueKeywords"] & (2 | 4 | 16) == (2 | 4 | 16)
            and live["transparentKeywords"] & (1 | 8) == (1 | 8)
            and live["opaqueBindings"] & RESIDUAL_BINDINGS == RESIDUAL_BINDINGS
            and live["transparentBindings"] & (1 | 16) == (1 | 16)
            and live["transparentUniforms"] & (1 | 16) == (1 | 16)
        )

        passed = (
            before_leaked
            and before_residual
            and after_cleared
            and live_observed
            and post["passed"]
        )
        all_passed &= passed
        cases.append(
            {
                "id": case_id,
                "before": pre,
                "after": post,
                "beforeLeaked": before_leaked,
                "beforeResidual": before_residual,
                "afterCleared": after_cleared,
                "liveObserved": live_observed,
                "passed": passed,
            }
        )

    metrics = {
        "task": "P4-2",
        "criterion": "X1",
        "masks": list(MASKS),
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
