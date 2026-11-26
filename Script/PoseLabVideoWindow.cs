using Godot;
using System.Collections.Generic;
using System.Linq;

// The Pose Tracer: a video imported by the Pose Lab's "Import video..." traced into a motion, key
// frame by key frame (see PoseLabVideo.cs).
//
//   - The video plays (Space; the timeline dragged; Left / Right a frame at a time, Shift: ten). The
//     yellow ticks on the timeline are the key frames set.
//   - "Set" makes the frame shown a key frame: a step of the action picked (its template's steps,
//     listed with the ones still to set). The player's joints are found in it then (about a second).
//   - "Add a step here" (the green button with the plus) makes the frame shown a step of its own,
//     besides the template's: a moment in between worth a step. Listed in time order, marked ✚.
//   - On a key frame the joints are listed: those the video shows (drag one where it really is) and
//     those it does not (hidden, off the picture, the racket's tip): pick one, click where it is.
//     What was placed at the key frame before is listed again (its place shown faintly).
//   - A joint MediaPipe has wrong and that cannot be seen: deleted (Delete, or the button), it is
//     put between the key frames around that have it (shown as a grey cross where it goes).
//   - Each key frame is its step in the lab as soon as its joints are found, fitted again as it is
//     marked; the small Pose Lab beside the frame shows it (as the video's camera sees it), and a
//     step picked in the lab shows its key frame here, the lab's body drawn on it.
//   - Every step set: "Make the motion" fits them all again, puts the ball in, and plays it.
public partial class PoseLab
{
    VideoWindow videoWindow;

    // --video-capture=<video>@<file.png>[@<joint>[@<key frame>]]: the Pose Tracer opened on an
    // imported video's key frame (the first, or the one given by its number from 1 in time order;
    // the joint chosen on it: a LabJoints key, or "-" for none), its picture saved, then the lab
    // quits (a review image); "!joint" / "~joint" as the mouse clicks / drags it on the picture. The key frame "+<frame>" instead: a step added at that frame (as
    // "Add a step here" does; it is kept), then a few frames on shown.
    string videoCapture;

    async void CaptureVideoWindow(string spec)
    {
        string[] parts = spec.Split('@');
        // The racket's natural holds first (read over the lab's first frames): a step fitted
        // before them would be laid on again with another hold.
        while (!HoldsKnown)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (parts.Length < 2 || !LoadVideo(parts[0]))
        {
            GD.PrintErr($"video capture: no imported video \"{spec}\"");
            GetTree().Quit(1);
            return;
        }
        OpenVideoWindow();
        List<VideoKey> keys = videoKeys.OrderBy(k => k.Frame).ToList();
        if (parts.Length > 3 && parts[3].StartsWith('+') && int.TryParse(parts[3][1..], out int added))
        {
            videoWindow.ShowFrame(added);
            videoWindow.AddStep();
            videoWindow.ShowFrame(added + 5);
        }
        else if (parts.Length > 3 && int.TryParse(parts[3], out int n) && n >= 1 && n <= keys.Count)
            videoWindow.ShowFrame(keys[n - 1].Frame);
        // "!joint": clicked on the picture; "~joint": dragged 40 px on it (no depth box after a drag).
        if (parts.Length > 2 && parts[2].Length > 1 && parts[2][0] is '!' or '~')
        {
            for (int f = 0; f < 10; f++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            videoWindow.PressJoint(parts[2][1..], parts[2][0] == '~' ? new Vector2(40f, 0f) : Vector2.Zero);
        }
        else if (parts.Length > 2 && parts[2] != "-")
            videoWindow.Choose(parts[2]);
        for (int f = 0; f < 90; f++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetViewport().GetTexture().GetImage().SavePng(parts[1]);
        GD.Print($"video capture: {parts[1]}");
        GetTree().Quit();
    }

    void OpenVideoWindow()
    {
        if (videoTrack == null)
        {
            // The video the motion was made from, when it was.
            string link = sequence.Steps.Select(s => s.Video).FirstOrDefault(v => v != null && v.Contains('@'));
            if (link != null)
                LoadVideo(link[..link.LastIndexOf('@')]);
        }
        if (videoWindow == null || !IsInstanceValid(videoWindow))
        {
            videoWindow = new VideoWindow { Lab = this, Name = "VideoWindow" };
            AddChild(videoWindow);
        }
        // The window over the whole screen, the lab's own panel hidden behind it (back when it closes).
        Vector2 screen = GetViewport().GetVisibleRect().Size;
        const int TitleBar = 34;
        videoWindow.Position = new Vector2I(4, TitleBar);
        videoWindow.Size = new Vector2I((int)screen.X - 8, (int)screen.Y - TitleBar - 4);
        layer.Visible = false;
        videoWindow.Show();
        // The lab shows the video's motion (when it has nothing else, or shows it already).
        if (videoTrack != null && PosedKeys().Count > 0 && (sequence.Steps.Count == 0 || IsVideoMotion))
            SyncAllKeys();
        videoWindow.Reload();
    }

    // Input that is the Video window's: a key while it has the keyboard; a press, the wheel or the
    // mouse moving over it (its title bar too). A drag begun in the lab goes on over it, and a
    // button let go there still ends one.
    bool ForVideoWindow(InputEvent e)
    {
        if (videoWindow == null || !IsInstanceValid(videoWindow) || !videoWindow.Visible)
            return false;
        if (e is InputEventKey)
            return videoWindow.HasFocus();
        if (e is not InputEventMouse mouse)
            return false;
        const int TitleBar = 40;
        var area = new Rect2(videoWindow.Position - new Vector2I(0, TitleBar), videoWindow.Size + new Vector2I(0, TitleBar));
        if (!area.HasPoint(mouse.Position))
            return false;
        return e switch
        {
            InputEventMouseButton b => b.Pressed,
            InputEventMouseMotion m => m.ButtonMask == 0 || !(dragging || rotating || turningWhole),
            _ => true,
        };
    }

    // The video's joints shown as the Pose Lab shows its body: the lab's own markers, each with its
    // part, what it is called, whether it is a turning handle (a magenta square, as in the lab) or a
    // place (a cyan round), and the video's points (MediaPipe's) it stands for: the first is where it
    // is (the hips: between the two), the rest move with it when it is moved by hand (a hand's
    // fingers, a foot's heel). MediaPipe's other points are not shown, but still fitted to.
    record LabJoint(string Key, string Part, string Words, bool Turn, string[] Members);

    static readonly LabJoint[] LabJoints =
    {
        new("head", "Waist", "head", false, new[] { "nose" }), // the head turns with the chest in the lab
        new("hips", "Body (hips)", "hips", false, new[] { "lhip", "rhip" }),
        new("rsh", "Right shoulder", "right shoulder", false, new[] { "rshoulder" }),
        new("lsh", "Left shoulder", "left shoulder", false, new[] { "lshoulder" }),
        new("rel", "Right elbow", "right elbow", false, new[] { "relbow" }),
        new("lel", "Left elbow", "left elbow", false, new[] { "lelbow" }),
        new("rwr", "Right hand + racket", "right hand (racket)", false, new[] { "rwrist", "rindex", "rpinky", "rthumb" }),
        new("lwr", "Left hand", "left hand", false, new[] { "lwrist", "lindex", "lpinky", "lthumb" }),
        new("rkn", "Right knee", "right knee", false, new[] { "rknee" }),
        new("lkn", "Left knee", "left knee", false, new[] { "lknee" }),
        new("ran", "Right foot", "right foot (ankle)", false, new[] { "rankle", "rheel" }),
        new("lan", "Left foot", "left foot (ankle)", false, new[] { "lankle", "lheel" }),
        new("rto", "Right toes (turn the foot)", "right toes", true, new[] { "rfoot" }),
        new("lto", "Left toes (turn the foot)", "left toes", true, new[] { "lfoot" }),
        new("rhead", "Racket head (turn about the handle)", "racket head (top of the blade)", true, new[] { VideoMoment.RacketTip }),
        new("rbt", "Racket face (open / close)", "blade's edge by the thumb (widest)", true, new[] { VideoMoment.RacketThumb }),
        new("rbf", "Racket face (open / close)", "blade's edge across from the thumb (widest)", true, new[] { VideoMoment.RacketFar }),
    };

    // A lab joint that is a point of the blade (MediaPipe does not see the racket: placed by hand).
    static bool IsBladeJoint(LabJoint j) => VideoMoment.IsRacketMark(j.Members[0]);

    static LabJoint LabJointOf(string key) => LabJoints.FirstOrDefault(j => j.Key == key);

    // The lab's skeleton, as it draws it (DrawBones): the hips up the chest to the neck and head,
    // the neck out to the shoulders, down the arms to the hands (the racket from the right), and
    // the hips out to the hip joints, down the legs to the feet and toes.
    static readonly (string A, string B)[] LabBones =
    {
        ("hips", "chest"), ("chest", "neck"), ("neck", "head"),
        ("neck", "rsh"), ("rsh", "rel"), ("rel", "rwr"), ("rwr", "rhead"), ("rbt", "rhead"), ("rbf", "rhead"), ("rbt", "rbf"),
        ("neck", "lsh"), ("lsh", "lel"), ("lel", "lwr"),
        ("hips", "lhip"), ("lhip", "lkn"), ("lkn", "lan"), ("lan", "lto"),
        ("hips", "rhip"), ("rhip", "rkn"), ("rkn", "ran"), ("ran", "rto"),
    };

    // The lab's points from joints by the video's names (at: where one is, or null): its markers',
    // and the skeleton's own (the chest, neck and head, the hip joints).
    static Dictionary<string, Vector2> LabPoints(System.Func<string, Vector2?> at)
    {
        var p = new Dictionary<string, Vector2>();
        foreach (var (key, name) in new[] { ("lhip", "lhip"), ("rhip", "rhip"), ("rsh", "rshoulder"), ("lsh", "lshoulder"), ("rel", "relbow"), ("lel", "lelbow"),
                                           ("rwr", "rwrist"), ("lwr", "lwrist"), ("rkn", "rknee"), ("lkn", "lknee"), ("ran", "rankle"), ("lan", "lankle"),
                                           ("rto", "rfoot"), ("lto", "lfoot"), ("head", "nose"), ("rhead", VideoMoment.RacketTip),
                                           ("rbt", VideoMoment.RacketThumb), ("rbf", VideoMoment.RacketFar) })
            if (at(name) is Vector2 v)
                p[key] = v;
        if (p.ContainsKey("lhip") && p.ContainsKey("rhip"))
            p["hips"] = (p["lhip"] + p["rhip"]) / 2f;
        if (p.ContainsKey("lsh") && p.ContainsKey("rsh"))
            p["neck"] = (p["lsh"] + p["rsh"]) / 2f;
        if (p.ContainsKey("hips") && p.ContainsKey("neck"))
            p["chest"] = p["hips"].Lerp(p["neck"], 0.45f); // the lab's chest marker: the waist's turn point
        return p;
    }

    // The lab's body on the frame (pixels): its joints (by the video's names), the racket's rim and
    // handle, its tip, and whether the camera sees its forehand (red) side.
    (Dictionary<string, Vector2> Joints, Vector2[] Rim, Vector2[] Handle, Vector2 Tip, bool RedSeen) RigOnFrame(VideoMoment m, float scale)
    {
        Vector3 hips = VideoHips(m, scale, V("hips.height"));
        Vector2 P(Vector3 own) => m.Project((own - hips) / scale);
        var joints = RigJoints().ToDictionary(kv => kv.Key, kv => P(kv.Value));
        Transform3D p = paddle.GlobalTransform;
        joints["rindex"] = P(O(p * IndexKnuckle));
        joints["rpinky"] = P(O(p * LittleKnuckle));
        joints["lfoot"] = P(O(ToesAt(0)));
        joints["rfoot"] = P(O(ToesAt(1)));
        foreach (var (mark, local) in RacketMarks)
            joints[mark] = P(O(p * local));
        var rim = new Vector2[28];
        for (int i = 0; i < rim.Length; i++)
        {
            float a = Mathf.Tau * i / rim.Length;
            rim[i] = P(O(p * new Vector3(HeadHalf.X * Mathf.Cos(a), HeadCentre.Y + HeadHalf.Y * Mathf.Sin(a), 0f)));
        }
        Vector2[] handle = { P(O(p * new Vector3(0f, HeadCentre.Y - HeadHalf.Y, 0f))), P(O(p * Paddle.Outline[8])) };
        bool red = O(p.Basis * Vector3.Forward).Dot(m.ToCamera) > 0f;
        return (joints, rim, handle, P(O(p * Paddle.Outline[0])), red);
    }

    // ---------------------------------------------------------------- the window

    partial class VideoWindow : Window
    {
        public PoseLab Lab;

        OptionButton actionPicker, namePicker, facePicker, speedPicker;
        LineEdit nameEdit, addName;
        ItemList keyList, foundList, missingList;
        Button playButton, setButton, removeButton, makeButton, addButton;
        CheckBox autoFit, showVideo, showMissing;
        LineEdit saveName;
        ItemList sceneList;
        Label tableFit;
        Label labStatus;
        Label timeLabel, info, keyTitle, hint;
        Control jointsBox;
        FrameCanvas canvas;
        Timeline timeline;

        int frame;                 // the frame shown
        float playAt;              // where playing has got (frames)
        bool playingVideo;
        Texture2D picture;
        int pictureFrame = -1;
        readonly Dictionary<int, Texture2D> cache = new();
        readonly Queue<int> cached = new();

        float zoom = 1f;
        Vector2 offset;
        bool viewFitted;
        string hover;     // the marker under the mouse (a LabJoints key)
        string dragging;  // a video point being dragged
        Vector2 pressedAt; // where the mouse went down on it
        bool dragMoved;    // it was moved (then no depth box on release: only a click opens it)
        string placing;   // a missing point to be placed by the next click
        string chosen;    // the joint last clicked (on the frame or in the lists): what Delete deletes
        SubViewport labView;
        TextureRect labRect;
        Camera3D labCamera;
        float orbitYaw, orbitPitch, labDistance = 2f; // the small Pose Lab turned and zoomed from the video's view
        bool orbiting;
        Vector3 viewDir = new Vector3(0.6f, 0.25f, 0.75f).Normalized(), viewUp = Vector3.Up; // own frame
        bool panning;
        int lastLabStep = -2;
        bool filling;
        double sinceInfo;

        VideoTrack Track => Lab.videoTrack;
        VideoKey Key => Track == null ? null : Lab.videoKeys.FirstOrDefault(k => k.Frame == frame);

        public override void _Ready()
        {
            Title = "Pose Tracer: a player's motion traced from a video";
            MinSize = new Vector2I(760, 520);
            Transient = false;
            Exclusive = false;
            CloseRequested += () =>
            {
                playingVideo = false;
                Hide();
                Lab.layer.Visible = !Lab.InGame || Lab.open; // the lab's panel back
            };

            var back = new PanelContainer();
            back.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            back.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.07f, 0.08f, 0.1f), ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 6, ContentMarginBottom = 6 });
            AddChild(back);
            var root = new VBoxContainer();
            root.AddThemeConstantOverride("separation", 4);
            back.AddChild(root);

            // The action, the strokes found, making the motion.
            var top = new HFlowContainer();
            top.AddChild(Caption("Action"));
            actionPicker = new OptionButton { ClipText = true, CustomMinimumSize = new Vector2(200f, 0f) };
            foreach (StrokeActions.Action a in StrokeActions.All)
                actionPicker.AddItem(StrokeActions.Label(a));
            actionPicker.TooltipText = "The action the key frames are for: its steps are the key frames to set.";
            actionPicker.ItemSelected += i =>
            {
                if (filling)
                    return;
                Lab.videoAction = StrokeActions.All[(int)i].Id;
                Lab.SaveVideoKeys();
                RefreshPanel();
            };
            top.AddChild(actionPicker);
            makeButton = MakeButton("Make the motion ▶", () => _ = Lab.MakeVideoMotion());
            top.AddChild(makeButton);
            // The motion named and saved (as the lab's Save does: res://Poses/<name>.json).
            top.AddChild(Caption("   Save as"));
            saveName = new LineEdit { CustomMinimumSize = new Vector2(220f, 0f), PlaceholderText = "the motion's name" };
            top.AddChild(saveName);
            top.AddChild(MakeButton("Save", () =>
            {
                Lab.sequenceName.Text = saveName.Text;
                Lab.Save();
            }));
            root.AddChild(top);

            // The frame, and the key frames and joints beside it.
            var middle = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            middle.AddThemeConstantOverride("separation", 6);
            root.AddChild(middle);
            canvas = new FrameCanvas { Owner_ = this, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1.7f, SizeFlagsVertical = Control.SizeFlags.ExpandFill, ClipContents = true, MouseFilter = Control.MouseFilterEnum.Stop };
            canvas.Resized += () => viewFitted = false;
            middle.AddChild(canvas);
            BuildDepthBox();
            // The small Pose Lab: the lab's own figure (its world, its markers), in the key frame's
            // step, seen from where the video's camera is.
            var labColumn = new VBoxContainer { CustomMinimumSize = new Vector2(260f, 0f), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            var labCaption = Caption("POSE LAB: this key frame's step, as the video sees it");
            labCaption.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            labCaption.CustomMinimumSize = new Vector2(200f, 0f);
            labColumn.AddChild(labCaption);
            // Drawn from a viewport as sharp as the screen (SharpenLabView: as many pixels as the
            // screen gives it, not the window's own 1280x720 layout's, smoothed).
            labRect = new TextureRect
            {
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(220f, 200f), MouseFilter = Control.MouseFilterEnum.Stop,
            };
            labRect.GuiInput += OnLabViewInput;
            labView = new SubViewport { HandleInputLocally = false, RenderTargetUpdateMode = SubViewport.UpdateMode.Always, Size = new Vector2I(220, 200), Msaa3D = Viewport.Msaa.Msaa4X };
            labCamera = new Camera3D { Fov = 48f, KeepAspect = Camera3D.KeepAspectEnum.Width, Current = true }; // the figure across, however narrow the view
            labView.AddChild(labCamera);
            labRect.AddChild(labView);
            labRect.Texture = labView.GetTexture();
            labColumn.AddChild(labRect);
            var labViewRow = new HFlowContainer();
            labViewRow.AddChild(Caption("drag: turn round · wheel: zoom"));
            labViewRow.AddChild(MakeButton("As the video sees it", () => { orbitYaw = orbitPitch = 0f; labDistance = 2f; }));
            labColumn.AddChild(labViewRow);
            middle.AddChild(labColumn);
            var sideScroll = new ScrollContainer { CustomMinimumSize = new Vector2(300f, 0f), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            middle.AddChild(sideScroll);
            var side = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            side.AddThemeConstantOverride("separation", 4);
            sideScroll.AddChild(side);

            side.AddChild(Heading("KEY FRAMES"));
            keyList = new ItemList { CustomMinimumSize = new Vector2(0f, 120f), AutoHeight = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            keyList.ItemSelected += i => PickKeyRow((int)i);
            side.AddChild(keyList);
            var setRow = new HFlowContainer();
            setRow.AddChild(Caption("Set this frame as"));
            namePicker = new OptionButton { ClipText = true, CustomMinimumSize = new Vector2(120f, 0f) };
            namePicker.ItemSelected += i => nameEdit.Text = namePicker.GetItemText((int)i);
            setRow.AddChild(namePicker);
            nameEdit = new LineEdit { CustomMinimumSize = new Vector2(110f, 0f), PlaceholderText = "its name" };
            setRow.AddChild(nameEdit);
            setButton = MakeButton("Set", SetKey);
            setRow.AddChild(setButton);
            removeButton = MakeButton("Remove", RemoveKey);
            setRow.AddChild(removeButton);
            side.AddChild(setRow);
            // A step of its own, besides the action's: stands out from the rest (green, a plus).
            var addRow = new HBoxContainer();
            addRow.AddThemeConstantOverride("separation", 6);
            addButton = AddStepButton();
            addButton.Pressed += AddStep;
            addRow.AddChild(addButton);
            addName = new LineEdit { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, PlaceholderText = "name (optional)", CustomMinimumSize = new Vector2(90f, 0f) };
            addName.TextSubmitted += _ => AddStep();
            addRow.AddChild(addName);
            side.AddChild(addRow);

            var joints = new VBoxContainer();
            joints.AddThemeConstantOverride("separation", 4);
            jointsBox = joints;
            side.AddChild(joints);
            keyTitle = Heading("JOINTS");
            joints.AddChild(keyTitle);
            // The missing first: they are what needs doing.
            joints.AddChild(Caption("Missing: pick one, then click where it is"));
            missingList = new ItemList { AutoHeight = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            missingList.ItemSelected += i => { placing = chosen = (string)missingList.GetItemMetadata((int)i); PickPartOf(placing); };
            joints.AddChild(missingList);
            joints.AddChild(Caption("Found by the video: drag one on the picture where it really is"));
            foundList = new ItemList { AutoHeight = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            foundList.ItemSelected += i => { string n = (string)foundList.GetItemMetadata((int)i); hover = chosen = n; PickPartOf(n); };
            joints.AddChild(foundList);
            facePicker = new OptionButton();
            foreach (string f in new[] { "racket side seen: not marked", "seen: the forehand (red) side", "seen: the backhand (black) side", "seen: edge on" })
                facePicker.AddItem(f);
            facePicker.TooltipText = "Which side of the racket faces the camera here (the fit turns the racket to show it).";
            facePicker.ItemSelected += i =>
            {
                if (filling || Key is not VideoKey k)
                    return;
                k.Face = i switch { 1 => "forehand", 2 => "backhand", 3 => "edge", _ => null };
                Changed();
            };
            joints.AddChild(Caption("Ball, table and net: pick one, then click where it is"));
            sceneList = new ItemList { AutoHeight = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            sceneList.ItemSelected += i => { placing = chosen = (string)sceneList.GetItemMetadata((int)i); if (placing == BallMark) PickPartOf(placing); };
            sceneList.TooltipText = "The ball: where it is in this key frame (its step's ball goes there).\nThe table's corners and the net's ends (as the player sees them: left its left): two or more turn the player to the table and place it across it. Marked once, they serve every key frame of the same camera.";
            joints.AddChild(sceneList);
            tableFit = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
            tableFit.AddThemeFontSizeOverride("font_size", 13);
            tableFit.AddThemeColorOverride("font_color", new Color(0.85f, 0.9f, 1f));
            joints.AddChild(tableFit);
            var deleteRow = new HFlowContainer();
            var delete = MakeButton("Delete the joint (Del)", DeleteChosen);
            delete.TooltipText = "The joint last clicked: MediaPipe's place for it is not used here; it is put between the key frames around that have it.";
            deleteRow.AddChild(delete);
            var restore = MakeButton("Restore it", RestoreChosen);
            restore.TooltipText = "The joint last clicked: back to the video's place.";
            deleteRow.AddChild(restore);
            joints.AddChild(deleteRow);
            joints.AddChild(facePicker);
            var fixes = new HFlowContainer();
            var swap = MakeButton("Left ⇄ right", SwapKey);
            swap.TooltipText = "MediaPipe mixed up the player's left and right in this frame: swap them (again: back).";
            fixes.AddChild(swap);
            var turn = MakeButton("Turn the player round", TurnRound);
            turn.TooltipText = "MediaPipe took the player's front for the back all through the video (the racket hand on the wrong side): turn it round (again: back).";
            fixes.AddChild(turn);
            fixes.AddChild(MakeButton("Clear the marks", () => { if (Key is VideoKey k) { k.Fixed.Clear(); k.Face = null; Changed(); } }));
            var again = MakeButton("Find the joints again", () => { if (Key is VideoKey k) { Lab.FindPose(k); RefreshPanel(); } });
            again.TooltipText = "Ask MediaPipe again for this key frame's joints (with two players in the picture: the one the Pose Lab's player picker says).";
            fixes.AddChild(again);
            joints.AddChild(fixes);
            autoFit = new CheckBox { Text = "fit its step as I mark", ButtonPressed = true };
            autoFit.TooltipText = "Once the motion is made: fit this key frame's step again each time a joint is placed.";
            joints.AddChild(autoFit);
            hint = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
            hint.AddThemeFontSizeOverride("font_size", 13);
            hint.AddThemeColorOverride("font_color", new Color(0.75f, 0.8f, 0.9f));
            side.AddChild(hint);
            showVideo = new CheckBox { Text = "show the joints on the video", ButtonPressed = true };
            side.AddChild(showVideo);
            showMissing = new CheckBox { Text = "show the missing joints", ButtonPressed = true };
            showMissing.TooltipText = "Off: only the joints the video shows, and those placed by hand (the missing and deleted ones hidden).";
            side.AddChild(showMissing);

            // Playing it.
            var bar = new HBoxContainer();
            bar.AddChild(MakeButton("⏮", () => GoKey(-1)));
            bar.AddChild(MakeButton("◀", () => GoTo(frame - 1)));
            playButton = MakeButton("▶", TogglePlayVideo);
            playButton.CustomMinimumSize = new Vector2(44f, 0f);
            bar.AddChild(playButton);
            bar.AddChild(MakeButton("▶|", () => GoTo(frame + 1)));
            bar.AddChild(MakeButton("⏭", () => GoKey(1)));
            timeline = new Timeline { Owner_ = this, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(200f, 40f), MouseFilter = Control.MouseFilterEnum.Stop };
            bar.AddChild(timeline);
            timeLabel = new Label { CustomMinimumSize = new Vector2(150f, 0f) };
            bar.AddChild(timeLabel);
            speedPicker = new OptionButton();
            foreach (string s in new[] { "×1", "×0.5", "×0.25", "×0.1" })
                speedPicker.AddItem(s);
            speedPicker.TooltipText = "How fast it plays.";
            bar.AddChild(speedPicker);
            root.AddChild(bar);

            labView.World3D = Lab.GetViewport().FindWorld3D();
            labStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
            labStatus.AddThemeFontSizeOverride("font_size", 13);
            labStatus.AddThemeColorOverride("font_color", new Color(0.55f, 0.95f, 0.65f));
            root.AddChild(labStatus);
            info = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(0f, 38f) };
            info.AddThemeFontSizeOverride("font_size", 13);
            root.AddChild(info);
        }

        public override void _EnterTree()
        {
            if (labView != null)
                labView.World3D = Lab.GetViewport().FindWorld3D();
        }

        // The small Pose Lab's viewport as many pixels as its place on the screen has (the window is
        // laid out at 1280x720 and scaled up to the screen: the view would be blurred with it).
        void SharpenLabView()
        {
            float screen = Mathf.Max(labRect.GetScreenTransform().Scale.X, DisplayServer.WindowGetSize().X / Mathf.Max(Lab.GetViewport().GetVisibleRect().Size.X, 1f));
            Vector2I want = (Vector2I)(labRect.Size * Mathf.Clamp(screen, 1f, 4f)).Round();
            if (want.X >= 16 && want.Y >= 16 && labView.Size != want)
                labView.Size = want;
        }

        // The small Pose Lab: a drag turns round the figure, the wheel zooms.
        void OnLabViewInput(InputEvent e)
        {
            switch (e)
            {
                case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                    labDistance = Mathf.Max(labDistance * 0.9f, 0.6f);
                    break;
                case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                    labDistance = Mathf.Min(labDistance * 1.1f, 8f);
                    break;
                case InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Right } b:
                    orbiting = b.Pressed;
                    break;
                case InputEventMouseMotion m when orbiting:
                    orbitYaw -= m.Relative.X * 0.01f;
                    orbitPitch = Mathf.Clamp(orbitPitch - m.Relative.Y * 0.01f, -1.3f, 1.3f); // dragged down: seen from higher, as in the lab
                    break;
                default:
                    return;
            }
            labView.GetParent<Control>().AcceptEvent();
        }

        static Label Caption(string text)
        {
            var l = new Label { Text = text };
            l.AddThemeFontSizeOverride("font_size", 13);
            l.AddThemeColorOverride("font_color", new Color(0.8f, 0.82f, 0.88f));
            return l;
        }

        // The video opened again (or newly read): its action, the strokes found, the first key frame.
        public void Reload()
        {
            filling = true;
            cache.Clear();
            cached.Clear();
            pictureFrame = -1;
            picture = null;
            playingVideo = false;
            if (Track != null)
            {
                actionPicker.Selected = System.Array.FindIndex(StrokeActions.All, a => a.Id == Lab.videoAction);
                frame = Lab.videoKeys.Count > 0 ? Lab.videoKeys.Min(k => k.Frame) : 0;
            }
            filling = false;
            viewFitted = false;
            RefreshPanel();
        }

        // ---------------------------------------------------------------- key frames

        // The key frames list: each step of the action, set (its time) or not; then any others.
        void RefreshPanel()
        {
            filling = true;
            bool open = Track != null;
            foreach (Control c in new Control[] { actionPicker, setButton, removeButton, makeButton, addButton, namePicker, nameEdit, addName })
                if (c is BaseButton b) b.Disabled = !open; else if (c is LineEdit e) e.Editable = open;
            keyList.Clear();
            namePicker.Clear();
            if (!open)
            {
                jointsBox.Visible = false;
                timeLabel.Text = "";
                Say(Lab.videoRun != null ? Lab.VideoReading() : "No video imported yet (or one imported by the tool's earlier version): \"Import video…\" in the Pose Lab's ACTION section.");
                filling = false;
                return;
            }
            string[] names = Lab.VideoStepNames();
            var missingSteps = new List<string>();
            // The steps added (not the action's): each in time order among the action's set ones.
            var added = Lab.videoKeys.Where(k => !names.Contains(k.Name)).OrderBy(k => k.Frame).ToList();
            int nextAdded = 0;
            void AddedRow(VideoKey k)
            {
                int row = keyList.AddItem($"✚  {k.Name}   {Track.T[k.Frame]:F2} s   added{KeyState(k)}");
                keyList.SetItemMetadata(row, k.Name);
                keyList.SetItemCustomFgColor(row, AddedColour);
                keyList.SetItemTooltip(row, "A step added (not one of the action's): \"Remove\" takes it away, the name box and \"Rename\" renames it.");
            }
            foreach (string n in names)
            {
                VideoKey k = Lab.videoKeys.FirstOrDefault(x => x.Name == n);
                if (k != null)
                    while (nextAdded < added.Count && added[nextAdded].Frame < k.Frame)
                        AddedRow(added[nextAdded++]);
                int row = keyList.AddItem(k != null ? $"✔  {n}   {Track.T[k.Frame]:F2} s{KeyState(k)}" : $"○  {n}   (not set)");
                keyList.SetItemMetadata(row, n);
                keyList.SetItemCustomFgColor(row, k != null ? new Color(1f, 0.85f, 0.3f) : new Color(0.65f, 0.65f, 0.7f));
                if (k == null)
                    missingSteps.Add(n);
            }
            while (nextAdded < added.Count)
                AddedRow(added[nextAdded++]);
            foreach (string n in names)
                namePicker.AddItem(n);
            VideoKey here = Key;
            string next = here?.Name ?? missingSteps.FirstOrDefault() ?? names.FirstOrDefault() ?? "key";
            int at = System.Array.IndexOf(names, next);
            if (at >= 0)
                namePicker.Selected = at;
            nameEdit.Text = next;
            setButton.Text = here != null ? "Rename" : "Set";
            removeButton.Disabled = here == null;
            addButton.Disabled = here != null;
            addButton.TooltipText = here != null
                ? $"This frame is already the key frame \"{here.Name}\": go to another frame (Left / Right, or play and stop) to add a step there."
                : $"Make this frame ({Track.T[frame]:F2} s) a step of its own, besides {StrokeActions.Label(Lab.VideoActionOf())}'s: a moment in between worth a step. Named as written beside it (or \"{AddedName("")}\").";
            var notFound = Lab.videoKeys.Where(k => k.NotFound).Select(k => k.Name).ToList();
            makeButton.Disabled = missingSteps.Count > 0 || Lab.PosedKeys().Count < 2 || Lab.FindingPose || notFound.Count > 0;
            makeButton.Text = missingSteps.Count > 0 ? $"Make the motion ({missingSteps.Count} to set)" : Lab.FindingPose ? "Make the motion (finding joints…)" : "Make the motion ▶";
            makeButton.TooltipText = missingSteps.Count > 0 ? $"Still to set: {string.Join(", ", missingSteps)}"
                : notFound.Count > 0 ? $"No player found at: {string.Join(", ", notFound)} (set it on another frame, or find its joints again)"
                : "Fit the lab's body to every key frame and play the motion to review.";
            RefreshJoints();
            if (here != null && here.HasPose)
            {
                // The small Pose Lab looks from where the video's camera is (image up its up).
                // The camera found from the table's points, when it was: from it to the hips.
                VideoMoment m = Lab.MomentOf(here);
                Vector3 dir = m.ToCamera, up = -m.CamY.Normalized();
                if (m.Eye is Vector3 eye && m.HipsAt is Vector2 hips)
                    (dir, up) = ((eye - new Vector3(hips.X, DefaultValue("hips.height"), hips.Y)).Normalized(), m.EyeUp);
                up = (up - dir * up.Dot(dir)).Normalized();
                if (dir.IsFinite() && up.IsFinite() && dir.LengthSquared() > 0.5f)
                    (viewDir, viewUp) = (dir, up);
            }
            timeLabel.Text = TimeText();
            filling = false;
            Say(Guide());
        }

        static string KeyState(VideoKey k) => k.HasPose ? "" : k.NotFound ? "   ✖ no player found" : "   … finding joints";

        // A key frame's joints found (the pose service answered).
        public void KeyFound(VideoKey k)
        {
            if (k.HasPose)
            {
                Lab.SyncKeyStep(k); // its step, in the lab
                if (k.Frame == frame)
                    lastLabStep = Lab.step;
                else if (Lab.StepOfKey(Key ?? k) is int s && s >= 0)
                    Lab.PickStep(s); // the lab keeps showing the key frame shown here
            }
            if (Visible)
                RefreshPanel();
            if (k.Frame == frame && k.HasPose)
                viewFitted = false;
        }

        // The joints at the key frame shown: found by the video, and missing (with what was placed at
        // the key frame before it).
        void RefreshJoints()
        {
            VideoKey k = Key;
            jointsBox.Visible = k != null && k.HasPose;
            foundList.Clear();
            missingList.Clear();
            if (k == null || !k.HasPose)
            {
                hint.Text = k == null ? "Play or drag the timeline to a moment of the stroke, then \"Set\" it as the step it is."
                    : k.NotFound ? "No player was found in this frame: set the step on a frame near it, or \"Find the joints again\" (the Pose Lab's player picker chooses who, with two)."
                    : "Finding the player's joints in this frame (MediaPipe, about a second)…";
                if (k != null && k.NotFound)
                    jointsBox.Visible = true;
                return;
            }
            keyTitle.Text = $"JOINTS at \"{k.Name}\"";
            var (_, _, seen) = Lab.FrameJoints(k);
            VideoKey before = PreviousKey(k);
            foundList.Visible = missingList.Visible = true;
            foreach (LabJoint j in LabJoints)
            {
                float s = SeenOf(j, seen);
                bool placed = Placed(k, j);
                bool placedBefore = before != null && Placed(before, j);
                if (Deleted(k, j))
                {
                    int del = missingList.AddItem($"{j.Words} — deleted: put between the key frames around");
                    missingList.SetItemMetadata(del, j.Key);
                    missingList.SetItemCustomFgColor(del, new Color(0.6f, 0.6f, 0.65f));
                    continue;
                }
                if (s >= FoundSeen) // found in this frame: this frame's own (whatever was placed at the one before)
                {
                    int row = foundList.AddItem($"{j.Words}{(placed ? "   ✎ moved" : "")}{DepthWords(k, j)}   {s * 100f:F0}%");
                    foundList.SetItemMetadata(row, j.Key);
                    if (placed)
                        foundList.SetItemCustomFgColor(row, new Color(1f, 0.85f, 0.3f));
                }
                else
                {
                    string state = placed ? "placed ✔" : placedBefore ? "placed at the key frame before: place it again" : "not placed (the lab will guess)";
                    int row = missingList.AddItem($"{j.Words} — {state}{DepthWords(k, j)}");
                    missingList.SetItemMetadata(row, j.Key);
                    missingList.SetItemCustomFgColor(row, placed ? new Color(1f, 0.85f, 0.3f) : placedBefore ? new Color(0.4f, 0.85f, 1f) : new Color(0.85f, 0.6f, 0.55f));
                    if (j.Key == placing)
                        missingList.Select(row);
                }
            }
            facePicker.Selected = k.Face switch { "forehand" => 1, "backhand" => 2, "edge" => 3, _ => 0 };
            int toPlace = Enumerable.Range(0, missingList.ItemCount).Count(i => !missingList.GetItemText(i).Contains("placed ✔"));
            hint.Text = $"{foundList.ItemCount} joints found, {missingList.ItemCount} missing ({toPlace} not placed: the lab will guess those).";
            RefreshScene(k);
        }

        // The ball, the table's corners and the net's ends at this key frame: marked here, from another
        // key frame (the table's), or not; and what the table's points make of the player's place.
        void RefreshScene(VideoKey k)
        {
            sceneList.Clear();
            var (marks, from) = Lab.TableMarks(k);
            foreach (var (key, words) in ScenePoints())
            {
                string state = k.Fixed.ContainsKey(key) ? "placed ✔"
                    : from != null && from != k && marks.ContainsKey(key) ? $"from \"{from.Name}\""
                    : "not placed";
                int row = sceneList.AddItem($"{words} — {state}");
                sceneList.SetItemMetadata(row, key);
                sceneList.SetItemCustomFgColor(row, k.Fixed.ContainsKey(key) ? new Color(1f, 0.85f, 0.3f) : state.StartsWith("from") ? new Color(0.4f, 0.85f, 1f) : new Color(0.7f, 0.7f, 0.75f));
                if (key == placing)
                    sceneList.Select(row);
            }
            tableFit.Text = Lab.TableFitOf(k) is var (fit, source)
                ? $"Table: the player turned {Mathf.Abs(fit.Degrees):F0}° {(fit.Degrees >= 0f ? "to its left" : "to its right")} from facing it straight, its hips {Mathf.Abs(fit.Hips.X):F2} m {(fit.Hips.X >= 0f ? "left" : "right")} of the middle line (the points miss by {fit.MissCm:F0} cm)"
                    + (fit.OtherEnd ? "; the points' names read from the other end (as the video shows them)" : "")
                    + (fit.Eye == null ? "; mark four or more to find the camera's view too" : "")
                    + (source != k ? $"; points from \"{source.Name}\"" : "") + "."
                : "Table: mark two or more of its points to turn the player to it (else it faces the table straight); four or more find the camera's view too.";
        }

        static IEnumerable<(string Key, string Words)> ScenePoints() =>
            new[] { (BallMark, "ball") }.Concat(TablePoints.Select(t => (t.Key, t.Words)));

        static string ScenePointWords(string key) => ScenePoints().FirstOrDefault(p => p.Key == key).Words;
        static bool IsScenePoint(string key) => key == BallMark || IsTablePoint(key);

        // How well the video shows a lab joint: its point's (the hips: the less seen of the two); the
        // racket's head not at all (MediaPipe does not see the racket).
        static float SeenOf(LabJoint j, Dictionary<string, float> seen) =>
            IsBladeJoint(j) ? 0f : j.Key == "hips" ? Mathf.Min(seen["lhip"], seen["rhip"]) : seen.GetValueOrDefault(j.Members[0]);

        static bool Placed(VideoKey k, LabJoint j) => j.Members.Any(k.Fixed.ContainsKey);
        static bool Deleted(VideoKey k, LabJoint j) => j.Members.Any(k.Deleted.Contains);

        // The joint last clicked deleted here: the video's place not used, put between the key
        // frames around instead (what was placed by hand taken away with it).
        void DeleteChosen()
        {
            if (Key is not VideoKey k || chosen == null || LabJointOf(chosen) is not LabJoint j)
                return;
            foreach (string name in j.Members)
            {
                k.Deleted.Add(name);
                k.Fixed.Remove(name);
            }
            placing = null;
            Changed();
            Say($"{j.Words} deleted at \"{k.Name}\": put between the key frames around that have it (the grey cross). Place it, or \"Restore it\", to have it back.");
        }

        void RestoreChosen()
        {
            if (Key is not VideoKey k || chosen == null || LabJointOf(chosen) is not LabJoint j)
                return;
            bool was = false;
            foreach (string name in j.Members)
                was |= k.Deleted.Remove(name) | k.Fixed.Remove(name);
            if (was)
                Changed();
        }

        // How well a joint must be seen to count as found (else it is listed as missing).
        const float FoundSeen = 0.6f;

        VideoKey PreviousKey(VideoKey k) => Lab.videoKeys.Where(x => x.Frame < k.Frame).OrderByDescending(x => x.Frame).FirstOrDefault();

        // What to do next, under the frame.
        string Guide()
        {
            if (Lab.videoRun != null)
                return Lab.VideoReading();
            if (Track == null)
                return "";
            string[] names = Lab.VideoStepNames();
            var unset = names.Where(n => Lab.videoKeys.All(k => k.Name != n)).ToList();
            VideoKey k = Key;
            string here = k != null
                ? $"Key frame \"{k.Name}\": drag a found joint where it really is; pick a missing one and click where it is (right-click a placed one to take it back)."
                : "Stop on a frame (Space plays and stops, Left / Right a frame, Shift: ten) and set it as a key frame: the player's joints are found in it then.";
            string left = unset.Count > 0 ? $"  Still to set for {StrokeActions.Label(Lab.VideoActionOf())}: {string.Join(", ", unset)}." : "  All its key frames are set: \"Make the motion\" (or \"Add a step here\" on a moment in between).";
            return here + left + RigSummary();
        }

        // How far the lab's body is off the video's on this key frame (when its step is shown).
        string RigSummary()
        {
            if (LinkedMoment() is not VideoMoment m)
                return "";
            Dictionary<string, Vector2> rig = RigPoints(m), video = VideoPointsOf(Key);
            var misses = LabJoints.Where(j => rig.ContainsKey(j.Key) && video.ContainsKey(j.Key))
                .Select(j => (j.Words, Cm: (rig[j.Key] - video[j.Key]).Length() / m.PixelsPerMetre * 100f))
                .OrderByDescending(x => x.Cm).Take(3).Select(x => $"{x.Words} {x.Cm:F1} cm");
            return $"  The lab's body off the video's most at: {string.Join(", ", misses)}.";
        }

        // The key frame shown, when the lab shows the step made from it.
        VideoMoment LinkedMoment()
        {
            if (Key is not VideoKey k || !k.HasPose || Lab.StepOfKey(k) is int s && (s < 0 || s != Lab.step) || Lab.playing || Lab.PosedKeys().Count == 0)
                return null;
            return Lab.MomentOf(k);
        }

        void Say(string text) => info.Text = text;

        void PickKeyRow(int row)
        {
            string name = (string)keyList.GetItemMetadata(row);
            if (Lab.videoKeys.FirstOrDefault(k => k.Name == name) is VideoKey k)
                GoTo(k.Frame);
            else
            {
                namePicker.Selected = System.Array.IndexOf(Lab.VideoStepNames(), name);
                nameEdit.Text = name;
                Say($"\"{name}\" is not set yet: go to its moment in the video, then \"Set\".");
            }
        }

        void SetKey()
        {
            string name = nameEdit.Text.Trim();
            if (Track == null || name.Length == 0)
                return;
            if (Key is VideoKey here)
            {
                here.Name = name; // renamed (its step too)
                Lab.SyncKeyStep(here);
            }
            else
            {
                // A step set again elsewhere moves (what was placed belongs to the old frame); the
                // player's joints are found in the new one, and it becomes its step when they are.
                foreach (VideoKey old in Lab.videoKeys.Where(k => k.Name == name).ToList())
                {
                    Lab.RemoveKeyStep(old);
                    Lab.videoKeys.Remove(old);
                }
                var key = new VideoKey { Frame = frame, Name = name };
                Lab.videoKeys.Add(key);
                Lab.FindPose(key);
            }
            Lab.SaveVideoKeys();
            RefreshPanel();
        }

        // A step of its own at the frame shown, besides the action's (never moving one of them):
        // named as written beside the button, or "extra step N"; its joints found as any key frame's.
        public void AddStep()
        {
            if (Track == null)
                return;
            if (Key is VideoKey here)
            {
                Say($"This frame is already the key frame \"{here.Name}\": go to another frame to add a step there.");
                return;
            }
            string name = AddedName(addName.Text);
            var key = new VideoKey { Frame = frame, Name = name };
            Lab.videoKeys.Add(key);
            Lab.FindPose(key);
            Lab.SaveVideoKeys();
            addName.Text = "";
            RefreshPanel();
            Say($"Step \"{name}\" added at {Track.T[frame]:F2} s (✚ in the list): its joints are being found, then it is a step in the lab between the ones around it. "
                + "Rename it with the name box and \"Rename\"; \"Remove\" takes it away.");
        }

        // An added step's name: the one written (or "extra step"), numbered when taken (by a key
        // frame, or one of the action's steps: an added step never stands for one).
        string AddedName(string written)
        {
            string[] names = Lab.VideoStepNames();
            string baseName = written.Trim().Length > 0 ? written.Trim() : "extra step";
            bool Taken(string n) => names.Contains(n) || Lab.videoKeys.Any(k => k.Name == n);
            if (!Taken(baseName) && written.Trim().Length > 0)
                return baseName;
            for (int i = written.Trim().Length > 0 ? 2 : 1; ; i++)
                if (!Taken($"{baseName} {i}"))
                    return $"{baseName} {i}";
        }

        static readonly Color AddedColour = new(0.4f, 0.95f, 0.8f);

        // The "Add a step here" button: green, a white plus in a disc as its icon, larger than the
        // panel's other buttons (they are all plain grey).
        static Button AddStepButton()
        {
            var b = new Button { Text = "Add a step here", Icon = PlusIcon(22), ExpandIcon = false, CustomMinimumSize = new Vector2(0f, 34f) };
            b.AddThemeFontSizeOverride("font_size", 14);
            b.AddThemeConstantOverride("h_separation", 6);
            StyleBoxFlat Box(Color fill, Color edge) => new()
            {
                BgColor = fill, BorderColor = edge, BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
                CornerRadiusTopLeft = 7, CornerRadiusTopRight = 7, CornerRadiusBottomLeft = 7, CornerRadiusBottomRight = 7,
                ContentMarginLeft = 10, ContentMarginRight = 12, ContentMarginTop = 4, ContentMarginBottom = 4,
            };
            b.AddThemeStyleboxOverride("normal", Box(new Color(0.08f, 0.5f, 0.36f), new Color(0.45f, 1f, 0.78f)));
            b.AddThemeStyleboxOverride("hover", Box(new Color(0.12f, 0.62f, 0.45f), new Color(0.7f, 1f, 0.88f)));
            b.AddThemeStyleboxOverride("pressed", Box(new Color(0.05f, 0.38f, 0.27f), new Color(0.45f, 1f, 0.78f)));
            b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            b.AddThemeStyleboxOverride("disabled", Box(new Color(0.16f, 0.2f, 0.19f), new Color(0.28f, 0.36f, 0.33f)));
            foreach (string c in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
                b.AddThemeColorOverride(c, Colors.White);
            b.AddThemeColorOverride("font_disabled_color", new Color(0.5f, 0.58f, 0.55f));
            b.AddThemeColorOverride("icon_disabled_color", new Color(1f, 1f, 1f, 0.35f));
            return b;
        }

        // A white disc with a green plus cut in it (smooth edged).
        static ImageTexture PlusIcon(int size)
        {
            var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
            float c = (size - 1) / 2f, r = size / 2f - 0.5f, arm = size * 0.28f, half = size * 0.08f;
            var plus = new Color(0.08f, 0.5f, 0.36f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Abs(x - c), dy = Mathf.Abs(y - c);
                    float disc = Mathf.Clamp(r - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f, 0f, 1f);
                    float bar = Mathf.Max(Mathf.Clamp(half - dx + 0.5f, 0f, 1f) * Mathf.Clamp(arm - dy + 0.5f, 0f, 1f),
                                          Mathf.Clamp(half - dy + 0.5f, 0f, 1f) * Mathf.Clamp(arm - dx + 0.5f, 0f, 1f));
                    Color px = Colors.White.Lerp(plus, bar);
                    px.A = disc;
                    img.SetPixel(x, y, px);
                }
            return ImageTexture.CreateFromImage(img);
        }

        void RemoveKey()
        {
            if (Key is not VideoKey k)
                return;
            Lab.RemoveKeyStep(k);
            Lab.videoKeys.Remove(k);
            Lab.SaveVideoKeys();
            RefreshPanel();
        }

        void SwapKey()
        {
            if (Key is not VideoKey k)
                return;
            k.Swapped = !k.Swapped;
            SwapSides(k.Fixed);
            k.Deleted = SwapSides(k.Deleted);
            Changed();
        }

        void TurnRound()
        {
            if (Track == null)
                return;
            Lab.videoTurned = !Lab.videoTurned;
            foreach (VideoKey k in Lab.videoKeys)
            {
                SwapSides(k.Fixed);
                k.Deleted = SwapSides(k.Deleted);
            }
            Lab.SaveVideoKeys();
            Lab.SyncAllKeys(); // every key frame's step: the player turned round
            lastLabStep = Lab.step;
            RefreshPanel();
        }

        // Something placed or changed on a key frame: kept, and its step fitted again (as asked).
        void Changed()
        {
            Lab.SaveVideoKeys();
            if (autoFit.ButtonPressed && Key is VideoKey k && k.HasPose)
            {
                Lab.SyncKeyStep(k);
                lastLabStep = Lab.step;
            }
            RefreshPanel();
        }

        // ---------------------------------------------------------------- playing

        void GoTo(int f)
        {
            if (Track == null)
                return;
            frame = Mathf.Clamp(f, 0, Track.Count - 1);
            playAt = frame;
            placing = null;
            hover = null;
            // The lab shows the step made from this key frame.
            if (Key is VideoKey k && Lab.StepOfKey(k) is int s && s >= 0 && s != Lab.step)
            {
                Lab.playing = false;
                Lab.PickStep(s);
            }
            lastLabStep = Lab.step; // the lab's step is this frame's now: not followed back to another
            RefreshPanel();
        }

        void GoKey(int way)
        {
            var frames = Lab.videoKeys.Select(k => k.Frame).OrderBy(f => f).ToList();
            int to = way < 0 ? frames.LastOrDefault(f => f < frame, -1) : frames.FirstOrDefault(f => f > frame, -1);
            if (to >= 0)
                GoTo(to);
        }

        void TogglePlayVideo()
        {
            if (Track == null)
                return;
            playingVideo = !playingVideo;
            if (playingVideo && frame >= Track.Count - 1)
                frame = 0;
            playAt = frame;
            playButton.Text = playingVideo ? "⏸" : "▶";
            if (!playingVideo)
                GoTo(frame);
        }

        public override void _Process(double delta)
        {
            if (!Visible)
                return;
            if (Track != null && playingVideo)
            {
                float speed = new[] { 1f, 0.5f, 0.25f, 0.1f }[Mathf.Max(speedPicker.Selected, 0)];
                playAt += (float)delta * speed * Track.Fps;
                if (playAt >= Track.Count - 1)
                {
                    playAt = Track.Count - 1;
                    TogglePlayVideo();
                }
                frame = (int)playAt;
                timeLabel.Text = TimeText();
            }
            // A step picked in the lab: its key frame shown.
            if (Track != null && Lab.step != lastLabStep && !playingVideo)
            {
                lastLabStep = Lab.step;
                if (Lab.step >= 0 && Lab.step < Lab.sequence.Steps.Count && Lab.sequence.Steps[Lab.step].Video is string link
                    && link.StartsWith(Lab.videoName + "@") && int.TryParse(link[(link.LastIndexOf('@') + 1)..], out int f) && f != frame)
                {
                    frame = f;
                    RefreshPanel();
                }
            }
            sinceInfo += delta;
            if (sinceInfo > 0.4)
            {
                sinceInfo = 0;
                if (Lab.videoRun != null || Track == null)
                    RefreshPanel();
                else if (!playingVideo)
                    Say(Guide());
            }
            PlaceDepthBox();
            SharpenLabView();
            canvas.QueueRedraw();
            timeline.QueueRedraw();
            labStatus.Text = "Pose Lab: " + Lab.status.Text;
            if (!saveName.HasFocus() && saveName.Text != Lab.sequenceName.Text)
                saveName.Text = Lab.sequenceName.Text;
            // The small Pose Lab follows the figure: from where the video's camera is, turned round
            // and zoomed as asked.
            Vector3 target = Lab.upper.GlobalPosition + Vector3.Up * 0.1f;
            Vector3 dir = Lab.W(viewDir).Normalized(), up = Lab.W(viewUp);
            if (orbitYaw != 0f || orbitPitch != 0f)
            {
                dir = dir.Rotated(Vector3.Up, orbitYaw);
                Vector3 side = Vector3.Up.Cross(dir);
                if (side.LengthSquared() > 1e-6f)
                    dir = dir.Rotated(side.Normalized(), orbitPitch);
                up = Vector3.Up;
            }
            Vector3 eye = target + dir * labDistance;
            if (!eye.IsEqualApprox(target) && Mathf.Abs(dir.Normalized().Dot(up.Normalized())) < 0.99f)
                labCamera.LookAtFromPosition(eye, target, up);
        }

        public override void _Input(InputEvent e)
        {
            // A click anywhere but on the depth box closes it (a joint clicked or picked from a list
            // opens it again: PickPartOf).
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right } press
                && depthBox != null && depthBox.Visible && !depthBox.GetGlobalRect().HasPoint(press.Position))
                depthHiddenFor = chosen;
            if (!Visible || Track == null || e is not InputEventKey { Pressed: true } key || GuiGetFocusOwner() is LineEdit)
                return;
            switch (key.Keycode)
            {
                case Godot.Key.Space:
                    TogglePlayVideo();
                    break;
                case Godot.Key.Left:
                    GoTo(frame - (key.ShiftPressed ? 10 : 1));
                    break;
                case Godot.Key.Right:
                    GoTo(frame + (key.ShiftPressed ? 10 : 1));
                    break;
                case Godot.Key.Delete:
                case Godot.Key.Backspace:
                    if (hover != null)
                        chosen = hover;
                    DeleteChosen();
                    break;
                default:
                    return;
            }
            SetInputAsHandled();
        }

        // The frame's picture (a few kept, for playing back and forth).
        Texture2D PictureOf(int f)
        {
            if (cache.TryGetValue(f, out Texture2D t))
                return t;
            Image image = Image.LoadFromFile(Track.FramePath(f));
            if (image == null)
                return null;
            t = ImageTexture.CreateFromImage(image);
            cache[f] = t;
            cached.Enqueue(f);
            while (cached.Count > 90)
                cache.Remove(cached.Dequeue());
            return t;
        }

        // ---------------------------------------------------------------- the frame

        // The Pose Lab's own colours: a place cyan, a turning handle magenta, the picked part yellow.
        static readonly Color PlaceColour = new(0.3f, 0.9f, 1f), TurnColour = new(1f, 0.35f, 0.85f), PickedColour = new(1f, 0.85f, 0.1f),
                              LineColour = new(0.55f, 0.95f, 1f, 0.9f), GhostColour = new(0.4f, 0.85f, 1f, 0.5f);

        Vector2 ToCanvas(Vector2 px) => offset + px * zoom;
        Vector2 ToPicture(Vector2 c) => (c - offset) / zoom;

        // A key frame's lab points from the video: its joints as found, with those placed by hand.
        // (A deleted joint: where it is put between the key frames around, or none.)
        Dictionary<string, Vector2> VideoPointsOf(VideoKey k)
        {
            Dictionary<string, Vector2> px = k.Deleted.Count > 0 && Lab.PosedKeys().Count > 0 ? Lab.MomentOf(k).Image : Lab.FrameJoints(k).Px;
            var guessed = k.Deleted.Count > 0 && Lab.PosedKeys().Count > 0 ? Lab.MomentOf(k).Guessed : new HashSet<string>();
            return LabPoints(name => k.Fixed.TryGetValue(name, out Vector2 f) ? f
                : k.Deleted.Contains(name) && !guessed.Contains(name) ? null
                : px.TryGetValue(name, out Vector2 v) ? v : null);
        }

        // The lab's body's points on the frame (when its step is shown).
        Dictionary<string, Vector2> RigPoints(VideoMoment m)
        {
            var rig = Lab.RigOnFrame(m, Lab.KeyScale());
            return LabPoints(name => rig.Joints.TryGetValue(name, out Vector2 v) ? v : null);
        }

        // A lab joint moved by hand to a place on the picture: its video points moved with it (a
        // hand's fingers with its wrist; the hips' two together); the racket head placed there.
        void MoveJoint(VideoKey k, string key, Vector2 to)
        {
            if (IsScenePoint(key))
            {
                k.Fixed[key] = to; // the ball, a table's corner, a net's end: where it is on the picture
                return;
            }
            LabJoint j = LabJointOf(key);
            foreach (string name in j.Members)
                k.Deleted.Remove(name); // placed: used again
            if (IsBladeJoint(j))
            {
                k.Fixed[j.Members[0]] = to;
                return;
            }
            var (px, _, _) = Lab.FrameJoints(k);
            Vector2 now = VideoPointsOf(k).TryGetValue(key, out Vector2 shown) ? shown : px[j.Members[0]];
            foreach (string name in j.Members)
                k.Fixed[name] = (k.Fixed.TryGetValue(name, out Vector2 f) ? f : px[name]) + (to - now);
        }

        void FitView(Dictionary<string, Vector2> px, Dictionary<string, float> seen)
        {
            if (canvas.Size.X < 50f || canvas.Size.Y < 50f)
                return; // not laid out yet: the next draw
            // The player (its joints, those off the picture too: MediaPipe guesses them there) and the
            // picture, with the room round it where joints the picture cuts off are placed.
            var points = px == null ? new List<Vector2>() : px.Values.ToList();
            Vector2 lo = Vector2.Zero, hi = Track.Size;
            foreach (Vector2 p in points)
            {
                lo = lo.Min(p);
                hi = hi.Max(p);
            }
            Vector2 roomLo = -OffPicture * Track.Size, roomHi = Track.Size + OffPictureBelow * Track.Size;
            lo = lo.Max(roomLo) - Vector2.One * 20f;
            hi = hi.Min(roomHi) + Vector2.One * 20f;
            hi.Y = Mathf.Max(hi.Y, Track.Size.Y * (1f + OffPictureBelow * 0.6f)); // always some room under it for the legs
            Vector2 size = hi - lo;
            zoom = Mathf.Min(canvas.Size.X / size.X, canvas.Size.Y / size.Y);
            offset = canvas.Size / 2f - (lo + hi) / 2f * zoom;
            viewFitted = true;
        }

        // The room round the picture (of its size) where a joint it cuts off is placed: a little each
        // side and above, more below (the legs, the camera so often on the upper body).
        static readonly Vector2 OffPicture = new(0.25f, 0.15f);
        const float OffPictureBelow = 0.7f;

        // Outside the picture: shaded, its edge dashed, said.
        void DrawRoom(FrameCanvas c, Font font)
        {
            Rect2 room = new(ToCanvas(-OffPicture * Track.Size), (Vector2.One + OffPicture + new Vector2(OffPicture.X, OffPictureBelow)) * Track.Size * zoom);
            c.DrawRect(room, new Color(0.13f, 0.14f, 0.18f));
            Vector2 a = ToCanvas(Vector2.Zero), b = ToCanvas(Track.Size);
            Color edge = new(0.6f, 0.65f, 0.75f, 0.8f);
            c.DrawDashedLine(a, new Vector2(b.X, a.Y), edge, 1f, 6f);
            c.DrawDashedLine(new Vector2(b.X, a.Y), b, edge, 1f, 6f);
            c.DrawDashedLine(b, new Vector2(a.X, b.Y), edge, 1f, 6f);
            c.DrawDashedLine(new Vector2(a.X, b.Y), a, edge, 1f, 6f);
            c.DrawString(font, new Vector2(a.X + 6f, b.Y + 18f), "outside the picture: place joints it cuts off here (the legs…)", HorizontalAlignment.Left, Mathf.Max(b.X - a.X - 12f, 60f), 12, new Color(0.7f, 0.75f, 0.85f));
        }

        // A skeleton as the lab draws it: its bones (to a turning handle dotted magenta, the picked
        // part's yellow), then its markers (a place round, a handle square; faint where the video
        // hardly shows it, ringed white where it was placed by hand).
        void DrawSkeleton(FrameCanvas c, Dictionary<string, Vector2> p, System.Func<LabJoint, float> seenOf, System.Func<LabJoint, bool> placedOf, System.Func<LabJoint, bool> deletedOf, string pickedPart)
        {
            if (!showMissing.ButtonPressed)
            {
                // Only what the video shows and what was placed: the missing (and deleted) left out,
                // and the bones to them.
                p = new Dictionary<string, Vector2>(p);
                foreach (LabJoint j in LabJoints.Where(j => deletedOf(j) || !placedOf(j) && seenOf(j) < FoundSeen))
                    p.Remove(j.Key);
            }
            bool Picked(string key) => LabJointOf(key)?.Part == pickedPart || key == "chest" && pickedPart == "Waist";
            foreach (var (a, b) in LabBones)
            {
                if (!p.TryGetValue(a, out Vector2 pa) || !p.TryGetValue(b, out Vector2 pb))
                    continue;
                bool turn = LabJointOf(b)?.Turn ?? false;
                Color colour = Picked(a) || Picked(b) ? PickedColour : turn ? TurnColour : LineColour;
                if (turn)
                    c.DrawDashedLine(ToCanvas(pa), ToCanvas(pb), colour, 2f, 4f);
                else
                    c.DrawLine(ToCanvas(pa), ToCanvas(pb), colour, Picked(a) || Picked(b) ? 3f : 2f, true);
            }
            if (p.TryGetValue("chest", out Vector2 chestAt))
                c.DrawCircle(ToCanvas(chestAt), 6f, pickedPart == "Waist" ? PickedColour : new Color(PlaceColour, 0.75f));
            foreach (LabJoint j in LabJoints)
            {
                if (!p.TryGetValue(j.Key, out Vector2 at))
                    continue;
                Vector2 q = ToCanvas(at);
                if (deletedOf(j))
                {
                    // Deleted: a grey cross where it is put between the key frames around.
                    Color grey = new(0.75f, 0.75f, 0.8f, 0.85f);
                    c.DrawLine(q + new Vector2(-6f, -6f), q + new Vector2(6f, 6f), grey, 2f);
                    c.DrawLine(q + new Vector2(-6f, 6f), q + new Vector2(6f, -6f), grey, 2f);
                    c.DrawArc(q, 9f, 0f, Mathf.Tau, 20, grey, 1f, true);
                    continue;
                }
                float seen = placedOf(j) ? 1f : seenOf(j);
                Color colour = j.Part == pickedPart ? PickedColour : j.Turn ? TurnColour : PlaceColour;
                colour.A = seen >= FoundSeen ? 0.9f : 0.35f;
                if (j.Turn)
                    c.DrawRect(new Rect2(q - Vector2.One * 6f, Vector2.One * 12f), colour);
                else
                    c.DrawCircle(q, 7f, colour);
                if (seen < FoundSeen)
                    c.DrawArc(q, 8f, 0f, Mathf.Tau, 16, new Color(colour, 0.9f), 1.5f, true); // hardly seen: a faint marker, outlined
                if (placedOf(j))
                    c.DrawArc(q, 10f, 0f, Mathf.Tau, 20, Colors.White, 2f, true);
            }
        }

        void DrawOn(FrameCanvas c)
        {
            c.DrawRect(new Rect2(Vector2.Zero, c.Size), new Color(0.04f, 0.04f, 0.05f));
            Font font = ThemeDB.FallbackFont;
            depthBoxAt = null; // (DrawChosen sets it when there is a joint to show it at)
            if (Track == null)
            {
                c.DrawString(font, new Vector2(16f, 30f), Lab.videoRun != null ? Lab.VideoReading() : "No video imported yet.", HorizontalAlignment.Left, c.Size.X - 32f, 15, Colors.White);
                return;
            }
            if (pictureFrame != frame)
            {
                picture = PictureOf(frame) ?? picture;
                pictureFrame = frame;
            }
            VideoKey k = Key;
            if (k == null || !k.HasPose)
            {
                // Not a key frame (or its joints not found yet): the picture alone.
                if (!viewFitted)
                    FitView(null, null);
                DrawRoom(c, font);
                if (picture != null)
                    c.DrawTextureRect(picture, new Rect2(ToCanvas(Vector2.Zero), Track.Size * zoom), false);
                DrawStrip(c, font, null);
                return;
            }
            var (px, _, seen) = Lab.FrameJoints(k);
            if (!viewFitted)
                FitView(px, seen);
            DrawRoom(c, font);
            if (picture != null)
                c.DrawTextureRect(picture, new Rect2(ToCanvas(Vector2.Zero), Track.Size * zoom), false);
            c.DrawRect(new Rect2(Vector2.Zero, c.Size), new Color(0f, 0f, 0f, 0.2f));

            string pickedPart = Parts[Lab.part].Name;
            Dictionary<string, Vector2> video = VideoPointsOf(k);
            // (The lab's body is not drawn over the video: the small Pose Lab beside it shows it.)
            if (showVideo.ButtonPressed)
                DrawSkeleton(c, video, j => SeenOf(j, seen), j => Placed(k, j), j => Deleted(k, j), pickedPart);
            // The joint being placed (picked from the missing list): where it was placed at the key
            // frame before, faintly, as a hint. (Only that one: the key frame before's joints all
            // drawn here were taken for this frame's.)
            if (placing != null && LabJointOf(placing) is LabJoint placingJoint && !Placed(k, placingJoint)
                && PreviousKey(k) is VideoKey before && before.HasPose && Placed(before, placingJoint)
                && VideoPointsOf(before).TryGetValue(placingJoint.Key, out Vector2 was))
            {
                c.DrawArc(ToCanvas(was), 9f, 0f, Mathf.Tau, 20, GhostColour, 2f, true);
                c.DrawString(font, ToCanvas(was) + new Vector2(11f, 4f), $"{placingJoint.Words} at the key frame before", HorizontalAlignment.Left, -1, 11, GhostColour);
            }
            DrawScene(c, font, k);
            if (hover != null && HoverPoint(video) is Vector2 h)
                c.DrawArc(ToCanvas(h), 14f, 0f, Mathf.Tau, 24, Colors.White, 2f, true);
            DrawChosen(c, font, video);
            DrawStrip(c, font, seen);
        }

        // ---------------------------------------------------------------- the chosen joint's depth

        // Right beside the joint last clicked on the frame: how far it is from the camera's eye, made
        // nearer or farther (2 cm a click, Shift 10 cm; Ctrl + wheel over the frame 1 cm a notch).
        // It moves along the camera's line, so the picture stays the same and only the lab's body
        // changes. A blade's point: from the right wrist's depth (not set: any depth on its line).
        PanelContainer depthBox;
        Label depthTitle, depthValue;
        Vector2? depthBoxAt; // the joint on the canvas while the box is shown (set as the frame is drawn)
        string depthHiddenFor; // the chosen joint whose box a click elsewhere closed

        void BuildDepthBox()
        {
            depthBox = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
            var style = new StyleBoxFlat { BgColor = new Color(0.05f, 0.05f, 0.08f, 0.9f), BorderColor = PickedColour };
            style.SetBorderWidthAll(1);
            style.SetCornerRadiusAll(4);
            style.SetContentMarginAll(4f);
            depthBox.AddThemeStyleboxOverride("panel", style);
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 2);
            depthTitle = new Label { HorizontalAlignment = HorizontalAlignment.Center };
            depthTitle.AddThemeFontSizeOverride("font_size", 11);
            depthTitle.AddThemeColorOverride("font_color", PickedColour);
            depthValue = new Label { HorizontalAlignment = HorizontalAlignment.Center };
            depthValue.AddThemeFontSizeOverride("font_size", 12);
            Button Step(string text, float by, string tip)
            {
                var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, TooltipText = tip };
                b.AddThemeFontSizeOverride("font_size", 12);
                b.Pressed += () => NudgeDepth(Input.IsKeyPressed(Godot.Key.Shift) ? by * 5f : by);
                return b;
            }
            var reset = new Button { Text = "reset", FocusMode = Control.FocusModeEnum.None, Flat = true, TooltipText = "Back to the depth the video has (a blade's point: any depth)." };
            reset.AddThemeFontSizeOverride("font_size", 11);
            reset.Pressed += () =>
            {
                if (Key is VideoKey k && LabJointOf(chosen) is LabJoint j)
                    SetDepth(k, j, null);
            };
            box.AddChild(depthTitle);
            box.AddChild(Step("▲ nearer", 0.02f, "Nearer the camera by 2 cm (Shift: 10 cm). Ctrl + wheel over the frame: 1 cm a notch."));
            box.AddChild(depthValue);
            box.AddChild(Step("▼ farther", -0.02f, "Farther from the camera by 2 cm (Shift: 10 cm). Ctrl + wheel over the frame: 1 cm a notch."));
            box.AddChild(reset);
            depthBox.AddChild(box);
            canvas.AddChild(depthBox);
        }

        // Whether a joint's depth can be set at a key frame: found or placed, not deleted; a blade's
        // point once it is placed.
        bool DepthSettable(VideoKey k, LabJoint j) => k != null && k.HasPose && !Deleted(k, j) && (!IsBladeJoint(j) || Placed(k, j));

        static string DepthText(VideoKey k, LabJoint j) => k.Depth.TryGetValue(j.Members[0], out float d)
            ? Mathf.Abs(d) < 0.0005f ? (IsBladeJoint(j) ? "as deep as the wrist" : "as the video has it") : $"{Mathf.Abs(d) * 100f:F0} cm {(d > 0f ? "nearer" : "farther")}"
            : IsBladeJoint(j) ? "not set: any depth" : "as the video has it";

        static string DepthWords(VideoKey k, LabJoint j) =>
            k.Depth.TryGetValue(j.Members[0], out float d) ? $"   ↕ {(d >= 0f ? "+" : "−")}{Mathf.Abs(d) * 100f:F0} cm" : "";

        void NudgeDepth(float by)
        {
            if (Key is VideoKey k && LabJointOf(chosen) is LabJoint j && DepthSettable(k, j))
                SetDepth(k, j, k.Depth.GetValueOrDefault(j.Members[0]) + by);
        }

        // A joint's depth set (its members all moved alike), or taken back (null).
        void SetDepth(VideoKey k, LabJoint j, float? depth)
        {
            foreach (string name in j.Members)
            {
                if (depth is float d && (IsBladeJoint(j) || Mathf.Abs(d) >= 0.0005f))
                    k.Depth[name] = Mathf.Clamp(d, -1.5f, 1.5f);
                else
                    k.Depth.Remove(name);
            }
            Changed();
            Lab.Status($"{j.Words}: {DepthText(k, j)}{(IsBladeJoint(j) ? " (from the right wrist)" : " (from the camera's eye)")}");
        }

        // The box put beside the chosen joint (or hidden), as the frame was last drawn.
        void PlaceDepthBox()
        {
            VideoKey k = Key;
            bool show = depthBoxAt != null && placing == null && chosen != depthHiddenFor && LabJointOf(chosen) is LabJoint j && DepthSettable(k, j);
            depthBox.Visible = show;
            if (!show)
                return;
            LabJoint joint = LabJointOf(chosen);
            depthTitle.Text = IsBladeJoint(joint) ? "depth from the wrist" : "depth from the eye";
            depthValue.Text = DepthText(k, joint);
            depthBox.ResetSize();
            Vector2 size = depthBox.Size, at = depthBoxAt.Value;
            float x = at.X + 22f + size.X <= canvas.Size.X ? at.X + 22f : at.X - 22f - size.X;
            depthBox.Position = new Vector2(Mathf.Clamp(x, 0f, Mathf.Max(canvas.Size.X - size.X, 0f)), Mathf.Clamp(at.Y - size.Y / 2f, 28f, Mathf.Max(canvas.Size.Y - size.Y, 28f)));
        }

        // The joint last clicked, named in a top corner of the frame (the one away from it).
        void DrawChosen(FrameCanvas c, Font font, Dictionary<string, Vector2> video)
        {
            depthBoxAt = chosen != null && !IsScenePoint(chosen) && video.TryGetValue(chosen, out Vector2 jointAt) ? ToCanvas(jointAt) : null;
            if (chosen == null)
                return;
            string text;
            Vector2 at;
            if (IsScenePoint(chosen))
            {
                text = ScenePointWords(chosen);
                at = Key?.Fixed.GetValueOrDefault(chosen) ?? Vector2.Zero;
            }
            else if (LabJointOf(chosen) is LabJoint j)
            {
                text = $"{j.Words}   ·   Pose Lab: {j.Part}";
                at = video.GetValueOrDefault(j.Key);
            }
            else
                return;
            Vector2 size = font.GetStringSize(text, HorizontalAlignment.Left, -1, 17) + new Vector2(24f, 14f);
            bool onLeft = ToCanvas(at).X < c.Size.X / 2f;
            var box = new Rect2(onLeft ? c.Size.X - size.X - 10f : 10f, 34f, size.X, size.Y);
            c.DrawRect(box, new Color(0.05f, 0.05f, 0.08f, 0.85f));
            c.DrawRect(box, PickedColour, false, 2f);
            c.DrawCircle(box.Position + new Vector2(12f, size.Y / 2f), 5f, PickedColour);
            c.DrawString(font, box.Position + new Vector2(22f, size.Y / 2f + 6f), text, HorizontalAlignment.Left, -1, 17, PickedColour);
        }

        // The strip along the top: what is under the mouse, or what a click does now.
        void DrawStrip(FrameCanvas c, Font font, Dictionary<string, float> seen)
        {
            string text = placing != null && Key != null ? $"Click where the {(IsScenePoint(placing) ? ScenePointWords(placing) : LabJointOf(placing).Words)} is (Esc: not now)"
                : hover != null && seen != null ? HoverText(seen)
                : Key is VideoKey k ? $"key frame \"{k.Name}\"  ·  {Track.T[frame]:F2} s{KeyState(k)}"
                : $"{Track.T[frame]:F2} s  ·  not a key frame";
            c.DrawRect(new Rect2(0f, 0f, c.Size.X, 26f), new Color(0f, 0f, 0f, 0.75f));
            c.DrawString(font, new Vector2(8f, 18f), text, HorizontalAlignment.Left, c.Size.X - 16f, 14, placing != null ? PlaceColour : Colors.White);
        }

        Vector2? HoverPoint(Dictionary<string, Vector2> video) =>
            IsScenePoint(hover) ? Key?.Fixed.GetValueOrDefault(hover) : video.TryGetValue(hover, out Vector2 v) ? v : null;

        // The ball (orange) and the table's and net's points (white squares, named; from another key
        // frame faint), the table's edges between its corners.
        void DrawScene(FrameCanvas c, Font font, VideoKey k)
        {
            var (marks, from) = Lab.TableMarks(k);
            if (from == null)
                marks = k.Fixed.Where(f => IsTablePoint(f.Key)).ToDictionary(f => f.Key, f => f.Value); // one point alone: shown all the same
            bool here = from == null || from == k;
            Color white = new(1f, 1f, 1f, here ? 0.9f : 0.45f);
            foreach (var (a, b) in new[] { ("table_nl", "table_nr"), ("table_nr", "table_fr"), ("table_fr", "table_fl"), ("table_fl", "table_nl"), ("net_l", "net_r") })
                if (marks.TryGetValue(a, out Vector2 pa) && marks.TryGetValue(b, out Vector2 pb))
                    c.DrawDashedLine(ToCanvas(pa), ToCanvas(pb), white, 1.5f, 6f);
            foreach (var (key, at) in marks)
            {
                Vector2 q = ToCanvas(at);
                c.DrawRect(new Rect2(q - Vector2.One * 5f, Vector2.One * 10f), white, false, 2f);
                c.DrawString(font, q + new Vector2(8f, -6f), TablePoints.First(t => t.Key == key).Words.Replace("table: ", "").Replace("net: ", "net "), HorizontalAlignment.Left, -1, 11, white);
            }
            if (k.Fixed.TryGetValue(BallMark, out Vector2 ball))
            {
                c.DrawCircle(ToCanvas(ball), 6f, new Color(1f, 0.55f, 0.1f));
                c.DrawArc(ToCanvas(ball), 9f, 0f, Mathf.Tau, 20, Colors.White, 1.5f, true);
            }
        }

        string HoverText(Dictionary<string, float> seen)
        {
            if (IsScenePoint(hover))
                return $"{ScenePointWords(hover)}, placed by hand  ·  drag to move it  ·  right-click: take it back";
            if (LabJointOf(hover) is not LabJoint j)
                return "";
            VideoKey k = Key;
            string how = Deleted(k, j) ? "deleted: put between the key frames around (right-click: back to the video's)"
                : Placed(k, j) ? "placed by hand (right-click: back to the video's)" : IsBladeJoint(j) ? "" : $"the video's, seen {SeenOf(j, seen) * 100f:F0}%  ·  Del: delete it";
            string depth = DepthSettable(k, j) ? $"  ·  Ctrl+wheel: nearer / farther ({DepthText(k, j)})" : "";
            return $"{j.Words}: the Pose Lab's \"{j.Part}\"  ·  {how}  ·  drag to move it{depth}";
        }

        // The video's marker near the mouse.
        string NearestPoint(Vector2 mouse)
        {
            VideoKey k = Key;
            if (Track == null || k == null || !k.HasPose)
                return null;
            const float Near = 11f;
            if (showVideo.ButtonPressed)
            {
                Dictionary<string, Vector2> video = VideoPointsOf(k);
                var seen = Lab.FrameJoints(k).Seen;
                bool Shown(LabJoint j) => showMissing.ButtonPressed || !Deleted(k, j) && (Placed(k, j) || SeenOf(j, seen) >= FoundSeen);
                var nearest = LabJoints.Where(j => video.ContainsKey(j.Key) && Shown(j))
                    .Select(j => (j.Key, D: ToCanvas(video[j.Key]).DistanceTo(mouse))).OrderBy(x => x.D).FirstOrDefault();
                if (nearest.Key != null && nearest.D < Near)
                    return nearest.Key;
                var scene = k.Fixed.Where(f => IsScenePoint(f.Key)).Select(f => (f.Key, D: ToCanvas(f.Value).DistanceTo(mouse))).OrderBy(x => x.D).FirstOrDefault();
                if (scene.Key != null && scene.D < Near)
                    return scene.Key;
            }
            return null;
        }

        // A joint chosen as if clicked on the frame.
        public void ShowFrame(int f) => GoTo(f);

        // As the mouse does it (a review image): a press on the joint, moved by `by` (none: a
        // click), then released.
        public void PressJoint(string key, Vector2 by)
        {
            if (Key is not VideoKey k || !VideoPointsOf(k).TryGetValue(key, out Vector2 at))
                return;
            Vector2 p = ToCanvas(at);
            hover = key;
            OnCanvasInput(canvas, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = p });
            if (by != Vector2.Zero)
                OnCanvasInput(canvas, new InputEventMouseMotion { Position = p + by, ButtonMask = MouseButtonMask.Left });
            OnCanvasInput(canvas, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = p + by });
        }

        public void Choose(string key)
        {
            PickPartOf(key);
            chosen = key;
        }

        void PickPartOf(string key)
        {
            depthHiddenFor = null; // a joint picked: its depth box shown
            int index = LabJointOf(key) is LabJoint j ? PartIndex(j.Part) : key == BallMark ? PartIndex("Ball") : -1;
            if (index >= 0)
                Lab.SelectPart(index);
        }

        void OnCanvasInput(FrameCanvas c, InputEvent e)
        {
            if (Track == null)
                return;
            VideoKey k = Key;
            if (e is InputEventMouseButton b)
            {
                if (b.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown && b.Pressed && b.CtrlPressed)
                {
                    // Ctrl + wheel: the joint under the mouse (or the one chosen) nearer or farther.
                    if (hover != null && !IsScenePoint(hover))
                    {
                        PickPartOf(hover);
                        chosen = hover;
                    }
                    NudgeDepth(b.ButtonIndex == MouseButton.WheelUp ? 0.01f : -0.01f);
                }
                else if (b.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown && b.Pressed)
                {
                    float by = b.ButtonIndex == MouseButton.WheelUp ? 1.15f : 1f / 1.15f;
                    Vector2 under = ToPicture(b.Position);
                    zoom = Mathf.Clamp(zoom * by, 0.05f, 20f);
                    offset = b.Position - under * zoom;
                    viewFitted = true;
                }
                else if (b.ButtonIndex == MouseButton.Middle)
                    panning = b.Pressed;
                else if (b.ButtonIndex == MouseButton.Left && b.Pressed)
                {
                    if (placing != null && k != null && k.HasPose)
                    {
                        // Placed; then on to the next one placed at the key frame before.
                        MoveJoint(k, placing, ToPicture(b.Position));
                        string was = placing;
                        placing = null;
                        Changed();
                        for (int i = 0; i < missingList.ItemCount; i++)
                        {
                            string n = (string)missingList.GetItemMetadata(i);
                            if (n != was && !Placed(k, LabJointOf(n)) && missingList.GetItemText(i).Contains("key frame before"))
                            {
                                placing = n;
                                missingList.Select(i);
                                break;
                            }
                        }
                    }
                    else if (hover != null)
                    {
                        // Picked; its depth box waits for the release, and only a click (no drag)
                        // opens it.
                        PickPartOf(hover);
                        dragging = chosen = hover;
                        depthHiddenFor = hover;
                        pressedAt = b.Position;
                        dragMoved = false;
                    }
                }
                else if (b.ButtonIndex == MouseButton.Left && !b.Pressed && dragging != null)
                {
                    bool moved = dragMoved;
                    if (!moved)
                        depthHiddenFor = null;
                    dragging = null;
                    if (moved)
                        Changed();
                }
                else if (b.ButtonIndex == MouseButton.Right && b.Pressed && hover != null && k != null)
                {
                    bool removed = IsScenePoint(hover) && k.Fixed.Remove(hover);
                    foreach (string name in LabJointOf(hover)?.Members ?? System.Array.Empty<string>())
                        removed |= k.Fixed.Remove(name) | k.Deleted.Remove(name) | k.Depth.Remove(name);
                    if (removed)
                        Changed();
                }
                c.AcceptEvent();
            }
            else if (e is InputEventMouseMotion motion)
            {
                if (panning)
                {
                    offset += motion.Relative;
                    viewFitted = true;
                }
                else if (dragging != null && k != null)
                {
                    // A few pixels of shake is still a click (the joint left where it was).
                    dragMoved |= motion.Position.DistanceTo(pressedAt) > 4f;
                    if (dragMoved)
                        MoveJoint(k, dragging, ToPicture(motion.Position));
                }
                else
                    hover = NearestPoint(motion.Position);
                c.AcceptEvent();
            }
            else if (e is InputEventKey { Pressed: true, Keycode: Godot.Key.Escape })
            {
                placing = null;
                c.AcceptEvent();
            }
        }

        // ---------------------------------------------------------------- the timeline

        // The track: the key frames set (yellow, named by their first letter; an added step green,
        // "+") and where it is (white; on a key frame, in its colour with a box round it, so the
        // key frame is not hidden under it).
        void DrawTimeline(Timeline t)
        {
            t.DrawRect(new Rect2(Vector2.Zero, t.Size), new Color(0.1f, 0.11f, 0.14f));
            if (Track == null || Track.Count < 2)
                return;
            float x0 = 6f, x1 = t.Size.X - 6f, mid = t.Size.Y * 0.5f;
            float X(int f) => x0 + (x1 - x0) * f / (Track.Count - 1);
            t.DrawRect(new Rect2(x0, mid - 5f, x1 - x0, 10f), new Color(0.26f, 0.3f, 0.38f));
            Font font = ThemeDB.FallbackFont;
            float now = X(frame);
            VideoKey on = Key;
            if (on == null)
                t.DrawLine(new Vector2(now, 2f), new Vector2(now, t.Size.Y - 2f), Colors.White, 2f);
            foreach (VideoKey k in Lab.videoKeys)
            {
                float x = X(k.Frame);
                Color tick = TickColour(k);
                if (k == on)
                {
                    // Here: the key frame's mark boxed, the line through it in its colour.
                    t.DrawRect(new Rect2(x - 7f, 1f, 14f, t.Size.Y - 2f), new Color(tick, 0.25f));
                    t.DrawRect(new Rect2(x - 7f, 1f, 14f, t.Size.Y - 2f), tick, false, 2f);
                    t.DrawLine(new Vector2(x, 2f), new Vector2(x, t.Size.Y - 2f), tick, 3f);
                    // Its name beside the box, under the track (on the side with room; the other
                    // marks left clear).
                    float w = font.GetStringSize(k.Name, HorizontalAlignment.Left, -1, 12).X;
                    float nx = x + 10f + w <= t.Size.X ? x + 10f : x - 10f - w;
                    t.DrawString(font, new Vector2(nx, t.Size.Y - 2f), k.Name, HorizontalAlignment.Left, -1, 12, tick);
                }
                else
                    t.DrawLine(new Vector2(x, mid - 9f), new Vector2(x, mid + 7f), tick, 3f);
                t.DrawString(font, new Vector2(x - 3f, mid - 10f), TickLetter(k), HorizontalAlignment.Left, -1, IsAddedKey(k) ? 13 : 11, k == on ? Colors.White : tick);
            }
        }

        bool IsAddedKey(VideoKey k) => !Lab.VideoStepNames().Contains(k.Name);
        Color TickColour(VideoKey k) => k.NotFound ? new Color(1f, 0.4f, 0.3f) : IsAddedKey(k) ? AddedColour : new Color(1f, 0.85f, 0.2f);
        string TickLetter(VideoKey k) => IsAddedKey(k) ? "+" : k.Name.Length > 0 ? k.Name[..1] : "?";

        // The time and the frame (counted from 1).
        string TimeText() => $"{Track.T[frame]:F2} s  ·  {frame + 1} / {Track.Count}";

        void OnTimelineInput(Timeline t, InputEvent e)
        {
            if (Track == null)
                return;
            float x0 = 6f, x1 = t.Size.X - 6f;
            // A key frame's mark within a few pixels catches the click (frames are only a few
            // pixels apart): the nearest one; else the frame under the mouse.
            int FrameAt(float x)
            {
                VideoKey near = Lab.videoKeys.OrderBy(k => Mathf.Abs(x0 + (x1 - x0) * k.Frame / (Track.Count - 1) - x)).FirstOrDefault();
                if (near != null && Mathf.Abs(x0 + (x1 - x0) * near.Frame / (Track.Count - 1) - x) <= 7f)
                    return near.Frame;
                return Mathf.Clamp(Mathf.RoundToInt((x - x0) / (x1 - x0) * (Track.Count - 1)), 0, Track.Count - 1);
            }
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } b)
            {
                if (playingVideo)
                    TogglePlayVideo();
                scrubbing = true;
                GoTo(FrameAt(b.Position.X));
            }
            else if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
                scrubbing = false;
            else if (e is InputEventMouseMotion m && scrubbing)
            {
                frame = FrameAt(m.Position.X);
                playAt = frame;
                timeLabel.Text = TimeText();
            }
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
                RefreshPanel();
            t.AcceptEvent();
        }

        bool scrubbing;

        partial class FrameCanvas : Control
        {
            public VideoWindow Owner_;
            public override void _Draw() => Owner_.DrawOn(this);
            public override void _GuiInput(InputEvent e) => Owner_.OnCanvasInput(this, e);
        }

        partial class Timeline : Control
        {
            public VideoWindow Owner_;
            public override void _Draw() => Owner_.DrawTimeline(this);
            public override void _GuiInput(InputEvent e) => Owner_.OnTimelineInput(this, e);
        }
    }
}
