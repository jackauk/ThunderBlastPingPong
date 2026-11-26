"""video2motion: a video made ready for the Pose Lab's Video window, and the player's joints found
in the frames the user picks there (nothing is read from the rest of the video).

  - Import (the Pose Lab's "Import video..."):
        video2motion.py <video> --out <folder>/<name> [--left-handed]
    Writes every frame (at most 960 pixels each way) to <name>_video/f00000.jpg..., and
    <name>.frames.json: fps, count, size, each frame's time. A left-handed player's frames are
    mirrored (the game's players are right-handed). Prints "VIDEO2MOTION progress <done> <of>".
  - The joints of a frame (the Video window, as a key frame is set):
        video2motion.py --serve
    Loads MediaPipe's pose model once, prints "VIDEO2MOTION ready", then reads requests from stdin,
    one JSON a line:
        {"id": 3, "path": ".../f00123.jpg", "pick": "auto", "hips": [x, y] or null}
    and answers each on stdout, one line:
        POSE {"id": 3, "found": true, "px": {...}, "up": {...}, "seen": {...}}
    px: each joint in the picture (pixels); up: in metres (MediaPipe's world, about the hips, Y up);
    seen: how well (MediaPipe's visibility, none off the picture). pick: with two players in the
    picture, which (auto / near: the largest, far: the smallest, left, right); hips: where the
    player's hips were at the key frame before (pixels), to keep to the same player.

  Tools/video2motion.sh <video> --out <folder>/<name>   (sets up Python and the model on first use)
"""
import argparse
import json
import os
import sys

import cv2
import numpy as np
import mediapipe as mp
from mediapipe.tasks import python as mp_tasks
from mediapipe.tasks.python import vision

HERE = os.path.dirname(os.path.abspath(__file__))
MODEL = os.path.join(HERE, "models", "pose_landmarker_heavy.task")

# MediaPipe's 33 pose landmarks, by the names the Pose Lab reads.
JOINTS = {
    "nose": 0, "lshoulder": 11, "rshoulder": 12, "lelbow": 13, "relbow": 14, "lwrist": 15, "rwrist": 16,
    "lpinky": 17, "rpinky": 18, "lindex": 19, "rindex": 20, "lthumb": 21, "rthumb": 22,
    "lhip": 23, "rhip": 24, "lknee": 25, "rknee": 26, "lankle": 27, "rankle": 28,
    "lheel": 29, "rheel": 30, "lfoot": 31, "rfoot": 32,
}
LARGEST = 960  # the frames kept at most this many pixels each way


def extract(video, out, left_handed):
    """Every frame of the video, for the Video window to play."""
    cap = cv2.VideoCapture(video)
    if not cap.isOpened():
        sys.exit(f"cannot open the video {video}")
    fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
    total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT)) or 0
    folder = out + "_video"
    os.makedirs(folder, exist_ok=True)
    for old in os.listdir(folder):  # an earlier import's taken away
        if old.startswith("f") and old.endswith(".jpg"):
            os.remove(os.path.join(folder, old))
    times, size = [], None
    while True:
        ok, frame = cap.read()
        if not ok:
            break
        if size is None:
            h, w = frame.shape[:2]
            scale = min(1.0, LARGEST / max(w, h))
            size = (int(round(w * scale)), int(round(h * scale)))
        if frame.shape[1] != size[0]:
            frame = cv2.resize(frame, size, interpolation=cv2.INTER_AREA)
        if left_handed:
            frame = cv2.flip(frame, 1)
        cv2.imwrite(os.path.join(folder, f"f{len(times):05d}.jpg"), frame, [cv2.IMWRITE_JPEG_QUALITY, 88])
        times.append(round(len(times) / fps, 4))
        if len(times) % 15 == 0:
            print(f"VIDEO2MOTION progress {len(times)} {max(total, len(times))}", flush=True)
    cap.release()
    if not times:
        sys.exit(f"no frames read from {video}")
    with open(out + ".frames.json", "w") as f:
        json.dump({"video": os.path.basename(video), "fps": fps, "count": len(times), "width": size[0], "height": size[1],
                   "folder": os.path.basename(folder), "leftHanded": left_handed, "t": times}, f, separators=(",", ":"))
    print(f"VIDEO2MOTION {len(times)} frames, {times[-1]:.1f} s, {size[0]} x {size[1]}", flush=True)


def size_of(img):
    ys = img[[0, 27, 28], 1]
    return abs(max(ys[1], ys[2]) - ys[0])


def hips_at(img):
    return (img[23, :2] + img[24, :2]) / 2


def choose(people, hips, pick):
    """The player: the one whose hips are nearest where they were (hips, in the picture's 0..1),
    else as picked (near: the largest in the picture, far: the smallest, left / right: by its hips)."""
    if not people:
        return None
    if hips is not None:
        return min(people, key=lambda p: np.linalg.norm(hips_at(p[0]) - hips))
    if pick == "far":
        return min(people, key=lambda p: size_of(p[0]))
    if pick == "left":
        return min(people, key=lambda p: hips_at(p[0])[0])
    if pick == "right":
        return max(people, key=lambda p: hips_at(p[0])[0])
    return max(people, key=lambda p: size_of(p[0]))  # auto / near


def serve():
    """The joints of the frames asked for, one at a time, the model loaded once."""
    options = vision.PoseLandmarkerOptions(
        base_options=mp_tasks.BaseOptions(model_asset_path=MODEL),
        running_mode=vision.RunningMode.IMAGE, num_poses=2,
        min_pose_detection_confidence=0.3, min_pose_presence_confidence=0.3)
    with vision.PoseLandmarker.create_from_options(options) as landmarker:
        print("VIDEO2MOTION ready", flush=True)
        for line in sys.stdin:
            if not line.strip():
                continue
            answer = {"found": False}
            try:
                request = json.loads(line)
                answer["id"] = request.get("id")
                frame = cv2.imread(request["path"])
                if frame is None:
                    raise ValueError(f"cannot read {request['path']}")
                h, w = frame.shape[:2]
                result = landmarker.detect(mp.Image(image_format=mp.ImageFormat.SRGB, data=cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)))
                people = [(np.array([[p.x, p.y, p.visibility] for p in img]), np.array([[p.x, p.y, p.z] for p in wld]))
                          for img, wld in zip(result.pose_landmarks, result.pose_world_landmarks)]
                hips = request.get("hips")
                chosen = choose(people, np.array(hips) / np.array([w, h]) if hips else None, request.get("pick", "auto"))
                if chosen is not None:
                    img, world = chosen
                    up = world * np.array([1.0, -1.0, -1.0])  # MediaPipe's world: y down, z away; with Y up
                    inside = (img[:, 0] >= 0) & (img[:, 0] <= 1) & (img[:, 1] >= 0) & (img[:, 1] <= 1)
                    seen = np.where(inside, np.clip(img[:, 2], 0, 1), 0.0)
                    answer.update({
                        "found": True,
                        "px": {j: [round(float(img[i, 0] * w), 1), round(float(img[i, 1] * h), 1)] for j, i in JOINTS.items()},
                        "up": {j: [round(float(v), 4) for v in up[i]] for j, i in JOINTS.items()},
                        "seen": {j: round(float(seen[i]), 2) for j, i in JOINTS.items()},
                    })
            except Exception as e:  # a bad request is answered, not the end of the service
                answer["error"] = str(e)
            print("POSE " + json.dumps(answer, separators=(",", ":")), flush=True)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("video", nargs="?")
    ap.add_argument("--out", help="the frames go to <out>_video/, their list to <out>.frames.json")
    ap.add_argument("--left-handed", action="store_true", help="a left-handed player (its frames mirrored, as the game's players are right-handed)")
    ap.add_argument("--serve", action="store_true", help="find the joints of the frames asked for on stdin")
    args = ap.parse_args()
    if args.serve:
        serve()
    elif args.video and args.out:
        extract(args.video, args.out, args.left_handed)
    else:
        ap.error("a video and --out, or --serve")


if __name__ == "__main__":
    main()
