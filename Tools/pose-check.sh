#!/usr/bin/env bash
# Builds the game and checks every saved motion (Poses/) and every Pose Lab template for joints
# past their normal range, a racket face turned away from the table at a contact, lost balance,
# and anything going through the table or the body (PoseCheck, PoseLabCheck).
#
#   Tools/pose-check.sh          report only
#   Tools/pose-check.sh fix      also put the saved motions right where that is safe (the old
#                                ones kept in Review/pose_backups/) and suggest template values
#
# Writes Review/pose_check.log and prints the problems (✖) and the summary. Exits 1 if any
# problem is left. Run it after every change to the motions, templates or the pose code.
set -euo pipefail
cd "$(dirname "$0")/.."

godot="${GODOT:-/k/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe}"
arg="--pose-check"
[ "${1:-}" = "fix" ] && arg="--pose-check=fix"

dotnet build -nologo -v q 2>&1 | grep -E " error |Build succeeded" || true
output="$(timeout 600 "$godot" --headless --path . res://Main.tscn -- --no-review "$arg" 2>&1 | grep -E "^POSECHECK|ERROR|Exception" || true)"
echo "$output" | grep -vE ": ok$" | grep -vE "^POSECHECK $"
echo "$output" | grep -qE "POSECHECK done: 0 problem" || exit 1
