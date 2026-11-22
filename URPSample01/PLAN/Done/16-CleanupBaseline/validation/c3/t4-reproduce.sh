#!/usr/bin/env bash
# Git Bash. Use the existing material worktree or a fresh detached 5ab7626 worktree.
set -eu
c3=$(cygpath -am "$(dirname "$0")")
b=${BASE_WORKTREE:-E:/GitHub/Glx.CustomURP-c3-material-5ab7626}
p="$b/URPSample01"
u=${UNITY_EXE:-'D:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Unity.exe'}
out="$c3/t4-baseline"
test "$(git -C "$b" rev-parse HEAD)" = "$(git -C "$b" rev-parse 5ab7626)"
git -C "$b" diff --quiet HEAD -- URPSample01/Packages
if ! git -C "$b" apply --reverse --check "$c3/t4-fixture.patch" 2>/dev/null; then
  git -C "$b" apply "$c3/t4-fixture.patch"
fi
mkdir -p "$out"
for api in 11 12; do
  python -c 'import shutil,sys; assert shutil.disk_usage(sys.argv[1]).free > 8*1024**3, "less than 8 GiB free"' "$p"
  "$u" -batchmode -projectPath "$p" -executeMethod "ClusteredDeferredArenaBuild.BuildT4D3D${api}AndExit" -logFile "$out/build-d3d$api.log"
  cmp "$p/Assets/Tests/ClusteredDeferred/Materials/CdReceiveShadowsOff.mat" "$c3/../../../../../Assets/Tests/ClusteredDeferred/Materials/CdReceiveShadowsOff.mat"
  cp "$p/PLAN/TODO/16-CleanupBaseline/validation/c3/t4/CdArenaT4BuildSummary-Direct3D$api.json" "$out/"
  dir="$out/player-d3d$api"
  # Avoid accepting stale frames from an earlier execution.
  mkdir -p "$dir"; rm -f "$dir"/*.png "$dir/T4FeatureMatrix.json" "$dir/CdError.txt"
  rc=0
  "$p/Builds/ClusteredDeferredArenaT4/Direct3D$api/CdArenaT4.exe" --cd-output "$dir" --cd-t4 -logFile "$dir/Player.log" || rc=$?
  python -c 'import json,sys,pathlib; p=pathlib.Path(sys.argv[1]); j=json.loads((p/"T4FeatureMatrix.json").read_text()); assert not j["error"] and j["graphicsDeviceType"]=="Direct3D"+sys.argv[2] and len(j["entries"])==7 and len(list(p.glob("*.png")))==28 and int(sys.argv[3])==(0 if all(e["passed"] for e in j["entries"]) else 2)' "$dir" "$api" "$rc"
done
git -C "$b" diff --quiet HEAD -- URPSample01/Packages
python "$c3/compare_t4_control.py"
