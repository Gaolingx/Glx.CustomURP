#!/usr/bin/env bash
# Git Bash on Windows. Use a fresh worktree for each reproduction.
set -eu
main=E:/GitHub/Glx.CustomURP
base=E:/GitHub/Glx.CustomURP-c3-material-5ab7626
c3="$main/URPSample01/PLAN/Done/16-CleanupBaseline/validation/c3"
unity='D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe'
case_name=Unity.ClusteredDeferred.Tests.PlayMode.ClusteredDeferredPlayModeTests.ClusteredMatchesForwardPlusAcrossMaterialTypes
# Preparation: df -h E:/GitHub; git -C "$main" worktree add --detach "$base" 5ab7626
# git -C "$base" apply "$c3/material-fixture.patch"
# Source: material test from 646f75a, using baseline helper CaptureFogRegions
# (same body as CaptureRegions). The original stencil test is retained.
# Candidate inputs: original c3/material-candidate, c3/tests/material-d3d{11,12}.{xml,log}.
git -C "$base" apply --reverse --check "$c3/material-fixture.patch"
test "$(git -C "$base" rev-parse HEAD)" = "$(git -C "$main" rev-parse 5ab7626)"
export C3_MATERIAL_OUTPUT="$c3/material-baseline"
suite_rc=0
for api in 11 12; do
  rc=0
  "$unity" -batchmode -projectPath "$base/URPSample01" "-force-d3d$api" -runTests -testPlatform PlayMode -testFilter "$case_name" -testResults "$c3/tests/material-baseline-d3d$api.xml" -logFile "$base/material-d3d$api.log" || rc=$?
  # A genuine assertion failure returns 2; still measure the frames exported before it.
  python -c 'import sys,xml.etree.ElementTree as ET; cases=ET.parse(sys.argv[1]).findall(".//test-case"); assert len(cases)==1 and cases[0].get("fullname")==sys.argv[2] and (int(sys.argv[3]),cases[0].get("result")) in [(0,"Passed"),(2,"Failed")]' "$c3/tests/material-baseline-d3d$api.xml" "$case_name" "$rc"
  if (( rc )); then suite_rc=$rc; fi
done
python "$c3/compare_material.py" > "$c3/material-comparison.json.tmp"
mv "$c3/material-comparison.json.tmp" "$c3/material-comparison.json"
exit "$suite_rc"
