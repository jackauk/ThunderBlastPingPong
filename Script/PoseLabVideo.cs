using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

// The Pose Lab's "Import video...": a motion made from a video of a player, key frame by key frame.
//
//   1. Import video...: Tools/video2motion.py takes the video's frames out, to play (a few seconds;
//      nothing is read from them yet).
//   2. The Pose Tracer (PoseLabVideoWindow.cs): play it, stop on a frame, set it as a key frame of
//      the action being made (its template's steps: ready, backswing, contact, finish...). Only
//      then are the player's joints found in it (MediaPipe's pose model, the tool's pose service:
//      about a second a frame).
//   3. At each key frame the joints the video shows are listed, and those it does not (hidden, off
//      the picture, the racket): the found ones are dragged where they really are, the missing ones
//      placed on the picture; the next key frame lists what was placed at the one before.
//   4. Every step of the action set: "Make the motion" fits the lab's own body to each key frame
//      (its values tuned until the rig's joints lie on the video's, what is missing guessed), times
//      the steps as the video does, puts the ball on the contact, and plays it to review; then it
//      is named and saved as any motion.
// The key frames, their joints and those placed by hand are kept with the frames (user://video/<name>.keys.json).
public partial class PoseLab
{
    // A key frame's pose as the fit takes it: the joints (the player's own frame, metres from the
    // middle of the hips), how well each is seen (0 hidden or off the picture .. 1), its frame, each
    // joint's place in it (pixels) and the camera that takes the own frame to the picture. Fixed:
    // joints placed by hand and the racket's tip ("racket_tip") marked (pixels); Face: which side of
    // the racket the camera sees.
    class VideoMoment
    {
        public string Name;
        public float T;
        public Dictionary<string, Vector3> Joints;
        public Vector3 Move;
        public Dictionary<string, float> Seen;
        public string Frame;                       // the frame's picture (a full path)
        public Dictionary<string, Vector2> Image;  // each joint in the picture (pixels)
        public Vector3 CamX, CamY;                 // own frame (m) -> picture (px from HipsImage)
        public Vector2 HipsImage;
        public Dictionary<string, Vector2> Fixed = new();
        public string Face;                        // "forehand", "backhand", "edge" or null
        // Deleted by hand: the video's not used. Guessed: those of them put between the key frames
        // around (Joints and Image hold the guess; fitted lightly); the rest left to the lab.
        public HashSet<string> Deleted = new(), Guessed = new();
        // Where the hips are in the lab (x, z), from the table's points marked on the frame; null:
        // the templates' ready place, moved as the hips moved across the picture.
        public Vector2? HipsAt;
        // The video's camera in the lab, found from the table's points (four or more), and its up.
        public Vector3? Eye;
        public Vector3 EyeUp;

        // The own frame turned (the camera with it: the picture stays the same).
        public void Turn(Basis t)
        {
            foreach (string k in Joints.Keys.ToList())
                Joints[k] = t * Joints[k];
            Move = t * Move;
            CamX = t * CamX;
            CamY = t * CamY;
        }

        // The blade's points marked on a frame: its tip, and its edges at the widest (the one by the
        // thumb, the other across from it); with the hand they place the blade and its turn.
        public const string RacketTip = "racket_tip", RacketThumb = "racket_thumb", RacketFar = "racket_far";
        public static bool IsRacketMark(string name) => name.StartsWith("racket_");
        // How much nearer the camera (+) or farther (metres) each joint is than the video has it
        // (a blade's point: than the right wrist), set by hand; along the camera's line, so the
        // picture stays the same.
        public Dictionary<string, float> Depth = new();
        public const float GuessSeen = 0.35f; // how much a guessed joint counts in the fit

        // A joint placed by hand is seen; a deleted one only as guessed (or not at all).
        public float SeenOf(string joint) => Fixed.ContainsKey(joint) ? 1f
            : Deleted.Contains(joint) ? (Guessed.Contains(joint) ? GuessSeen : 0f)
            : Seen != null && Seen.TryGetValue(joint, out float v) ? v : 1f;
        // The legs are in the picture (ankles seen): else the hips' height and the feet are not read.
        public bool LegsSeen => (SeenOf("lankle") + SeenOf("rankle")) / 2f >= 0.5f;

        public Vector2 Project(Vector3 rel) => HipsImage + new Vector2(CamX.Dot(rel), CamY.Dot(rel));

        // The smallest move in the own frame that moves a point by d on the picture (its depth
        // along the camera's line kept).
        public Vector3 Unproject(Vector2 d)
        {
            float a = CamX.Dot(CamX), b = CamX.Dot(CamY), c = CamY.Dot(CamY), det = a * c - b * b;
            if (Mathf.Abs(det) < 1e-6f)
                return Vector3.Zero;
            return CamX * ((c * d.X - b * d.Y) / det) + CamY * ((a * d.Y - b * d.X) / det);
        }

        public float PixelsPerMetre => Mathf.Max((CamX.Length() + CamY.Length()) / 2f, 1e-3f);

        // From the player towards the camera (own frame).
        public Vector3 ToCamera => -CamX.Cross(CamY).Normalized();

        // Each joint and the one it hangs from (down the legs and arms), parents first.
        static readonly (string Joint, string From)[] Chain =
        {
            ("lknee", "lhip"), ("rknee", "rhip"), ("lankle", "lknee"), ("rankle", "rknee"), ("lheel", "lankle"), ("rheel", "rankle"), ("lfoot", "lankle"), ("rfoot", "rankle"),
            ("lelbow", "lshoulder"), ("relbow", "rshoulder"), ("lwrist", "lelbow"), ("rwrist", "relbow"),
            ("lindex", "lwrist"), ("lpinky", "lwrist"), ("lthumb", "lwrist"), ("rindex", "rwrist"), ("rpinky", "rwrist"), ("rthumb", "rwrist"),
        };

        // The joints with the ones placed by hand moved there (on the picture: their depth kept, but
        // one the video hardly saw takes the depth of the joint it hangs from: MediaPipe's guess at
        // the depth of what it does not see, out of the picture most of all, is wild), then each
        // moved nearer or farther as set by hand (Depth).
        public Dictionary<string, Vector3> FittedJoints()
        {
            var joints = new Dictionary<string, Vector3>(Joints);
            // (Exactly there: from where the moment's camera puts the joint, not from MediaPipe's
            // point, which the camera fitted to all the joints misses by a few pixels.)
            foreach (var (name, at) in Fixed)
                if (joints.ContainsKey(name) && Image.ContainsKey(name))
                    joints[name] += Unproject(at - Project(joints[name]));
            Vector3 eye = CamX.Cross(CamY).LengthSquared() > 1e-8f ? ToCamera : Vector3.Zero;
            foreach (var (joint, from) in Chain)
                if (Fixed.ContainsKey(joint) && joints.ContainsKey(joint) && joints.ContainsKey(from) && (Seen?.GetValueOrDefault(joint) ?? 1f) < 0.5f)
                    joints[joint] += eye * (joints[from] - joints[joint]).Dot(eye);
            foreach (var (name, d) in Depth)
                if (joints.ContainsKey(name))
                    joints[name] += eye * d;
            return joints;
        }
    }

    // ---------------------------------------------------------------- the video

    // A video's frames as the import left them (<name>.frames.json and <name>_video/).
    class VideoTrack
    {
        public string Folder;   // the frames (full path)
        public float Fps;
        public int Count;
        public Vector2 Size;    // the frames' size (pixels)
        public float[] T;

        public string FramePath(int f) => System.IO.Path.Combine(Folder, $"f{f:D5}.jpg");

        public static VideoTrack Load(string path)
        {
            if (!System.IO.File.Exists(path))
                return null;
            using JsonDocument doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
            JsonElement r = doc.RootElement;
            if (!r.TryGetProperty("folder", out JsonElement folder))
                return null;
            return new VideoTrack
            {
                Folder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), folder.GetString()),
                Fps = r.GetProperty("fps").GetSingle(),
                Count = r.GetProperty("count").GetInt32(),
                Size = new Vector2(r.GetProperty("width").GetSingle(), r.GetProperty("height").GetSingle()),
                T = r.GetProperty("t").EnumerateArray().Select(e => e.GetSingle()).ToArray(),
            };
        }
    }

    // A key frame: a frame of the video set as a step of the motion. Its joints as MediaPipe found
    // them (when it has: Px null while looking, NotFound when it found no one), and what was placed on
    // it by hand.
    class VideoKey
    {
        public int Frame;
        public string Name;
        public Dictionary<string, Vector2> Px;   // in the picture (pixels)
        public Dictionary<string, Vector3> Up;   // in metres (Y up, about the hips)
        public Dictionary<string, float> Seen;
        public bool NotFound;
        public Dictionary<string, Vector2> Fixed = new();
        public HashSet<string> Deleted = new(); // the video's joints not to use here (put between the key frames around)
        public Dictionary<string, float> Depth = new(); // nearer the camera (+) than the video has it, metres (VideoMoment.Depth)
        public string Face;
        public bool Swapped; // MediaPipe's left and right swapped in this frame

        public bool HasPose => Px != null;
        public Vector2 HipsPx => (Px["lhip"] + Px["rhip"]) / 2f;
    }

    VideoTrack videoTrack;
    List<VideoKey> videoKeys = new();
    string videoAction;  // the action the key frames are for (a StrokeActions id)
    bool videoTurned;    // MediaPipe took the player's front for the back all through: turned round
    string videoName = "";
    float? videoScale;   // the video's body to the rig's (VideoScale), worked out once for the key frames

    FileDialog videoDialog;
    OptionButton videoPick;
    CheckBox videoLefty;
    Task<(int Code, string Output)> videoRun;
    string videoFile;              // the video being imported (its name, for the status line)
    string pendingVideo;           // and the name it is kept under
    volatile float videoProgress;  // how much of it is done (0..1), as the tool says
    float videoShown = -1f;
    bool fitting; // trying poses for the fit: the lab's checks and drawing wait

    static string VideoPath(string name, string ending) => ProjectSettings.GlobalizePath($"user://video/{name}{ending}");

    static string VideoPython()
    {
        string tools = ProjectSettings.GlobalizePath("res://Tools");
        string python = System.IO.Path.Combine(tools, ".venv", "Scripts", "python.exe");
        if (!System.IO.File.Exists(python))
            python = System.IO.Path.Combine(tools, ".venv", "bin", "python");
        return System.IO.File.Exists(python) ? python : null;
    }

    // The rows in the ACTION section.
    void BuildVideoImport(VBoxContainer box)
    {
        var row = new HFlowContainer();
        row.AddChild(MakeButton("Import video…", () => videoDialog.PopupCentered(new Vector2I(900, 600))));
        videoPick = new OptionButton();
        foreach (string p in new[] { "player: auto", "nearest", "farthest", "on the left", "on the right" })
            videoPick.AddItem(p);
        videoPick.TooltipText = "With two players in the picture (a match), which one the key frames' joints are found for.";
        row.AddChild(videoPick);
        videoLefty = new CheckBox { Text = "left-handed" };
        videoLefty.AddThemeFontSizeOverride("font_size", 12);
        videoLefty.TooltipText = "A left-handed player: the video is mirrored as it is imported (the game's players are right-handed).";
        row.AddChild(videoLefty);
        var windowButton = MakeButton("Pose Tracer…", OpenVideoWindow);
        windowButton.TooltipText = "The Pose Tracer: the video imported last (or the one the motion was made from) traced into a motion: play it, set the key frames, place the joints, make the motion.";
        row.AddChild(windowButton);
        box.AddChild(row);

        videoDialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem,
            Title = "A video of a player's strokes", UseNativeDialog = true,
        };
        videoDialog.Filters = new[] { "*.mp4, *.mov, *.avi, *.mkv, *.webm, *.m4v ; Videos" };
        videoDialog.FileSelected += ReadVideo;
        AddChild(videoDialog);
    }

    // The video's frames taken out to play (in the background: a few seconds), then it is opened in
    // the Pose Tracer (_Process: CheckVideoRun). Nothing is read from them until a key frame is set.
    void ReadVideo(string path)
    {
        string python = VideoPython();
        if (python == null)
        {
            Status("the video tool is not set up: run Tools/video2motion.sh once (it installs Python 3.12, MediaPipe and the pose model)");
            return;
        }
        string name = string.Concat(System.IO.Path.GetFileNameWithoutExtension(path).Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_'));
        string outBase = VideoPath(name, "");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outBase));
        var args = new List<string> { ProjectSettings.GlobalizePath("res://Tools/video2motion.py"), path, "--out", outBase };
        if (videoLefty.ButtonPressed)
            args.Add("--left-handed");
        videoFile = System.IO.Path.GetFileName(path);
        pendingVideo = name;
        videoProgress = 0f;
        videoShown = -1f;
        Status($"importing {videoFile}…");
        videoRun = Task.Run(() =>
        {
            var info = new System.Diagnostics.ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string a in args)
                info.ArgumentList.Add(a);
            using var run = System.Diagnostics.Process.Start(info);
            Task<string> errorsRead = run.StandardError.ReadToEndAsync(); // both read at once: neither pipe fills
            var said = new System.Text.StringBuilder();
            string line;
            while ((line = run.StandardOutput.ReadLine()) != null)
            {
                string[] w = line.Split(' ');
                if (w.Length == 4 && w[0] == "VIDEO2MOTION" && w[1] == "progress" && float.TryParse(w[2], out float done) && float.TryParse(w[3], out float of) && of > 0f)
                    videoProgress = Mathf.Clamp(done / of, 0f, 1f);
                else
                    said.AppendLine(line);
            }
            string errors = errorsRead.Result;
            run.WaitForExit();
            return (run.ExitCode, said + "\n" + string.Join("\n", errors.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(2)));
        });
    }

    // How far the import has got, for the status line and the Pose Tracer.
    string VideoReading() => $"importing {videoFile}: {videoProgress * 100f:F0}% of its frames…";

    void CheckVideoRun()
    {
        PollPoseService();
        if (videoRun != null && !videoRun.IsCompleted && Mathf.Abs(videoProgress - videoShown) >= 0.01f)
        {
            videoShown = videoProgress;
            Status(VideoReading());
        }
        if (videoRun == null || !videoRun.IsCompleted)
            return;
        var (code, output) = videoRun.Result;
        videoRun = null;
        string said = string.Join("; ", output.Split('\n').Where(l => l.StartsWith("VIDEO2MOTION ")).Select(l => l["VIDEO2MOTION ".Length..].Trim()));
        if (code != 0 || !LoadVideo(pendingVideo))
        {
            Status($"the video could not be imported: {output.Trim().Split('\n').LastOrDefault()}");
            return;
        }
        Status($"{videoFile}: {said}. In the Pose Tracer: play it, set the key frames of the action (their joints are found as each is set), make the motion.");
        OpenVideoWindow();
        StartPoseService(); // ready by the time the first key frame is set
    }

    // A video imported before opened: its frames and the key frames set. False for none (or one
    // imported by the earlier version of the tool).
    bool LoadVideo(string name)
    {
        VideoTrack track = VideoTrack.Load(VideoPath(name, ".frames.json"));
        if (track == null)
            return false;
        videoTrack = track;
        videoName = name;
        videoScale = null;
        videoKeys = new List<VideoKey>();
        videoTurned = false;
        videoAction = null;
        if (System.IO.File.Exists(VideoPath(name, ".keys.json")))
        {
            using JsonDocument doc = JsonDocument.Parse(System.IO.File.ReadAllText(VideoPath(name, ".keys.json")));
            JsonElement r = doc.RootElement;
            videoAction = r.TryGetProperty("action", out JsonElement a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            videoTurned = r.TryGetProperty("turned", out JsonElement t) && t.ValueKind == JsonValueKind.True;
            static Vector2 V2(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle());
            foreach (JsonElement k in r.GetProperty("keys").EnumerateArray())
            {
                var key = new VideoKey { Frame = k.GetProperty("frame").GetInt32(), Name = k.GetProperty("name").GetString() };
                if (k.TryGetProperty("fixed", out JsonElement fx))
                    key.Fixed = fx.EnumerateObject().ToDictionary(j => j.Name, j => V2(j.Value));
                key.Face = k.TryGetProperty("face", out JsonElement fc) && fc.ValueKind == JsonValueKind.String ? fc.GetString() : null;
                key.Swapped = k.TryGetProperty("swapped", out JsonElement sw) && sw.ValueKind == JsonValueKind.True;
                if (k.TryGetProperty("deleted", out JsonElement del) && del.ValueKind == JsonValueKind.Array)
                    key.Deleted = del.EnumerateArray().Select(e => e.GetString()).ToHashSet();
                if (k.TryGetProperty("depth", out JsonElement dp) && dp.ValueKind == JsonValueKind.Object)
                    key.Depth = dp.EnumerateObject().ToDictionary(j => j.Name, j => j.Value.GetSingle());
                if (k.TryGetProperty("pose", out JsonElement pose) && pose.ValueKind == JsonValueKind.Object)
                    SetPose(key, pose);
                if (key.Frame >= 0 && key.Frame < track.Count)
                    videoKeys.Add(key);
            }
        }
        videoAction ??= StrokeActions.All[Mathf.Clamp(actionPicker.Selected, 0, StrokeActions.All.Length - 1)].Id;
        // A key frame whose joints were not found yet (the lab closed while looking): looked for again.
        foreach (VideoKey k in videoKeys.Where(k => !k.HasPose && !k.NotFound))
            FindPose(k);
        return true;
    }

    // MediaPipe's answer for a key frame (the service's, or as kept): its joints, or none found.
    static void SetPose(VideoKey key, JsonElement answer)
    {
        if (!answer.TryGetProperty("found", out JsonElement found) || !found.GetBoolean())
        {
            key.NotFound = true;
            return;
        }
        key.NotFound = false;
        key.Px = answer.GetProperty("px").EnumerateObject().ToDictionary(j => j.Name, j => new Vector2(j.Value[0].GetSingle(), j.Value[1].GetSingle()));
        key.Up = answer.GetProperty("up").EnumerateObject().ToDictionary(j => j.Name, j => new Vector3(j.Value[0].GetSingle(), j.Value[1].GetSingle(), j.Value[2].GetSingle()));
        key.Seen = answer.GetProperty("seen").EnumerateObject().ToDictionary(j => j.Name, j => j.Value.GetSingle());
    }

    // The key frames (with their joints as found), the action and the turn kept (user://video/<name>.keys.json).
    void SaveVideoKeys()
    {
        if (videoTrack == null)
            return;
        videoScale = null; // what was placed changes the body's size a little
        static JsonArray A(params float[] v) => new(v.Select(x => (JsonNode)x).ToArray());
        var keys = new JsonArray();
        foreach (VideoKey k in videoKeys.OrderBy(k => k.Frame))
        {
            var placed = new JsonObject();
            foreach (var (name, at) in k.Fixed)
                placed[name] = A(Mathf.Snapped(at.X, 0.1f), Mathf.Snapped(at.Y, 0.1f));
            JsonObject pose = null;
            if (k.HasPose)
            {
                var px = new JsonObject();
                var up = new JsonObject();
                var seen = new JsonObject();
                foreach (var (j, v) in k.Px)
                    px[j] = A(v.X, v.Y);
                foreach (var (j, v) in k.Up)
                    up[j] = A(v.X, v.Y, v.Z);
                foreach (var (j, v) in k.Seen)
                    seen[j] = v;
                pose = new JsonObject { ["found"] = true, ["px"] = px, ["up"] = up, ["seen"] = seen };
            }
            else if (k.NotFound)
                pose = new JsonObject { ["found"] = false };
            keys.Add(new JsonObject
            {
                ["frame"] = k.Frame, ["name"] = k.Name, ["fixed"] = placed, ["deleted"] = new JsonArray(k.Deleted.OrderBy(d => d).Select(d => (JsonNode)d).ToArray()),
                ["face"] = k.Face, ["swapped"] = k.Swapped,
                ["depth"] = new JsonObject(k.Depth.OrderBy(d => d.Key).Select(d => new KeyValuePair<string, JsonNode>(d.Key, Mathf.Snapped(d.Value, 0.001f)))),
                ["pose"] = pose,
            });
        }
        var root = new JsonObject { ["action"] = videoAction, ["turned"] = videoTurned, ["keys"] = keys };
        System.IO.File.WriteAllText(VideoPath(videoName, ".keys.json"), root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // ---------------------------------------------------------------- finding a frame's joints

    // The tool's pose service (video2motion.py --serve): MediaPipe's model loaded once, then a frame's
    // joints found as each key frame is set (about a second each), in the background.
    System.Diagnostics.Process poseService;
    readonly System.Collections.Concurrent.ConcurrentQueue<string> poseAnswers = new();
    readonly Dictionary<int, VideoKey> poseAsked = new();
    readonly List<VideoKey> poseWaiting = new(); // asked before the service was ready
    bool poseReady;
    int poseNext;

    void StartPoseService()
    {
        if (poseService != null && !poseService.HasExited)
            return;
        string python = VideoPython();
        if (python == null)
            return;
        var info = new System.Diagnostics.ProcessStartInfo(python)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        info.ArgumentList.Add(ProjectSettings.GlobalizePath("res://Tools/video2motion.py"));
        info.ArgumentList.Add("--serve");
        poseReady = false;
        poseService = System.Diagnostics.Process.Start(info);
        System.Diagnostics.Process service = poseService;
        service.ErrorDataReceived += (_, _) => { }; // MediaPipe's chatter, read so the pipe never fills
        service.BeginErrorReadLine();
        Task.Run(() =>
        {
            string line;
            while ((line = service.StandardOutput.ReadLine()) != null)
                poseAnswers.Enqueue(line);
        });
    }

    // A key frame's joints asked for (they come in PollPoseService). With two in the picture, the
    // player is the one whose hips are nearest where they were at the key frame before.
    void FindPose(VideoKey key)
    {
        key.Px = null;
        key.Up = null;
        key.Seen = null;
        key.NotFound = false;
        StartPoseService();
        if (!poseReady)
        {
            if (!poseWaiting.Contains(key))
                poseWaiting.Add(key);
            return;
        }
        VideoKey before = videoKeys.Where(k => k.HasPose && k != key).OrderBy(k => Mathf.Abs(k.Frame - key.Frame)).FirstOrDefault();
        var request = new JsonObject
        {
            ["id"] = ++poseNext,
            ["path"] = videoTrack.FramePath(key.Frame),
            ["pick"] = new[] { "auto", "near", "far", "left", "right" }[Mathf.Max(videoPick.Selected, 0)],
            ["hips"] = before == null ? null : new JsonArray(before.HipsPx.X, before.HipsPx.Y),
        };
        poseAsked[poseNext] = key;
        poseService.StandardInput.WriteLine(request.ToJsonString());
        poseService.StandardInput.Flush();
    }

    bool FindingPose => poseWaiting.Count > 0 || poseAsked.Count > 0;

    void PollPoseService()
    {
        while (poseAnswers.TryDequeue(out string line))
        {
            if (line.StartsWith("VIDEO2MOTION ready"))
            {
                poseReady = true;
                foreach (VideoKey k in poseWaiting.ToList())
                    FindPose(k);
                poseWaiting.Clear();
                continue;
            }
            if (!line.StartsWith("POSE "))
                continue;
            using JsonDocument doc = JsonDocument.Parse(line[5..]);
            JsonElement answer = doc.RootElement;
            if (!answer.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.Number || !poseAsked.Remove(id.GetInt32(), out VideoKey key))
                continue;
            if (!videoKeys.Contains(key))
                continue; // taken away while it was looked for
            SetPose(key, answer);
            SaveVideoKeys();
            videoWindow?.KeyFound(key);
        }
        if (poseService != null && poseService.HasExited && FindingPose)
        {
            poseAsked.Clear();
            poseWaiting.Clear();
            Status("the pose service stopped (MediaPipe): set the key frame again to look once more");
        }
    }

    public override void _ExitTree()
    {
        if (poseService != null && !poseService.HasExited)
        {
            try { poseService.Kill(); } catch (System.InvalidOperationException) { }
        }
    }

    // ---------------------------------------------------------------- a key frame's joints

    static HashSet<string> SwapSides(HashSet<string> names) =>
        names.Select(n => n.Length > 1 && (n[0] == 'l' || n[0] == 'r') && !VideoMoment.IsRacketMark(n) ? (n[0] == 'l' ? "r" : "l") + n[1..] : n).ToHashSet();

    static void SwapSides<T>(Dictionary<string, T> d)
    {
        foreach (string left in d.Keys.Where(k => k.StartsWith('l') && d.ContainsKey("r" + k[1..])).ToList())
        {
            string right = "r" + left[1..];
            (d[left], d[right]) = (d[right], d[left]);
        }
    }

    // A key frame's joints as the window shows them and the fit takes them: where each is in the
    // picture, in metres (about the middle of the hips, Y up) and how well seen; MediaPipe's left and
    // right swapped (the key frame's Swapped), and the player turned round (videoTurned: swapped,
    // and the depth mirrored through the picture's plane, so each joint stays where it is in it).
    (Dictionary<string, Vector2> Px, Dictionary<string, Vector3> Up, Dictionary<string, float> Seen) FrameJoints(VideoKey key)
    {
        var px = new Dictionary<string, Vector2>(key.Px);
        var seen = new Dictionary<string, float>(key.Seen);
        Vector3 middle = (key.Up["lhip"] + key.Up["rhip"]) / 2f;
        var up = key.Up.ToDictionary(kv => kv.Key, kv => kv.Value - middle);
        bool swapped = key.Swapped;
        if (videoTurned)
        {
            var (camX, camY, _) = FitCamera(up, px, seen);
            Vector3 c = -camX.Cross(camY);
            if (c.LengthSquared() > 1e-8f)
            {
                c = c.Normalized();
                foreach (string k in up.Keys.ToList())
                    up[k] -= 2f * up[k].Dot(c) * c;
            }
            swapped = !swapped;
        }
        if (swapped)
        {
            SwapSides(px);
            SwapSides(up);
            SwapSides(seen);
        }
        return (px, up, seen);
    }

    // The camera as seen from far (an affine one) that best puts the joints (metres) on their places
    // in the picture: the rows taking metres to pixels, and where the origin falls. A least-squares
    // fit over the joints seen well (all of them, too few of those).
    static (Vector3 CamX, Vector3 CamY, Vector2 At) FitCamera(Dictionary<string, Vector3> rel, Dictionary<string, Vector2> px, Dictionary<string, float> seen)
    {
        var use = rel.Keys.Where(k => px.ContainsKey(k) && seen.GetValueOrDefault(k) >= 0.5f).ToList();
        if (use.Count < 5)
            use = rel.Keys.Where(px.ContainsKey).ToList();
        var m = new double[4, 4];
        var bx = new double[4];
        var by = new double[4];
        foreach (string k in use)
        {
            Vector3 r = rel[k];
            double[] row = { r.X, r.Y, r.Z, 1.0 };
            for (int a = 0; a < 4; a++)
            {
                for (int b = 0; b < 4; b++)
                    m[a, b] += row[a] * row[b];
                bx[a] += row[a] * px[k].X;
                by[a] += row[a] * px[k].Y;
            }
        }
        for (int a = 0; a < 3; a++)
            m[a, a] += 1e-6; // steady when the joints lie nearly in a plane
        double[] wx = Solve(m, bx), wy = Solve(m, by);
        return (new Vector3((float)wx[0], (float)wx[1], (float)wx[2]), new Vector3((float)wy[0], (float)wy[1], (float)wy[2]), new Vector2((float)wx[3], (float)wy[3]));
    }

    // m x = b, by elimination (the larger pivot first).
    static double[] Solve(double[,] m0, double[] b0)
    {
        int n = b0.Length;
        var m = (double[,])m0.Clone();
        var b = (double[])b0.Clone();
        for (int c = 0; c < n; c++)
        {
            int p = c;
            for (int r = c + 1; r < n; r++)
                if (System.Math.Abs(m[r, c]) > System.Math.Abs(m[p, c]))
                    p = r;
            if (System.Math.Abs(m[p, c]) < 1e-12)
                return new double[n];
            for (int k = 0; k < n; k++)
                (m[c, k], m[p, k]) = (m[p, k], m[c, k]);
            (b[c], b[p]) = (b[p], b[c]);
            for (int r = 0; r < n; r++)
            {
                if (r == c)
                    continue;
                double f = m[r, c] / m[c, c];
                for (int k = c; k < n; k++)
                    m[r, k] -= f * m[c, k];
                b[r] -= f * b[c];
            }
        }
        return Enumerable.Range(0, n).Select(i => b[i] / m[i, i]).ToArray();
    }

    // The player's own frame at a moment (Y up, Z the way the hips and shoulders face, level; X to
    // the player's left): the turn taking metres into it.
    static Basis OwnFrameOf(Dictionary<string, Vector3> up)
    {
        Vector3 across = (up["lshoulder"] - up["rshoulder"]) + (up["lhip"] - up["rhip"]);
        across.Y = 0f;
        across = across.LengthSquared() > 1e-8f ? across.Normalized() : Vector3.Right;
        Vector3 forward = across.Cross(Vector3.Up);
        Vector3 left = Vector3.Up.Cross(forward);
        return new Basis(left, Vector3.Up, forward).Transposed();
    }

    // ---------------------------------------------------------------- the ball, the table, the net

    // Points of the scene marked on a frame (kept with the joints placed by hand, by these names).
    public const string BallMark = "ball";

    // The table's corners and the net's ends, as the player sees them (left: its left), where they
    // are in the lab (the player's end at -z, the table's middle the origin, +x the player's left).
    // Named as the video shows them instead (far: from the camera, left: the picture's), with the
    // player facing the camera, they are the same table turned half round: the fit tries both.
    // Two or more marked on a frame give the way the player faces the table, and where across it
    // the player stands (how far along the camera's line cannot be told from a picture: the lab's).
    static readonly (string Key, string Words, Vector3 At)[] TablePoints =
    {
        ("table_nl", "table: near left corner", new(TableSpec.HalfWidth, TableSpec.Height, -TableSpec.HalfLength)),
        ("table_nr", "table: near right corner", new(-TableSpec.HalfWidth, TableSpec.Height, -TableSpec.HalfLength)),
        ("table_fl", "table: far left corner", new(TableSpec.HalfWidth, TableSpec.Height, TableSpec.HalfLength)),
        ("table_fr", "table: far right corner", new(-TableSpec.HalfWidth, TableSpec.Height, TableSpec.HalfLength)),
        ("net_l", "net: left post's top", new(TableSpec.NetHalfWidth, TableSpec.NetTop, 0f)),
        ("net_r", "net: right post's top", new(-TableSpec.NetHalfWidth, TableSpec.NetTop, 0f)),
    };

    static bool IsTablePoint(string key) => TablePoints.Any(t => t.Key == key);

    // The table's points for a key frame: its own (two or more), else those of the key frame nearest
    // it that has (the camera the same); and which key frame they are from.
    (Dictionary<string, Vector2> Marks, VideoKey From) TableMarks(VideoKey key)
    {
        static Dictionary<string, Vector2> Of(VideoKey k) => k.Fixed.Where(f => IsTablePoint(f.Key)).ToDictionary(f => f.Key, f => f.Value);
        if (Of(key).Count >= 2)
            return (Of(key), key);
        VideoKey from = videoKeys.Where(k => k != key && Of(k).Count >= 2).OrderBy(k => Mathf.Abs(k.Frame - key.Frame)).FirstOrDefault();
        return from == null ? (new Dictionary<string, Vector2>(), null) : (Of(from), from);
    }

    // A table point named from the other end (near and far, left and right swapped): the table
    // turned half round about its middle.
    static Vector3 OtherEnd(Vector3 p) => new(-p.X, p.Y, -p.Z);

    // What the table's points make of a key frame: how the video's own frame is turned in the lab,
    // where the hips stand (x, z), how far the player turns from facing the table straight
    // (degrees, + to its left), how far the points miss (cm at the player), whether their names were
    // read from the other end, and the video's camera found from them (lab; null: not found, fewer
    // than four points) with its up.
    record TableFit(Basis Turn, Vector2 Hips, float Degrees, float MissCm, bool OtherEnd, Vector3? Eye = null, Vector3 EyeUp = default);

    // The table fit for a key frame's moment: the points fall where they were clicked, named as the
    // player sees them or as the video shows them (the player facing the camera: the table turned
    // half round), whichever puts the player at its own end. Four or more: the camera found from the
    // table as it is (in perspective: the net nearer the camera looks bigger), the player's end the
    // one its size in the picture puts it at. Two or three: as seen from far, the naming that puts
    // the hips nearer the lab's place kept. Null for fewer than two points.
    TableFit FitTable(VideoMoment m, Dictionary<string, Vector2> marks, float scale, Vector2 place, float hipsY)
    {
        var points = TablePoints.Where(t => marks.ContainsKey(t.Key)).Select(t => (t.At, Px: marks[t.Key])).ToList();
        if (points.Count < 2)
            return null;
        if (points.Count >= 4 && videoTrack != null && FitTableSeen(m, points, scale, place) is TableFit seen)
            return seen;
        var named = FitTableFar(m, points, scale, place, hipsY);
        var turned = FitTableFar(m, points.Select(p => (OtherEnd(p.At), p.Px)).ToList(), scale, place, hipsY);
        return turned.Cost < named.Cost
            ? new TableFit(turned.Turn, turned.Hips, turned.Degrees, turned.MissCm, true)
            : new TableFit(named.Turn, named.Hips, named.Degrees, named.MissCm, false);
    }

    // The table fit with the camera found from the table's points (TableCamera): the hips on their
    // line of sight, where it passes nearest the lab's place; the own frame turned so the video's
    // camera (seen from far) matches the found one's view at the hips. The names read as given, or
    // from the other end when the player's size in the picture puts it at the far end of them.
    TableFit FitTableSeen(VideoMoment m, List<(Vector3 At, Vector2 Px)> points, float scale, Vector2 place)
    {
        if (TableCamera(points, videoTrack.Size) is not var (eye, look, focal, missPx))
            return null;
        Vector2 centre = videoTrack.Size / 2f;
        Vector3 ray = look * new Vector3((m.HipsImage.X - centre.X) / focal, -(m.HipsImage.Y - centre.Y) / focal, -1f); // a metre deep a step
        float sizeDepth = focal * scale / m.PixelsPerMetre;
        bool other = (eye + ray * sizeDepth).Z > 0f;
        if (other)
        {
            var half = new Basis(Vector3.Up, Mathf.Pi);
            (eye, look, ray) = (half * eye, half * look, half * ray);
        }
        var flat = new Vector2(ray.X, ray.Z);
        float depth = Mathf.Max((place - new Vector2(eye.X, eye.Z)).Dot(flat) / Mathf.Max(flat.LengthSquared(), 1e-6f), 0.5f);
        Vector3 hips = eye + ray * depth;
        // The found camera's rows at the hips (pixels per lab metre), and the yaw taking the video's
        // own-frame rows nearest them.
        Vector3 right = look * Vector3.Right, up = look * Vector3.Up, forward = look * Vector3.Forward;
        Vector3 local = look.Transposed() * (hips - eye);
        float k = focal / depth;
        Vector3 rowX = k * (right - local.X / depth * forward), rowY = k * (-up + local.Y / depth * forward);
        Vector3 a = m.CamX / scale, b = m.CamY / scale;
        float yaw = Mathf.Atan2(a.Z * rowX.X - a.X * rowX.Z + b.Z * rowY.X - b.X * rowY.Z, a.X * rowX.X + a.Z * rowX.Z + b.X * rowY.X + b.Z * rowY.Z);
        return new TableFit(new Basis(Vector3.Up, yaw), new Vector2(hips.X, hips.Z), Mathf.RadToDeg(yaw), missPx / k * 100f, other, eye, up);
    }

    // The camera that sees the table's points where they were clicked (a pinhole centred on the
    // picture): where it is, its turn (columns: right, up, back), its focal length (px) and how far
    // the points miss (rms, px). Least squares (Levenberg-Marquardt) from starts all round the
    // table, held level (no roll) and near a normal lens; null when no start finds it.
    static (Vector3 Eye, Basis Look, float Focal, float MissPx)? TableCamera(List<(Vector3 At, Vector2 Px)> points, Vector2 size)
    {
        static double[,] Rotation(double yaw, double pitch, double roll)
        {
            double cy = System.Math.Cos(yaw), sy = System.Math.Sin(yaw), cp = System.Math.Cos(pitch), sp = System.Math.Sin(pitch), cr = System.Math.Cos(roll), sr = System.Math.Sin(roll);
            // Ry * Rx * Rz
            return new[,]
            {
                { cy * cr + sy * sp * sr, -cy * sr + sy * sp * cr, sy * cp },
                { cp * sr, cp * cr, -sp },
                { -sy * cr + cy * sp * sr, sy * sr + cy * sp * cr, cy * cp },
            };
        }
        int n = points.Count * 2 + 2;
        double[] Residual(double[] q)
        {
            var r = new double[n];
            double[,] rot = Rotation(q[3], q[4], q[5]);
            double f = size.X * System.Math.Exp(q[6]);
            for (int i = 0; i < points.Count; i++)
            {
                double dx = points[i].At.X - q[0], dy = points[i].At.Y - q[1], dz = points[i].At.Z - q[2];
                double lx = rot[0, 0] * dx + rot[1, 0] * dy + rot[2, 0] * dz;
                double ly = rot[0, 1] * dx + rot[1, 1] * dy + rot[2, 1] * dz;
                double depth = -(rot[0, 2] * dx + rot[1, 2] * dy + rot[2, 2] * dz);
                if (depth < 0.1)
                {
                    r[2 * i] = r[2 * i + 1] = 1e4; // behind the camera
                    continue;
                }
                r[2 * i] = size.X / 2.0 + f * lx / depth - points[i].Px.X;
                r[2 * i + 1] = size.Y / 2.0 - f * ly / depth - points[i].Px.Y;
            }
            r[n - 2] = 200.0 * q[5]; // level
            r[n - 1] = 100.0 * q[6]; // a focal length about the picture's width
            return r;
        }
        static double Squared(double[] r) => r.Sum(v => v * v);
        double bestCost = double.MaxValue;
        double[] best = null;
        for (int start = 0; start < 8; start++)
        {
            double a = start * System.Math.PI / 4.0;
            var q = new[] { 3.0 * System.Math.Sin(a), 1.3, 3.0 * System.Math.Cos(a), a, -0.15, 0.0, 0.0 };
            double[] r = Residual(q);
            double cost = Squared(r), lambda = 1e-2;
            for (int iteration = 0; iteration < 200 && lambda < 1e8; iteration++)
            {
                var jac = new double[n, 7];
                for (int j = 0; j < 7; j++)
                {
                    double[] moved = (double[])q.Clone();
                    moved[j] += 1e-6;
                    double[] rj = Residual(moved);
                    for (int i = 0; i < n; i++)
                        jac[i, j] = (rj[i] - r[i]) / 1e-6;
                }
                var normal = new double[7, 7];
                var grad = new double[7];
                for (int x = 0; x < 7; x++)
                {
                    for (int y = 0; y < 7; y++)
                        for (int i = 0; i < n; i++)
                            normal[x, y] += jac[i, x] * jac[i, y];
                    for (int i = 0; i < n; i++)
                        grad[x] -= jac[i, x] * r[i];
                }
                for (int x = 0; x < 7; x++)
                    normal[x, x] += lambda * normal[x, x] + 1e-9;
                double[] dq = Solve(normal, grad);
                double[] tried = q.Select((v, i) => v + dq[i]).ToArray();
                double[] rt = Residual(tried);
                double triedCost = Squared(rt);
                if (triedCost < cost)
                    (q, r, cost, lambda) = (tried, rt, triedCost, lambda * 0.3);
                else
                    lambda *= 10.0;
            }
            if (cost < bestCost)
                (bestCost, best) = (cost, q);
        }
        if (best == null)
            return null;
        double[,] m = Rotation(best[3], best[4], best[5]);
        var look = new Basis(new Vector3((float)m[0, 0], (float)m[1, 0], (float)m[2, 0]), new Vector3((float)m[0, 1], (float)m[1, 1], (float)m[2, 1]), new Vector3((float)m[0, 2], (float)m[1, 2], (float)m[2, 2]));
        double[] miss = Residual(best);
        if (miss.Take(points.Count * 2).Any(v => v >= 1e4))
            return null;
        float rms = (float)System.Math.Sqrt(miss.Take(points.Count * 2).Sum(v => v * v) / points.Count);
        return (new Vector3((float)best[0], (float)best[1], (float)best[2]), look, size.X * (float)System.Math.Exp(best[6]), rms);
    }

    // The table fit seen from far (the video's camera as it is): each turn tried (by degrees, then
    // finer), the hips for it by least squares (held near the lab's place along the camera's line,
    // which such a camera cannot tell), the turn that misses least kept; its cost with the hold.
    (float Cost, Basis Turn, Vector2 Hips, float Degrees, float MissCm) FitTableFar(VideoMoment m, List<(Vector3 At, Vector2 Px)> marked, float scale, Vector2 place, float hipsY)
    {
        var points = marked.Select(p => (p.At, Px: p.Px - m.HipsImage)).ToList();
        float hold = 0.15f * m.PixelsPerMetre / scale; // a metre off the lab's place counts as 15 cm on the picture
        (float Cost, Vector2 Hips) Try(float degrees)
        {
            var r = new Basis(Vector3.Up, Mathf.DegToRad(degrees));
            Vector3 a = r * m.CamX / scale, b = r * m.CamY / scale;
            // Rows of a (h.x, h.z) = rhs, for each point and each picture axis, and the hold.
            var rows = new List<(float X, float Z, float R)>();
            foreach (var (at, px) in points)
            {
                rows.Add((a.X, a.Z, a.Dot(at) - px.X - a.Y * hipsY));
                rows.Add((b.X, b.Z, b.Dot(at) - px.Y - b.Y * hipsY));
            }
            rows.Add((hold, 0f, hold * place.X));
            rows.Add((0f, hold, hold * place.Y));
            float sxx = 0f, sxz = 0f, szz = 0f, sxr = 0f, szr = 0f;
            foreach (var (x, z, rhs) in rows)
            {
                sxx += x * x; sxz += x * z; szz += z * z; sxr += x * rhs; szr += z * rhs;
            }
            float det = sxx * szz - sxz * sxz;
            if (Mathf.Abs(det) < 1e-9f)
                return (float.MaxValue, place);
            var h = new Vector2((szz * sxr - sxz * szr) / det, (sxx * szr - sxz * sxr) / det);
            float cost = rows.Sum(row => Mathf.Pow(row.X * h.X + row.Z * h.Y - row.R, 2f));
            return (cost, h);
        }
        float best = 0f;
        (float Cost, Vector2 Hips) bestFit = (float.MaxValue, place);
        for (float d = -180f; d < 180f; d += 1f)
            if (Try(d) is var t && t.Cost < bestFit.Cost)
                (best, bestFit) = (d, t);
        for (float d = best - 1f; d <= best + 1f; d += 0.1f)
            if (Try(d) is var t && t.Cost < bestFit.Cost)
                (best, bestFit) = (d, t);
        var turn = new Basis(Vector3.Up, Mathf.DegToRad(best));
        // How far the points miss on the picture (cm at the player's distance).
        float miss = Mathf.Sqrt(points.Average(p =>
        {
            Vector3 rel = turn.Inverse() * (p.At - new Vector3(bestFit.Hips.X, hipsY, bestFit.Hips.Y)) / scale;
            return (new Vector2(m.CamX.Dot(rel), m.CamY.Dot(rel)) - p.Px).LengthSquared();
        })) / m.PixelsPerMetre * scale * 100f;
        return (bestFit.Cost, turn, bestFit.Hips, best, miss);
    }

    // The ball marked on a key frame, in the lab: on the picture where it was clicked, as far from the
    // camera as the hand nearest it on the picture (a ball is held, tossed or hit near a hand).
    Vector3? MarkedBall(VideoMoment m, float scale, float hipsHeight)
    {
        if (!m.Fixed.TryGetValue(BallMark, out Vector2 at))
            return null;
        Dictionary<string, Vector3> rel = m.FittedJoints();
        string hand = new[] { "lwrist", "rwrist" }.OrderBy(h => (m.Project(rel[h]) - at).LengthSquared()).First();
        Vector3 ball = rel[hand] + m.Unproject(at - m.Project(rel[hand]));
        return VideoHips(m, scale, hipsHeight) + ball * scale;
    }

    // A step's ball where it was marked on its key frame (not the contact's: that one is on the racket).
    void PutMarkedBall(int i)
    {
        PoseSequence.Step st = sequence.Steps[i];
        if (st.Meets || MomentOfStep(i) is not VideoMoment m || MarkedBall(m, KeyScale(), st.Values.GetValueOrDefault("hips.height", DefaultValue("hips.height"))) is not Vector3 ball)
            return;
        st.Values["ball.x"] = ball.X;
        st.Values["ball.y"] = ball.Y;
        st.Values["ball.z"] = ball.Z;
        st.Values["ball.show"] = 1f;
    }

    // The key frames whose joints were found, in time order (the motion is made of these).
    List<VideoKey> PosedKeys() => videoKeys.Where(k => k.HasPose).OrderBy(k => k.Frame).ToList();

    // A key frame's pose for the fit: in the own frame of the first key frame (the motion's), the hips
    // moved as they moved across the picture since it (the picture's move, at its camera), the camera
    // fitted to it, and what was placed on it by hand.
    // A joint deleted at a key frame is put between the key frames around it that have it (by
    // time; the one there is, at the ends), placed on the picture by the key frame's camera.
    // With the table's points marked (on it, or on the key frame nearest that has), the own frame is
    // turned to the table and the hips placed across it as they fall.
    VideoMoment MomentOf(VideoKey key, bool guess = true, bool toTable = true)
    {
        VideoKey first = PosedKeys().First();
        var (firstPx, firstUp, firstSeen) = FrameJoints(first);
        Basis own = OwnFrameOf(firstUp);
        var (px, up, seen) = FrameJoints(key);
        var rel = up.ToDictionary(kv => kv.Key, kv => own * kv.Value);
        foreach (string d in key.Deleted)
            if (seen.ContainsKey(d))
                seen[d] = 0f; // not used for the camera either
        var (camX, camY, at) = FitCamera(rel, px, seen);
        var guessed = new HashSet<string>();
        if (guess)
        {
            List<VideoKey> keys = PosedKeys();
            foreach (string d in key.Deleted.Where(rel.ContainsKey))
            {
                VideoKey before = keys.LastOrDefault(k => k.Frame < key.Frame && !k.Deleted.Contains(d));
                VideoKey after = keys.FirstOrDefault(k => k.Frame > key.Frame && !k.Deleted.Contains(d));
                if (before == null && after == null)
                    continue;
                Vector3 Of(VideoKey k) => MomentOf(k, guess: false, toTable: false).FittedJoints()[d];
                Vector3 there = before == null ? Of(after) : after == null ? Of(before)
                    : Of(before).Lerp(Of(after), (videoTrack.T[key.Frame] - videoTrack.T[before.Frame]) / Mathf.Max(videoTrack.T[after.Frame] - videoTrack.T[before.Frame], 1e-3f));
                rel[d] = there;
                px[d] = at + new Vector2(camX.Dot(there), camY.Dot(there));
                guessed.Add(d);
            }
        }
        var (startX, startY, _) = FitCamera(firstUp.ToDictionary(kv => kv.Key, kv => own * kv.Value), firstPx, firstSeen);
        var start = new VideoMoment { CamX = startX, CamY = startY };
        Vector3 move = start.Unproject((px["lhip"] + px["rhip"]) / 2f - (firstPx["lhip"] + firstPx["rhip"]) / 2f);
        move.Y = 0f;
        var moment = new VideoMoment
        {
            Name = key.Name, T = videoTrack.T[key.Frame], Joints = rel, Move = move, Seen = seen, Frame = videoTrack.FramePath(key.Frame),
            Image = px, CamX = camX, CamY = camY, HipsImage = at, Fixed = key.Fixed, Face = key.Face,
            Deleted = key.Deleted, Guessed = guessed, Depth = key.Depth,
        };
        if (toTable && TableFitOf(key, moment) is var (fit, _))
        {
            moment.Turn(fit.Turn);
            moment.HipsAt = fit.Hips;
            moment.Eye = fit.Eye;
            moment.EyeUp = fit.EyeUp;
        }
        return moment;
    }

    // A key frame's table fit (its moment as found) and the key frame the points are from, or null.
    (TableFit Fit, VideoKey From)? TableFitOf(VideoKey key, VideoMoment moment = null)
    {
        var (marks, from) = TableMarks(key);
        if (from == null)
            return null;
        moment ??= MomentOf(key, toTable: false);
        float scale = KeyScale();
        Vector2 place = new(VideoReadyPlace.X + moment.Move.X, VideoReadyPlace.Y + moment.Move.Z);
        return FitTable(moment, marks, scale, place, DefaultValue("hips.height")) is TableFit fit ? (fit, from) : null;
    }

    // The action the key frames are for, and its steps (its template's: the key frames to set).
    StrokeActions.Action VideoActionOf() => StrokeActions.Find(videoAction) ?? StrokeActions.All[0];
    string[] VideoStepNames() => VideoActionOf().Template.Steps.Select(k => k.Name).Distinct().ToArray();

    float KeyScale()
    {
        videoScale ??= VideoScale(PosedKeys().Select(k => MomentOf(k, toTable: false)).ToList()); // (sizes: the table's turn changes none)
        return videoScale.Value;
    }

    // ---------------------------------------------------------------- the motion

    // The motion made from the key frames: each fitted (from the one before), timed as the video,
    // the ball put on the contact (and a serve's toss), then played to review.
    async Task MakeVideoMotion()
    {
        if (videoTrack == null || PosedKeys().Count < 2 || FindingPose)
        {
            Status(FindingPose ? "the key frames' joints are still being found: a moment" : "set at least two key frames (with the player found in them) first");
            return;
        }
        StrokeActions.Action action = VideoActionOf();
        bool forehand = !action.Template.Backhand, serve = action.Template.Serve;
        List<VideoKey> keys = PosedKeys();
        videoScale = null;
        float scale = KeyScale();
        playing = false;
        var seq = new PoseSequence { Name = $"{action.Id}_{videoName}", Grip = grip.ToString() };
        Dictionary<string, float> previous = null;
        float lastT = videoTrack.T[keys[0].Frame];
        var report = new List<string>();
        foreach (VideoKey key in keys)
        {
            Status($"making {StrokeActions.Label(action)} from {videoName}: fitting \"{key.Name}\" ({seq.Steps.Count + 1} of {keys.Count})…");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            VideoMoment m = MomentOf(key);
            (Dictionary<string, float> fitted, float error) = FitMoment(m, scale, forehand, previous);
            report.Add($"{key.Name} {error * 100f:F1} cm");
            seq.Steps.Add(new PoseSequence.Step
            {
                Name = key.Name,
                Duration = seq.Steps.Count == 0 ? 0f : Mathf.Max(m.T - lastT, 0.04f),
                Values = fitted,
                Video = $"{videoName}@{key.Frame}",
            });
            previous = fitted;
            lastT = m.T;
        }
        sequence = seq;
        sequenceName.Text = seq.Name;
        step = 0;
        RefreshSteps();
        // The contact step meets the ball: on the middle of the face towards the table (the step
        // named for it: "contact", "contact under the ball"...).
        int c = seq.Steps.FindIndex(s => s.Name.Contains("contact"));
        if (c >= 0)
        {
            seq.Steps[c].Meets = true;
            BallOnFace(c, serve);
            if (serve)
                PlaceServeBall(seq, c);
            RefreshSteps();
        }
        for (int i = 0; i < seq.Steps.Count; i++)
            PutMarkedBall(i); // a ball marked on a key frame: where it is in that step
        PickStep(0);
        playTime = 0f;
        if (!playing)
            TogglePlay();
        Status($"{seq.Name}: {seq.Steps.Count} steps made from the video, the rig's joints off the video's by {string.Join(", ", report)} on average. "
            + "It plays to review: pick a step to see it on its key frame in the Pose Tracer. When it is right, give it a name and Save.");
    }

    // ---------------------------------------------------------------- the lab kept in step

    // The lab shows the video's motion: its steps are this video's key frames.
    bool IsVideoMotion => sequence.Steps.Count > 0 && sequence.Steps.All(s => s.Video != null && s.Video.StartsWith(videoName + "@"));

    static int FrameOfStep(PoseSequence.Step s) => s.Video is string link && int.TryParse(link[(link.LastIndexOf('@') + 1)..], out int f) ? f : -1;

    // A key frame's step fitted to it (made where there is none, in time order), picked: so the lab
    // shows what the Pose Tracer does. The lab's motion becomes the video's when it is not.
    void SyncKeyStep(VideoKey key)
    {
        if (videoTrack == null || !key.HasPose || !videoKeys.Contains(key))
            return;
        playing = false;
        if (!IsVideoMotion)
        {
            // The lab's motion becomes the video's: a step for every key frame with its joints.
            sequence = new PoseSequence { Name = $"{VideoActionOf().Id}_{videoName}", Grip = grip.ToString() };
            sequenceName.Text = sequence.Name;
            step = -1;
            foreach (VideoKey other in PosedKeys().Where(k => k != key))
                FitKeyStep(other);
        }
        PickStep(FitKeyStep(key));
    }

    // A key frame's step fitted (made where there is none, in time order); its index.
    int FitKeyStep(VideoKey key)
    {
        int i = StepOfKey(key);
        if (i < 0)
        {
            i = sequence.Steps.Count(s => FrameOfStep(s) < key.Frame);
            Dictionary<string, float> start = i > 0 ? sequence.Steps[i - 1].Values : i < sequence.Steps.Count ? sequence.Steps[i].Values : DefaultPose();
            sequence.Steps.Insert(i, new PoseSequence.Step { Name = key.Name, Video = $"{videoName}@{key.Frame}", Values = new Dictionary<string, float>(start) });
        }
        sequence.Steps[i].Name = key.Name;
        sequence.Steps[i].Meets = key.Name.Contains("contact");
        RetimeSteps();
        RefreshSteps();
        RefitStep(i);
        return StepOfKey(key);
    }

    // A key frame taken away: its step too.
    void RemoveKeyStep(VideoKey key)
    {
        int i = IsVideoMotion ? StepOfKey(key) : -1;
        if (i < 0)
            return;
        playing = false;
        sequence.Steps.RemoveAt(i);
        RetimeSteps();
        step = Mathf.Min(step, sequence.Steps.Count - 1);
        RefreshSteps();
        if (step >= 0)
            PickStep(step);
    }

    // The steps timed as the video: each from the key frame before.
    void RetimeSteps()
    {
        for (int i = 0; i < sequence.Steps.Count; i++)
            sequence.Steps[i].Duration = i == 0 ? 0f : Mathf.Max(videoTrack.T[FrameOfStep(sequence.Steps[i])] - videoTrack.T[FrameOfStep(sequence.Steps[i - 1])], 0.04f);
    }

    // All the key frames' steps fitted again (the player turned round, or MediaPipe's sides swapped).
    void SyncAllKeys()
    {
        videoScale = null;
        foreach (VideoKey k in PosedKeys())
            SyncKeyStep(k);
    }

    // The video key frame a step was made from (the reading opened if it is not), or null.
    VideoMoment MomentOfStep(int i)
    {
        if (i < 0 || i >= sequence.Steps.Count || sequence.Steps[i].Video is not string link)
            return null;
        int at = link.LastIndexOf('@');
        if (at < 0 || !int.TryParse(link[(at + 1)..], out int frame))
            return null;
        string name = link[..at];
        if ((videoTrack == null || videoName != name) && !LoadVideo(name))
            return null;
        VideoKey key = videoKeys.FirstOrDefault(k => k.Frame == frame);
        return key == null || !key.HasPose ? null : MomentOf(key);
    }

    // The step made from a key frame (or -1).
    int StepOfKey(VideoKey key) => sequence.Steps.FindIndex(s => s.Video == $"{videoName}@{key.Frame}");

    // The step fitted to its key frame again (with what was placed on it): the fitted values
    // replaced, the rest of the step (the ball, the free hand's palm, its timing) kept. From the
    // step before, as the motion was made.
    void RefitStep(int i)
    {
        if (MomentOfStep(i) is not VideoMoment m)
            return;
        StrokeActions.Action action = VideoActionOf();
        float scale = KeyScale();
        PoseSequence.Step st = sequence.Steps[i];
        Dictionary<string, float> before = i > 0 ? sequence.Steps[i - 1].Values : null;
        var kept = new Dictionary<string, float>(st.Values);
        (Dictionary<string, float> fitted, float error) = FitMoment(m, scale, !action.Template.Backhand, before);
        foreach (string k in FitSet)
            if (fitted.TryGetValue(k, out float v))
                kept[k] = v;
        st.Values = kept;
        if (st.Meets)
            BallOnFace(i, action.Template.Serve); // (before the step is shown: see there)
        else
            PutMarkedBall(i);
        RefreshSteps();
        PickStep(i);
        Status($"step {i + 1} \"{st.Name}\" fitted to its key frame again: the rig's joints off the video's by {error * 100f:F1} cm on average");
    }

    // The contact step's ball put on the middle of the face towards the table, the racket where
    // the fit put it; then the step picked. (Posed with the contact lock off: on, it would first
    // pull the racket, and the arm with it, to the ball the step had before (on a new step the
    // step before's: a tossed ball, high up), and the ball would then be put there.)
    void BallOnFace(int c, bool serve)
    {
        bool wasPosingOther = posingOther;
        posingOther = true;
        pose = new Dictionary<string, float>(sequence.Steps[c].Values);
        Apply();
        float side = RacketBasis.Z.Z >= 0f ? 1f : -1f;
        Vector3 there = O(paddle.GlobalTransform * new Vector3(HeadCentre.X, HeadCentre.Y, side * (FaceOut + Ball.Radius)));
        Hold("ball.x", there.X);
        Hold("ball.y", there.Y);
        Hold("ball.z", there.Z);
        pose["ball.show"] = serve ? 1f : pose.GetValueOrDefault("ball.show");
        posingOther = wasPosingOther;
        sequence.Steps[c].Values = new Dictionary<string, float>(pose);
        PickStep(c);
    }

    // ---------------------------------------------------------------- the fit

    // Where the ready moment is put: the templates' ready place (the table's position in the video
    // is not known), the rest moved from there as the hips moved in the video.
    static readonly Vector2 VideoReadyPlace = new(0.1f, -1.85f);

    // The shakehand hand's knuckles (index and little finger) in paddle space, as the Paddle's hand
    // has them: with the wrist they give the hand's direction and its palm's way, as the video's
    // wrist, index and pinky do.
    static readonly Vector3 IndexKnuckle = new(0.004f, -0.1f, 0.026f), LittleKnuckle = new(0.02f, -0.146f, 0.026f);

    static readonly string[] RacketTurns = { "racket.open", "racket.noseUp", "racket.noseForward", "racket.turn" };

    // The blade's points a frame can mark, in paddle space: the tip (the top of the blade) and the
    // rim at its widest, by the thumb (-X) and across from it (+X).
    static (string Name, Vector3 Local)[] RacketMarks => new[]
    {
        (VideoMoment.RacketTip, Paddle.Outline[0]),
        (VideoMoment.RacketThumb, new Vector3(-HeadHalf.X, HeadCentre.Y, 0f)),
        (VideoMoment.RacketFar, new Vector3(HeadHalf.X, HeadCentre.Y, 0f)),
    };

    // Whether the racket's rim goes into the body or the table (as the lab's check has it).
    bool RacketThrough()
    {
        Collide.Capsule[] bodyParts = BodyCapsules();
        foreach (Vector3 point in Paddle.Outline)
        {
            Vector3 p = paddle.GlobalTransform * point;
            if (Collide.OutOfTable(p, Paddle.RimClearance).LengthSquared() > 1e-6f
                || Collide.OutOfAll(p, Paddle.RimClearance, bodyParts, table: false).LengthSquared() > 1e-6f)
                return true;
        }
        return false;
    }

    // The values the fit tunes (and a refit replaces; the rest of a step is kept).
    static readonly string[] FitMetres = { "body.x", "body.z", "hips.height", "racket.x", "racket.y", "racket.z", "lhand.x", "lhand.y", "lhand.z",
                                           "lfoot.x", "lfoot.z", "rfoot.x", "rfoot.z", "rshoulder.up", "rshoulder.forward", "lshoulder.up", "lshoulder.forward" };
    static readonly string[] FitDegrees = { "hips.turn", "hips.lean", "hips.side", "waist.turn", "waist.bend", "waist.side", "racket.open", "racket.noseUp",
                                            "racket.noseForward", "racket.turn", "relbow.swivel", "lelbow.swivel", "lknee.swivel", "rknee.swivel" };
    static readonly string[] FitSet = FitMetres.Concat(FitDegrees).Concat(new[] { "racket.backhand", "lfoot.turn", "rfoot.turn", "lfoot.lift", "rfoot.lift" }).ToArray();

    // Where a moment's hips go in the lab (own frame): the templates' ready place moved as the hips
    // moved in the video, as high as the ankles on the floor make them (the legs not in the
    // picture: unseenHeight). The video's joints are put round it, scaled to the rig.
    static Vector3 VideoHips(VideoMoment m, float scale, float unseenHeight)
    {
        Dictionary<string, Vector3> rel = m.FittedJoints();
        float ankleLow = Mathf.Min(rel["lankle"].Y, rel["rankle"].Y);
        float y = m.LegsSeen ? Leg.AnkleHeight - scale * ankleLow : unseenHeight;
        return m.HipsAt is Vector2 at ? new Vector3(at.X, y, at.Y) : new Vector3(VideoReadyPlace.X + m.Move.X, y, VideoReadyPlace.Y + m.Move.Z);
    }

    // How much the video's body is scaled to fit the rig: by the legs and trunk when the legs
    // are in the picture, else by the trunk and arms.
    float VideoScale(List<VideoMoment> moments)
    {
        bool legs = moments.All(m => m.LegsSeen);
        return RigSize(legs) / Mathf.Max(moments.Average(m => Size(m.FittedJoints(), legs)), 0.1f);
    }

    // Each joint's weight in the fit (the racket hand most).
    static readonly (string Video, float Weight)[] FitJoints =
    {
        ("lhip", 2f), ("rhip", 2f), ("lknee", 1.5f), ("rknee", 1.5f), ("lankle", 1f), ("rankle", 1f),
        ("lshoulder", 2f), ("rshoulder", 2f), ("lelbow", 1.5f), ("relbow", 2f), ("lwrist", 1.5f), ("rwrist", 3f), ("nose", 0.3f),
    };

    Dictionary<string, Vector3> RigJoints()
    {
        Vector3 head = chest.GlobalTransform * new Vector3(0f, 0.53f, 0f);
        return new Dictionary<string, Vector3>
        {
            ["lhip"] = O(legs[0].HipPoint), ["rhip"] = O(legs[1].HipPoint), ["lknee"] = O(legs[0].KneePoint), ["rknee"] = O(legs[1].KneePoint),
            ["lankle"] = O(legs[0].AnklePoint), ["rankle"] = O(legs[1].AnklePoint),
            ["lshoulder"] = O(leftArm.ShoulderPoint), ["rshoulder"] = O(rightArm.ShoulderPoint), ["lelbow"] = O(leftArm.ElbowPoint), ["relbow"] = O(rightArm.ElbowPoint),
            ["lwrist"] = O(leftArm.WristPoint), ["rwrist"] = O(rightArm.WristPoint),
            ["nose"] = O(head + chest.GlobalBasis.Z.Normalized() * 0.1f),
        };
    }

    // The rig's size: legs (hip, knee, ankle) and trunk (hips to shoulders), at the lab's default pose.
    float RigSize(bool legs)
    {
        Dictionary<string, float> keep = pose;
        pose = DefaultPose();
        Apply();
        float size = Size(RigJoints(), legs);
        pose = keep;
        Apply();
        return size;
    }

    // A body's size to scale by: its legs and trunk, or (the legs not in the picture) its trunk,
    // shoulders and arms.
    static float Size(Dictionary<string, Vector3> j, bool legs)
    {
        float trunk = ((j["lhip"] + j["rhip"]) / 2).DistanceTo((j["lshoulder"] + j["rshoulder"]) / 2);
        if (legs)
            return trunk + 0.5f * (j["lhip"].DistanceTo(j["lknee"]) + j["lknee"].DistanceTo(j["lankle"]) + j["rhip"].DistanceTo(j["rknee"]) + j["rknee"].DistanceTo(j["rankle"]));
        return trunk + j["lshoulder"].DistanceTo(j["rshoulder"])
            + 0.5f * (j["lshoulder"].DistanceTo(j["lelbow"]) + j["lelbow"].DistanceTo(j["lwrist"]) + j["rshoulder"].DistanceTo(j["relbow"]) + j["relbow"].DistanceTo(j["rwrist"]));
    }

    // A serve's ball, as the game's serve reads it (ServeMotion): shown on the open palm at the start,
    // highest at the toss (35 cm over the hand: the rules ask 16 cm at least), coming down to the
    // racket before the contact, on the racket at the contact, and gone after it (struck). The game
    // tosses it for real, to fall onto the racket at the contact.
    void PlaceServeBall(PoseSequence seq, int contact)
    {
        Vector3 BallOf(Dictionary<string, float> v) => new(v["ball.x"], v["ball.y"], v["ball.z"]);
        void Put(Dictionary<string, float> v, Vector3 at, bool shown)
        {
            v["ball.x"] = at.X;
            v["ball.y"] = at.Y;
            v["ball.z"] = at.Z;
            v["ball.show"] = shown ? 1f : 0f;
        }
        Vector3 struck = BallOf(seq.Steps[contact].Values);
        int toss = seq.Steps.FindIndex(st => st.Name.Contains("toss"));
        Vector3 onPalm = struck;
        posingOther = true;
        for (int i = 0; i < seq.Steps.Count; i++)
        {
            Dictionary<string, float> v = seq.Steps[i].Values;
            if (i < contact && (toss < 0 || i <= toss))
            {
                // The free hand open, palm up, the ball on it.
                v["lhand.palmUp"] = 80f;
                v["lhand.open"] = 1f;
                pose = new Dictionary<string, float>(v);
                Apply();
                Vector3 wrist = O(leftArm.WristPoint), along = (O(leftArm.WristPoint) - O(leftArm.ElbowPoint)).Normalized();
                Vector3 palm = O(leftArm.PalmFacing).Normalized();
                onPalm = wrist + along * Arm.PalmCentre + palm * (Arm.PalmHalfThickness + Ball.Radius);
                Put(v, i == toss ? onPalm + Vector3.Up * 0.35f : onPalm, true);
            }
            else if (i < contact)
            {
                Vector3 top = toss >= 0 ? BallOf(seq.Steps[toss].Values) : onPalm + Vector3.Up * 0.35f;
                Put(v, top.Lerp(struck, 0.6f), true); // coming down to the racket
            }
            else if (i > contact)
                Put(v, struck, false); // struck: gone
            seq.Steps[i].Values = new Dictionary<string, float>(v);
        }
        posingOther = false;
    }

    // One moment fitted: the lab's values tuned so the rig's joints lie on the video's (scaled to the
    // rig, put where the stroke has moved the hips to), the racket hand turned as the video's hand.
    // A start worked out from the joints (hips and chest turned along their lines, feet where the
    // ankles are, the hands at the wrists), then each value nudged up and down while that brings the
    // joints closer, the nudges halving as it settles. Returns the values and the joints' mean miss.
    (Dictionary<string, float> Values, float Error) FitMoment(VideoMoment m, float scale, bool forehand, Dictionary<string, float> from)
    {
        Dictionary<string, Vector3> rel = m.FittedJoints(); // with the joints placed by hand
        bool legs = m.LegsSeen;
        // The hips' height from the ankles on the floor, or (the legs not in the picture) the lab's.
        Vector3 hips = VideoHips(m, scale, from?["hips.height"] ?? DefaultValue("hips.height"));
        var target = rel.ToDictionary(kv => kv.Key, kv => hips + kv.Value * scale);
        // The video's hand: its direction (wrist to knuckles) and the way its palm faces.
        Vector3 wrist = rel["rwrist"], index = rel["rindex"], pinky = rel["rpinky"];
        Vector3 handWay = ((index + pinky) / 2 - wrist).Normalized();
        Vector3 palmWay = (index - wrist).Cross(pinky - wrist).Normalized();

        pose = from != null ? new Dictionary<string, float>(from) : DefaultPose();
        float Deg(float r) => Mathf.RadToDeg(r);
        Vector3 hipLine = target["lhip"] - target["rhip"], shoulderLine = target["lshoulder"] - target["rshoulder"];
        float hipsTurn = Deg(Mathf.Atan2(-hipLine.Z, hipLine.X)), chestTurn = Deg(Mathf.Atan2(-shoulderLine.Z, shoulderLine.X));
        pose["body.x"] = hips.X;
        pose["body.z"] = hips.Z;
        pose["hips.height"] = Mathf.Clamp(hips.Y, ChannelOf["hips.height"].Min, ChannelOf["hips.height"].Max);
        pose["hips.turn"] = hipsTurn;
        pose["waist.turn"] = Mathf.Clamp(Mathf.Wrap(chestTurn - hipsTurn, -180f, 180f), -90f, 90f);
        foreach (var (side, l) in new[] { ("lfoot", "l"), ("rfoot", "r") })
        {
            if (!legs)
            {
                // Not in the picture: the lab's stance under the hips.
                pose[side + ".x"] = hips.X + DefaultValue(side + ".x") - DefaultValue("body.x");
                pose[side + ".z"] = hips.Z + DefaultValue(side + ".z") - DefaultValue("body.z");
                pose[side + ".lift"] = 0f;
                continue;
            }
            Vector3 ankle = target[l + "ankle"], toe = target[l + "foot"] - target[l + "heel"];
            pose[side + ".x"] = ankle.X;
            pose[side + ".z"] = ankle.Z;
            pose[side + ".lift"] = Mathf.Clamp(ankle.Y - Leg.AnkleHeight, 0f, 0.3f);
            if (new Vector2(toe.X, toe.Z).Length() > 0.02f)
                pose[side + ".turn"] = Deg(Mathf.Atan2(toe.X, toe.Z));
        }
        pose["racket.backhand"] = forehand ? 0f : 1f;
        if (from == null)
        {
            Vector3 r = target["rwrist"] + handWay * 0.18f;
            (pose["racket.x"], pose["racket.y"], pose["racket.z"]) = (r.X, r.Y, r.Z);
        }
        Vector3 lw = target["lwrist"];
        (pose["lhand.x"], pose["lhand.y"], pose["lhand.z"]) = (lw.X, lw.Y, lw.Z);
        foreach (string k in pose.Keys.ToList())
            if (ChannelOf.TryGetValue(k, out Channel ch))
                pose[k] = Mathf.Clamp(pose[k], ch.Min, ch.Max);

        // Each joint weighed by how well it is seen: a hidden one only lightly (MediaPipe's guess),
        // the legs not at all when they are out of the picture.
        string[] legJoints = { "lknee", "rknee", "lankle", "rankle" };
        // A joint deleted by hand and not guessed: not fitted at all (the lab's guess).
        var weights = FitJoints.ToDictionary(j => j.Video, j => legJoints.Contains(j.Video) && !legs || m.Deleted.Contains(j.Video) && !m.Guessed.Contains(j.Video)
            ? 0f : j.Weight * Mathf.Max(m.SeenOf(j.Video), 0.25f));
        float totalWeight = weights.Values.Sum();
        // A blade's point with a depth set: in the lab, as far from the camera as the right wrist and
        // that much nearer.
        var markTargets = new Dictionary<string, Vector3>();
        foreach (var (mark, _) in RacketMarks)
            if (m.Fixed.TryGetValue(mark, out Vector2 markAt) && m.Depth.TryGetValue(mark, out float depth))
                markTargets[mark] = hips + (rel["rwrist"] + m.Unproject(markAt - m.Project(rel["rwrist"])) + m.ToCamera * depth) * scale;
        float Cost()
        {
            Apply();
            Dictionary<string, Vector3> rig = RigJoints();
            float cost = 0f;
            foreach (var (name, w) in weights)
                cost += w * rig[name].DistanceSquaredTo(target[name]);
            // The racket hand turned as the video's: its direction and its palm.
            Transform3D p = paddle.GlobalTransform;
            Vector3 w0 = O(p * paddle.WristLocal), i0 = O(p * IndexKnuckle), l0 = O(p * LittleKnuckle);
            Vector3 rigWay = ((i0 + l0) / 2 - w0).Normalized(), rigPalm = (i0 - w0).Cross(l0 - w0).Normalized();
            // Only a hand the video shows: a hidden one's turn is MediaPipe's guess, which would
            // twist the racket about at random from step to step.
            float handSeen = m.SeenOf("rwrist");
            if (handSeen >= 0.4f)
                cost += handSeen * (0.04f * (1f - rigWay.Dot(handWay)) + 0.04f * (1f - rigPalm.Dot(palmWay)));
            // The wrist kept easy (bent off the grip's line as little as will do), and the racket
            // turned on from the step before rather than round some other way to much the same place.
            // The blade's points where they were marked on the frame (with a depth set: there in
            // the lab; else anywhere along the camera's line), and the side of it the camera sees.
            foreach (var (mark, local) in RacketMarks)
            {
                if (!m.Fixed.TryGetValue(mark, out Vector2 markAt))
                    continue;
                Vector3 markGot = O(p * local);
                float miss = markTargets.TryGetValue(mark, out Vector3 there) ? markGot.DistanceTo(there)
                    : (m.Project((markGot - hips) / scale) - markAt).Length() / m.PixelsPerMetre * scale;
                cost += 3f * miss * miss;
            }
            if (m.Face != null && m.CamX != Vector3.Zero)
            {
                // The forehand (red) rubber's way out, against the way to the camera.
                float facing = O(p.Basis * Vector3.Forward).Normalized().Dot(m.ToCamera);
                float off = m.Face switch
                {
                    "forehand" => Mathf.Max(0.3f - facing, 0f),
                    "backhand" => Mathf.Max(0.3f + facing, 0f),
                    _ => Mathf.Max(Mathf.Abs(facing) - 0.25f, 0f),
                };
                cost += 0.08f * off * off;
            }
            float bend = Mathf.Max(WristBend() - 45f, 0f) / 45f;
            cost += 0.05f * bend * bend;
            // Never through the body or the table: the hidden racket's guess least of all.
            if (RacketThrough())
                cost += 1f;
            if (from != null)
                foreach (string k in RacketTurns)
                {
                    float d = Mathf.Wrap(pose[k] - from[k], -180f, 180f) / 90f;
                    cost += (handSeen >= 0.4f ? 0.002f : 0.01f) * d * d;
                }
            return cost / totalWeight;
        }

        string[] metres = FitMetres, degrees = FitDegrees;
        if (!legs)
        {
            // Nothing in the picture to fit the legs to: they keep the lab's stance.
            string[] legValues = { "hips.height", "lfoot.x", "lfoot.z", "rfoot.x", "rfoot.z", "lknee.swivel", "rknee.swivel" };
            metres = metres.Except(legValues).ToArray();
            degrees = degrees.Except(legValues).ToArray();
        }
        var steps = metres.ToDictionary(k => k, _ => 0.03f).Concat(degrees.ToDictionary(k => k, _ => 6f)).ToDictionary(kv => kv.Key, kv => kv.Value);
        float smallest(string k) => metres.Contains(k) ? 0.002f : 0.5f;

        fitting = true;
        bool wasPosingOther = posingOther;
        posingOther = true; // the picked step's contact lock must not pull the racket about while trying
        // Fitted from two starts, the better kept: on from the step before (the racket where it
        // was), and with the racket at the video's hand, the elbow as the grip has it (from the step
        // before alone the arm can stay caught the wrong way round the body).
        var startSteps = new Dictionary<string, float>(steps);
        var onFrom = new Dictionary<string, float>(pose);
        (Dictionary<string, float> Pose, float Cost) first = Descend();
        if (from != null)
        {
            pose = onFrom;
            Vector3 r = target["rwrist"] + handWay * 0.18f;
            (pose["racket.x"], pose["racket.y"], pose["racket.z"]) = (r.X, r.Y, r.Z);
            pose["relbow.swivel"] = 0f;
            steps = new Dictionary<string, float>(startSteps);
            var second = Descend();
            if (second.Cost < first.Cost)
                first = second;
        }
        // The fit laid on and read with the contact lock still off: on (the step being fitted
        // the one picked, as when its key frame is marked), it would pull the racket, and the arm
        // with it, to the ball the pose still has (the step before's: on a new step, the tossed
        // ball high up), and that would be returned as the fit.
        pose = first.Pose;
        fitting = false;
        Apply();
        Dictionary<string, Vector3> got = RigJoints();
        var fittedPose = new Dictionary<string, float>(pose);
        posingOther = wasPosingOther;
        // The miss over the joints seen well (what the video shows).
        var shown = FitJoints.Where(j => weights[j.Video] > 0f && m.SeenOf(j.Video) >= 0.5f).ToList();
        float miss = (shown.Count > 0 ? shown : FitJoints.Where(j => weights[j.Video] > 0f).ToList()).Average(j => got[j.Video].DistanceTo(target[j.Video]));
        return (fittedPose, miss);

        // Each value nudged up and down while that brings the joints closer, the nudges halving as
        // it settles.
        (Dictionary<string, float> Pose, float Cost) Descend()
        {
        float best = Cost();
        for (int sweep = 0; sweep < 80; sweep++)
        {
            bool moved = false;
            foreach (string k in steps.Keys.ToList())
            {
                Channel ch = ChannelOf[k];
                float was = pose[k];
                bool improved = false;
                foreach (float dir in new[] { 1f, -1f })
                {
                    float to = Mathf.Clamp(was + dir * steps[k], ch.Min, ch.Max);
                    if (Mathf.IsEqualApprox(to, was))
                        continue;
                    var before = new Dictionary<string, float>(pose);
                    pose[k] = to;
                    float c = Cost();
                    if (c < best - 1e-7f)
                    {
                        best = c;
                        moved = improved = true;
                        break;
                    }
                    pose = before;
                }
                if (!improved)
                    steps[k] = Mathf.Max(steps[k] * 0.5f, smallest(k));
            }
            if (!moved && steps.All(kv => kv.Value <= smallest(kv.Key) + 1e-6f))
                break;
        }
        return (new Dictionary<string, float>(pose), best);
        }
    }
}
