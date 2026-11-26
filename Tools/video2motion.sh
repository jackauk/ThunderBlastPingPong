#!/usr/bin/env bash
# A video made ready for the Pose Lab's Video window, and the joints of the frames picked there (see
# Tools/video2motion.py). Sets up its Python (3.12, MediaPipe, OpenCV) and pose model on first use.
#   Tools/video2motion.sh <video> --out <folder>/<name> [--left-handed]   (or --serve: the joints of frames asked for)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
py="$here/.venv/Scripts/python.exe"
[ -x "$py" ] || py="$here/.venv/bin/python"
if [ ! -x "$py" ]; then
    echo "setting up Python 3.12 with MediaPipe and OpenCV in Tools/.venv ..."
    py install 3.12 >/dev/null 2>&1 || true
    py -V:3.12 -m venv "$here/.venv"
    py="$here/.venv/Scripts/python.exe"
    "$py" -m pip install --quiet --upgrade pip
    "$py" -m pip install --quiet mediapipe opencv-python numpy
fi
model="$here/models/pose_landmarker_heavy.task"
if [ ! -f "$model" ]; then
    echo "downloading MediaPipe's pose model ..."
    mkdir -p "$here/models"
    curl -sSL -o "$model" "https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_heavy/float16/latest/pose_landmarker_heavy.task"
fi
exec "$py" "$here/video2motion.py" "$@"
