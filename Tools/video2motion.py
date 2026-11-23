"""video2motion: a table tennis player's strokes from a video, for the Pose Lab.

Reads a video with MediaPipe's pose model (33 body joints in 3D, in metres), follows one player
through it, splits it at camera cuts (each shot read on its own), smooths the joints, and finds
each stroke's key moments, from the racket hand followed on the body (so a moving camera does not
count):
  - a serve: the free hand rising at least 25 cm (the toss); ready = the ball on the palm (the
    free hand lowest before), toss = the free hand highest, contact = the racket hand fastest
    within 0.7 s after;
  - another stroke: a clear peak of the racket hand's speed; ready = the calmest moment before;
  - backswing = the racket hand furthest back before the contact, finish = where it has slowed.
How well each joint is seen is kept (MediaPipe's visibility; none off the picture), so the Pose
Lab fits only lightly what is hidden and leaves out legs that are not in the picture.
It writes them to a JSON file that the Pose Lab's "Import video..." turns into a motion: for each
moment it fits the lab's own body to the joints, so the motion is the lab's and the game's.

Joints are in the player's own frame, as the Pose Lab's: metres, Y up, the player facing +Z (as it
faces the table at the ready moment), +X to its left; relative to the middle of the hips, with how
far the hips have moved since the ready moment (from the hips' track across the picture: footwork
along the camera's line of sight is not seen).

  Tools/video2motion.sh <video> [--pick auto|near|far|left|right] [--start s] [--end s]
                         [--left-handed] [--out file.json]

A picture of the moments found, with the joints drawn on them, is written beside the JSON
(<out>_moments.png), to see at a glance what was read.
"""
import argparse
import json
import math
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
# Left and right swapped (a left-handed player is mirrored into a right-handed one).
MIRROR = list(range(33))
for a, b in [(1, 4), (2, 5), (3, 6), (7, 8), (9, 10)] + [(i, i + 1) for i in range(11, 33, 2)]:
    MIRROR[a], MIRROR[b] = b, a
BONES = [(11, 12), (11, 13), (13, 15), (12, 14), (14, 16), (11, 23), (12, 24), (23, 24), (23, 25), (25, 27),
         (24, 26), (26, 28), (27, 31), (28, 32), (16, 20), (16, 18), (15, 19), (15, 17)]


def read_video(path, start, end, pick):
    """The chosen player's joints in every frame: image (x, y in 0..1, visibility) and world (metres)."""
    cap = cv2.VideoCapture(path)
    if not cap.isOpened():
        sys.exit(f"cannot open the video {path}")
    fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
    width, height = cap.get(cv2.CAP_PROP_FRAME_WIDTH), cap.get(cv2.CAP_PROP_FRAME_HEIGHT)
    options = vision.PoseLandmarkerOptions(
        base_options=mp_tasks.BaseOptions(model_asset_path=MODEL),
        running_mode=vision.RunningMode.VIDEO, num_poses=2,
        min_pose_detection_confidence=0.4, min_pose_presence_confidence=0.4, min_tracking_confidence=0.4)
    times, images, worlds, frames, looks = [], [], [], [], []
    previous = None
    with vision.PoseLandmarker.create_from_options(options) as landmarker:
        index = 0
        while True:
            ok, frame = cap.read()
            if not ok:
                break
            t = index / fps
            index += 1
            if t < start or (end is not None and t > end):
                continue
            rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
            result = landmarker.detect_for_video(mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb), int(t * 1000))
            people = [(np.array([[p.x, p.y, p.visibility] for p in img]), np.array([[p.x, p.y, p.z] for p in wld]))
                      for img, wld in zip(result.pose_landmarks, result.pose_world_landmarks)]
            chosen = choose(people, previous, pick)
            times.append(t)
            frames.append(frame)
            looks.append(cv2.resize(frame, (64, 36)).astype(np.float32))
            if chosen is None:
                images.append(np.full((33, 3), np.nan))
                worlds.append(np.full((33, 3), np.nan))
                continue
            images.append(chosen[0])
            worlds.append(chosen[1])
            previous = chosen[0]
    cap.release()
    if not times:
        sys.exit("no frames read (check --start / --end)")
    return fps, (width, height), np.array(times), np.array(images), np.array(worlds), frames, looks


def find_cuts(looks, images):
    """Where the video cuts to another shot: the whole picture changes at once, far more than from
    frame to frame (a moving camera changes it a little each frame). Each shot is read on its own."""
    change = np.array([float(np.mean(np.abs(looks[k] - looks[k - 1]))) for k in range(1, len(looks))])
    if len(change) == 0:
        return []
    limit = max(12.0, 6.0 * float(np.median(change)))
    return [k + 1 for k, c in enumerate(change) if c > limit]


def hips_at(img):
    return (img[23, :2] + img[24, :2]) / 2


def size_of(img):
    ys = img[[0, 27, 28], 1]
    return abs(max(ys[1], ys[2]) - ys[0])


def choose(people, previous, pick):
    """The player to follow: the one nearest where it was; at first, as picked (near: the largest
    in the picture, far: the smallest, left / right: by where its hips are)."""
    if not people:
        return None
    if previous is not None:
        return min(people, key=lambda p: np.linalg.norm(hips_at(p[0]) - hips_at(previous)))
    if pick == "far":
        return min(people, key=lambda p: size_of(p[0]))
    if pick == "left":
        return min(people, key=lambda p: hips_at(p[0])[0])
    if pick == "right":
        return max(people, key=lambda p: hips_at(p[0])[0])
    return max(people, key=lambda p: size_of(p[0]))  # auto / near


def fill_and_smooth(series, times, sigma_seconds=0.035):
    """Missing frames filled in between the ones seen, then a gentle Gaussian smoothing over time
    (the joints jitter a little from frame to frame)."""
    out = series.copy()
    flat = out.reshape(len(times), -1)
    for k in range(flat.shape[1]):
        col = flat[:, k]
        good = ~np.isnan(col)
        if good.sum() == 0:
            continue
        flat[:, k] = np.interp(times, times[good], col[good])
    dt = np.median(np.diff(times)) if len(times) > 1 else 1 / 30
    sigma = max(sigma_seconds / dt, 0.5)
    radius = int(3 * sigma)
    kernel = np.exp(-0.5 * (np.arange(-radius, radius + 1) / sigma) ** 2)
    kernel /= kernel.sum()
    padded = np.pad(flat, ((radius, radius), (0, 0)), mode="edge")
    smoothed = np.stack([np.convolve(padded[:, k], kernel, mode="valid") for k in range(flat.shape[1])], axis=1)
    return smoothed.reshape(series.shape)


# The body as a tree from the hips, for rebuilding depth bone by bone (parent, child).
TREE = [(None, 23), (None, 24), (23, 25), (25, 27), (27, 29), (27, 31), (24, 26), (26, 28), (28, 30), (28, 32),
        (23, 11), (24, 12), (11, 13), (13, 15), (15, 17), (15, 19), (15, 21), (12, 14), (14, 16), (16, 18), (16, 20), (16, 22),
        (11, 0), (12, 0)]


def depth_from_bones(images, up, size):
    """MediaPipe's joints with their depth stretched back out.

    Across the picture the joints are seen well; their depth (towards or away from the camera) is
    what a single camera reads worst, and MediaPipe's comes out flattened, the more so for a body
    seen from the side (its width then lies along the depth). Its depth keeps the right order and
    moves smoothly, so it is kept, only stretched by one factor for the whole clip: the one that
    makes the bones' lengths come nearest their true lengths. A bone's true length is the longest
    it is seen across the picture in the clip (some time it lies square to the camera), at least as
    long as MediaPipe makes it, left and right the same. Returns the joints and the stretch."""
    w, h = size
    pix = images[:, :, :2] * np.array([w, h])
    height_px = np.median(np.abs(np.maximum(pix[:, 27, 1], pix[:, 28, 1]) - pix[:, 0, 1])) or 1.0
    height_m = np.median(np.linalg.norm(up[:, 0] - (up[:, 27] + up[:, 28]) / 2, axis=1))
    m = height_m / height_px  # metres a pixel
    bones = [(p, c) for p, c in TREE if p is not None] + [(23, 24), (11, 12)]
    true = {}
    for a, b in bones:
        across = np.linalg.norm(pix[:, b] - pix[:, a], axis=1) * m
        true[(a, b)] = max(np.percentile(across, 95), np.median(np.linalg.norm(up[:, b] - up[:, a], axis=1)))
    for (a, b) in list(true):
        mirror = (MIRROR[a], MIRROR[b])
        if mirror in true and mirror != (a, b):
            true[(a, b)] = true[mirror] = (true[(a, b)] + true[mirror]) / 2

    def miss(k):
        total = 0.0
        for (a, b), length in true.items():
            d = up[:, b] - up[:, a]
            total += float(np.sum((np.sqrt(d[:, 0] ** 2 + d[:, 1] ** 2 + (k * d[:, 2]) ** 2) - length) ** 2))
        return total
    stretch = min(np.arange(1.0, 3.01, 0.05), key=miss)
    out = up.copy()
    out[:, :, 2] *= stretch
    return out, float(stretch)


def own_frame(up_joints):
    """The rotation into the player's own frame at a moment: Y up, Z the way the hips and
    shoulders face (horizontal), X to the player's left."""
    across = (up_joints[11] - up_joints[12]) + (up_joints[23] - up_joints[24])  # towards the left
    across[1] = 0
    across /= np.linalg.norm(across) + 1e-9
    up = np.array([0.0, 1.0, 0.0])
    forward = np.cross(across, up)
    forward /= np.linalg.norm(forward) + 1e-9
    left = np.cross(up, forward)
    return np.stack([left, up, forward])  # rows: own X, Y, Z


def find_strokes(times, up, min_speed):
    """Each stroke's moments in a shot.

    The racket hand is followed on the body (from the right shoulder, in the frame the body faces
    each moment), so a moving camera or a turning body does not make it seem fast.
      - A serve: the free hand rises at least 25 cm (the toss); its highest is the toss, the contact
        the racket hand's fastest within 0.7 s after, the ready moment the free hand at its lowest
        before (the ball on the palm).
      - Any other stroke: the racket hand's speed clearly peaks (above min_speed and three times
        its usual speed in the shot), not at a serve's contact.
    Then the backswing is the racket hand furthest back before the contact, the finish where its
    speed has dropped to a third after it."""
    n = len(times)
    frames = [own_frame(up[k]) for k in range(n)]
    hand = np.array([(up[k, 16] - up[k, 12]) @ frames[k].T for k in range(n)])
    free = np.array([((up[k, 15] - (up[k, 23] + up[k, 24]) / 2) @ frames[k].T)[1] for k in range(n)])
    speed = np.zeros(n)
    if n > 2:
        speed[1:-1] = np.linalg.norm(hand[2:] - hand[:-2], axis=1) / (times[2:] - times[:-2])

    def window(a, b):
        return np.where((times >= a) & (times <= b))[0]

    def moments_for(contact, ready, toss=None):
        before = window(times[contact] - 0.7, times[contact] - 0.03)
        if toss is not None:
            before = before[before > toss] if (before > toss).any() else before
        back = int(before[np.argmin(hand[before, 2])]) if len(before) else max(contact - 3, 0)
        after = window(times[contact] + 0.01, times[contact] + 0.6)
        slow = [k for k in after if speed[k] < speed[contact] / 3]
        finish = int(slow[0]) if slow else (int(after[-1]) if len(after) else contact)
        idx = {"ready": ready, "backswing": back, "contact": contact, "finish": finish}
        if toss is not None:
            idx["toss"] = toss
        return {"index": idx, "peak": float(speed[contact]), "serve": toss is not None}

    strokes = []
    # Serves: each rise of the free hand.
    k = 1
    while k < n - 1:
        if free[k] >= free[k - 1] and free[k] >= free[k + 1]:
            low_w = window(times[k] - 1.2, times[k])
            low = int(low_w[np.argmin(free[low_w])])
            if free[k] - free[low] >= 0.25:
                follow = window(times[k], times[k] + 0.7)
                if len(follow):
                    contact = int(follow[np.argmax(speed[follow])])
                    if speed[contact] >= 0.6 * min_speed:
                        strokes.append(moments_for(contact, low, toss=k))
                        k = max(contact, k) + 1
                        continue
        k += 1
    # Other strokes: clear peaks of the racket hand's speed, away from the serves.
    usual = float(np.median(speed)) if n else 0.0
    limit = max(min_speed, 3.0 * usual)
    taken = [times[st["index"]["contact"]] for st in strokes]
    k = 1
    while k < n - 1:
        if speed[k] >= limit and speed[k] >= speed[k - 1] and speed[k] >= speed[k + 1]:
            near = window(times[k] - 0.25, times[k] + 0.25)
            peak = int(near[np.argmax(speed[near])])
            if all(abs(times[peak] - t) > 0.8 for t in taken):
                calm = window(times[peak] - 1.2, times[peak] - 0.3)
                ready = int(calm[np.argmin(speed[calm])]) if len(calm) else max(peak - 8, 0)
                strokes.append(moments_for(peak, ready))
                taken.append(times[peak])
            k = max(peak, k) + 1  # on past the peak (never back to it)
        else:
            k += 1
    strokes.sort(key=lambda st: st["index"]["contact"])
    return strokes, speed


def draw_moments(frames, images, picks, labels, path):
    tiles = []
    for k, label in zip(picks, labels):
        frame = frames[k].copy()
        h, w = frame.shape[:2]
        img = images[k]
        for a, b in BONES:
            if not (np.isnan(img[a, 0]) or np.isnan(img[b, 0])):
                cv2.line(frame, (int(img[a, 0] * w), int(img[a, 1] * h)), (int(img[b, 0] * w), int(img[b, 1] * h)), (60, 230, 60), 3)
        if not np.isnan(img[16, 0]):
            cv2.circle(frame, (int(img[16, 0] * w), int(img[16, 1] * h)), 9, (40, 120, 255), -1)
        scale = 360 / h
        frame = cv2.resize(frame, (int(w * scale), 360))
        cv2.putText(frame, label, (10, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.9, (255, 255, 255), 3)
        cv2.putText(frame, label, (10, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.9, (20, 20, 20), 1)
        tiles.append(frame)
    if tiles:
        rows = [np.hstack(tiles[i:i + 4] + [np.zeros_like(tiles[0])] * (4 - len(tiles[i:i + 4]))) for i in range(0, len(tiles), 4)]
        cv2.imwrite(path, np.vstack(rows))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("video")
    ap.add_argument("--out", help="the JSON written (default: beside the video, <name>.strokes.json)")
    ap.add_argument("--pick", default="auto", choices=["auto", "near", "far", "left", "right"], help="which player, with two in the picture")
    ap.add_argument("--start", type=float, default=0.0, help="from this time (s)")
    ap.add_argument("--end", type=float, default=None, help="up to this time (s)")
    ap.add_argument("--left-handed", action="store_true", help="a left-handed player (mirrored to a right-handed one, as the game's)")
    ap.add_argument("--min-speed", type=float, default=2.0, help="the racket hand's speed (m/s) that counts as a stroke")
    ap.add_argument("--depth", default="mediapipe", choices=["mediapipe", "bones"], help="MediaPipe's own depth (default), or stretched to the bones' lengths (a body seen side-on)")
    args = ap.parse_args()

    out = args.out or os.path.splitext(args.video)[0] + ".strokes.json"
    fps, size, times_all, images_all, worlds_all, frames, looks = read_video(args.video, args.start, args.end, args.pick)
    seen_all = ~np.isnan(images_all[:, 0, 0])
    if seen_all.sum() < 5:
        sys.exit(f"no player found in the video ({seen_all.sum()} of {len(times_all)} frames)")
    cuts = find_cuts(looks, images_all)
    bounds = [0] + cuts + [len(times_all)]
    result = {"tool": "video2motion", "video": os.path.basename(args.video), "fps": fps,
              "leftHanded": args.left_handed, "joints": list(JOINTS), "shots": len(bounds) - 1, "strokes": []}
    picks, labels = [], []
    stretch, fastest = 1.0, 0.0
    names = ["ready", "toss", "backswing", "contact", "finish"]
    for shot, (a, b) in enumerate(zip(bounds, bounds[1:]), 1):
        if (~np.isnan(images_all[a:b, 0, 0])).sum() < max(5, int(0.5 * fps)):
            continue  # too short a shot, or the player not in it
        times = times_all[a:b]
        images = fill_and_smooth(images_all[a:b], times)
        worlds = fill_and_smooth(worlds_all[a:b], times)
        if args.left_handed:
            images, worlds = images[:, MIRROR], worlds[:, MIRROR]
            images[:, :, 0] = 1 - images[:, :, 0]
            worlds[:, :, 0] = -worlds[:, :, 0]
        # MediaPipe's world: x right in the picture, y down, z away from the camera; with Y up.
        up = worlds * np.array([1.0, -1.0, -1.0])
        if args.depth == "bones":
            up, stretch = depth_from_bones(images, up, size)
        # Metres per picture unit from the player's height on screen, and the hips' move across it.
        height_m = np.median(np.linalg.norm(up[:, 0] - (up[:, 27] + up[:, 28]) / 2, axis=1))
        height_px = np.median([size_of(img) for img in images]) or 1.0
        per_unit = height_m / height_px
        hips_img = (images[:, 23, :2] + images[:, 24, :2]) / 2
        move_up = np.zeros((len(times), 3))
        move_up[:, 0] = (hips_img[:, 0] - hips_img[0, 0]) * per_unit * size[0] / size[1]
        move_up[:, 1] = -(hips_img[:, 1] - hips_img[0, 1]) * per_unit
        hips_mid_up = (up[:, 23] + up[:, 24]) / 2
        # Each stroke is put in its own frame (as the player faces at its ready moment).
        strokes, speed = find_strokes(times, up, args.min_speed)
        fastest = max(fastest, float(speed.max()) if len(speed) else 0.0)
        for st in strokes:
            idx = dict(st["index"])
            frame = own_frame(up[idx["ready"]])

            def rel_at(k):
                return (up[k] - hips_mid_up[k]) @ frame.T
            serve = bool(st["serve"])
            order = sorted(idx.items(), key=lambda kv: (kv[1], names.index(kv[0])))
            moments, used = [], set()
            for name, k in order:
                if k in used and name != "contact":
                    continue  # two moments on one frame: the first is kept (the contact always)
                used.add(k)
                rel = rel_at(k)
                move = (move_up[k] - move_up[idx["ready"]]) @ frame.T
                # How well each joint is seen: MediaPipe's visibility, none outside the picture
                # (a joint off the edge is only guessed).
                img = images[k]
                inside = (img[:, 0] >= 0) & (img[:, 0] <= 1) & (img[:, 1] >= 0) & (img[:, 1] <= 1)
                seen = np.where(inside, np.clip(img[:, 2], 0, 1), 0.0)
                moments.append({"name": name, "t": float(times[k]),
                                "joints": {j: [round(float(v), 4) for v in rel[i]] for j, i in JOINTS.items()},
                                "seen": {j: round(float(seen[i]), 2) for j, i in JOINTS.items()},
                                "move": [round(float(move[0]), 4), 0.0, round(float(move[2]), 4)]})
                picks.append(a + k)
                labels.append(f"stroke {len(result['strokes']) + 1}: {name} {times[k]:.2f}s")
            contact = next((m for m in moments if m["name"] == "contact"), None)
            if contact is None:
                continue
            result["strokes"].append({"forehand": bool(contact["joints"]["rwrist"][0] < 0.05), "serve": serve, "shot": shot,
                                      "peakSpeed": round(st["peak"], 2), "moments": moments})
    with open(out, "w") as f:
        json.dump(result, f, indent=1)
    sheet = os.path.splitext(out)[0] + "_moments.png"
    draw_moments(frames, images_all, picks, labels, sheet)
    serves = sum(1 for st in result["strokes"] if st["serve"])
    print(f"VIDEO2MOTION {len(result['strokes'])} stroke(s) ({serves} serve(s)) in {times_all[-1] - times_all[0]:.1f} s of video, "
          f"{result['shots']} shot(s); {seen_all.sum()} of {len(times_all)} frames with the player; fastest racket hand "
          f"{max([st['peakSpeed'] for st in result['strokes']], default=fastest):.1f} m/s" + (f"; depth stretched x{stretch:.2f}" if args.depth == "bones" else ""))
    print(f"VIDEO2MOTION written {out}")
    print(f"VIDEO2MOTION moments {sheet}")


if __name__ == "__main__":
    main()
