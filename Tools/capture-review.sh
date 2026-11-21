#!/usr/bin/env bash
# Builds the game, runs it for a few seconds and keeps the last rendered frame as a review image.
#
#   Tools/capture-review.sh <label> [seconds]
#
# Saves Review/Replies/<date>_<time>_<label>.png and prints its path. Set GODOT to use another
# Godot 4.7.2 mono console executable. The game runs with --no-review, so the run does not also
# create a game review session.
set -euo pipefail
cd "$(dirname "$0")/.."

label="${1:-review}"
seconds="${2:-6}"
godot="${GODOT:-/k/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe}"
fps=30

out="Review/Replies"
mkdir -p "$out"
[ -f Review/.gdignore ] || : > Review/.gdignore

frames="$(mktemp -d)"
trap 'rm -rf "$frames"' EXIT

dotnet build ThunderBlastPingPong.sln -nologo -v q > /dev/null
"$godot" --path . --write-movie "$(cygpath -m "$frames")/frame.png" --fixed-fps "$fps" \
    --quit-after "$((seconds * fps))" -- --no-review > /dev/null 2>&1

last="$(ls "$frames"/frame*.png | sort | tail -n 1)"
dest="$out/$(date +%Y-%m-%d_%H-%M-%S)_${label}.png"
cp "$last" "$dest"
echo "$dest"
