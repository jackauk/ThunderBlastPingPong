using Godot;
using System.Collections.Generic;
using System.Linq;

// Pose Lab: pose the player and the racket by hand, step by step, and play the motion between
// the steps. It works in two places:
//  - in the game (Main.tscn): F2 freezes the match where it is and opens the lab on the real
//    NPC, starting from the pose it has at that moment; F2 again puts everything back and the
//    match carries on;
//  - on its own (PoseLab.tscn): a figure built from the same parts beside a table.
//
// Every part that can move has its values, in the NPC's own frame (the table centred at the
// origin, the player behind the -Z end line facing +Z, its right hand at -X; metres and degrees,
// turns positive to the player's left): where it is and how its joints are turned. The arms and
// legs follow the racket, the free hand and the feet (the elbows and knees bend to reach them);
// each elbow also turns out or in round the line from shoulder to hand.
// Moving a joint carries what hangs from it: the body, hips, waist or a shoulder take the arm
// (elbow, forearm, hand, fingers, racket, a ball held in the hand) with them; dragging an elbow
// swings the arm round the shoulder with its bend kept; the hand takes its fingers and racket.
// The feet stay planted (shift while dragging the body brings them too); a knee turns out or in
// over its planted foot; the toes turn the foot.
//
// A step is the whole pose at one moment (the start of a serve, the toss, the contact...) and the
// time the move into it takes. Play moves every part from each step to the next. Motions are
// saved as res://Poses/<name>.json (see PoseSequence) for the game to use.
//
// Mouse: click a round marker to pick a part, drag it to move it; right drag turns the view,
// middle drag (or shift + right drag) pans, the wheel zooms.
// Keys: arrows move the picked part across the floor, Page Up/Down up and down (shift: 5 cm a
// press instead of 1); Space play/stop; Ctrl+Z undo; Ctrl+S save; Ctrl+D duplicate the step; 1-4 views.
// Modes (how a drag works): Q carry (as above), W move the point only, E rotate the joint only,
// R move the whole body, T move the ball; see Mode. - / + lower / raise the centre of gravity;
// J / L / I / K move the body left / right / to the table / back over the planted feet (shift: the
// whole player steps, feet too).
//
// PoseLab.tscn with the user argument --capture=<folder>: builds the example serve, saves it,
// plays it, saves a picture at each step and quits.
public partial class PoseLab : Node3D
{
    // In the game: the NPCs it can pose (the visible ones).
    public Opponent[] Targets;
    bool InGame => Targets != null;
    public const Key ToggleKey = Key.F2;

    // ---------------------------------------------------------------- parts and their values

    record Channel(string Key, string Label, float Min, float Max, float Default, float Step);

    // A part: its values; which of them a drag moves (X, Y, Z: a part moved about), or which turn
    // a drag makes (Spin: a handle turned round a joint); and what the arrow keys turn on a
    // turning handle (left / right, up / down).
    record Part(string Name, Channel[] Channels, string X = null, string Y = null, string Z = null,
                Spin Spin = Spin.None, string KeysAcross = null, string KeysUpDown = null);

    // Parts a drag works on with its own rule: the turning handles, the shoulders (moved on the
    // chest) and the elbows (the arm swung round the shoulder).
    enum Spin { None, HipsTurn, BodyLean, ChestTurn, RacketHead, RacketHandle, RacketFace, RightShoulder, LeftShoulder, RightElbow, LeftElbow, LeftKnee, RightKnee, LeftToes, RightToes, WholeTurn }

    // Turning handles are drawn as pink cubes; parts moved about as round markers.
    static bool Turns(Spin s) => s is not (Spin.None or Spin.RightShoulder or Spin.LeftShoulder or Spin.RightElbow or Spin.LeftElbow or Spin.LeftKnee or Spin.RightKnee);

    // The chain from the body to the hand: what moves a shoulder carries its arm, hand, fingers
    // and racket (and a ball held in the hand).
    static bool MovesShoulders(string key) =>
        key.StartsWith("body.") || key.StartsWith("hips.") || key.StartsWith("waist.") || key.StartsWith("rshoulder.") || key.StartsWith("lshoulder.");
    const float BallInHand = 0.08f; // a shown ball this near the left hand is held in it

    static Channel M(string key, string label, float min, float max, float def) => new(key, label, min, max, def, 0.005f);
    static Channel D(string key, string label, float min, float max, float def) => new(key, label, min, max, def, 1f);

    static readonly Channel[] HipsAngles =
    {
        D("hips.turn", "hips turn (°, + left)", -180f, 180f, 0f),
        D("hips.lean", "lean forward (°)", -20f, 70f, 9f),
        D("hips.side", "lean sideways (°, + right)", -30f, 30f, 0f),
    };
    static readonly Channel[] WaistAngles =
    {
        D("waist.turn", "chest turn (°, + left)", -90f, 90f, 0f),
        D("waist.bend", "chest bend forward (°)", -20f, 60f, 0f),
        D("waist.side", "chest bend sideways (°, + right)", -30f, 30f, 0f),
    };
    static readonly Channel[] RacketAngles =
    {
        new("racket.backhand", "backhand side (0 / 1)", 0f, 1f, 0f, 1f),
        D("racket.open", "forearm turn: face open (+) / closed (°)", -180f, 180f, 0f),
        D("racket.noseUp", "wrist: nose up (+) / down (°)", -90f, 90f, 0f),
        D("racket.noseForward", "wrist: nose forward (+) / back (°)", -180f, 180f, 0f),
        D("racket.turn", "whole hand turned (°, + left)", -180f, 180f, 0f),
    };

    static readonly Part[] Parts =
    {
        new("Body (hips)", new[]
        {
            M("body.x", "across (m, + left)", -1.6f, 1.6f, 0f),
            M("body.z", "to / from table (m)", -3.5f, -1.4f, -1.8f),
            M("hips.height", "hips height (m)", 0.45f, 1.0f, 0.86f),
        }.Concat(HipsAngles).ToArray(), X: "body.x", Z: "body.z"),
        new("Waist", WaistAngles),
        new("Right hand + racket", new[]
        {
            M("racket.x", "racket across (m, + left)", -1.8f, 1.8f, -0.3f),
            M("racket.y", "racket height (m)", 0.1f, 2.3f, 1.0f),
            M("racket.z", "racket to / from table (m)", -3.5f, 0.6f, -1.45f),
        }.Concat(RacketAngles).ToArray(), "racket.x", "racket.y", "racket.z"),
        new("Right elbow", new[]
        {
            D("relbow.swivel", "elbow out (+) / in from the grip's (°)", -120f, 120f, 0f),
        }, Spin: Spin.RightElbow, KeysAcross: "relbow.swivel", KeysUpDown: "relbow.swivel"),
        new("Left hand", new[]
        {
            M("lhand.x", "across (m, + left)", -1.6f, 1.8f, 0.25f),
            M("lhand.y", "height (m)", 0.1f, 2.4f, 1.0f),
            M("lhand.z", "to / from table (m)", -3.5f, 0.6f, -1.45f),
            D("lhand.palmUp", "palm up (+) / down (°)", -90f, 90f, 60f),
            D("lhand.palmTurn", "palm turn (°)", -180f, 180f, 0f),
            new("lhand.open", "fingers open (0..1)", 0f, 1f, 0.3f, 0.05f),
        }, "lhand.x", "lhand.y", "lhand.z"),
        new("Left elbow", new[]
        {
            D("lelbow.swivel", "elbow out (+) / in (°)", -180f, 180f, 20f),
        }, Spin: Spin.LeftElbow, KeysAcross: "lelbow.swivel", KeysUpDown: "lelbow.swivel"),
        new("Left foot", new[]
        {
            M("lfoot.x", "across (m, + left)", -1.6f, 1.8f, 0.23f),
            M("lfoot.z", "to / from table (m)", -3.6f, -1.3f, -1.8f),
            D("lfoot.turn", "toes turned (°, + left)", -180f, 180f, 15f),
            M("lfoot.lift", "lifted (m)", 0f, 0.3f, 0f),
        }, X: "lfoot.x", Z: "lfoot.z"),
        new("Right foot", new[]
        {
            M("rfoot.x", "across (m, + left)", -1.8f, 1.6f, -0.23f),
            M("rfoot.z", "to / from table (m)", -3.6f, -1.3f, -1.8f),
            D("rfoot.turn", "toes turned (°, + left)", -180f, 180f, -15f),
            M("rfoot.lift", "lifted (m)", 0f, 0.3f, 0f),
        }, X: "rfoot.x", Z: "rfoot.z"),
        new("Ball", new[]
        {
            M("ball.x", "across (m, + left)", -1.6f, 1.6f, 0.25f),
            M("ball.y", "height (m)", 0.1f, 3f, 1.06f),
            M("ball.z", "to / from table (m)", -3.5f, 1.5f, -1.45f),
            new("ball.show", "shown (0 / 1)", 0f, 1f, 0f, 1f),
        }, "ball.x", "ball.y", "ball.z"),

        // Turning handles (drawn in pink, with the ring or line they turn on).
        new("Racket head (turn about the handle)", RacketAngles, Spin: Spin.RacketHead, KeysAcross: "racket.noseForward", KeysUpDown: "racket.noseUp"),
        new("Racket handle (turn about the head)", RacketAngles, Spin: Spin.RacketHandle, KeysAcross: "racket.noseForward", KeysUpDown: "racket.noseUp"),
        new("Racket face (open / close)", RacketAngles, Spin: Spin.RacketFace, KeysAcross: "racket.open", KeysUpDown: "racket.open"),
        new("Hips turn", HipsAngles, Spin: Spin.HipsTurn, KeysAcross: "hips.turn", KeysUpDown: "hips.lean"),
        new("Body lean", HipsAngles, Spin: Spin.BodyLean, KeysAcross: "hips.side", KeysUpDown: "hips.lean"),
        new("Chest turn (waist)", WaistAngles, Spin: Spin.ChestTurn, KeysAcross: "waist.turn", KeysUpDown: "waist.bend"),

        // The shoulders on the chest: shrugged up or down, rolled forward or back.
        new("Right shoulder", new[]
        {
            M("rshoulder.up", "shrug up (+) / down (m)", -0.04f, 0.1f, 0f),
            M("rshoulder.forward", "forward (+) / back (m)", -0.08f, 0.1f, 0f),
        }, Spin: Spin.RightShoulder, KeysAcross: "rshoulder.forward", KeysUpDown: "rshoulder.up"),
        new("Left shoulder", new[]
        {
            M("lshoulder.up", "shrug up (+) / down (m)", -0.04f, 0.1f, 0f),
            M("lshoulder.forward", "forward (+) / back (m)", -0.08f, 0.1f, 0f),
        }, Spin: Spin.LeftShoulder, KeysAcross: "lshoulder.forward", KeysUpDown: "lshoulder.up"),

        // The knees turn out or in round the line from hip to ankle, the feet staying planted; the
        // toes turn the foot on the floor.
        new("Left knee", new[] { D("lknee.swivel", "knee out (+) / in (°)", -60f, 60f, 0f) }, Spin: Spin.LeftKnee, KeysAcross: "lknee.swivel", KeysUpDown: "lknee.swivel"),
        new("Right knee", new[] { D("rknee.swivel", "knee out (+) / in (°)", -60f, 60f, 0f) }, Spin: Spin.RightKnee, KeysAcross: "rknee.swivel", KeysUpDown: "rknee.swivel"),
        new("Left toes (turn the foot)", new[] { D("lfoot.turn", "toes turned (°, + left)", -180f, 180f, 15f) }, Spin: Spin.LeftToes, KeysAcross: "lfoot.turn", KeysUpDown: "lfoot.turn"),
        new("Right toes (turn the foot)", new[] { D("rfoot.turn", "toes turned (°, + left)", -180f, 180f, -15f) }, Spin: Spin.RightToes, KeysAcross: "rfoot.turn", KeysUpDown: "rfoot.turn"),

        // Whole-body mode: the whole player turned on the spot (the ring on the floor round it).
        new("Whole body turn", System.Array.Empty<Channel>(), Spin: Spin.WholeTurn),
    };

    static readonly Dictionary<string, Channel> ChannelOf = Parts.SelectMany(p => p.Channels).DistinctBy(c => c.Key).ToDictionary(c => c.Key);

    static Dictionary<string, float> DefaultPose() => ChannelOf.Values.ToDictionary(c => c.Key, c => c.Default);

    // A value's default (for motions saved before the value existed; PoseRig).
    public static float DefaultValue(string key) => ChannelOf.TryGetValue(key, out Channel c) ? c.Default : 0f;

    // ---------------------------------------------------------------- body geometry (as in Opponent)

    const float HipHalfWidth = 0.1f;
    const float WaistHeight = 0.22f;
    static readonly Vector3 RightShoulderOnChest = new(-0.2f, 0.33f, 0f);
    static readonly Vector3 LeftShoulderOnChest = new(0.2f, 0.33f, 0f);

    // ---------------------------------------------------------------- state

    Dictionary<string, float> pose = DefaultPose();
    Dictionary<string, float> gamePose;  // the NPC's pose when the game was frozen
    readonly Stack<Dictionary<string, float>> undo = new();
    ulong lastUndoAt;
    PoseSequence sequence = new() { Name = "my_motion" };
    int part = 2;         // the picked part
    int step = -1;        // the picked step
    bool playing;
    float playTime;
    float playSpeed = 0.25f;
    bool loop = true;
    bool ease = true;
    bool open;            // in the game: the lab is showing and the match frozen

    // The figure being posed: built here, or the NPC's own parts in the game.
    Node3D body, upper, chest;
    Leg[] legs;
    Arm rightArm, leftArm;
    Paddle paddle;
    Opponent target;
    Basis toWorld = Basis.Identity; // own frame to world
    Vector3 W(Vector3 own) => toWorld * own;
    Vector3 O(Vector3 world) => toWorld.Transposed() * world;

    GripStyle grip = GripStyle.Shakehand;
    MeshInstance3D ball;
    readonly List<MeshInstance3D> markers = new();
    StandardMaterial3D markerMaterial, pickedMaterial, spinMaterial;
    MeshInstance3D ring;  // what the picked turning handle turns on

    // What the game had before the lab opened, put back when it closes.
    record Saved(Transform3D Body, Transform3D Upper, Transform3D Chest, Transform3D Paddle);
    Saved saved;
    Camera3D gameCamera;
    Input.MouseModeEnum mouseBefore;

    // View.
    Camera3D camera;
    Vector3 viewTarget = new(0f, 0.95f, -1.7f);
    float viewYaw = Mathf.DegToRad(-35f), viewPitch = Mathf.DegToRad(12f), viewDistance = 3.2f;
    bool dragging;
    Plane dragPlane;
    Vector3 dragOffset;

    // How a drag, the arrow keys and the sliders work (keys Q W E R T; the key of the mode in use
    // goes back to Carry):
    //  Carry       a joint moved takes what hangs from it (body -> arms, hand -> ball...);
    //  PointOnly   only the picked point moves: the hands, racket, ball and feet stay where they
    //              are and the limbs bend to keep them (an elbow turns round shoulder-to-hand);
    //  Rotate      the picked joint stays put and turns, what hangs from it turning with it:
    //              left / right drag (or arrows) turns about the view's up, up / down about its
    //              side (the body and chest: turn and bend; a foot or knee: its turn);
    //  WholeBody   any drag moves the whole player across the floor, feet, arms and racket with
    //              it (PgUp / PgDn: hips up and down); the pink handle on the ring round it on
    //              the floor (or the , and . keys) turns the whole player on the spot;
    //  Ball        any drag moves the ball (shown, out of the hand if it was in it).
    // Each mode shows only the markers it works on (ShownIn); the others are hidden.
    enum Mode { Carry, PointOnly, Rotate, WholeBody, Ball }
    static readonly (Mode Mode, Key Key, string Label, string Help)[] Modes =
    {
        (Mode.Carry, Key.Q, "Q carry", "Q  carry: a joint moved takes what hangs from it"),
        (Mode.PointOnly, Key.W, "W point only", "W  point only: just the picked point moves, the rest stays"),
        (Mode.Rotate, Key.E, "E rotate", "E  rotate joint: the joint stays put and turns, drag left/right and up/down"),
        (Mode.WholeBody, Key.R, "R whole body", "R  whole body: drag to move it; the floor ring handle (or , .) turns it"),
        (Mode.Ball, Key.T, "T ball", "T  ball: drag anywhere moves the ball"),
    };
    Mode mode = Mode.Carry;
    readonly Dictionary<Mode, Button> modeButtons = new();
    Label modeLabel, modeBanner;

    // A rotation in progress (Rotate mode): the pose it started from and the mouse there.
    bool rotating;
    bool turningWhole;     // whole-body mode: the turn handle is being dragged
    float wholeTurnFrom;   // the mouse's angle round the ring as that began (radians)
    Dictionary<string, float> rotateFrom;
    Vector2 rotateMouse;
    const float DegreesPerPixel = 0.5f;

    // Panel.
    CanvasLayer layer;
    VBoxContainer partBox;
    readonly Dictionary<string, (HSlider Slider, SpinBox Box)> fields = new();
    OptionButton partPicker, gripPicker, targetPicker, loadPicker, speedPicker, actionPicker;
    Label actionNotes;
    ItemList stepList;
    LineEdit stepName, sequenceName;
    SpinBox stepTime;
    OptionButton finishPicker;
    MethodTable methodTable;
    OptionButton startPicker;
    HSlider timeline;
    Label checks, status, timeLabel;
    Button playButton;
    bool syncing;

    string captureFolder;
    string actionsFolder;

    // ---------------------------------------------------------------- setup

    public override void _Ready()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--capture="))
                captureFolder = arg["--capture=".Length..];
            if (arg.StartsWith("--actions="))
                actionsFolder = arg["--actions=".Length..];
        }

        if (InGame)
        {
            ProcessMode = ProcessModeEnum.Always; // runs while the match is frozen
            camera = new Camera3D { Fov = 45f };
            AddChild(camera);
        }
        else
        {
            BuildWorld();
            BuildFigure();
        }
        BuildMarkers();
        BuildPanel();
        SelectPart(part);
        FitPickedToMode();
        RefreshSteps();
        RefreshSaved();
        ShowActionNotes(0);
        UpdatePower();

        if (InGame)
        {
            layer.Visible = false;
            ShowMarkers(false);
        }
        else
        {
            ReadNaturalHolds();
            Apply();
            if (captureFolder != null)
                CaptureExample();
            else if (actionsFolder != null)
                CaptureActions();
        }
    }

    void BuildWorld()
    {
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.18f, 0.2f, 0.24f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.6f, 0.6f, 0.65f),
                AmbientLightEnergy = 0.8f
            }
        });
        AddChild(new DirectionalLight3D { Rotation = new Vector3(-0.9f, 0.5f, 0f), LightEnergy = 1.1f, ShadowEnabled = true });

        var floorMat = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.22f, 0.2f) };
        AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(8f, 9f), Material = floorMat } });

        // The table, the net and the white lines: where the player stands against them matters.
        var tableMat = new StandardMaterial3D { AlbedoColor = new Color(0.08f, 0.22f, 0.45f) };
        var white = new StandardMaterial3D { AlbedoColor = Colors.White };
        Box(new Vector3(TableSpec.HalfWidth * 2f, 0.03f, TableSpec.HalfLength * 2f), new Vector3(0f, TableSpec.Height - 0.015f, 0f), tableMat);
        Box(new Vector3(TableSpec.HalfWidth * 2f, 0.002f, 0.02f), new Vector3(0f, TableSpec.Height + 0.001f, -TableSpec.HalfLength + 0.01f), white);
        Box(new Vector3(TableSpec.HalfWidth * 2f, 0.002f, 0.02f), new Vector3(0f, TableSpec.Height + 0.001f, TableSpec.HalfLength - 0.01f), white);
        Box(new Vector3(0.003f, 0.002f, TableSpec.HalfLength * 2f), new Vector3(0f, TableSpec.Height + 0.001f, 0f), white);
        Box(new Vector3(TableSpec.NetHalfWidth * 2f, TableSpec.NetHeight, 0.004f), new Vector3(0f, TableSpec.Height + TableSpec.NetHeight / 2f, 0f),
            new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.9f, 0.9f, 0.6f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha });
        var legMat = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.2f, 0.2f) };
        foreach (float x in new[] { -0.6f, 0.6f })
            foreach (float z in new[] { -1.1f, 1.1f })
                Box(new Vector3(0.05f, TableSpec.Height - 0.03f, 0.05f), new Vector3(x, (TableSpec.Height - 0.03f) / 2f, z), legMat);

        camera = new Camera3D { Current = true, Fov = 45f };
        AddChild(camera);
    }

    void Box(Vector3 size, Vector3 at, Material material) =>
        AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size, Material = material }, Position = at });

    void BuildFigure()
    {
        var skin = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.65f, 0.5f) };
        var shirt = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.75f, 0.1f) };
        var shorts = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.1f, 0.15f) };
        var shoe = new StandardMaterial3D { AlbedoColor = new Color(0.92f, 0.92f, 0.9f), Roughness = 0.6f };

        body = new Node3D { Name = "Body" };
        AddChild(body);
        legs = new[] { new Leg { Name = "LegLeft", Trousers = shorts, Shoe = shoe }, new Leg { Name = "LegRight", Trousers = shorts, Shoe = shoe } };
        foreach (Leg leg in legs)
            body.AddChild(leg);
        upper = new Node3D { Name = "Upper" };
        body.AddChild(upper);
        upper.AddChild(new MeshInstance3D { Mesh = new CapsuleMesh { Radius = 0.165f, Height = 0.36f, Material = shorts }, Position = new Vector3(0f, 0.1f, 0f) });
        chest = new Node3D { Name = "Chest", Position = new Vector3(0f, WaistHeight, 0f) };
        upper.AddChild(chest);
        chest.AddChild(new MeshInstance3D { Mesh = new CapsuleMesh { Radius = 0.17f, Height = 0.48f, Material = shirt }, Position = new Vector3(0f, 0.2f, 0f) });
        chest.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.11f, Height = 0.22f, Material = skin }, Position = new Vector3(0f, 0.53f, 0f) });

        rightArm = new Arm { Name = "ArmRight", Skin = skin, Sleeve = shirt };
        leftArm = new Arm { Name = "ArmLeft", Skin = skin, Sleeve = shirt, WithHand = true };
        AddChild(rightArm);
        AddChild(leftArm);
        MakePaddle();
    }

    void BuildMarkers()
    {
        ball = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.02f, Height = 0.04f, Material = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.55f, 0.1f) } } };
        AddChild(ball);

        markerMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.3f, 0.9f, 1f, 0.75f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            NoDepthTest = true, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, RenderPriority = 1
        };
        pickedMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(1f, 0.85f, 0.1f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            NoDepthTest = true, RenderPriority = 2
        };
        spinMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(1f, 0.35f, 0.85f, 0.85f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            NoDepthTest = true, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, RenderPriority = 1
        };
        // Places are round markers; turning handles are small cubes.
        for (int i = 0; i < Parts.Length; i++)
        {
            Mesh shape = !Turns(Parts[i].Spin)
                ? new SphereMesh { Radius = 0.025f, Height = 0.05f }
                : new BoxMesh { Size = Vector3.One * 0.04f };
            var marker = new MeshInstance3D { Mesh = shape, MaterialOverride = MarkerMaterial(i) };
            AddChild(marker);
            markers.Add(marker);
        }
        ring = new MeshInstance3D { Mesh = new ImmediateMesh(), MaterialOverride = spinMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(ring);

        // The centre of gravity: a red ball seen through the body, a line down to the floor.
        var cogMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(1f, 0.22f, 0.18f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            NoDepthTest = true, RenderPriority = 3
        };
        cogMarker = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.035f, Height = 0.07f }, MaterialOverride = cogMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(cogMarker);
        cogLine = new MeshInstance3D { Mesh = new ImmediateMesh(), MaterialOverride = cogMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(cogLine);

        // The skeleton's lines, seen through the body (each line's colour is in its vertices).
        bones = new MeshInstance3D
        {
            Mesh = new ImmediateMesh(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                NoDepthTest = true, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, RenderPriority = 0, CullMode = BaseMaterial3D.CullModeEnum.Disabled
            }
        };
        AddChild(bones);
    }

    MeshInstance3D bones;

    // Skeleton line looks: point to point thin and dotted (cyan); a joint to its turning handle
    // thin and solid (pink); the picked joint's lines yellow and a little wider, same pattern.
    static readonly Color PointLine = new(0.55f, 0.95f, 1f, 0.9f);
    static readonly Color TurnLine = new(1f, 0.35f, 0.85f, 0.9f);
    static readonly Color PickedLine = new(1f, 0.85f, 0.1f, 1f);
    const float LineWidth = 0.004f, PickedLineWidth = 0.008f;
    const float DotLength = 0.008f, DotGap = 0.009f;
    // The turning handles' points: a line to one of them is a turn line.
    static readonly HashSet<string> TurnPoints = new() { "rhead", "rhandle", "rface", "hturn", "lean", "cturn", "lto", "rto" };

    // The skeleton drawn as lines from joint to joint: hips - chest - neck - head; neck - each
    // shoulder - elbow - wrist (the right on to the racket); hips - each hip - knee - ankle -
    // toes (dotted); and each turning handle to the joint it turns (solid pink). The lines at the picked joint
    // (for the racket hand, the racket's too) are drawn in yellow.
    void DrawBones()
    {
        Vector3 Marker(string name) => MarkerAt(PartIndex(name));
        Vector3 rsh = ShoulderAt(true), lsh = ShoulderAt(false), neck = (rsh + lsh) * 0.5f;
        Transform3D racket = paddle.GlobalTransform;
        var at = new Dictionary<string, Vector3>
        {
            ["hips"] = upper.GlobalPosition, ["chest"] = chest.GlobalPosition, ["neck"] = neck,
            ["head"] = chest.GlobalTransform * new Vector3(0f, 0.53f, 0f),
            ["rsh"] = rsh, ["lsh"] = lsh, ["rel"] = rightArm.ElbowPoint, ["lel"] = leftArm.ElbowPoint,
            ["rwr"] = rightArm.WristPoint, ["lwr"] = W(LeftHandAt),
            ["racket"] = racket.Origin, ["rhead"] = racket * RacketHeadLocal, ["rhandle"] = racket * RacketHandleLocal, ["rface"] = racket * RacketFaceLocal,
            ["lhip"] = legs[0].HipPoint, ["rhip"] = legs[1].HipPoint, ["lkn"] = legs[0].KneePoint, ["rkn"] = legs[1].KneePoint,
            ["lan"] = legs[0].AnklePoint, ["ran"] = legs[1].AnklePoint, ["lto"] = ToesAt(0), ["rto"] = ToesAt(1),
            ["hturn"] = Marker("Hips turn"), ["lean"] = Marker("Body lean"), ["cturn"] = Marker("Chest turn (waist)"),
        };
        // Which marker each point is (only a shown marker's handle line is drawn).
        var partOf = new Dictionary<string, string>
        {
            ["hips"] = "Body (hips)", ["chest"] = "Waist", ["rsh"] = "Right shoulder", ["lsh"] = "Left shoulder",
            ["rel"] = "Right elbow", ["lel"] = "Left elbow", ["rwr"] = "Right hand + racket", ["lwr"] = "Left hand",
            ["rhead"] = "Racket head (turn about the handle)", ["rhandle"] = "Racket handle (turn about the head)", ["rface"] = "Racket face (open / close)",
            ["lkn"] = "Left knee", ["rkn"] = "Right knee", ["lan"] = "Left foot", ["ran"] = "Right foot",
            ["lto"] = "Left toes (turn the foot)", ["rto"] = "Right toes (turn the foot)",
            ["hturn"] = "Hips turn", ["lean"] = "Body lean", ["cturn"] = "Chest turn (waist)",
        };
        var picked = new HashSet<string>(partOf.Where(p => p.Value == Parts[part].Name).Select(p => p.Key));
        if (picked.Contains("rwr"))
            picked.Add("racket"); // the hand holds the racket

        // (from, to, turn line?) - the picked ones drawn last, over the rest.
        var plain = new List<(Vector3, Vector3, bool)>();
        var lit = new List<(Vector3, Vector3, bool)>();
        void Bone(string a, string b)
        {
            var into = picked.Contains(a) || picked.Contains(b) ? lit : plain;
            into.Add((at[a], at[b], TurnPoints.Contains(a) || TurnPoints.Contains(b)));
        }
        bool Shown(string point) => markers[PartIndex(partOf[point])].Visible;

        Bone("hips", "chest"); Bone("chest", "neck"); Bone("neck", "head");
        Bone("neck", "rsh"); Bone("rsh", "rel"); Bone("rel", "rwr"); Bone("rwr", "racket");
        Bone("rhandle", "racket"); Bone("racket", "rhead");
        Bone("neck", "lsh"); Bone("lsh", "lel"); Bone("lel", "lwr");
        Bone("hips", "lhip"); Bone("lhip", "lkn"); Bone("lkn", "lan"); Bone("lan", "lto");
        Bone("hips", "rhip"); Bone("rhip", "rkn"); Bone("rkn", "ran"); Bone("ran", "rto");
        // Handles to their joints.
        if (Shown("rface")) Bone("racket", "rface");
        if (Shown("hturn")) Bone("hips", "hturn");
        if (Shown("lean")) Bone("hips", "lean");
        if (Shown("cturn")) Bone("chest", "cturn");

        // Each line made of flat strips turned to face the camera (true lines are a pixel thin):
        // one strip for a solid line, short ones with gaps for a dotted one.
        Vector3 eye = camera.GlobalPosition;
        var mesh = (ImmediateMesh)bones.Mesh;
        mesh.ClearSurfaces();
        if (plain.Count + lit.Count == 0)
            return;
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        void Strip(Vector3 a, Vector3 b, Vector3 side, Color colour)
        {
            Vector3 a0 = bones.ToLocal(a - side), a1 = bones.ToLocal(a + side), b0 = bones.ToLocal(b - side), b1 = bones.ToLocal(b + side);
            foreach (Vector3 v in new[] { a0, b0, b1, a0, b1, a1 })
            {
                mesh.SurfaceSetColor(colour);
                mesh.SurfaceAddVertex(v);
            }
        }
        void Line((Vector3 A, Vector3 B, bool Turn) line, bool isPicked)
        {
            Vector3 along = line.B - line.A;
            float length = along.Length();
            Vector3 side = along.Cross(eye - (line.A + line.B) * 0.5f);
            if (length < 1e-4f || side.LengthSquared() < 1e-10f)
                return;
            side = side.Normalized() * ((isPicked ? PickedLineWidth : LineWidth) * 0.5f);
            Color colour = isPicked ? PickedLine : line.Turn ? TurnLine : PointLine;
            if (line.Turn)
            {
                Strip(line.A, line.B, side, colour); // solid
                return;
            }
            Vector3 step = along / length;
            for (float s = 0f; s < length; s += DotLength + DotGap)
                Strip(line.A + step * s, line.A + step * Mathf.Min(s + DotLength, length), side, colour);
        }
        foreach (var line in plain)
            Line(line, false);
        foreach (var line in lit)
            Line(line, true);
        mesh.SurfaceEnd();
    }

    StandardMaterial3D MarkerMaterial(int i) => i == part ? pickedMaterial : !Turns(Parts[i].Spin) ? markerMaterial : spinMaterial;

    void ShowMarkers(bool show)
    {
        foreach (MeshInstance3D marker in markers)
            marker.Visible = show;
        ball.Visible = show && V("ball.show") > 0.5f;
        ring.Visible = show;
        cogMarker.Visible = cogLine.Visible = show;
        bones.Visible = show;
    }

    // The lab's own racket (not in the game, where the NPC's is used).
    void MakePaddle()
    {
        paddle?.QueueFree();
        paddle = GD.Load<PackedScene>("res://Paddle.tscn").Instantiate<Paddle>();
        paddle.Grip = grip;
        paddle.AvoidTable = false;
        paddle.Driven = true;
        AddChild(paddle);
    }

    // ---------------------------------------------------------------- the game's natural holds

    // The racket's angles are the forearm's and wrist's turns from the natural hold the game uses
    // (all 0: the forehand or backhand grip at rest, the face to the table): the forearm turning
    // round the handle (opening or closing the face), the wrist tipping the nose up or down and
    // forward or back; then the whole hand turned about the vertical.
    Basis naturalForehand = Basis.Identity, naturalBackhand = Basis.Identity;
    Paddle holdReader;
    int holdStage = -1;

    // Read from a hidden paddle with the same grip (its holder space turned to face the table),
    // over the next frames; the pose is laid on again once they are known.
    void ReadNaturalHolds()
    {
        holdReader?.QueueFree();
        holdReader = GD.Load<PackedScene>("res://Paddle.tscn").Instantiate<Paddle>();
        holdReader.Grip = paddle?.Grip ?? grip;
        holdReader.Driven = true;
        holdReader.AvoidTable = false;
        holdReader.Visible = false;
        AddChild(holdReader);
        holdStage = 0;
    }

    void StepNaturalHolds()
    {
        var holderToOwn = new Basis(Vector3.Up, Mathf.Pi);
        switch (holdStage)
        {
            case 0:
                holdReader.Freeze(StrokeStyle.Drive, true, 0f, 0f, Vector2.Zero);
                break;
            case 1:
                naturalForehand = holderToOwn * holdReader.NeutralBasis;
                holdReader.Freeze(StrokeStyle.Drive, false, 0f, 0f, Vector2.Zero);
                break;
            case 2:
                naturalBackhand = holderToOwn * holdReader.NeutralBasis;
                holdReader.QueueFree();
                holdReader = null;
                holdStage = -1;
                if (InGame && open && gamePose == null)
                    TakeGamePose();
                else
                    Apply();
                return;
        }
        holdStage++;
    }

    bool HoldsKnown => holdStage < 0 && holdReader == null;

    // The forearm's line from the wrist in the forehand or backhand hold (paddle space), where
    // the elbow starts before its swivel (as the game's arm and PoseRig).
    Vector3 GripForearm => paddle.NaturalForearm(Forehand);

    // Which side the racket plays: below 0.5 the forehand, from 0.5 the backhand (as PoseRig and
    // PoseSequence), the same for the racket's hold and the forearm's line. (A blend between a
    // forehand and a backhand step passes 0.5 exactly at its middle.)
    bool Forehand => V("racket.backhand") < 0.5f;

    // (Face open and nose forward mean the same on either side: PoseSequence.AngleSide.)
    Basis RacketBasis => new Basis(Vector3.Up, R("racket.turn"))
        * (Forehand ? naturalForehand : naturalBackhand)
        * new Basis(Vector3.Up, PoseSequence.AngleSide(Forehand) * R("racket.open"))
        * new Basis(Vector3.Back, -R("racket.noseUp"))
        * new Basis(Vector3.Right, PoseSequence.AngleSide(Forehand) * R("racket.noseForward"));

    // ---------------------------------------------------------------- in the game: open and close

    Opponent[] VisibleTargets() => Targets.Where(t => t.Visible && t.ProcessMode != ProcessModeEnum.Disabled).ToArray();

    void Open()
    {
        Opponent[] visible = VisibleTargets();
        if (visible.Length == 0)
            return;
        target = target != null && visible.Contains(target) ? target : visible[0];
        targetPicker.Clear();
        foreach (Opponent t in visible)
            targetPicker.AddItem(t.Tag);
        targetPicker.Selected = System.Array.IndexOf(visible, target);
        targetPicker.Visible = visible.Length > 1;

        GetTree().Paused = true;
        open = true;
        mouseBefore = Input.MouseMode;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        layer.Visible = true;
        gameCamera = GetViewport().GetCamera3D();
        camera.MakeCurrent();
        Attach(target);
        viewTarget = upper.GlobalPosition + Vector3.Up * 0.2f;
        View(-75f, 14f); // from its right, the feet in view
        viewDistance = 3.0f;
        Status($"game frozen: posing {target.Tag}. {ToggleKey} to go back to the game.");
    }

    // The lab poses this NPC's own parts; what they were is kept to put back.
    void Attach(Opponent npc)
    {
        (body, upper, chest, legs, rightArm, leftArm, paddle) = npc.Rig;
        toWorld = npc.ToWorld;
        weightBox?.SetValueNoSignal(npc.BodyMass); // the NPC's own weight
        staminaBox?.SetValueNoSignal(npc.Stamina);
        UpdatePower();
        saved = new Saved(body.Transform, upper.Transform, chest.Transform, paddle.GlobalTransform);
        grip = paddle.Grip;
        gamePose = null;
        ShowMarkers(true);
        ReadNaturalHolds(); // then the NPC's pose is read (TakeGamePose)
    }

    void Detach()
    {
        if (body == null || saved == null)
            return;
        body.Transform = saved.Body;
        upper.Transform = saved.Upper;
        chest.Transform = saved.Chest;
        paddle.GlobalTransform = saved.Paddle;
        // The NPC lays its own arms and legs again on its next frame.
    }

    void Close()
    {
        playing = false;
        playButton.Text = "▶ Play";
        dragging = false;
        Detach();
        open = false;
        layer.Visible = false;
        ShowMarkers(false);
        gameCamera?.MakeCurrent();
        Input.MouseMode = mouseBefore;
        GetTree().Paused = false;
    }

    void SwitchTarget(int index)
    {
        Opponent[] visible = VisibleTargets();
        if (index < 0 || index >= visible.Length || visible[index] == target)
            return;
        Detach();
        target = visible[index];
        Attach(target);
        viewTarget = upper.GlobalPosition + Vector3.Up * 0.2f;
        View(-75f, 14f);
        Status($"posing {target.Tag}");
    }

    // The NPC's pose as it stands, read back into values.
    void TakeGamePose()
    {
        var p = DefaultPose();
        float Deg(float radians) => Mathf.RadToDeg(radians);

        // Body and hips (the hips' sideways shift over the feet goes into the body's place).
        Vector3 hipsAt = O(upper.GlobalPosition);
        p["body.x"] = hipsAt.X;
        p["body.z"] = hipsAt.Z;
        p["hips.height"] = upper.Position.Y;
        Vector3 hipsTurn = upper.Rotation;
        p["hips.lean"] = Deg(hipsTurn.X);
        p["hips.turn"] = Deg(hipsTurn.Y);
        p["hips.side"] = Deg(hipsTurn.Z);
        Basis hips = upper.Basis;
        p["waist.turn"] = Deg((hips * chest.Basis * hips.Inverse()).GetEuler().Y);

        for (int i = 0; i < 2; i++)
        {
            string side = i == 0 ? "lfoot" : "rfoot";
            Vector3 foot = O(legs[i].AnklePoint) - Vector3.Up * Leg.AnkleHeight;
            Vector3 toes = O(legs[i].Toes);
            p[$"{side}.x"] = foot.X;
            p[$"{side}.z"] = foot.Z;
            p[$"{side}.lift"] = foot.Y < 0.05f ? 0f : foot.Y; // a small step's lift is set down: the foot on the floor
            p[$"{side}.turn"] = Deg(Mathf.Atan2(toes.X, toes.Z));
        }

        // The racket: its place, and its turn from the natural hold as forearm and wrist angles.
        Basis racket = toWorld.Transposed() * paddle.GlobalBasis.Orthonormalized();
        Vector3 racketAt = O(paddle.GlobalPosition);
        p["racket.x"] = racketAt.X;
        p["racket.y"] = racketAt.Y;
        p["racket.z"] = racketAt.Z;
        p["racket.backhand"] = paddle.Forehand ? 0f : 1f;
        Basis natural = paddle.Forehand ? naturalForehand : naturalBackhand;
        Vector3 turns = (natural.Inverse() * racket).Orthonormalized().GetEuler(EulerOrder.Yzx);
        p["racket.open"] = PoseSequence.AngleSide(paddle.Forehand) * Deg(turns.Y);
        p["racket.noseUp"] = -Deg(turns.Z);
        p["racket.noseForward"] = PoseSequence.AngleSide(paddle.Forehand) * Deg(turns.X);
        p["racket.turn"] = 0f;

        Transform3D shoulders = chest.GlobalTransform;
        Vector3 rightShoulderAt = shoulders * RightShoulderOnChest, leftShoulderAt = shoulders * LeftShoulderOnChest;
        Vector3 wrist = paddle.GlobalTransform * paddle.WristLocal;
        Vector3 gripElbow = wrist + (paddle.GlobalBasis * paddle.NaturalForearm(paddle.Forehand)).Normalized() * Arm.ForearmSize;
        p["relbow.swivel"] = Deg(Swivel(rightShoulderAt, wrist, gripElbow - rightShoulderAt, rightArm.ElbowPoint, W(Vector3.Left)));

        Vector3 hand = O(leftArm.WristPoint);
        p["lhand.x"] = hand.X;
        p["lhand.y"] = hand.Y;
        p["lhand.z"] = hand.Z;
        Vector3 palm = O(leftArm.PalmFacing).Normalized();
        p["lhand.palmUp"] = Deg(Mathf.Asin(Mathf.Clamp(palm.Y, -1f, 1f)));
        p["lhand.palmTurn"] = Deg(Mathf.Atan2(palm.X, palm.Z));
        p["lhand.open"] = leftArm.Open;
        p["lelbow.swivel"] = Deg(Swivel(leftShoulderAt, leftArm.WristPoint, Vector3.Down, leftArm.ElbowPoint, W(Vector3.Right)));

        if (target.TargetBall != null)
        {
            Vector3 ballAt = O(target.TargetBall.GlobalPosition);
            p["ball.x"] = ballAt.X;
            p["ball.y"] = ballAt.Y;
            p["ball.z"] = ballAt.Z;
            p["ball.show"] = 0f; // the real ball is there already
        }

        foreach (var (key, value) in p.ToList())
            p[key] = Mathf.Clamp(value, ChannelOf[key].Min, ChannelOf[key].Max);
        gamePose = p;
        pose = new Dictionary<string, float>(p);
        SyncFields();
        Apply();
    }

    // The swivel (radians) that puts the elbow where it is, as Pole measures it.
    static float Swivel(Vector3 shoulder, Vector3 hand, Vector3 start, Vector3 elbow, Vector3 outSide)
    {
        Vector3 axis = (hand - shoulder).Normalized();
        Vector3 down = start - axis * start.Dot(axis);
        if (down.LengthSquared() < 1e-4f)
            down = Vector3.Down - axis * Vector3.Down.Dot(axis);
        down = down.Normalized();
        Vector3 outward = axis.Cross(down);
        if (outward.Dot(outSide) < 0f)
            outward = -outward;
        Vector3 actual = elbow - shoulder;
        return Mathf.Atan2(actual.Dot(outward), actual.Dot(down));
    }

    // ---------------------------------------------------------------- the pose on the figure

    float V(string key) => pose.TryGetValue(key, out float v) ? v : ChannelOf[key].Default;
    float R(string key) => Mathf.DegToRad(V(key));
    Vector3 RacketAt => new(V("racket.x"), V("racket.y"), V("racket.z"));
    Vector3 LeftHandAt => new(V("lhand.x"), V("lhand.y"), V("lhand.z"));
    Vector3 BallAt => new(V("ball.x"), V("ball.y"), V("ball.z"));
    Vector3 FootAt(int i) => new(V(i == 0 ? "lfoot.x" : "rfoot.x"), V(i == 0 ? "lfoot.lift" : "rfoot.lift"), V(i == 0 ? "lfoot.z" : "rfoot.z"));
    Vector3 ToesOf(int i)
    {
        float turn = R(i == 0 ? "lfoot.turn" : "rfoot.turn");
        return new Vector3(Mathf.Sin(turn), 0f, Mathf.Cos(turn));
    }

    Vector3 rightShoulder, leftShoulder, rightWrist;

    // The free hand's palm (own frame).
    Vector3 Palm
    {
        get
        {
            float up = R("lhand.palmUp"), turn = R("lhand.palmTurn");
            return new Vector3(Mathf.Sin(turn) * Mathf.Cos(up), Mathf.Sin(up), Mathf.Cos(turn) * Mathf.Cos(up));
        }
    }

    // The body from the floor to the shoulders: hips, waist, chest.
    void PlaceTrunk()
    {
        body.GlobalTransform = new Transform3D(toWorld, W(new Vector3(V("body.x"), 0f, V("body.z"))));
        upper.Position = new Vector3(0f, V("hips.height"), 0f);
        upper.Rotation = new Vector3(R("hips.lean"), R("hips.turn"), R("hips.side")); // + side tips the top to its right (-X)
        // The feet stay on the floor and the legs keep their length: hips too high for a leg to
        // reach its foot come down to where it can.
        float drop = 0f;
        for (int i = 0; i < 2; i++)
        {
            Vector3 hip = upper.GlobalTransform * new Vector3((i == 0 ? 1f : -1f) * HipHalfWidth, 0f, 0f);
            Vector3 ankle = W(FootAt(i)) + Vector3.Up * Leg.AnkleHeight;
            float reach = LegReach(i);
            float across = new Vector2(hip.X - ankle.X, hip.Z - ankle.Z).Length();
            if (across < reach)
                drop = Mathf.Max(drop, hip.Y - (ankle.Y + Mathf.Sqrt(reach * reach - across * across)));
        }
        if (drop > 1e-4f)
        {
            Hold("hips.height", V("hips.height") - drop);
            upper.Position = new Vector3(0f, V("hips.height"), 0f);
            atFullReach.Add("hips held as high as the legs reach the floor");
        }
        // The chest turns about the vertical through the spine (the shoulders stay level), then
        // bends forward or sideways on the hips.
        Basis hips = upper.Basis;
        chest.Basis = hips.Inverse() * new Basis(Vector3.Up, R("waist.turn")) * hips
                      * Basis.FromEuler(new Vector3(R("waist.bend"), 0f, R("waist.side")));
    }

    // Where a knee points: over the toes, turned out (+) or in by its swivel round the line from
    // hip to ankle (the left knee's out is +X, the right's -X).
    Vector3 KneeToward(int i, Vector3 hip) =>
        Pole(hip, W(FootAt(i)) + Vector3.Up * Leg.AnkleHeight, W(ToesOf(i)), R(i == 0 ? "lknee.swivel" : "rknee.swivel"), W(i == 0 ? Vector3.Right : Vector3.Left));

    // How far a leg reaches from hip to ankle (a hair short of straight).
    float LegReach(int i) => 0.998f * (legs[i].ThighLength + legs[i].ShinLength);

    // A shoulder joint (world), on the chest where it is shrugged and rolled to.
    Vector3 ShoulderAt(bool right)
    {
        string s = right ? "rshoulder" : "lshoulder";
        Vector3 rest = right ? RightShoulderOnChest : LeftShoulderOnChest;
        return chest.GlobalTransform * (rest + new Vector3(0f, V(s + ".up"), V(s + ".forward")));
    }

    Transform3D ShoulderFrame(bool right) => new(chest.GlobalBasis, ShoulderAt(right));

    // ---------------------------------------------------------------- the chain: parents carry children

    // A change to anything that moves a shoulder (body, hips, waist, the shoulder itself) carries
    // each arm along with it: elbow, forearm, hand, fingers and racket move as one with the
    // shoulder (and a ball held in the left hand with it). The feet stay planted unless feetToo.
    void Carrying(System.Action change, bool feetToo = false)
    {
        Transform3D right0 = ShoulderFrame(true), left0 = ShoulderFrame(false);
        Transform3D racket0 = paddle.GlobalTransform;
        Vector3 rightElbow0 = rightArm.ElbowPoint, leftElbow0 = leftArm.ElbowPoint;
        Vector3 hand0 = W(LeftHandAt), palm0 = W(Palm), ball0 = W(BallAt);
        bool ballHeld = V("ball.show") > 0.5f && ball0.DistanceTo(hand0) < BallInHand;
        Vector3 body0 = W(new Vector3(V("body.x"), 0f, V("body.z")));

        change();
        PlaceTrunk();

        Transform3D right = ShoulderFrame(true) * right0.AffineInverse(), left = ShoulderFrame(false) * left0.AffineInverse();
        SetRacket(right * racket0);
        KeepElbow(true, right * rightElbow0);
        MoveLeftHand(left * hand0, left.Basis * palm0, ballHeld ? left * ball0 : null);
        KeepElbow(false, left * leftElbow0);
        if (feetToo)
        {
            Vector3 moved = O(W(new Vector3(V("body.x"), 0f, V("body.z"))) - body0);
            foreach (string side in new[] { "lfoot", "rfoot" })
            {
                Hold(side + ".x", V(side + ".x") + moved.X);
                Hold(side + ".z", V(side + ".z") + moved.Z);
            }
        }
        SyncFields();
        Apply();
    }

    // The left hand put here (world), its palm facing so, and the ball it holds with it.
    void MoveLeftHand(Vector3 hand, Vector3 palm, Vector3? heldBall)
    {
        Vector3 at = O(hand);
        Hold("lhand.x", at.X);
        Hold("lhand.y", at.Y);
        Hold("lhand.z", at.Z);
        Vector3 p = O(palm).Normalized();
        Hold("lhand.palmUp", Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(p.Y, -1f, 1f))));
        Hold("lhand.palmTurn", Mathf.RadToDeg(Mathf.Atan2(p.X, p.Z)));
        if (heldBall is Vector3 b)
        {
            Vector3 ballAt = O(b);
            Hold("ball.x", ballAt.X);
            Hold("ball.y", ballAt.Y);
            Hold("ball.z", ballAt.Z);
        }
    }

    // The elbow's swivel that puts it at `elbow` (world) for the hand where its values now say,
    // so an arm carried as one keeps its bend.
    void KeepElbow(bool right, Vector3 elbow)
    {
        Vector3 shoulder = ShoulderAt(right);
        if (right)
        {
            var racket = new Transform3D(toWorld * RacketBasis, W(RacketAt));
            Vector3 wrist = racket * paddle.WristLocal;
            Vector3 gripElbow = wrist + (racket.Basis * GripForearm).Normalized() * Arm.ForearmSize;
            Hold("relbow.swivel", Mathf.RadToDeg(Swivel(shoulder, wrist, gripElbow - shoulder, elbow, W(Vector3.Left))));
        }
        else
            Hold("lelbow.swivel", Mathf.RadToDeg(Swivel(shoulder, W(LeftHandAt), Vector3.Down, elbow, W(Vector3.Right))));
    }

    // The racket where the pose has it and the right arm reaching it (a racket out of reach comes
    // in with the hand at full stretch).
    void PlaceRacketAndArm()
    {
        var racket = new Transform3D(toWorld * RacketBasis, W(RacketAt));
        paddle.GlobalTransform = racket;
        rightWrist = racket * paddle.WristLocal;
        // The right elbow starts where the grip puts it (up the forearm's natural line from the
        // wrist, as in the game), and the swivel turns it out or in from there.
        Vector3 gripElbow = rightWrist + (racket.Basis * GripForearm).Normalized() * Arm.ForearmSize;
        rightArm.Solve(rightShoulder, rightWrist, Pole(rightShoulder, rightWrist, gripElbow - rightShoulder, R("relbow.swivel"), W(Vector3.Left)));
        // The arm keeps its length: a racket out of reach comes in with the hand at full stretch.
        if (rightArm.WristPoint.DistanceTo(rightWrist) > 1e-4f)
        {
            paddle.GlobalPosition += rightArm.WristPoint - rightWrist;
            rightWrist = rightArm.WristPoint;
            Vector3 racketGot = O(paddle.GlobalPosition);
            Hold("racket.x", racketGot.X);
            Hold("racket.y", racketGot.Y);
            Hold("racket.z", racketGot.Z);
            if (!atFullReach.Contains("right arm straight: the racket comes in to its reach"))
                atFullReach.Add("right arm straight: the racket comes in to its reach");
        }

    }

    void Apply()
    {
        if (body == null || (InGame && !open))
            return;
        atFullReach.Clear();
        PlaceTrunk();

        for (int i = 0; i < 2; i++)
        {
            Vector3 hip = upper.GlobalTransform * new Vector3((i == 0 ? 1f : -1f) * HipHalfWidth, 0f, 0f);
            legs[i].Solve(hip, W(FootAt(i)), W(ToesOf(i)), KneeToward(i, hip));
            // Still out of reach (too far across even with the hips this low): the foot slides in
            // along the floor towards the hips, never up off it.
            Vector3 footGot = O(legs[i].AnklePoint) - Vector3.Up * Leg.AnkleHeight;
            if (footGot.DistanceTo(FootAt(i)) > 1e-4f)
            {
                string side = i == 0 ? "lfoot" : "rfoot";
                Vector3 hipOwn = O(hip), foot = FootAt(i);
                float reach = LegReach(i), high = hipOwn.Y - (foot.Y + Leg.AnkleHeight);
                float across = high < reach ? Mathf.Sqrt(reach * reach - high * high) : 0f;
                var way = new Vector2(foot.X - hipOwn.X, foot.Z - hipOwn.Z).Normalized();
                Hold(side + ".x", hipOwn.X + way.X * across);
                Hold(side + ".z", hipOwn.Z + way.Y * across);
                legs[i].Solve(hip, W(FootAt(i)), W(ToesOf(i)), KneeToward(i, hip));
                atFullReach.Add((i == 0 ? "left" : "right") + " leg straight: the foot slides in along the floor");
            }
        }

        rightShoulder = ShoulderAt(true);
        leftShoulder = ShoulderAt(false);

        PlaceRacketAndArm();
        // The step that meets the ball: the blade is kept on it, whatever moved (the racket comes
        // to the ball, the arm with it), then placed again.
        if (ContactLocked && KeepOnBall())
            PlaceRacketAndArm();

        leftArm.Open = V("lhand.open");
        leftArm.Solve(leftShoulder, W(LeftHandAt), Pole(leftShoulder, W(LeftHandAt), Vector3.Down, R("lelbow.swivel"), W(Vector3.Right)), W(Palm));
        if (leftArm.WristPoint.DistanceTo(W(LeftHandAt)) > 1e-4f)
        {
            Vector3 handGot = O(leftArm.WristPoint);
            Hold("lhand.x", handGot.X);
            Hold("lhand.y", handGot.Y);
            Hold("lhand.z", handGot.Z);
            atFullReach.Add("left arm straight: the hand comes in to its reach");
        }

        ball.GlobalPosition = W(BallAt);
        ball.Visible = V("ball.show") > 0.5f || ContactLocked; // the ball it meets, shown while the step is edited

        if (fitting)
            return; // trying poses for a fit from a video: the markers, checks and drawing wait
        for (int i = 0; i < Parts.Length; i++)
        {
            markers[i].GlobalPosition = MarkerAt(i);
            markers[i].MaterialOverride = MarkerMaterial(i);
            markers[i].Scale = Vector3.One * (i == part ? 1.4f : 1f);
            markers[i].Visible = ShownIn(mode, Parts[i]) && (Parts[i].Name != "Ball" || ball.Visible || i == part);
        }
        UpdateChecks();
        if (atFullReach.Count > 0)
            SyncFields(); // the values show where the limbs really got to
        DrawRing();
        ShowCentreOfGravity();
        DrawBones();
    }

    // Arms and legs never stretch: what they cannot reach is held where they get to, and the
    // pose's values follow (so a saved step is what was seen).
    readonly List<string> atFullReach = new();

    void Hold(string key, float value) => pose[key] = Mathf.Clamp(value, ChannelOf[key].Min, ChannelOf[key].Max);

    // Where the elbow points: towards `start` (round the line from shoulder to hand) at 0, turned
    // out to the arm's own side (outSide) as the swivel grows, in when negative.
    static Vector3 Pole(Vector3 shoulder, Vector3 hand, Vector3 start, float swivel, Vector3 outSide)
    {
        Vector3 axis = (hand - shoulder).Normalized();
        Vector3 down = start - axis * start.Dot(axis);
        if (down.LengthSquared() < 1e-4f)
            down = Vector3.Down - axis * Vector3.Down.Dot(axis);
        down = down.Normalized();
        Vector3 outward = axis.Cross(down);
        if (outward.Dot(outSide) < 0f)
            outward = -outward;
        return down * Mathf.Cos(swivel) + outward * Mathf.Sin(swivel);
    }

    // Points on the racket (paddle space) for its turning handles: the head (the rim's top), the
    // handle's end, and just off the rim's side for the face.
    static readonly Vector3 RacketHeadLocal = new(0f, 0.1f, 0f);
    static readonly Vector3 RacketHandleLocal = new(0f, -0.175f, 0f);
    static readonly Vector3 RacketFaceLocal = new(0.105f, 0f, 0f);
    const float RingRadius = 0.45f;

    Vector3 ChestRingCentre => chest.GlobalPosition + Vector3.Up * 0.2f;

    // World position of each part's marker.
    Vector3 MarkerAt(int i)
    {
        switch (Parts[i].Spin)
        {
            case Spin.RacketHead: return paddle.GlobalTransform * RacketHeadLocal;
            case Spin.RacketHandle: return paddle.GlobalTransform * RacketHandleLocal;
            case Spin.RacketFace: return paddle.GlobalTransform * RacketFaceLocal;
            case Spin.HipsTurn: return upper.GlobalPosition + W(new Basis(Vector3.Up, R("hips.turn")) * new Vector3(0f, 0f, RingRadius));
            case Spin.BodyLean: return upper.GlobalTransform * new Vector3(0f, 1.0f, 0f);
            case Spin.ChestTurn: return ChestRingCentre + W(new Basis(Vector3.Up, R("hips.turn") + R("waist.turn")) * new Vector3(0f, 0f, RingRadius));
            case Spin.RightShoulder: return ShoulderAt(true);
            case Spin.LeftShoulder: return ShoulderAt(false);
            case Spin.RightElbow: return rightArm.ElbowPoint;
            case Spin.LeftElbow: return leftArm.ElbowPoint;
            case Spin.LeftKnee: return legs[0].KneePoint;
            case Spin.RightKnee: return legs[1].KneePoint;
            case Spin.LeftToes: return ToesAt(0);
            case Spin.RightToes: return ToesAt(1);
            case Spin.WholeTurn: return WholeRingCentre + W(new Basis(Vector3.Up, R("hips.turn") - 0.75f * Mathf.Pi) * new Vector3(0f, 0f, WholeRingRadius)); // behind, to its right
        }
        return Parts[i].Name switch
        {
            "Body (hips)" => upper.GlobalPosition,
            "Waist" => chest.GlobalPosition,
            "Right hand + racket" => rightArm.WristPoint,
            "Left hand" => W(LeftHandAt),
            "Left foot" => legs[0].AnklePoint,  // the ankle joint; a drag moves the foot on the floor
            "Right foot" => legs[1].AnklePoint,
            _ => W(BallAt),
        };
    }

    // The ring on the floor the whole player turns on, round its body.
    Vector3 WholeRingCentre => W(new Vector3(V("body.x"), 0.02f, V("body.z")));
    const float WholeRingRadius = 0.7f;

    // The whole player turned on the spot about its body (degrees, + to its left): hips, feet
    // (place and toes) and, carried, the arms, racket and a ball held in the hand. The turns go
    // all the way round (kept within -180..180, past 180 coming round to -180).
    void TurnWhole(float degrees)
    {
        float a = Mathf.DegToRad(degrees), cos = Mathf.Cos(a), sin = Mathf.Sin(a);
        float cx = V("body.x"), cz = V("body.z");
        Carrying(() =>
        {
            TurnBy("hips.turn", degrees);
            foreach (string side in new[] { "lfoot", "rfoot" })
            {
                float x = V(side + ".x") - cx, z = V(side + ".z") - cz;
                Hold(side + ".x", cx + x * cos + z * sin);
                Hold(side + ".z", cz - x * sin + z * cos);
                TurnBy(side + ".turn", degrees);
            }
        });
    }

    // A turn (hips or a foot, -180..180) turned on by `degrees`, coming round past 180.
    void TurnBy(string key, float degrees) => Hold(key, Mathf.Wrap(V(key) + degrees, -180f, 180f));

    // The whole body's centre of gravity (world): each body segment's mass (as a share of the
    // whole, Winter 2009: trunk 49.7%, head and neck 8.1%, thigh 10%, shank 4.65%, foot 1.45%,
    // upper arm 2.8%, forearm 1.6%, hand 0.6%) at its own centre along the bone.
    Vector3 CentreOfGravity()
    {
        Vector3 sum = Vector3.Zero;
        static Vector3 Along(Vector3 a, Vector3 b, float f) => a + (b - a) * f;
        Vector3 shoulders = (ShoulderAt(true) + ShoulderAt(false)) * 0.5f;
        sum += 0.497f * Along(upper.GlobalPosition, shoulders, 0.5f);
        sum += 0.081f * (chest.GlobalTransform * new Vector3(0f, 0.53f, 0f));
        foreach (bool right in new[] { true, false })
        {
            Arm arm = right ? rightArm : leftArm;
            Vector3 shoulder = ShoulderAt(right), elbow = arm.ElbowPoint, wrist = arm.WristPoint;
            sum += 0.028f * Along(shoulder, elbow, 0.436f);
            sum += 0.016f * Along(elbow, wrist, 0.43f);
            sum += 0.006f * (wrist + (wrist - elbow).Normalized() * 0.05f);
        }
        for (int i = 0; i < 2; i++)
        {
            sum += 0.1f * Along(legs[i].HipPoint, legs[i].KneePoint, 0.433f);
            sum += 0.0465f * Along(legs[i].KneePoint, legs[i].AnklePoint, 0.433f);
            sum += 0.0145f * (W(FootAt(i) + ToesOf(i) * 0.07f) + Vector3.Up * 0.04f);
        }
        return sum; // the shares add up to 1
    }

    // - / + : the centre of gravity lowered or raised (the hips, 1 cm a press, shift 5), the feet
    // planted and the knees bending; the arms come along (in point-only mode the hands stay).
    void RaiseBody(float by)
    {
        PushUndo(false);
        float before = O(CentreOfGravity()).Y;
        Trunk(() => Hold("hips.height", V("hips.height") + by));
        float after = O(CentreOfGravity()).Y;
        Status($"centre of gravity {after:F3} m high (was {before:F3}); hips {V("hips.height"):F3} m"
               + (Mathf.Abs(after - before) < 1e-4f ? (by > 0f ? " - as high as the legs reach" : " - as low as it goes") : ""));
    }

    // J / L / I / K: the body moved left / right / to the table / back, 1 cm a press. The feet stay
    // planted and the hips shift over them (the weight moving onto a foot, or leaning in or back);
    // with shift the whole player steps there instead, feet and all, 5 cm a press. The arms come
    // along (in point-only mode the hands stay).
    void ShiftBody(float across, float forward, bool step)
    {
        PushUndo(false);
        Vector3 before = O(CentreOfGravity());
        Trunk(() =>
        {
            Hold("body.x", V("body.x") + across);
            Hold("body.z", V("body.z") + forward);
        }, feetToo: step);
        Vector3 after = O(CentreOfGravity());
        Vector3 feet = (FootAt(0) + FootAt(1)) * 0.5f;
        Status((step ? "stepped" : "body shifted over the feet")
               + $": centre of gravity {(after - before).Length() * 100f:F1} cm moved; it is {(after.X - feet.X) * 100f:+0;-0} cm left, {(after.Z - feet.Z) * 100f:+0;-0} cm to the table of the middle of the feet");
    }

    // The centre of gravity shown: a ball (seen through the body) and its line down to the floor.
    MeshInstance3D cogMarker, cogLine;

    void ShowCentreOfGravity()
    {
        Vector3 cog = CentreOfGravity();
        cogMarker.GlobalPosition = cog;
        var mesh = (ImmediateMesh)cogLine.Mesh;
        mesh.ClearSurfaces();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        Vector3 floor = cog - Vector3.Up * O(cog).Y;
        mesh.SurfaceAddVertex(cogLine.ToLocal(cog));
        mesh.SurfaceAddVertex(cogLine.ToLocal(floor));
        // a small cross where it falls on the floor
        foreach (Vector3 d in new[] { Vector3.Right, Vector3.Back })
        {
            mesh.SurfaceAddVertex(cogLine.ToLocal(floor + d * 0.06f + Vector3.Up * 0.005f));
            mesh.SurfaceAddVertex(cogLine.ToLocal(floor - d * 0.06f + Vector3.Up * 0.005f));
        }
        mesh.SurfaceEnd();
    }

    // The tip of a shoe (world), for turning the foot.
    Vector3 ToesAt(int i) => W(FootAt(i) + ToesOf(i) * ToeReach + Vector3.Up * 0.03f);
    const float ToeReach = 0.19f; // ankle to the toe end of the shoe, along the floor

    // The ring (or line) the picked turning handle turns on.
    void DrawRing()
    {
        var mesh = (ImmediateMesh)ring.Mesh;
        mesh.ClearSurfaces();
        Spin spin = mode == Mode.WholeBody ? Spin.WholeTurn : Parts[part].Spin;
        if (spin == Spin.None)
            return;
        var points = new List<Vector3>();
        void Circle(Vector3 centre, Vector3 axis, float radius)
        {
            Vector3 a = Limb.AlongY(axis).X, b = axis.Normalized().Cross(a);
            for (int k = 0; k <= 48; k++)
            {
                float t = Mathf.Tau * k / 48f;
                points.Add(centre + (a * Mathf.Cos(t) + b * Mathf.Sin(t)) * radius);
            }
        }
        Transform3D racket = paddle.GlobalTransform;
        switch (spin)
        {
            case Spin.HipsTurn:
                Circle(upper.GlobalPosition, Vector3.Up, RingRadius);
                break;
            case Spin.WholeTurn:
                Circle(WholeRingCentre, Vector3.Up, WholeRingRadius);
                break;
            case Spin.ChestTurn:
                Circle(ChestRingCentre, Vector3.Up, RingRadius);
                break;
            case Spin.BodyLean:
                points.Add(upper.GlobalPosition);
                points.Add(MarkerAt(part));
                break;
            case Spin.RacketHead:
            case Spin.RacketHandle:
                points.Add(racket * (RacketHandleLocal * 1.6f));
                points.Add(racket * (RacketHeadLocal * 1.6f));
                break;
            case Spin.RacketFace:
                Circle(racket.Origin, racket * RacketHeadLocal - racket * RacketHandleLocal, RacketFaceLocal.X);
                break;
            case Spin.LeftKnee:
            case Spin.RightKnee:
            {
                // The line from hip to ankle the knee turns round.
                Leg leg = legs[spin == Spin.LeftKnee ? 0 : 1];
                points.Add(leg.HipPoint);
                points.Add(leg.AnklePoint);
                break;
            }
            case Spin.LeftToes:
            case Spin.RightToes:
            {
                int i = spin == Spin.LeftToes ? 0 : 1;
                Circle(W(FootAt(i)) + Vector3.Up * 0.03f, Vector3.Up, ToeReach);
                break;
            }
            case Spin.RightElbow:
            case Spin.LeftElbow:
                // The upper arm the elbow swings on.
                points.Add(ShoulderAt(spin == Spin.RightElbow));
                points.Add(MarkerAt(part));
                break;
        }
        if (points.Count < 2)
            return; // the shoulders have no ring
        mesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
        foreach (Vector3 p in points)
            mesh.SurfaceAddVertex(ring.ToLocal(p));
        mesh.SurfaceEnd();
    }

    // A turning handle dragged to `to` (world): the joint turns so the handle follows.
    void SpinTo(Spin spin, Vector3 to)
    {
        float Deg(float radians) => Mathf.RadToDeg(radians);
        Transform3D start = spinStart;
        switch (spin)
        {
            case Spin.RacketHead:
            case Spin.RacketHandle:
            {
                // About the other end: the shortest turn that brings this end towards the mouse.
                bool head = spin == Spin.RacketHead;
                Vector3 pivot = start * (head ? RacketHandleLocal : RacketHeadLocal);
                Vector3 from = start * (head ? RacketHeadLocal : RacketHandleLocal) - pivot;
                Vector3 toward = to - pivot;
                if (from.LengthSquared() < 1e-8f || toward.LengthSquared() < 1e-8f)
                    return;
                var turn = new Basis(new Quaternion(from.Normalized(), toward.Normalized()));
                SetRacket(new Transform3D(turn * start.Basis, pivot + turn * (start.Origin - pivot)));
                break;
            }
            case Spin.RacketFace:
            {
                // Round the racket's own line, through its middle.
                Vector3 axis = (start * RacketHeadLocal - start * RacketHandleLocal).Normalized();
                Vector3 from = start * RacketFaceLocal - start.Origin, toward = to - start.Origin;
                from -= axis * from.Dot(axis);
                toward -= axis * toward.Dot(axis);
                if (from.LengthSquared() < 1e-8f || toward.LengthSquared() < 1e-8f)
                    return;
                var turn = new Basis(axis, from.SignedAngleTo(toward, axis));
                SetRacket(new Transform3D(turn * start.Basis, start.Origin));
                break;
            }
            case Spin.RightElbow:
            case Spin.LeftElbow:
            {
                // The arm swings round the shoulder as one, the elbow following the mouse: the
                // forearm, hand, fingers, racket (or held ball) go with it, the elbow's bend kept.
                // Point only: the hand stays and the elbow turns out or in towards the mouse.
                bool right = spin == Spin.RightElbow;
                if (mode == Mode.PointOnly)
                {
                    KeepElbow(right, to);
                    break;
                }
                Vector3 shoulder = ShoulderAt(right);
                Vector3 from = elbowStart - shoulder, toward = to - shoulder;
                if (from.LengthSquared() < 1e-8f || toward.LengthSquared() < 1e-8f)
                    return;
                var swing = new Basis(new Quaternion(from.Normalized(), toward.Normalized()));
                var about = new Transform3D(swing, shoulder - swing * shoulder);
                if (right)
                    SetRacket(about * start);
                else
                    MoveLeftHand(about * handStart, swing * palmStart, ballStartHeld ? about * ballStart : null);
                KeepElbow(right, about * elbowStart);
                break;
            }
            case Spin.HipsTurn:
                Trunk(() =>
                {
                    Vector3 v = O(to - upper.GlobalPosition);
                    Hold("hips.turn", Deg(Mathf.Atan2(v.X, v.Z)));
                });
                return;
            case Spin.ChestTurn:
                Trunk(() =>
                {
                    Vector3 v = O(to - ChestRingCentre);
                    Hold("waist.turn", Mathf.Wrap(Deg(Mathf.Atan2(v.X, v.Z)) - V("hips.turn"), -180f, 180f));
                });
                return;
            case Spin.BodyLean:
            {
                // The top of the body tips towards the mouse: forward / back is lean, to the sides
                // is the sideways lean (+ to its right, -X).
                Vector3 v = new Basis(Vector3.Up, -R("hips.turn")) * O(to - upper.GlobalPosition);
                if (v.LengthSquared() < 1e-6f)
                    return;
                Trunk(() =>
                {
                    Hold("hips.lean", Deg(Mathf.Atan2(v.Z, v.Y)));
                    Hold("hips.side", Deg(Mathf.Asin(Mathf.Clamp(-v.X / v.Length(), -1f, 1f))));
                });
                return;
            }
            case Spin.LeftKnee:
            case Spin.RightKnee:
            {
                // The knee turns out or in round the line from hip to ankle towards the mouse; the
                // foot stays planted.
                int i = spin == Spin.LeftKnee ? 0 : 1;
                Vector3 hip = legs[i].HipPoint, ankle = W(FootAt(i)) + Vector3.Up * Leg.AnkleHeight;
                Hold(i == 0 ? "lknee.swivel" : "rknee.swivel", Deg(Swivel(hip, ankle, W(ToesOf(i)), to, W(i == 0 ? Vector3.Right : Vector3.Left))));
                break;
            }
            case Spin.LeftToes:
            case Spin.RightToes:
            {
                // The foot turns on the floor about its ankle, its toes towards the mouse.
                int i = spin == Spin.LeftToes ? 0 : 1;
                Vector3 v = O(to) - FootAt(i);
                if (new Vector2(v.X, v.Z).LengthSquared() > 1e-6f)
                    Hold(i == 0 ? "lfoot.turn" : "rfoot.turn", Deg(Mathf.Atan2(v.X, v.Z)));
                break;
            }
            case Spin.RightShoulder:
            case Spin.LeftShoulder:
            {
                // The shoulder slides on the chest (shrug, roll forward or back) and its arm comes
                // with it.
                bool right = spin == Spin.RightShoulder;
                string s = right ? "rshoulder" : "lshoulder";
                Vector3 onChest = chest.GlobalTransform.AffineInverse() * to - (right ? RightShoulderOnChest : LeftShoulderOnChest);
                Trunk(() =>
                {
                    ShoulderTo(s + ".up", onChest.Y);
                    ShoulderTo(s + ".forward", onChest.Z);
                });
                return;
            }
        }
        SyncFields();
        Apply();
    }

    // What an elbow swing started from.
    Vector3 elbowStart, handStart, palmStart, ballStart;
    bool ballStartHeld;

    // The racket's values for a racket placed so (world): its place, and its turn from the
    // natural hold as forearm and wrist angles (the hand's turn about the vertical kept).
    void SetRacket(Transform3D racket)
    {
        Vector3 at = O(racket.Origin);
        Hold("racket.x", at.X);
        Hold("racket.y", at.Y);
        Hold("racket.z", at.Z);
        Basis natural = Forehand ? naturalForehand : naturalBackhand;
        Basis own = toWorld.Transposed() * racket.Basis.Orthonormalized();
        Vector3 turns = ((new Basis(Vector3.Up, R("racket.turn")) * natural).Inverse() * own).Orthonormalized().GetEuler(EulerOrder.Yzx);
        Hold("racket.open", PoseSequence.AngleSide(Forehand) * Mathf.RadToDeg(turns.Y));
        Hold("racket.noseUp", -Mathf.RadToDeg(turns.Z));
        Hold("racket.noseForward", PoseSequence.AngleSide(Forehand) * Mathf.RadToDeg(turns.X));
    }

    Transform3D spinStart;  // the racket as a turning drag began
    Vector3 spinOffset;     // from the mouse's point on the drag plane to the handle (world)

    // What the pose asks of the body: the joints' bends, and anything a person could not do or
    // that passes through the table or the body (red).
    void UpdateChecks()
    {
        var lines = new List<string>();
        var problems = new List<string>();
        float Inner(Vector3 a, Vector3 joint, Vector3 b) => Mathf.RadToDeg((a - joint).AngleTo(b - joint));
        Vector3 leftHand = W(LeftHandAt);

        lines.Add($"right elbow {Inner(rightShoulder, rightArm.ElbowPoint, rightWrist):F0}°   left elbow {Inner(leftShoulder, leftArm.ElbowPoint, leftHand):F0}°  (180 = straight)");
        lines.Add($"left knee {Inner(legs[0].HipPoint, legs[0].KneePoint, legs[0].AnklePoint):F0}°   right knee {Inner(legs[1].HipPoint, legs[1].KneePoint, legs[1].AnklePoint):F0}°");
        // The wrist, as the check measures it: off the grip's line, and which way.
        var (wristBend, palmward) = paddle.WristOffGrip(rightArm.ElbowPoint, Forehand);
        lines.Add($"right wrist bent {wristBend:F0}° off the grip's line, {PoseCheck.WristWay(palmward)} (goes about {PoseCheck.WristRange(palmward):F0}° that way)");
        lines.Add($"centre of gravity {O(CentreOfGravity()).Y:F2} m high (red ball; - / + lower / raise)");

        lines.AddRange(atFullReach);
        for (int i = 0; i < 2; i++)
        {
            if (legs[i].HipPoint.DistanceTo(legs[i].AnklePoint) > LegReach(i) - 0.002f)
                lines.Add((i == 0 ? "left" : "right") + " leg straight: the hips are as high as it lets them");
        }
        // Joints past their range, a wrong racket face at a contact step, lost balance (PoseCheck).
        foreach (PoseCheck.Issue issue in RunPoseCheck(AtContactStep))
            (issue.Serious ? problems : lines).Add(issue.Serious ? issue.Text : "! " + issue.Text);
        problems.AddRange(Collisions());

        checks.Text = string.Join("\n", lines) + (problems.Count > 0 ? "\n" + string.Join("\n", problems.Select(p => "✖ " + p)) : "\n✔ a body can do this");
        checks.AddThemeColorOverride("font_color", problems.Count > 0 ? new Color(1f, 0.45f, 0.4f) : new Color(0.7f, 1f, 0.7f));
    }

    bool AtContactStep => step >= 0 && step < sequence.Steps.Count && sequence.Steps[step].Name.Contains("contact");

    List<PoseCheck.Issue> RunPoseCheck(bool atContact) =>
        PoseCheck.Run(new PoseCheck.Body((body, upper, chest, legs, rightArm, leftArm, paddle), toWorld), Forehand, atContact);

    // The hard rule: nothing through the table or the body.
    List<string> Collisions()
    {
        var problems = new List<string>();
        Vector3 leftHand = W(LeftHandAt);
        Collide.Capsule[] bodyParts = BodyCapsules();
        bool racketIn = false, racketInTable = false;
        foreach (Vector3 point in Paddle.Outline)
        {
            Vector3 p = paddle.GlobalTransform * point;
            racketInTable |= Collide.OutOfTable(p, Paddle.RimClearance).LengthSquared() > 1e-6f;
            racketIn |= Collide.OutOfAll(p, Paddle.RimClearance, bodyParts, table: false).LengthSquared() > 1e-6f;
        }
        if (racketInTable)
            problems.Add("racket goes into the table");
        if (racketIn)
            problems.Add("racket goes into the body");
        if (Collide.OutOfTable(leftHand, 0.04f).LengthSquared() > 1e-6f)
            problems.Add("left hand goes into the table");
        if (Collide.OutOfAll(leftHand, 0.04f, bodyParts, table: false).LengthSquared() > 1e-6f)
            problems.Add("left hand goes into the body");
        // The body against the table top itself (legs and feet may go under it, as when stepping in).
        foreach (Vector3 p in new[] { rightArm.ElbowPoint, leftArm.ElbowPoint, upper.GlobalPosition + Vector3.Up * 0.1f, legs[0].KneePoint, legs[1].KneePoint,
                                      (legs[0].HipPoint + legs[0].KneePoint) * 0.5f, (legs[1].HipPoint + legs[1].KneePoint) * 0.5f })
        {
            if (InTableTop(p, 0.06f))
            {
                problems.Add("the body goes into the table");
                break;
            }
        }
        if (ball.Visible && Collide.OutOfTable(W(BallAt), Ball.Radius).LengthSquared() > 1e-6f)
            problems.Add("ball inside the table");
        return problems;
    }

    // Inside the table top (the board, 3 cm thick), with this much room.
    static bool InTableTop(Vector3 p, float room) =>
        Mathf.Abs(p.X) < TableSpec.HalfWidth + room && Mathf.Abs(p.Z) < TableSpec.HalfLength + room
        && p.Y > TableSpec.Height - 0.03f - room && p.Y < TableSpec.Height + room;

    Collide.Capsule[] BodyCapsules()
    {
        Transform3D c = chest.GlobalTransform, u = upper.GlobalTransform;
        var list = new List<Collide.Capsule>
        {
            new(c * new Vector3(0f, 0.13f, 0f), c * new Vector3(0f, 0.27f, 0f), 0.17f),
            new(c * new Vector3(0f, 0.53f, 0f), c * new Vector3(0f, 0.53f, 0f), 0.11f),
            new(u * new Vector3(0f, 0.085f, 0f), u * new Vector3(0f, 0.115f, 0f), 0.165f),
        };
        foreach (Leg leg in legs)
        {
            list.Add(new(leg.HipPoint, leg.KneePoint, 0.075f));
            list.Add(new(leg.KneePoint, leg.AnklePoint, 0.055f));
        }
        return list.ToArray();
    }

    // ---------------------------------------------------------------- panel

    void BuildPanel()
    {
        layer = new CanvasLayer { Layer = 10 };
        AddChild(layer);
        var panel = new PanelContainer
        {
            AnchorLeft = 1f, AnchorRight = 1f, AnchorTop = 0f, AnchorBottom = 1f,
            OffsetLeft = -400f, OffsetRight = 0f, OffsetTop = 0f, OffsetBottom = 0f,
            // Anything wider than 400 px pushes the panel's left edge out, never its right one off
            // the window (the rows below wrap, so that should not happen).
            GrowHorizontal = Control.GrowDirection.Begin,
        };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.08f, 0.09f, 0.11f, 0.93f), ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6 });
        layer.AddChild(panel);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        panel.AddChild(scroll);
        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(box);

        VBoxContainer root = box; // the sections below each fold away (Section)
        // The title and its buttons wrap onto a second line when they do not fit beside it (in the
        // game the target picker and "Back to game" join them).
        var titleRow = new HFlowContainer();
        titleRow.AddChild(Heading(InGame ? "POSE LAB (game frozen)" : "POSE LAB"));
        titleRow.AddChild(MakeButton("Fold all", () => FoldAll(true)));
        titleRow.AddChild(MakeButton("Open all", () => FoldAll(false)));
        if (InGame)
        {
            targetPicker = new OptionButton();
            targetPicker.ItemSelected += i => SwitchTarget((int)i);
            titleRow.AddChild(targetPicker);
            titleRow.AddChild(MakeButton($"Back to game ({ToggleKey})", Close));
        }
        box.AddChild(titleRow);
        box.AddChild(Small("Click a marker to pick a part, drag to move it. Right drag: turn view, middle drag: pan, wheel: zoom. Click a section's title to fold it."));
        // The focus: what is being edited now, always in view at the top, and the last message.
        var focusFrame = new PanelContainer();
        focusFrame.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.1f, 0.2f, 0.28f), BorderColor = new Color(0.35f, 0.75f, 1f), BorderWidthLeft = 4,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
            ContentMarginLeft = 10, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6,
        });
        var focusBox = new VBoxContainer();
        focusFrame.AddChild(focusBox);
        focusLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        focusLabel.AddThemeFontSizeOverride("font_size", 14);
        focusLabel.AddThemeColorOverride("font_color", new Color(0.85f, 0.95f, 1f));
        focusBox.AddChild(focusLabel);
        status = Small("");
        status.AddThemeColorOverride("font_color", new Color(0.7f, 0.85f, 0.75f));
        focusBox.AddChild(status);
        // The frame rate and what the lab's own work takes of each frame (clear of the game's
        // overlay at the top left).
        fpsLabel = Small("");
        focusBox.AddChild(fpsLabel);
        box.AddChild(focusFrame);

        // Mode: how a drag works (keys Q W E R T).
        box = Section(root, "MODE  (how a drag works)", "mode", true);
        var modeRow = new HFlowContainer();
        foreach (var (m, _, label, _) in Modes)
        {
            Mode chosen = m;
            Button button = MakeButton(label, () => SetMode(chosen));
            button.ToggleMode = true;
            button.SetPressedNoSignal(m == mode);
            modeButtons[m] = button;
            modeRow.AddChild(button);
        }
        box.AddChild(modeRow);
        modeLabel = Small(Modes[0].Help);
        modeLabel.AddThemeColorOverride("font_color", new Color(0.55f, 0.95f, 0.6f));
        box.AddChild(modeLabel);

        // The mode, large over the view's top left.
        modeBanner = new Label { Text = "Mode: " + Modes[0].Help, Position = new Vector2(14f, 10f) };
        modeBanner.AddThemeFontSizeOverride("font_size", 18);
        modeBanner.AddThemeColorOverride("font_color", new Color(0.55f, 0.95f, 0.6f));
        modeBanner.AddThemeColorOverride("font_outline_color", Colors.Black);
        modeBanner.AddThemeConstantOverride("outline_size", 5);
        layer.AddChild(modeBanner);

        BuildKeycaps();

        // Action: the kind of stroke this motion is (each saved as its own file).
        box = Section(root, "ACTION  (which stroke)", "action", true);
        var actionRow = new HBoxContainer();
        actionPicker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FitToLongestItem = false, ClipText = true, CustomMinimumSize = new Vector2(120f, 0f) };
        foreach (StrokeActions.Action a in StrokeActions.All)
            actionPicker.AddItem(StrokeActions.Label(a)); // the method table's names: a method's default, then its variants
        actionPicker.ItemSelected += i =>
        {
            ShowActionNotes((int)i);
            FillStartPicker(DefaultStart(StrokeActions.All[(int)i]));
        };
        actionRow.AddChild(actionPicker);
        box.AddChild(actionRow);
        // The precondition: what a new motion starts from (step 1).
        var startRow = new HBoxContainer();
        var startLabel = new Label { Text = "Starts from", VerticalAlignment = VerticalAlignment.Center };
        startLabel.AddThemeFontSizeOverride("font_size", 13);
        startRow.AddChild(startLabel);
        startPicker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FitToLongestItem = false, ClipText = true, CustomMinimumSize = new Vector2(120f, 0f) };
        startPicker.TooltipText = "Step 1 of a motion opened from its template, or made with New: the ready stance, or where another motion ends "
            + "(by default the footwork ticked as this method's first action in the method table).";
        startRow.AddChild(startPicker);
        startRow.AddChild(MakeButton("Add as step 1", AddStartStep));
        box.AddChild(startRow);
        FillStartPicker("");
        var actionButtons = new HFlowContainer();
        actionButtons.AddChild(MakeButton("Open (yours, or the start)", () => OpenAction(actionPicker.Selected, false)));
        actionButtons.AddChild(MakeButton("Start again from the template", () => OpenAction(actionPicker.Selected, true)));
        // Which saved motions the game picks from (at random) for each method.
        methodTable = new MethodTable { Name = "MethodTable" };
        AddChild(methodTable);
        actionButtons.AddChild(MakeButton("Method table…", () => methodTable.PopupCentered()));
        // The racket collection: equip the player and the NPCs.
        actionButtons.AddChild(MakeButton("Rackets…", () => RacketLocker.Open(GetTree())));
        // Rallies planned from above, for the NPCs to play.
        actionButtons.AddChild(MakeButton("Strategy map…", () => StrategyMap.Open(GetTree(), GetParent() as Match)));
        box.AddChild(actionButtons);
        // A stroke read from a video of a player (Tools/video2motion.py, PoseLabVideo).
        BuildVideoImport(box);
        actionNotes = Small("");
        actionNotes.AddThemeColorOverride("font_color", new Color(0.8f, 0.85f, 0.95f));
        box.AddChild(actionNotes);

        // Part.
        box = Section(root, "PART  (the picked joint)", "part", true);
        var partRow = new HBoxContainer();
        partRow.AddChild(new Label { Text = "Part" });
        partPicker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FitToLongestItem = false, ClipText = true, CustomMinimumSize = new Vector2(120f, 0f) };
        foreach (Part p in Parts)
            partPicker.AddItem(p.Name);
        partPicker.ItemSelected += i => SelectPart((int)i);
        partRow.AddChild(partPicker);
        if (!InGame)
        {
            gripPicker = new OptionButton();
            gripPicker.AddItem("Shakehand");
            gripPicker.AddItem("Penhold");
            gripPicker.ItemSelected += i =>
            {
                grip = i == 0 ? GripStyle.Shakehand : GripStyle.Penhold;
                MakePaddle();
                ReadNaturalHolds();
            };
            partRow.AddChild(gripPicker);
        }
        box.AddChild(partRow);
        partBox = new VBoxContainer();
        box.AddChild(partBox);
        var partButtons = new HFlowContainer();
        partButtons.AddChild(MakeButton("Reset this part", ResetPart));
        partButtons.AddChild(MakeButton(InGame ? "Back to game pose" : "Reset all", ResetAll));
        partButtons.AddChild(MakeButton("Undo", Undo));
        partButtons.AddChild(MakeButton("Ease the wrist", () => { PushUndo(true); EaseWrist(); Status("waist and hand turned to ease the wrist"); }));
        box.AddChild(partButtons);

        checks = Small("");
        box.AddChild(checks);

        // Steps.
        box = Section(root, "STEPS", "steps", true);
        stepList = new ItemList { CustomMinimumSize = new Vector2(0f, 130f), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        stepList.ItemSelected += i => PickStep((int)i);
        box.AddChild(stepList);
        var nameRow = new HBoxContainer();
        nameRow.AddChild(Small("Name"));
        stepName = new LineEdit { Text = "step 1", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        nameRow.AddChild(stepName);
        nameRow.AddChild(Small("move time (s)"));
        stepTime = new SpinBox { MinValue = 0.02, MaxValue = 5, Step = 0.01, Value = 0.3, CustomMinimumSize = new Vector2(80f, 0f) };
        nameRow.AddChild(stepTime);
        box.AddChild(nameRow);
        var stepButtons = new HFlowContainer();
        stepButtons.AddChild(MakeButton("Add as new step", AddStep));
        stepButtons.AddChild(MakeButton("Overwrite step", OverwriteStep));
        box.AddChild(stepButtons);
        var stepButtons2 = new HFlowContainer();
        stepButtons2.AddChild(MakeButton("Delete", DeleteStep));
        stepButtons2.AddChild(MakeButton("Move up", () => MoveStep(-1)));
        stepButtons2.AddChild(MakeButton("Move down", () => MoveStep(1)));
        stepButtons2.AddChild(MakeButton("Insert between", InsertBetween));
        stepButtons2.AddChild(MakeButton("Duplicate", DuplicateStep));
        box.AddChild(stepButtons2);
        // A big turn of the racket on the move into the picked step: one turn the short way round
        // rather than each angle on its own (the pose check's fix sets it where that eases the wrist).
        shortTurnBox = new CheckBox { Text = "racket turns the short way into this step" };
        shortTurnBox.AddThemeFontSizeOverride("font_size", 12);
        shortTurnBox.TooltipText = "For a big turn of the racket between two steps (a serve swinging round under the ball): it turns as one, the shortest way, "
            + "instead of each angle moving on its own, which can take the wrist round the long way past its range.";
        shortTurnBox.Toggled += on =>
        {
            if (syncing || step < 1 || step >= sequence.Steps.Count)
                return;
            sequence.Steps[step].ShortTurn = on;
            RefreshSteps();
            Status(on ? $"step {step + 1}: the racket turns the short way round on the move into it" : $"step {step + 1}: each racket angle moves on its own into it");
        };
        box.AddChild(shortTurnBox);
        // The contact: the focus of a stroke. How the racket goes through the ball (finish) and the
        // spin it is shaped for, with what the game's contact physics makes of it.
        box = Section(root, "CONTACT  (racket meets ball)", "contact", true, focus: true);
        contactLine = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        contactLine.AddThemeFontSizeOverride("font_size", 16);
        box.AddChild(contactLine);
        contactDetail = Small("");
        contactDetail.AddThemeColorOverride("font_color", new Color(0.85f, 0.82f, 0.75f));
        box.AddChild(contactDetail);
        var spinRow = new HBoxContainer();
        var spinName = Small("Spin");
        spinName.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        spinName.CustomMinimumSize = new Vector2(48f, 0f);
        spinRow.AddChild(spinName);
        spinPicker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FitToLongestItem = false, ClipText = true, CustomMinimumSize = new Vector2(120f, 0f) };
        spinPicker.AddItem("as made (your path)");
        spinPicker.SetItemTooltip(0, "the racket's path into the ball as your steps have it");
        spinPicker.AddItem("no spin: hit through the middle");
        spinPicker.SetItemTooltip(1, "the path square to the face, straight through the ball's middle: all push, a fast 'dead' ball with next to no spin");
        spinPicker.AddItem("light spin: half brush");
        spinPicker.SetItemTooltip(2, "the path at 40° to the face: half push, half brush");
        spinPicker.AddItem("heavy spin: thin brush");
        spinPicker.SetItemTooltip(3, "the path at 10° to the face, grazing the ball: much spin, a slower ball");
        spinPicker.ItemSelected += i => PickSpin((PoseSequence.SpinLevel)(int)i);
        spinRow.AddChild(spinPicker);
        box.AddChild(spinRow);
        var finishRow = new HBoxContainer();
        var finishName = Small("Finish");
        finishName.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        finishName.CustomMinimumSize = new Vector2(48f, 0f);
        finishRow.AddChild(finishName);
        finishPicker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FitToLongestItem = false, ClipText = true, CustomMinimumSize = new Vector2(120f, 0f) };
        finishPicker.AddItem("normal");
        finishPicker.SetItemTooltip(0, "speeds up steadily into the ball, then follows through to the finish pose");
        finishPicker.AddItem("snap: sudden stop");
        finishPicker.SetItemTooltip(1, "hard acceleration into the ball, then braked hard just after it: the racket runs on a few cm by inertia (more spin, a harder hit)");
        finishPicker.AddItem("soft: brake early");
        finishPicker.SetItemTooltip(2, "slow acceleration, braked before the ball: a soft, light touch with less spin");
        finishPicker.ItemSelected += i => PickFinish((PoseSequence.FinishStyle)(int)i);
        finishRow.AddChild(finishPicker);
        box.AddChild(finishRow);
        var brushRow = new HBoxContainer();
        var brushName = Small("Brush");
        brushName.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        brushName.CustomMinimumSize = new Vector2(48f, 0f);
        brushRow.AddChild(brushName);
        brushPicker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FitToLongestItem = false, ClipText = true, CustomMinimumSize = new Vector2(120f, 0f) };
        brushPicker.AddItem("the way your path goes");
        brushPicker.AddItem("topspin: brush up the back");
        brushPicker.AddItem("backspin: brush down, under");
        brushPicker.AddItem("sidespin: brush across to the left");
        brushPicker.AddItem("sidespin: brush across to the right");
        brushPicker.TooltipText = "Which way the racket brushes the ball, which sets the kind of spin; Spin sets how much. Both shape the move into the contact and out of it.";
        brushPicker.ItemSelected += i =>
        {
            if (!sequence.HasFinish)
            {
                brushPicker.Selected = 0;
                Status("the brush needs the contact step with a step before and after it: \"Make the finish step\" makes them");
                return;
            }
            sequence.ContactBrush = (PoseSequence.Brush)(int)i;
            PickSpin(sequence.ContactSpin);
        };
        brushRow.AddChild(brushPicker);
        box.AddChild(brushRow);
        lockBox = new CheckBox { Text = "the picked step meets the ball (blade kept on the ball)" };
        lockBox.AddThemeFontSizeOverride("font_size", 12);
        lockBox.TooltipText = "The blade is brought to the ball (the arm, hand and wrist with it) and kept on it, touching anywhere on the face, whatever joint you move in this step. Move the ball (T) and the blade follows.";
        lockBox.Toggled += LockContact;
        box.AddChild(lockBox);
        var contactButtons = new HFlowContainer();
        contactButtons.AddChild(MakeButton("Make the finish step", MakeFinish));
        contactButtons.AddChild(MakeButton("▶ Play through the contact", PlayContact));
        box.AddChild(contactButtons);

        // Play.
        box = Section(root, "PLAY", "play", true);
        var playRow = new HFlowContainer();
        playButton = MakeButton("▶ Play", TogglePlay);
        playRow.AddChild(playButton);
        playRow.AddChild(Small("speed"));
        speedPicker = new OptionButton();
        foreach (string s in new[] { "x0.1", "x0.25", "x0.5", "x1 (real)" })
            speedPicker.AddItem(s);
        speedPicker.Selected = 1;
        speedPicker.ItemSelected += i => playSpeed = new[] { 0.1f, 0.25f, 0.5f, 1f }[i];
        playRow.AddChild(speedPicker);
        var loopBox = new CheckBox { Text = "loop", ButtonPressed = loop };
        loopBox.Toggled += on => loop = on;
        playRow.AddChild(loopBox);
        var easeBox = new CheckBox { Text = "ease", ButtonPressed = ease };
        easeBox.Toggled += on => ease = on;
        playRow.AddChild(easeBox);
        box.AddChild(playRow);
        timeline = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.001, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        timeline.ValueChanged += v => { if (!syncing) Scrub((float)v); };
        box.AddChild(timeline);
        timeLabel = Small("");
        box.AddChild(timeLabel);

        // Power: what the racket gives the ball, for this player's weight.
        box = Section(root, "POWER & CONTACT PHYSICS  (details)", "power", false);
        SpinBox Number(HFlowContainer row, string label, double min, double max, double step, double value)
        {
            var name = new Label { Text = label, VerticalAlignment = VerticalAlignment.Center };
            name.AddThemeFontSizeOverride("font_size", 12);
            row.AddChild(name);
            var number = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = value, CustomMinimumSize = new Vector2(84f, 0f) };
            number.ValueChanged += _ => UpdatePower();
            row.AddChild(number);
            return number;
        }
        var powerRow = new HFlowContainer();
        weightBox = Number(powerRow, "weight (kg)", 35, 140, 1, InGame && target != null ? target.BodyMass : Human.DefaultBodyMass);
        weightBox.ValueChanged += v =>
        {
            if (InGame && target != null)
                target.BodyMass = (float)v; // the NPC plays at this weight from now on
        };
        staminaBox = Number(powerRow, "stamina (0 tires fast .. 1)", 0, 1, 0.05, InGame && target != null ? target.Stamina : 0.6);
        staminaBox.TooltipText = "How fast the NPC tires in rallies and recovers between points (Script/Fatigue.cs).";
        staminaBox.ValueChanged += v =>
        {
            if (InGame && target != null)
                target.Stamina = (float)v; // the NPC tires at this rate from now on
        };
        racketSpeedBox = Number(powerRow, "racket (m/s)", 0, 30, 0.1, Mathf.Snapped(Human.DriveRacketSpeed(0.6f), 0.1f));
        ballSpeedBox = Number(powerRow, "ball coming (m/s)", 0, 30, 0.1, 8);
        angleBox = Number(powerRow, "path to face (°, 90 = through centre)", 0, 90, 1, 90);
        spinBox = Number(powerRow, "spin coming (rev/s, + top)", -150, 150, 1, 0);
        brushDownBox = new CheckBox { Text = "brush down (chop / push)", ButtonPressed = false };
        brushDownBox.AddThemeFontSizeOverride("font_size", 12);
        brushDownBox.Toggled += _ => UpdatePower();
        powerRow.AddChild(brushDownBox);
        box.AddChild(powerRow);
        box.AddChild(MakeButton("Racket speed from this motion (swing into contact)", RacketSpeedFromMotion));
        powerText = Small("");
        powerText.AddThemeColorOverride("font_color", new Color(0.8f, 0.85f, 0.95f));
        box.AddChild(powerText);

        // File.
        box = Section(root, "SAVE / LOAD  (res://Poses/<name>.json)", "file", true);
        var saveRow = new HBoxContainer();
        sequenceName = new LineEdit { Text = sequence.Name, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        saveRow.AddChild(sequenceName);
        saveRow.AddChild(MakeButton("Save", Save));
        saveRow.AddChild(MakeButton("New", NewSequence));
        box.AddChild(saveRow);
        var loadRow = new HBoxContainer();
        loadPicker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FitToLongestItem = false, ClipText = true, CustomMinimumSize = new Vector2(120f, 0f) };
        loadRow.AddChild(loadPicker);
        loadRow.AddChild(MakeButton("Load", Load));
        box.AddChild(loadRow);
        box = Section(root, "KEYS", "keys", false);
        box.AddChild(Small("Keys: Q carry, W point only, E rotate joint, R whole body (, . turn it), T ball; - / + centre of gravity down / up; J L I K body left / right / to table / back over the feet (shift: step, feet too); arrows / PgUp PgDn move the part (shift ×5), Space play, Ctrl+Z undo, Ctrl+S save, Ctrl+D duplicate step, 1-4 views"
                           + (InGame ? $", {ToggleKey} back to the game." : ".")));
    }

    // A foldable section of the panel: its title (a button: click to fold or open it, remembered
    // in user://pose_lab_panel.cfg) over its body, on a slightly lighter card; the focus card
    // (the contact) framed in orange. Returns the body to fill.
    readonly List<(Button Title, Control Body, string Key, string Text)> sections = new();
    const string PanelStateFile = "user://pose_lab_panel.cfg";
    ConfigFile panelState;

    VBoxContainer Section(VBoxContainer root, string title, string key, bool openAtFirst, bool focus = false)
    {
        if (panelState == null)
        {
            panelState = new ConfigFile();
            panelState.Load(PanelStateFile); // none yet: every section as it opens at first
        }
        bool open = (bool)panelState.GetValue("open", key, openAtFirst);
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = focus ? new Color(0.17f, 0.12f, 0.07f) : new Color(0.12f, 0.13f, 0.16f),
            BorderColor = new Color(1f, 0.6f, 0.2f),
            BorderWidthLeft = focus ? 3 : 0, BorderWidthRight = focus ? 3 : 0, BorderWidthTop = focus ? 3 : 0, BorderWidthBottom = focus ? 3 : 0,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
            ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 4, ContentMarginBottom = 6,
        });
        root.AddChild(card);
        var inside = new VBoxContainer();
        inside.AddThemeConstantOverride("separation", 4);
        card.AddChild(inside);
        var header = new Button { Flat = true, Alignment = HorizontalAlignment.Left, FocusMode = Control.FocusModeEnum.None };
        header.AddThemeFontSizeOverride("font_size", 15);
        Color colour = focus ? new Color(1f, 0.65f, 0.25f) : new Color(1f, 0.85f, 0.3f);
        foreach (string state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
            header.AddThemeColorOverride(state, colour);
        inside.AddChild(header);
        var body = new VBoxContainer { Visible = open };
        body.AddThemeConstantOverride("separation", 4);
        inside.AddChild(body);
        var entry = (header, (Control)body, key, title);
        sections.Add(entry);
        ShowFold(entry);
        header.Pressed += () =>
        {
            body.Visible = !body.Visible;
            ShowFold(entry);
            SaveFolds();
        };
        return body;
    }

    static void ShowFold((Button Title, Control Body, string Key, string Text) s) =>
        s.Title.Text = (s.Body.Visible ? "▾  " : "▸  ") + s.Text;

    void FoldAll(bool fold)
    {
        foreach (var s in sections)
        {
            s.Body.Visible = !fold;
            ShowFold(s);
        }
        SaveFolds();
    }

    void SaveFolds()
    {
        foreach (var s in sections)
            panelState.SetValue("open", s.Key, s.Body.Visible);
        panelState.Save(PanelStateFile);
    }

    // The focus bar: what is being edited now (the action, the step, the part, the mode).
    Label focusLabel, contactLine, contactDetail;
    OptionButton spinPicker;
    CheckBox shortTurnBox;
    string focusShown;

    void UpdateFocus()
    {
        if (focusLabel == null)
            return;
        // The action, when the motion open is that action's own (not one loaded under another name).
        string action = actionPicker != null && actionPicker.Selected >= 0 && StrokeActions.All[actionPicker.Selected] is { } a
                        && (sequence.Name == a.Id || sequenceName?.Text == a.Id) ? StrokeActions.Label(a) : "";
        string motion = sequenceName != null && sequenceName.Text != "" ? sequenceName.Text : "(unnamed)";
        string stepText = step >= 0 && step < sequence.Steps.Count
            ? $"step {step + 1} of {sequence.Steps.Count}: \"{sequence.Steps[step].Name}\"{(step == sequence.ContactIndex ? "  ● CONTACT" : "")}"
            : $"{sequence.Steps.Count} steps, none picked";
        string modeText = Modes.FirstOrDefault(m => m.Mode == mode).Label ?? mode.ToString();
        string locked = ContactLocked
            ? $"\n◉ CONTACT LOCK: this step meets the ball; the blade stays on it whatever you move ({lockNote}{(atFullReach.Any(r => r.StartsWith("right arm")) ? "; OUT OF REACH: the arm is straight" : "")}). Overwrite step to keep a change."
            : "";
        string text = $"▶ {motion}{(action != "" ? $"   ({action})" : "")}\n{stepText}\npart: {Parts[part].Name}   ·   drag: {modeText}{locked}";
        if (text == focusShown)
            return;
        focusShown = text;
        focusLabel.Text = text;
    }



    // ---------------------------------------------------------------- contact: spin

    // The face the racket hits with at the contact (own frame, pointing out of the rubber towards
    // the ball), found as the game finds it (Opponent.AimServeMotion): the face on the ball's side
    // when the step shows the ball off the blade's plane, else the face the racket moves towards
    // (path: its move into the contact), else the side's own rubber (forehand or backhand).
    Vector3 HittingFace(Dictionary<string, float> values, Vector3 path = default)
    {
        Dictionary<string, float> keep = pose;
        pose = values;
        Vector3 z = RacketBasis.Z.Normalized();
        bool forehand = Forehand;
        pose = keep;
        Vector3 ball = new(values.GetValueOrDefault("ball.x"), values.GetValueOrDefault("ball.y"), values.GetValueOrDefault("ball.z"));
        // The ball decides when the step has it: shown (a serve), or on the blade (a step meeting it).
        bool hasBall = values.GetValueOrDefault("ball.show") > 0.5f || ball.DistanceTo(PoseRig.RacketAt(values)) < 0.12f;
        float side = hasBall ? z.Dot(ball - PoseRig.RacketAt(values)) : 0f;
        if (Mathf.Abs(side) < 0.005f)
            side = z.Dot(path);
        if (Mathf.Abs(side) < 1e-4f)
            side = z.Z; // the face towards the table
        return side >= 0f ? z : -z;
    }

    // A spin level for the contact: the move into it and out of it turned about the contact
    // point so the racket meets the face at the level's angle (PoseSequence.BrushAngle), brushing
    // the way the path did (up, down or across; up when it went straight in), each move as long
    // as it was. The path as made is kept on the contact step, so "as made" puts it back.
    void PickSpin(PoseSequence.SpinLevel level) => PickSpin(level, false);

    void PickSpin(PoseSequence.SpinLevel level, bool keepFinishLength)
    {
        if (!sequence.HasFinish)
        {
            spinPicker.Selected = 0;
            Status("the spin needs a step named \"contact...\" with a step before it and one after it (the finish)");
            return;
        }
        int c = sequence.ContactIndex;
        PoseSequence.Step before = sequence.Steps[c - 1], contact = sequence.Steps[c], after = sequence.Steps[c + 1];
        // A serve's contact found as the step nearest the ball is named for it, so it stays this
        // step once the moves round it change (the game looks for a step named "contact" first);
        // a step marked as meeting the ball is the contact already.
        string renamed = null;
        if (!contact.Meets && !contact.Name.ToLowerInvariant().Contains("contact"))
            contact.Name = renamed = "contact: " + contact.Name;
        static void Put(Dictionary<string, float> v, Vector3 p)
        {
            v["racket.x"] = p.X;
            v["racket.y"] = p.Y;
            v["racket.z"] = p.Z;
        }
        if (level == PoseSequence.SpinLevel.AsShaped && sequence.ContactBrush != PoseSequence.Brush.AsMade)
            level = PoseSequence.SpinLevel.Light; // a brush asked for needs an amount: light to start with
        if (contact.Unshaped is { Length: 6 } u)
        {
            Put(before.Values, new Vector3(u[0], u[1], u[2]));
            Put(after.Values, new Vector3(u[3], u[4], u[5]));
            contact.Unshaped = null;
        }
        sequence.ContactSpin = level;
        if (level != PoseSequence.SpinLevel.AsShaped)
        {
            Vector3 b = PoseRig.RacketAt(before.Values), at = PoseRig.RacketAt(contact.Values), a = PoseRig.RacketAt(after.Values);
            contact.Unshaped = new[] { b.X, b.Y, b.Z, a.X, a.Y, a.Z };
            Vector3 n = HittingFace(contact.Values, at - b), into = at - b;
            if (into.Length() < 0.05f)
                into = n * 0.3f; // no backswing yet: come in from behind the face
            // The brush along the face: for the spin the brush picker asks, the way that gives it
            // with this face, whatever its tilt (the rubber drags the ball's touching point along,
            // spinning it about n x brush, so brushing along n x axis spins it about the axis); the
            // ball going forward (+ Z): topspin about + X, backspin about - X, brushed across to
            // the left about - Y, to the right about + Y. As made: the way the path goes.
            Vector3 Along(Vector3 way) => way - n * n.Dot(way);
            Vector3 brush = sequence.ContactBrush switch
            {
                PoseSequence.Brush.Top => n.Cross(Vector3.Right),
                PoseSequence.Brush.Back => n.Cross(Vector3.Left),
                PoseSequence.Brush.SideLeft => n.Cross(Vector3.Down),
                PoseSequence.Brush.SideRight => n.Cross(Vector3.Up),
                _ => Along(into),
            };
            if (brush.Length() < 1e-3f)
                brush = Vector3.Up - n * n.Dot(Vector3.Up); // straight in: brush up
            if (brush.Length() < 1e-3f)
                brush = Vector3.Right - n * n.Dot(Vector3.Right);
            float angle = Mathf.DegToRad(PoseSequence.BrushAngle(level));
            Vector3 way = (brush.Normalized() * Mathf.Cos(angle) + n * Mathf.Sin(angle)).Normalized();
            Put(before.Values, at - way * Mathf.Max(into.Length(), 0.05f));
            Put(after.Values, at + way * Mathf.Max(a.DistanceTo(at), keepFinishLength ? 0.05f : 0.05f));
            EaseMoved(before, after);
        }
        if (step == c - 1 || step == c + 1)
            PickStep(step); // the picked step shown as it is now
        RefreshSteps();
        UpdateContact();
        Status((renamed != null ? $"step {c + 1} renamed \"{renamed}\" (the contact, kept there); " : "")
            + (level == PoseSequence.SpinLevel.AsShaped
            ? "spin: your path into the ball, as you made it"
            : $"spin: the move into the contact and out of it turned to {PoseSequence.BrushAngle(level):F0}° to the face (the contact pose kept): {ContactSummary()}"));
        PlayContact();
    }

    // The steps whose racket the spin moved: the waist and hand turned to ease the wrist there
    // (EaseWrist, the racket kept where it is), as a player lines the arm up behind the racket,
    // so the swing into and out of the contact does not twist the wrist past its range.
    void EaseMoved(params PoseSequence.Step[] moved)
    {
        Dictionary<string, float> keep = pose;
        posingOther = true;
        foreach (PoseSequence.Step s in moved)
        {
            pose = new Dictionary<string, float>(s.Values);
            Apply();
            EaseWrist();
            s.Values = new Dictionary<string, float>(pose);
        }
        posingOther = false;
        pose = keep;
        Apply();
    }

    void SyncShortTurn()
    {
        if (shortTurnBox == null)
            return;
        bool has = step >= 1 && step < sequence.Steps.Count;
        syncing = true;
        shortTurnBox.ButtonPressed = has && sequence.Steps[step].ShortTurn;
        syncing = false;
        shortTurnBox.Disabled = !has;
    }

    // ---------------------------------------------------------------- contact lock

    // The blade face (paddle space): the rubber's surface this far from the middle of the blade,
    // inside an ellipse round the head; a ball touching it anywhere there counts.
    const float FaceOut = 0.0064f;
    static readonly Vector2 HeadCentre = new(0f, 0.012f), HeadHalf = new(0.075f, 0.078f);
    const float EdgeRoom = 0.006f; // the ball's touch kept this far inside the edge

    // The picked step is the one that meets the ball, and its pose is being edited (not played).
    bool ContactLocked => !playing && !scrubbed && !posingOther && step >= 0 && step < sequence.Steps.Count && sequence.Steps[step].Meets;
    // Set while the lab poses something other than the picked step (easing other steps, a
    // template's starting motion, the pose check): the lock is the picked step's alone.
    bool posingOther;
    bool scrubbed;            // the pose shown is a moment of the playback, not a step
    string lockNote = "";     // what the lock did last, for the focus bar

    // Moves the racket (its turn kept) so the ball touches the hitting face: the face on the
    // ball's side, the touch where the ball is over the face or, past the edge, the nearest point
    // inside it. Returns whether it moved.
    bool KeepOnBall()
    {
        Vector3 ballInPaddle = paddle.GlobalTransform.AffineInverse() * W(BallAt);
        // The face towards the table (the ball is struck towards the other side), so a ball moved
        // through the blade does not end up behind it; a face lying nearly flat (pointing up or
        // down) takes the side the ball is on.
        float towardsTable = RacketBasis.Z.Z;
        float side = Mathf.Abs(towardsTable) > 0.2f ? Mathf.Sign(towardsTable)
            : Mathf.Abs(ballInPaddle.Z) > 0.002f ? Mathf.Sign(ballInPaddle.Z) : 1f;
        Vector2 onFace = new(ballInPaddle.X, ballInPaddle.Y);
        Vector2 rel = (onFace - HeadCentre) / (HeadHalf - Vector2.One * EdgeRoom);
        if (rel.Length() > 1f)
            onFace = HeadCentre + rel.Normalized() * (HeadHalf - Vector2.One * EdgeRoom);
        var wanted = new Vector3(onFace.X, onFace.Y, side * (FaceOut + Ball.Radius));
        Vector3 shift = paddle.GlobalBasis * (ballInPaddle - wanted); // the paddle moved this much puts the ball there
        float fromMiddle = (onFace - HeadCentre).Length() * 100f;
        lockNote = $"touching {fromMiddle:F1} cm from the middle of the blade";
        if (shift.Length() < 0.0005f)
            return false;
        Vector3 to = RacketAt + toWorld.Inverse() * shift;
        Hold("racket.x", to.X);
        Hold("racket.y", to.Y);
        Hold("racket.z", to.Z);
        return true;
    }

    // Marks the picked step as the one meeting the ball (or unmarks it). Marking it: a ball not
    // yet placed in the step (a stroke's, without the serve's ball) is put on the middle of the
    // hitting face; otherwise the blade is brought to the ball, the arm, hand and wrist with it
    // (and the wrist eased, the blade kept on the ball). The step keeps the result.
    void LockContact(bool on)
    {
        if (syncing)
            return;
        if (step < 0 || step >= sequence.Steps.Count)
        {
            Status("pick the step that meets the ball first");
            SyncLock();
            return;
        }
        PushUndo(true);
        foreach (PoseSequence.Step st in sequence.Steps)
            st.Meets = false;
        if (on)
        {
            sequence.Steps[step].Meets = true;
            scrubbed = false;
            bool ballPlaced = V("ball.show") > 0.5f || BallAt.DistanceTo(RacketAt) < 0.25f;
            if (!ballPlaced)
            {
                // Out from the middle of the face that points towards the table (+ Z, own frame):
                // the ball is struck towards the other side, whichever rubber that is.
                float side = RacketBasis.Z.Z >= 0f ? 1f : -1f;
                Vector3 there = O(paddle.GlobalTransform * new Vector3(HeadCentre.X, HeadCentre.Y, side * (FaceOut + Ball.Radius)));
                Hold("ball.x", there.X);
                Hold("ball.y", there.Y);
                Hold("ball.z", there.Z);
            }
            Apply();
            EaseWrist();
            Apply();
            sequence.Steps[step].Values = new Dictionary<string, float>(pose);
            Status($"step {step + 1} meets the ball: the blade is on it ({lockNote}); move any joint and it stays on the ball. "
                + (ballPlaced ? "" : "The ball was put on the middle of the face: move it (T) to where the stroke meets it, the blade follows."));
        }
        else
        {
            Apply();
            Status($"step {step + 1} no longer locked to the ball");
        }
        RefreshSteps();
    }

    void SyncLock()
    {
        if (lockBox == null)
            return;
        syncing = true;
        lockBox.ButtonPressed = step >= 0 && step < sequence.Steps.Count && sequence.Steps[step].Meets;
        syncing = false;
        lockBox.Disabled = step < 0 || step >= sequence.Steps.Count;
    }

    CheckBox lockBox;
    OptionButton brushPicker;

    // The finish worked out for the contact: the step after it (made if there is none, and a
    // backswing before it too) placed along the path the brush and spin decide, from the contact
    // pose, the wrist eased there. The kind of spin from the brush (up for topspin, down and under
    // for backspin, across for sidespin), how much from the amount (as Spin: the path's angle to
    // the face); the follow-through the longer the more it brushes.
    void MakeFinish()
    {
        int c = sequence.ContactIndex;
        if (c < 0)
        {
            if (step < 0 || step >= sequence.Steps.Count)
            {
                Status("pick the step that meets the ball, tick \"meets the ball\", then make the finish");
                return;
            }
            sequence.Steps[step].Meets = true;
            c = step;
        }
        PoseSequence.Step contact = sequence.Steps[c];
        if (c == 0)
        {
            sequence.Steps.Insert(0, new PoseSequence.Step { Name = "backswing", Duration = 0f, Values = new Dictionary<string, float>(contact.Values) });
            contact.Duration = Mathf.Max(contact.Duration, 0.25f);
            c = 1;
        }
        bool made = c + 1 >= sequence.Steps.Count;
        if (made)
            sequence.Steps.Insert(c + 1, new PoseSequence.Step { Name = "finish", Duration = 0.2f, Values = new Dictionary<string, float>(contact.Values) });
        PoseSequence.SpinLevel level = sequence.ContactSpin == PoseSequence.SpinLevel.AsShaped ? PoseSequence.SpinLevel.Light : sequence.ContactSpin;
        PoseSequence.Step after = sequence.Steps[c + 1];
        // The follow-through: a hit through the middle stops soonest, a thin brush runs on longest.
        float length = level switch { PoseSequence.SpinLevel.None => 0.22f, PoseSequence.SpinLevel.Light => 0.3f, _ => 0.38f };
        Vector3 at = PoseRig.RacketAt(contact.Values);
        after.Values["racket.x"] = at.X;
        after.Values["racket.y"] = at.Y;
        after.Values["racket.z"] = at.Z + length; // a start for the reshape below, which turns it
        after.Unshaped = null;
        contact.Unshaped = null;
        step = c;
        PickSpin(level, keepFinishLength: true);
        Status($"{(made ? "finish step made" : "finish step worked out")} for {BrushWords(sequence.ContactBrush)}, {level.ToString().ToLower()} spin asked; the contact gives {ContactSummary()}");
    }

    static string BrushWords(PoseSequence.Brush b) => b switch
    {
        PoseSequence.Brush.Top => "topspin (brushing up)",
        PoseSequence.Brush.Back => "backspin (brushing down, under the ball)",
        PoseSequence.Brush.SideLeft => "sidespin (brushing across to the left)",
        PoseSequence.Brush.SideRight => "sidespin (brushing across to the right)",
        _ => "the brush your path makes",
    };

    void PlayContact()
    {
        if (!sequence.HasFinish)
        {
            Status("needs a step named \"contact...\" with a step before it and one after it");
            return;
        }
        playTime = sequence.StartOf(Mathf.Max(sequence.ContactIndex - 1, 0));
        playing = true;
        playButton.Text = "■ Stop";
    }

    // What the contact makes of the ball, worked out with the game's own hit (Ball.Analyse): the
    // racket at its speed at the ball (the swing's, PoseSequence.SwingInfo) along its path into the
    // contact step, the hitting face as the contact step has it. The ball is a slow one without
    // spin (2 m/s into the face; for a serve, falling from the toss), so what shows is the spin
    // the stroke itself makes: a ball coming in a rally adds its own (POWER & CONTACT PHYSICS).
    (Ball.RacketContact Contact, Vector3 Racket, Vector3 Face, float Angle)? ContactResult()
    {
        if (!sequence.HasFinish || sequence.SwingInfo() is not PoseSequence.Swing swing)
            return null;
        int c = sequence.ContactIndex;
        Vector3 b = PoseRig.RacketAt(sequence.Steps[c - 1].Values), at = PoseRig.RacketAt(sequence.Steps[c].Values);
        Vector3 n = HittingFace(sequence.Steps[c].Values, at - b);
        Vector3 racket = (at - b).Normalized() * swing.ContactSpeed;
        Vector3 coming = IsServe ? new Vector3(0f, -2f, 0f) : -n * 2f;
        var ball = new BallState(at + n * (Ball.Radius + 0.006f), coming, Vector3.Zero);
        float weight = weightBox != null ? (float)weightBox.Value : Human.DefaultBodyMass;
        Ball.RacketContact result = Ball.Analyse(ball, n, racket, Ball.RubberGrip, weight);
        float angle = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(racket.Normalized().Dot(n), -1f, 1f)));
        return (result, racket, n, angle);
    }

    // Why no legal serve can be played from the serve's contact (the ball where the contact step
    // has it), or null: worked out with the game's serve search, kept until the ball there moves.
    Vector3 serveCheckedAt = new(float.NaN, 0f, 0f);
    string serveImpossible;

    string ServeImpossibleFrom()
    {
        Dictionary<string, float> v = sequence.Steps[sequence.ContactIndex].Values;
        var at = new Vector3(v.GetValueOrDefault("ball.x"), v.GetValueOrDefault("ball.y"), v.GetValueOrDefault("ball.z"));
        if (at.IsEqualApprox(serveCheckedAt))
            return serveImpossible;
        serveCheckedAt = at;
        float above = (at.Y - TableSpec.Height) * 100f, behind = (-TableSpec.HalfLength - at.Z) * 100f;
        serveImpossible = behind < 0f ? $"the ball is met {-behind:F0} cm over the table; a serve is struck from behind the end line"
            : above < 0f ? $"the ball is met {-above:F0} cm below the table's height; a serve is struck above it"
            : ShotSolver.CanServeFrom(at, Vector3.Zero) ? null
            : $"met {above:F0} cm above the table and {behind:F0} cm behind the end line, it cannot bounce on your side and still clear the net: meet it higher or nearer the table";
        return serveImpossible;
    }

    // A serve that rises more than this after its bounce on the receiver's side, or crosses the
    // net this far over it, sits up to be attacked (a good short serve bounces 10-20 cm and skims
    // the net by a few cm).
    const float HighServeBounce = 0.3f, HighServeOverNet = 0.2f;

    // The serve the game would play from the contact (Opponent.Serve): its spin as the level asks
    // (or the NPC's own mix), aimed short and low over the receiver's half; of those that are
    // legal, the lowest. How far over the net it passes and how high it bounces on the other side
    // (metres), with why it is high. Kept until the ball at the contact or the level changes.
    (Vector3 At, PoseSequence.SpinLevel Level, bool Under) gameServeKey;
    (float OverNet, float Bounce, float Low, float High, string Why)? gameServe;

    (float OverNet, float Bounce, float Low, float High, string Why)? GameServe()
    {
        int c = sequence.ContactIndex;
        Dictionary<string, float> v = sequence.Steps[c].Values;
        var at = new Vector3(v.GetValueOrDefault("ball.x"), v.GetValueOrDefault("ball.y"), v.GetValueOrDefault("ball.z"));
        PoseSequence.SpinLevel level = sequence.ContactSpin;
        // Underspin as the game reads the motion (ServeMotion): an open face, or the racket coming down.
        bool under = v.GetValueOrDefault("racket.open") > 45f || PoseRig.RacketAt(v).Y < PoseRig.RacketAt(sequence.Steps[c - 1].Values).Y;
        var key = (at, level, under);
        if (gameServe != null && key.at.IsEqualApprox(gameServeKey.At) && key.level == gameServeKey.Level && key.under == gameServeKey.Under)
            return gameServe;
        gameServeKey = key;
        float share = level switch
        {
            PoseSequence.SpinLevel.None => 0.04f,
            PoseSequence.SpinLevel.Light => 0.42f,
            PoseSequence.SpinLevel.Heavy => 1.3f,
            _ => under ? 1.2f : 0.8f,
        };
        float revs = Human.ServeSpin(target?.Skill ?? 0.6f) * share * Mathf.Tau;
        var spin = new Vector3(under ? -revs : revs, 0f, 0f); // the ball going +Z: + X is topspin
        if (ServeFrom(at, spin) is not { } here)
            return gameServe = null;
        string why = "";
        if (here.Bounce > HighServeBounce || here.OverNet > HighServeOverNet)
        {
            // What would bring it down: the contact a little higher, or nearer the table (still
            // behind the end line), tried with the same serve search; the best that helps is named.
            var tries = new List<(Vector3 Move, string Words)>
            {
                (new Vector3(0f, 0.1f, 0f), "10 cm higher"), (new Vector3(0f, 0.2f, 0f), "20 cm higher"),
                (new Vector3(0f, -0.08f, 0f), "8 cm lower"),
            };
            float room = -TableSpec.HalfLength - 0.01f - at.Z; // how far it can come nearer the table
            if (room > 0.04f)
                tries.Add((new Vector3(0f, 0f, Mathf.Min(room, 0.1f)), $"{Mathf.Min(room, 0.1f) * 100f:F0} cm nearer the table"));
            (string Words, float OverNet, float Bounce)? better = null;
            foreach (var (move, words) in tries)
            {
                Vector3 there = at + move;
                if (there.Y < TableSpec.Height + 0.05f || ServeFrom(there, spin) is not { } f)
                    continue;
                if (f.Bounce + 0.05f < here.Bounce && (better == null || f.Bounce < better.Value.Bounce))
                    better = (words, f.OverNet, f.Bounce);
            }
            float above = (at.Y - TableSpec.Height) * 100f;
            string cause = better?.Words switch
            {
                string w when w.Contains("higher") => $"Met only {above:F0} cm above the table, the ball has to be struck down hard so that it bounces up over the net.",
                string w when w.Contains("lower") => $"Met {above:F0} cm above the table, the ball comes down steeply and bounces up high.",
                _ => "Far behind the end line, the ball has a long way to go before the net.",
            };
            why = better is { } b
                ? $"{cause} Meet the ball {b.Words}: over the net {b.OverNet * 100f:F0} cm, bouncing {b.Bounce * 100f:F0} cm."
                : under && level is not PoseSequence.SpinLevel.Heavy
                ? "With little backspin the ball does not check after its bounce: pick heavy spin to keep it low and short."
                : "No nearby contact point does much better: brush under the ball more thinly (heavy spin), or toss lower so it falls slower.";
        }
        return gameServe = (here.OverNet, here.Bounce, here.Low, here.High, why);
    }

    // The game's serve from here with this spin, aimed where the game aims it (Opponent.Serve:
    // an underspin serve 0.35-0.6 m past the net, a topspin one 0.6-1.1 m): how far over the net
    // it passes and how high it bounces on the other side (metres), as the middle of that range
    // gives them, with the lowest and highest bounce over it; null when none is legal.
    static (float OverNet, float Bounce, float Low, float High)? ServeFrom(Vector3 at, Vector3 spin)
    {
        bool under = spin.X < 0f;
        float[] depths = under ? new[] { 0.35f, 0.48f, 0.6f } : new[] { 0.6f, 0.85f, 1.1f };
        var found = new List<(float OverNet, float Bounce)>();
        foreach (float depth in depths)
        {
            Vector2 aim = new(0f, depth);
            if (!ShotSolver.SearchServe(at, spin, aim, out Vector3 velocity))
                continue;
            var s = new BallState(at, velocity, spin);
            float overNet = float.NaN, bounce = 0f;
            int bounces = 0;
            for (float t = 0f; t < 2.5f && bounces < 3; t += Ball.SubStepTime)
            {
                Vector3 before = s.Position;
                BallEvent e = Ball.Step(ref s, Ball.SubStepTime, false);
                if (float.IsNaN(overNet) && before.Z < 0f && s.Position.Z >= 0f)
                    overNet = s.Position.Y - Ball.Radius - TableSpec.NetTop;
                if (e == BallEvent.Table)
                    bounces++;
                else if (e == BallEvent.Floor)
                    break;
                if (bounces == 2)
                    bounce = Mathf.Max(bounce, s.Position.Y - Ball.Radius - TableSpec.Height);
            }
            found.Add((overNet, bounce));
        }
        if (found.Count == 0)
            return null;
        var middle = found[found.Count / 2];
        return (middle.OverNet, middle.Bounce, found.Min(f => f.Bounce), found.Max(f => f.Bounce));
    }

    // A serve: the motion shows the ball at the start (and tosses it).
    bool IsServe => sequence.Steps.Count > 0 && sequence.Steps[0].Values.GetValueOrDefault("ball.show") > 0.5f;

    // The spin of a ball after the contact, split along its flight: top (+) or back (-) and side
    // (+ to the left), rev/s.
    static (float Top, float Side) SpinParts(BallState after)
    {
        Vector3 flat = new(after.Velocity.X, 0f, after.Velocity.Z);
        Vector3 across = flat.LengthSquared() > 1e-4f ? Vector3.Up.Cross(flat.Normalized()) : Vector3.Right;
        return (after.Spin.Dot(across) / Mathf.Tau, after.Spin.Dot(Vector3.Up) / Mathf.Tau);
    }

    // Top/back and side spin together (the turn about the flight barely shows in the bounce).
    static float SpinOf(BallState after)
    {
        var (top, side) = SpinParts(after);
        return Mathf.Sqrt(top * top + side * side);
    }

    static string SpinClass(float revs) => revs < 10f ? "NO SPIN" : revs < 35f ? "LIGHT SPIN" : "HEAVY SPIN";

    string ContactSummary()
    {
        if (ContactResult() is not { } r)
            return "";
        BallState after = r.Contact.After;
        var (top, side) = SpinParts(after);
        float total = SpinOf(after);
        return $"{SpinClass(total).ToLower()}: {Mathf.Abs(top):F0} rev/s {(top >= 0f ? "topspin" : "backspin")}, {Mathf.Abs(side):F0} rev/s sidespin ({(side < 0f ? "brushed left" : "brushed right")}), ball {after.Velocity.Length():F1} m/s";
    }

    // The contact card: the result in large type (its colour by the spin: blue none, yellow light,
    // orange heavy), the racket and path underneath, and the pickers as the motion has them.
    void UpdateContact()
    {
        if (contactLine == null)
            return;
        spinPicker.Selected = (int)sequence.ContactSpin;
        brushPicker.Selected = (int)sequence.ContactBrush;
        spinPicker.Disabled = !sequence.HasFinish;
        spinPicker.TooltipText = sequence.HasFinish ? "" : "needs a step named \"contact...\" with a step before it and one after it (the finish)";
        if (!sequence.HasFinish)
        {
            contactLine.Text = "no contact yet";
            contactLine.AddThemeColorOverride("font_color", new Color(0.6f, 0.62f, 0.68f));
            contactDetail.Text = "Name a step \"contact\" (the racket at the ball), with a step before it (the backswing) and one after it (the finish): then pick the spin and the finish here.";
            return;
        }
        if (ContactResult() is not { } r)
        {
            contactLine.Text = "the racket does not move into the contact";
            contactLine.AddThemeColorOverride("font_color", new Color(1f, 0.7f, 0.4f));
            contactDetail.Text = "Move the racket in the step before the contact (the backswing) away from where it meets the ball.";
            return;
        }
        BallState after = r.Contact.After;
        var (top, side) = SpinParts(after);
        float total = SpinOf(after);
        string cls = SpinClass(total);
        contactLine.Text = $"{cls}  ·  " + (Mathf.Abs(top) < 1f && Mathf.Abs(side) >= 3f ? "" : $"{Mathf.Abs(top):F0} rev/s{(Mathf.Abs(top) < 1f ? "" : top > 0f ? " topspin" : " backspin")}")
            + (Mathf.Abs(side) >= 3f ? $"{(Mathf.Abs(top) < 1f ? "" : " + ")}{Mathf.Abs(side):F0} rev/s sidespin ({(side < 0f ? "brushed left" : "brushed right")})" : "")
            + $"  ·  {after.Velocity.Length():F1} m/s";
        contactLine.AddThemeColorOverride("font_color", total < 10f ? new Color(0.55f, 0.8f, 1f) : total < 35f ? new Color(1f, 0.9f, 0.4f) : new Color(1f, 0.55f, 0.2f));
        PoseSequence.SpinLevel level = sequence.ContactSpin;
        string wanted = level switch
        {
            PoseSequence.SpinLevel.None when total >= 10f => "  (asked: no spin; the face's tilt still turns the ball a little)",
            PoseSequence.SpinLevel.Light when total >= 35f => "  (asked: light; a slower swing gives less)",
            PoseSequence.SpinLevel.Heavy when total < 35f => "  (asked: heavy; swing faster into the ball for more: a shorter move time into the contact, or the snap finish)",
            _ => "",
        };
        bool serve = IsServe;
        string serveLine = "";
        if (serve && ServeImpossibleFrom() is string why)
            wanted += $"  (NO LEGAL SERVE FROM HERE: {why})";
        else if (serve && GameServe() is { } g)
        {
            serveLine = $"\nTHE SERVE IN THE GAME: over the net {g.OverNet * 100f:F0} cm, bouncing up {g.Bounce * 100f:F0} cm on the other side"
                + (g.High - g.Low >= 0.03f ? $" ({g.Low * 100f:F0}-{g.High * 100f:F0} cm as it is aimed short or long)" : "");
            if (g.Bounce > HighServeBounce || g.OverNet > HighServeOverNet)
                serveLine += $"  ⚠ HIGH: easy to attack. {g.Why}";
        }
        if (r.Angle <= 0f)
            wanted += "  (the racket is moving away from the face at the ball: it would not hit it; turn the face, or pick a spin to set the path)";
        contactDetail.Text = $"racket {r.Racket.Length():F1} m/s at the ball, its path {r.Angle:F0}° to the face (90 = through the middle){wanted}\n"
            + $"{(r.Contact.Grips ? $"the ball grips the rubber after {r.Contact.GripsAt * 1000f:F2} ms" : "the ball slides on the rubber the whole contact")}; "
            + (serve ? "met as it falls from the toss" : "the spin the stroke itself makes (a ball coming in a rally adds its own: POWER & CONTACT PHYSICS)")
            + serveLine
            + (serve ? $"\nIN THE GAME: the NPC's serve is worked out for {(level == PoseSequence.SpinLevel.AsShaped ? "its own mix of spin (pick a level to set it)" : level == PoseSequence.SpinLevel.None ? "no spin (a dead ball)" : level.ToString().ToLower() + " spin")}, whatever the speed of this swing" : "");
    }


    static Label Heading(string text)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", 15);
        label.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.3f));
        return label;
    }

    static Label Small(string text)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        label.AddThemeFontSizeOverride("font_size", 12);
        return label;
    }

    static Button MakeButton(string text, System.Action action)
    {
        var button = new Button { Text = text };
        button.AddThemeFontSizeOverride("font_size", 12);
        button.Pressed += action;
        return button;
    }

    void SelectPart(int index)
    {
        part = index;
        if (partPicker.Selected != index)
            partPicker.Selected = index;
        foreach (Node child in partBox.GetChildren())
            child.QueueFree();
        fields.Clear();
        foreach (Channel c in Parts[index].Channels)
        {
            var row = new HBoxContainer();
            var label = Small(c.Label);
            label.CustomMinimumSize = new Vector2(150f, 0f);
            label.SizeFlagsHorizontal = Control.SizeFlags.Fill;
            row.AddChild(label);
            var slider = new HSlider { MinValue = c.Min, MaxValue = c.Max, Step = c.Step, Value = V(c.Key), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            var spin = new SpinBox { MinValue = c.Min, MaxValue = c.Max, Step = c.Step, Value = V(c.Key), CustomMinimumSize = new Vector2(92f, 0f) };
            string key = c.Key;
            slider.ValueChanged += v => { if (!syncing) SetValue(key, (float)v); };
            spin.ValueChanged += v => { if (!syncing) SetValue(key, (float)v); };
            row.AddChild(slider);
            row.AddChild(spin);
            partBox.AddChild(row);
            fields[key] = (slider, spin);
        }
        Apply();
    }

    void SetValue(string key, float value)
    {
        PushUndo(false);
        float v = Mathf.Clamp(value, ChannelOf[key].Min, ChannelOf[key].Max);
        if (key.StartsWith("rshoulder.") || key.StartsWith("lshoulder."))
            Trunk(() => ShoulderTo(key, v)); // the other shoulder answers, both arms come along
        else if (MovesShoulders(key))
            Trunk(() => pose[key] = v); // the arms come with the shoulders
        else if (key is "lhand.x" or "lhand.y" or "lhand.z" && mode != Mode.PointOnly)
            MovingLeftHand(() => pose[key] = v);
        else
        {
            pose[key] = v;
            SyncFields();
            Apply();
        }
    }

    // A change to the trunk or a shoulder: the arms come along (Carrying), except in point-only
    // mode, where the hands and racket stay where they are and the arms bend to keep them.
    void Trunk(System.Action change, bool feetToo = false)
    {
        if (mode != Mode.PointOnly)
        {
            Carrying(change, feetToo);
            return;
        }
        change();
        SyncFields();
        Apply();
    }

    // A shoulder's value: the other shoulder answers (the girdle), except in point-only mode.
    void ShoulderTo(string key, float value)
    {
        if (mode == Mode.PointOnly)
            Hold(key, value);
        else
            MoveShoulder(key, value);
    }

    void SetMode(Mode to)
    {
        if (to == mode && to != Mode.Carry)
            to = Mode.Carry; // the key of the mode in use goes back to carry
        mode = to;
        dragging = rotating = turningWhole = false;
        foreach (var (m, button) in modeButtons)
            button.SetPressedNoSignal(m == mode);
        string help = Modes.First(m => m.Mode == mode).Help;
        modeLabel.Text = help;
        modeBanner.Text = "Mode: " + help;
        FitPickedToMode();
        Apply(); // the mode's markers only
        if (mode == Mode.WholeBody)
            SelectPart(PartIndex("Body (hips)"));
        else if (mode == Mode.Ball)
        {
            SelectPart(PartIndex("Ball"));
            if (V("ball.show") < 0.5f)
            {
                PushUndo(true);
                pose["ball.show"] = 1f;
                SyncFields();
                Apply();
            }
        }
        Status("mode: " + help);
    }

    static int PartIndex(string name) => System.Array.FindIndex(Parts, p => p.Name == name);

    // The markers each mode shows (the others are hidden and cannot be picked):
    //  Carry      the joints that carry what hangs from them: body, its turn / lean, chest turn,
    //             shoulders, elbows, the hands;
    //  PointOnly  the points that move: body, hands, shoulders, elbows, knees, feet;
    //  Rotate     the joints that turn and the turning handles (no ball);
    //  WholeBody  the body; Ball  the ball.
    static bool ShownIn(Mode m, Part p)
    {
        bool point = p.Spin is Spin.None or Spin.RightShoulder or Spin.LeftShoulder or Spin.RightElbow or Spin.LeftElbow or Spin.LeftKnee or Spin.RightKnee;
        return m switch
        {
            Mode.Carry => p.Name is "Body (hips)" or "Right hand + racket" or "Left hand"
                          || p.Spin is Spin.HipsTurn or Spin.BodyLean or Spin.ChestTurn or Spin.RightShoulder or Spin.LeftShoulder or Spin.RightElbow or Spin.LeftElbow,
            Mode.PointOnly => point && p.Name is not ("Waist" or "Ball"),
            Mode.Rotate => p.Name != "Ball" && p.Spin != Spin.WholeTurn,
            Mode.WholeBody => p.Name == "Body (hips)" || p.Spin == Spin.WholeTurn,
            _ => p.Name == "Ball",
        };
    }

    // The part picker offers only what the mode shows; a picked part the mode hides gives way to
    // the mode's first (the racket hand where it is shown).
    void FitPickedToMode()
    {
        for (int i = 0; i < Parts.Length; i++)
            partPicker.SetItemDisabled(i, !ShownIn(mode, Parts[i]));
        if (ShownIn(mode, Parts[part]))
            return;
        int hand = PartIndex("Right hand + racket");
        SelectPart(ShownIn(mode, Parts[hand]) ? hand : System.Array.FindIndex(Parts, p => ShownIn(mode, p)));
    }

    // Rotate mode: the picked joint turns in place. Returns false for a part this mode leaves to
    // its usual drag (the pink turning handles already turn).
    bool BeginRotate()
    {
        rotateFrom = null;
        Part p = Parts[part];
        bool joint = p.Spin is Spin.RightShoulder or Spin.LeftShoulder or Spin.RightElbow or Spin.LeftElbow or Spin.LeftKnee or Spin.RightKnee
                     || p.Name is "Body (hips)" or "Waist" or "Right hand + racket" or "Left hand" or "Left foot" or "Right foot";
        if (p.Name == "Ball")
        {
            Status("the ball has no joint to turn (T moves it)");
            return true;
        }
        if (!joint)
            return false;
        rotateFrom = new Dictionary<string, float>(pose);
        return true;
    }

    // Turns the picked joint by these angles from where the rotation began (degrees): `across`
    // about the view's up (the near side follows the mouse sideways), `down` about the view's
    // side (the near side goes down). The trunk turns and bends instead; a knee or foot turns.
    void RotateBy(float across, float down)
    {
        if (rotateFrom == null)
            return;
        pose = new Dictionary<string, float>(rotateFrom);
        Apply();
        Part p = Parts[part];
        float From(string key) => rotateFrom[key];
        switch (p.Name)
        {
            case "Body (hips)":
                Carrying(() => { TurnBy("hips.turn", across); Hold("hips.lean", From("hips.lean") + down); });
                return;
            case "Waist":
                Carrying(() => { Hold("waist.turn", From("waist.turn") + across); Hold("waist.bend", From("waist.bend") + down); });
                return;
            case "Left foot":
            case "Right foot":
            {
                string turn = p.Name == "Left foot" ? "lfoot.turn" : "rfoot.turn";
                TurnBy(turn, across);
                SyncFields();
                Apply();
                return;
            }
        }
        if (p.Spin is Spin.LeftKnee or Spin.RightKnee)
        {
            string swivel = p.Spin == Spin.LeftKnee ? "lknee.swivel" : "rknee.swivel";
            Hold(swivel, From(swivel) + across);
            SyncFields();
            Apply();
            return;
        }

        Basis view = camera.GlobalBasis;
        var turn3 = new Basis(view.Y.Normalized(), Mathf.DegToRad(across)) * new Basis(view.X.Normalized(), Mathf.DegToRad(down));
        Transform3D About(Vector3 pivot) => new(turn3, pivot - turn3 * pivot);
        bool right = p.Name == "Right hand + racket" || p.Spin is Spin.RightShoulder or Spin.RightElbow;
        if (right)
        {
            // The racket (with the hand) turns about the wrist, the forearm about the elbow, or
            // the whole arm about the shoulder.
            Vector3 elbow = rightArm.ElbowPoint;
            Vector3 pivot = p.Spin == Spin.RightShoulder ? ShoulderAt(true) : p.Spin == Spin.RightElbow ? elbow : rightArm.WristPoint;
            Transform3D about = About(pivot);
            SetRacket(about * paddle.GlobalTransform);
            KeepElbow(true, p.Spin == Spin.RightShoulder ? about * elbow : elbow);
        }
        else
        {
            // The left hand turns its palm about the wrist, the forearm about the elbow, or the
            // whole arm about the shoulder; a ball held in the hand turns with it.
            Vector3 hand = W(LeftHandAt), ballAt = W(BallAt), elbow = leftArm.ElbowPoint;
            bool held = V("ball.show") > 0.5f && ballAt.DistanceTo(hand) < BallInHand;
            Vector3 pivot = p.Spin == Spin.LeftShoulder ? ShoulderAt(false) : p.Spin == Spin.LeftElbow ? elbow : hand;
            Transform3D about = About(pivot);
            MoveLeftHand(about * hand, turn3 * W(Palm), held ? about * ballAt : null);
            KeepElbow(false, p.Spin == Spin.LeftShoulder ? about * elbow : elbow);
        }
        SyncFields();
        Apply();
    }

    // Whole-body mode, by keys: the player moves across the floor (feet too) or the hips go up
    // and down, the arms and racket coming along.
    void MoveWhole(float across, float forward, float up)
    {
        PushUndo(false);
        Carrying(() =>
        {
            Hold("body.x", V("body.x") + across);
            Hold("body.z", V("body.z") + forward);
            Hold("hips.height", V("hips.height") + up);
        }, feetToo: true);
    }

    // The shoulders work as one girdle: one shrugged up pulls the other down, one rolled forward
    // sends the other back. Near rest the other gives a little (a fifth of the move); the harder
    // the one is pushed towards its limit, the more the other gives (up to four fifths at the
    // limit; four tenths of a full move all told). The other's share is the same coming back, so
    // a shoulder returned to rest puts the other back where it was.
    void MoveShoulder(string key, float value)
    {
        float from = V(key);
        Hold(key, value);
        string other = (key.StartsWith("rshoulder.") ? "lshoulder" : "rshoulder") + key[key.IndexOf('.')..];
        Hold(other, V(other) - (Girdle(key, V(key)) - Girdle(key, from)));
    }

    // How far the other shoulder has given for this one at v (metres): the share 0.2 + 0.6 e^2
    // (e: how far towards its limit, 0..1) summed along the way.
    static float Girdle(string key, float v)
    {
        Channel c = ChannelOf[key];
        float limit = v >= 0f ? c.Max : -c.Min;
        if (limit <= 0f)
            return 0f;
        float e = Mathf.Clamp(Mathf.Abs(v) / limit, 0f, 1f);
        return Mathf.Sign(v) * limit * (0.2f * e + 0.2f * e * e * e);
    }

    // The left hand moved: a ball held in it goes with it.
    void MovingLeftHand(System.Action change)
    {
        Vector3 hand0 = LeftHandAt;
        bool held = V("ball.show") > 0.5f && BallAt.DistanceTo(hand0) < BallInHand;
        change();
        if (held)
        {
            Vector3 moved = LeftHandAt - hand0;
            Hold("ball.x", V("ball.x") + moved.X);
            Hold("ball.y", V("ball.y") + moved.Y);
            Hold("ball.z", V("ball.z") + moved.Z);
        }
        SyncFields();
        Apply();
    }

    void SyncFields()
    {
        syncing = true;
        foreach (var (key, (slider, box)) in fields)
        {
            slider.SetValueNoSignal(V(key));
            box.SetValueNoSignal(V(key));
        }
        syncing = false;
    }

    void ResetPart()
    {
        PushUndo(true);
        Dictionary<string, float> from = gamePose ?? DefaultPose();
        foreach (Channel c in Parts[part].Channels)
            pose[c.Key] = from.TryGetValue(c.Key, out float v) ? v : c.Default;
        SyncFields();
        Apply();
    }

    void ResetAll()
    {
        PushUndo(true);
        pose = new Dictionary<string, float>(gamePose ?? DefaultPose());
        SyncFields();
        Apply();
    }

    // A snapshot before a change; changes close together (one drag) share one.
    void PushUndo(bool always)
    {
        ulong now = Time.GetTicksMsec();
        if (always || now - lastUndoAt > 600)
            undo.Push(new Dictionary<string, float>(pose));
        lastUndoAt = now;
    }

    void Undo()
    {
        if (undo.Count == 0)
            return;
        pose = undo.Pop();
        SyncFields();
        Apply();
        Status("undone");
    }

    void Status(string text) => status.Text = text;

    // ---------------------------------------------------------------- steps

    void RefreshSteps()
    {
        stepList.Clear();
        for (int i = 0; i < sequence.Steps.Count; i++)
        {
            PoseSequence.Step s = sequence.Steps[i];
            stepList.AddItem((i == 0
                ? $"{i + 1}. {s.Name}   (start, at 0.00 s)"
                : $"{i + 1}. {s.Name}   (+{s.Duration:F2} s, at {sequence.StartOf(i):F2} s)")
                + (sequence.HasFinish && i == sequence.ContactIndex && sequence.ContactSpin != PoseSequence.SpinLevel.AsShaped ? $"   [{sequence.ContactSpin.ToString().ToLower()} spin]" : "")
                + (sequence.HasFinish && i == sequence.ContactIndex + 1 && sequence.Finish != PoseSequence.FinishStyle.Normal ? $"   [{sequence.Finish.ToString().ToLower()} finish]" : "")
                + (i > 0 && s.ShortTurn ? "   [short turn]" : ""));
            if (i == sequence.ContactIndex)
                stepList.SetItemCustomFgColor(i, new Color(1f, 0.65f, 0.25f)); // the contact stands out
        }
        if (finishPicker != null)
        {
            finishPicker.Selected = (int)sequence.Finish;
            finishPicker.Disabled = !sequence.HasFinish;
            finishPicker.TooltipText = sequence.HasFinish ? "" : "needs a step named \"contact...\" with a step before it and one after it (the finish)";
        }
        if (step >= 0 && step < sequence.Steps.Count)
            stepList.Select(step);
        UpdateContact();
        SyncShortTurn();
        SyncLock();
        syncing = true;
        timeline.MaxValue = Mathf.Max(sequence.Length, 0.001f);
        syncing = false;
        timeLabel.Text = $"{sequence.Steps.Count} steps, {sequence.Length:F2} s of motion";
        stepName.Text = $"step {sequence.Steps.Count + 1}";
    }

    void AddStep()
    {
        sequence.Steps.Add(new PoseSequence.Step { Name = StepNameOr($"step {sequence.Steps.Count + 1}"), Duration = (float)stepTime.Value, Values = new Dictionary<string, float>(pose) });
        step = sequence.Steps.Count - 1;
        RefreshSteps();
        Status($"step {step + 1} saved: \"{sequence.Steps[step].Name}\". Change the pose for the next step and add it too.");
    }

    void InsertBetween()
    {
        if (step < 0)
        {
            AddStep();
            return;
        }
        sequence.Steps.Insert(step + 1, new PoseSequence.Step { Name = StepNameOr("in between"), Duration = (float)stepTime.Value, Values = new Dictionary<string, float>(pose) });
        step++;
        RefreshSteps();
        Status($"step inserted as number {step + 1}");
    }

    // The picked step copied (its saved pose, move time and name + " copy") right after it, and
    // the copy picked, to change from there.
    void DuplicateStep()
    {
        if (step < 0 || step >= sequence.Steps.Count)
        {
            Status("pick a step in the list first");
            return;
        }
        PoseSequence.Step from = sequence.Steps[step];
        sequence.Steps.Insert(step + 1, new PoseSequence.Step
        {
            Name = from.Name + " copy",
            Duration = from.Duration > 0f ? from.Duration : 0.3f, // a copy of the start needs a move time
            Values = new Dictionary<string, float>(from.Values),
        });
        RefreshSteps();
        PickStep(step + 1);
        stepList.Select(step);
        Status($"step {step} duplicated as step {step + 1}: \"{sequence.Steps[step].Name}\"");
    }

    string StepNameOr(string fallback) => string.IsNullOrWhiteSpace(stepName.Text) ? fallback : stepName.Text.Trim();

    void OverwriteStep()
    {
        if (step < 0 || step >= sequence.Steps.Count)
        {
            Status("pick a step in the list first");
            return;
        }
        PoseSequence.Step s = sequence.Steps[step];
        s.Values = new Dictionary<string, float>(pose);
        s.Duration = (float)stepTime.Value;
        // The name box after a refresh offers the next new step's name ("step N"): that is not a
        // new name for this one.
        if (!string.IsNullOrWhiteSpace(stepName.Text) && stepName.Text.Trim() != $"step {sequence.Steps.Count + 1}")
            s.Name = stepName.Text.Trim();
        RefreshSteps();
        Status($"step {step + 1} overwritten");
    }

    void DeleteStep()
    {
        if (step < 0 || step >= sequence.Steps.Count)
            return;
        sequence.Steps.RemoveAt(step);
        step = Mathf.Min(step, sequence.Steps.Count - 1);
        RefreshSteps();
    }

    void MoveStep(int by)
    {
        int to = step + by;
        if (step < 0 || to < 0 || to >= sequence.Steps.Count)
            return;
        (sequence.Steps[step], sequence.Steps[to]) = (sequence.Steps[to], sequence.Steps[step]);
        step = to;
        RefreshSteps();
    }

    void PickStep(int index)
    {
        step = index;
        PoseSequence.Step s = sequence.Steps[index];
        PushUndo(true);
        pose = new Dictionary<string, float>(s.Values);
        stepName.Text = s.Name;
        stepTime.Value = s.Duration;
        scrubbed = false;
        SyncShortTurn();
        SyncLock();
        SyncFields();
        Apply();
        syncing = true;
        timeline.Value = sequence.StartOf(index);
        syncing = false;
    }

    // ---------------------------------------------------------------- play

    void TogglePlay()
    {
        if (sequence.Steps.Count < 2)
        {
            Status("add at least 2 steps to play");
            return;
        }
        playing = !playing;
        if (playing && playTime >= sequence.Length)
            playTime = 0f;
        playButton.Text = playing ? "■ Stop" : "▶ Play";
    }

    void Scrub(float time)
    {
        if (sequence.Steps.Count == 0)
            return;
        playTime = time;
        scrubbed = true;
        pose = sequence.Sample(time, ease);
        SyncFields();
        Apply();
    }

    // ---------------------------------------------------------------- power: racket into ball

    SpinBox weightBox, staminaBox, racketSpeedBox, ballSpeedBox, angleBox, spinBox;
    CheckBox brushDownBox;
    Label powerText;

    // One contact, worked out with the game's own hit (Ball.Analyse / HitByRacket), seen from the
    // side: the ball comes straight at the player (own frame, along -Z) with its spin (+ topspin),
    // the face stands upright facing it, and the racket moves at its speed at `angle` degrees to
    // the face (90: straight through the ball's centre, as a smash; less: brushing along the
    // face, up for topspin or down for backspin).
    Ball.RacketContact StrokeContact(float angle, float spinIn, float weight, bool down)
    {
        float coming = (float)ballSpeedBox.Value, speed = (float)racketSpeedBox.Value;
        // A ball going -Z with topspin turns its top towards -Z: spin about -X.
        var ball = new BallState(new Vector3(0f, 1f, -1.5f), new Vector3(0f, 0f, -coming), new Vector3(-spinIn * Mathf.Tau, 0f, 0f));
        float a = Mathf.DegToRad(angle);
        var racket = speed * new Vector3(0f, (down ? -1f : 1f) * Mathf.Cos(a), Mathf.Sin(a));
        return Ball.Analyse(ball, Vector3.Back, racket, Ball.RubberGrip, weight);
    }

    // The ball after (own frame, going +Z): speed, spin in rev/s (+ topspin) and its climb.
    static (float Speed, float Spin, float Climb) Leaving(BallState after) =>
        (after.Velocity.Length(), after.Spin.X / Mathf.Tau, Mathf.RadToDeg(Mathf.Atan2(after.Velocity.Y, after.Velocity.Z)));

    void UpdatePower()
    {
        if (powerText == null)
            return;
        UpdateContact(); // the ball coming and the weight count in the contact card too
        float weight = (float)weightBox.Value, racketSpeed = (float)racketSpeedBox.Value, coming = (float)ballSpeedBox.Value;
        float angle = (float)angleBox.Value, spinIn = (float)spinBox.Value;
        bool down = brushDownBox.ButtonPressed;
        float m = Human.BallMass, M = Human.EffectiveRacketMass(weight);
        Ball.RacketContact c = StrokeContact(angle, spinIn, weight, down);
        var (speed, spin, climb) = Leaving(c.After);
        float push = m * (c.After.Velocity.Z + coming); // along the face's normal (N s)
        string Spin(float s) => Mathf.Abs(s) < 0.5f ? "no spin" : $"{Mathf.Abs(s):F0} rev/s {(s > 0f ? "topspin" : "backspin")}";
        string change = speed > coming + 0.05f ? "faster than it came" : speed < coming - 0.05f ? "slower than it came" : "as fast as it came";

        var text = new System.Text.StringBuilder();
        text.AppendLine($"CONTACT ({Ball.ContactTime * 1000f:F1} ms on the rubber, measured 1.3-1.8 at any speed): path {angle:F0}° to the face, "
            + $"closing {c.Approach:F1} m/s, the ball's contact point slipping {c.Slip:F1} m/s across the rubber");
        text.AppendLine(c.Slip < 0.05f
            ? "nothing slips (the racket drives straight through the centre of a ball without spin): friction has no work, no spin is made"
            : c.Grips
            ? $"friction stops the slip after {c.GripsAt * 1000f:F2} ms (sliding), then the ball rolls on the rubber for {(Ball.ContactTime - c.GripsAt) * 1000f:F2} ms and the rubber springs back: the spin is made in the first {c.GripsAt * 1000f:F2} ms"
            : $"it slides the whole {Ball.ContactTime * 1000f:F1} ms: friction can give {c.FrictionAvailable:F1} m/s of the {c.FrictionNeeded:F1} needed to grip, so it spins less than a grip would");
        text.AppendLine($"push {push * 1000f:F0} mN·s: on average {push / Ball.ContactTime:F0} N, peak {push / Ball.ContactTime * Mathf.Pi / 2f:F0} N");
        text.AppendLine($"the ball leaves at {speed:F1} m/s ({speed * 3.6f:F0} km/h, {change}), {Spin(spin)}, climbing {climb:F0}°");
        if (c.HeldToRecord)
            text.AppendLine($"held to the fastest ball ever hit ({Human.FastestBall:F0} m/s, {Human.FastestBall * 3.6f:F0} km/h): an impact this hard presses the sponge down to the blade, "
                + $"and the rubber gives back less (restitution {Ball.RubberRestitution:F2} -> {c.Restitution:F2})");
        if (Mathf.Abs(spinIn) > 0.5f)
        {
            var (s0, sp0, cl0) = Leaving(StrokeContact(angle, 0f, weight, down).After);
            text.AppendLine($"the {Spin(spinIn)} it brought: {speed - s0:+0.0;-0.0} m/s, {spin - sp0:+0;-0} rev/s, {climb - cl0:+0;-0}° of climb against the same stroke at a ball without spin "
                + "(its spinning surface rubs the rubber: the ball is thrown off along the rubber, and the spin turns)");
        }
        text.AppendLine("the same swing at other angles (speed, spin, climb, grip):");
        foreach (float a in new[] { 90f, 75f, 60f, 45f, 30f, 15f })
        {
            Ball.RacketContact ca = StrokeContact(a, spinIn, weight, down);
            var (sa, spa, cla) = Leaving(ca.After);
            text.AppendLine($"   {a,2:F0}°  {sa,4:F1} m/s{(ca.HeldToRecord ? " (record)" : "")}  {Spin(spa),-20}  {cla,3:F0}°  {(ca.Grips ? $"grips at {ca.GripsAt * 1000f:F2} ms" : "slides throughout")}");
        }
        text.AppendLine("straight through the centre the push is all along the face's normal: most speed, little spin; the flatter the path, the more goes into the brush (spin), the less into speed");
        float Flat(float kg) => Leaving(StrokeContact(90f, 0f, kg, down).After).Speed;
        text.Append($"weight: the ball meets the racket {Human.RacketMass * 1000f:F0} g + hand {Human.HandShare * weight * 1000f:F0} g = {M * 1000f:F0} g "
            + $"({Human.ImpactShare(weight) * 100f:F2}% of an endlessly heavy racket's push); a flat hit at 50 kg {Flat(50f):F2} m/s, at 100 kg {Flat(100f):F2} m/s");
        powerText.Text = text.ToString().Replace("\r\n", "\n"); // AppendLine writes \r\n on Windows, which the label shows as an extra line
    }

    // A finish style for the swing: the moves into the contact and the finish are retimed for it
    // at once, the racket speed at the ball goes into the power panel, and the swing plays.
    void PickFinish(PoseSequence.FinishStyle style)
    {
        if (!sequence.HasFinish)
        {
            finishPicker.Selected = 0;
            Status("the finish style needs a step named \"contact...\" with a step before it and one after it (the finish)");
            return;
        }
        sequence.Finish = style;
        RefreshSteps();
        PoseSequence.Swing swing = sequence.SwingInfo();
        if (swing == null)
        {
            Status("the racket does not move into the contact step: nothing to time");
            return;
        }
        racketSpeedBox.Value = Mathf.Min(swing.ContactSpeed, (float)racketSpeedBox.MaxValue);
        Status(FinishSummary(swing));
        playTime = sequence.StartOf(swing.Contact - 1);
        playing = true;
        playButton.Text = "■ Stop";
    }

    static string FinishSummary(PoseSequence.Swing s)
    {
        float normal = 2f * s.InDistance / s.InTime;
        return s.Style switch
        {
            PoseSequence.FinishStyle.Snap =>
                $"snap: held back, then let go late: {s.ContactSpeed:F1} m/s at the ball ({s.ContactSpeed / normal:F1}x normal); "
                + $"the forearm brakes at {PoseSequence.SnapBrake:F0} m/s² once the ball has left ({Ball.ContactTime * 1000f:F1} ms): "
                + $"the racket runs on {s.StopDistance * 100f:F0} cm by inertia in {s.StopTime * 1000f:F0} ms and stops "
                + $"({(s.FinishDistance - s.StopDistance) * 100f:F0} cm short of the finish pose)",
            PoseSequence.FinishStyle.Soft =>
                $"soft: a gentle start, at most {s.PeakSpeed:F1} m/s, braked to {s.ContactSpeed:F1} m/s at the ball ({s.ContactSpeed / normal:F1}x normal); "
                + $"then it glides {s.StopDistance * 100f:F0} cm to rest in {s.StopTime:F2} s",
            _ =>
                $"normal: speeds up steadily over {s.InDistance * 100f:F0} cm in {s.InTime:F2} s to {s.ContactSpeed:F1} m/s at the ball, "
                + $"then follows through {s.FinishDistance * 100f:F0} cm to the finish pose in {s.StopTime:F2} s",
        };
    }

    // The racket's speed (its centre) at the motion's contact step, from the steps' places, move
    // time and finish style (or, without a finish, its average over the move into the picked step).
    void RacketSpeedFromMotion()
    {
        if (sequence.SwingInfo() is PoseSequence.Swing swing)
        {
            racketSpeedBox.Value = Mathf.Min(swing.ContactSpeed, (float)racketSpeedBox.MaxValue);
            Status(FinishSummary(swing) + $"   (measured players: {Human.DriveRacketSpeed(0f):F0}-{Human.DriveRacketSpeed(1f):F1} m/s at contact)");
            return;
        }
        int contact = sequence.Steps.FindIndex(s => s.Name.StartsWith("contact"));
        if (contact < 0)
            contact = step;
        if (contact < 1 || contact >= sequence.Steps.Count)
        {
            Status("needs a contact step (or a picked step) with a step before it");
            return;
        }
        static Vector3 At(Dictionary<string, float> v) =>
            new(v.GetValueOrDefault("racket.x"), v.GetValueOrDefault("racket.y"), v.GetValueOrDefault("racket.z"));
        PoseSequence.Step to = sequence.Steps[contact];
        float metres = At(sequence.Steps[contact - 1].Values).DistanceTo(At(to.Values));
        float speed = metres / Mathf.Max(to.Duration, 1e-3f);
        racketSpeedBox.Value = Mathf.Min(speed, (float)racketSpeedBox.MaxValue);
        Status($"racket moves {metres * 100f:F0} cm in {to.Duration:F2} s into '{to.Name}': {speed:F1} m/s on average "
               + $"(measured players: {Human.DriveRacketSpeed(0f):F0}-{Human.DriveRacketSpeed(1f):F1} m/s at contact)");
    }

    // ---------------------------------------------------------------- shortcut keycaps

    // A key drawn as a raised 3D keycap beside the panel: it lights up while the key is held
    // (or the cap clicked), and stays lit in green while what it switches on is on (the mode in
    // use, playing). Clicking a cap does what the key does.
    record Keycap(Button Cap, Key Key, bool Ctrl, System.Func<bool> On);
    readonly List<Keycap> keycaps = new();
    readonly Dictionary<Button, ulong> flashUntil = new();
    readonly Dictionary<Button, int> capLook = new(); // 0 up, 1 on, 2 held: restyled on change only
    PanelContainer keyBoard;

    static StyleBoxFlat CapStyle(Color face, Color edge, bool down) => new()
    {
        BgColor = face,
        BorderColor = edge,
        BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1,
        BorderWidthBottom = down ? 1 : 5,          // a raised cap shows its side; pressed, it sinks
        CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
        ContentMarginLeft = 7, ContentMarginRight = 7,
        ContentMarginTop = down ? 7 : 3, ContentMarginBottom = down ? 3 : 3,
        ShadowColor = new Color(0f, 0f, 0f, down ? 0.15f : 0.45f), ShadowSize = down ? 1 : 3, ShadowOffset = new Vector2(0f, down ? 1f : 2f),
    };
    static readonly StyleBoxFlat CapUp = CapStyle(new Color(0.24f, 0.26f, 0.31f), new Color(0.09f, 0.1f, 0.12f), false);
    static readonly StyleBoxFlat CapOn = CapStyle(new Color(0.22f, 0.55f, 0.32f), new Color(0.08f, 0.25f, 0.13f), false);
    static readonly StyleBoxFlat CapHeld = CapStyle(new Color(1f, 0.82f, 0.25f), new Color(0.55f, 0.4f, 0.05f), true);

    void BuildKeycaps()
    {
        keyBoard = new PanelContainer
        {
            AnchorLeft = 0f, AnchorRight = 0f, AnchorTop = 1f, AnchorBottom = 1f,
            OffsetLeft = 10f, OffsetBottom = -10f, OffsetTop = -10f,
            GrowVertical = Control.GrowDirection.Begin,
        };
        keyBoard.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.11f, 0.82f),
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 6, ContentMarginBottom = 8,
        });
        layer.AddChild(keyBoard);
        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 6);
        keyBoard.AddChild(rows);
        var title = Heading("SHORTCUTS");
        title.AddThemeFontSizeOverride("font_size", 13);
        rows.AddChild(title);

        HBoxContainer Row(string name)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 5);
            var label = Small(name);
            label.CustomMinimumSize = new Vector2(52f, 0f);
            label.SizeFlagsHorizontal = Control.SizeFlags.Fill;
            label.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
            label.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.3f));
            row.AddChild(label);
            rows.AddChild(row);
            return row;
        }
        void Cap(HBoxContainer row, string text, string caption, Key key, bool ctrl = false, System.Func<bool> on = null, System.Action click = null)
        {
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 1);
            var cap = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(34f, 32f) };
            cap.AddThemeFontSizeOverride("font_size", 13);
            foreach (string look in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled" })
                cap.AddThemeStyleboxOverride(look, CapUp);
            cap.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            cap.ButtonDown += () =>
            {
                flashUntil[cap] = Time.GetTicksMsec() + 200;
                if (click != null)
                    click();
                else if (key != Key.Shift)
                    KeyPressed(new InputEventKey { Keycode = key, Pressed = true, CtrlPressed = ctrl });
            };
            box.AddChild(cap);
            var under = new Label { Text = caption, HorizontalAlignment = HorizontalAlignment.Center };
            under.AddThemeFontSizeOverride("font_size", 10);
            under.AddThemeColorOverride("font_color", new Color(0.75f, 0.78f, 0.85f));
            box.AddChild(under);
            row.AddChild(box);
            keycaps.Add(new Keycap(cap, key, ctrl, on));
        }

        HBoxContainer modes = Row("MODE");
        foreach (var (m, k, label, _) in Modes)
        {
            Mode chosen = m;
            Cap(modes, k.ToString(), label[2..], k, on: () => mode == chosen);
        }

        HBoxContainer move = Row("MOVE");
        Cap(move, "←", "left", Key.Left);
        Cap(move, "→", "right", Key.Right);
        Cap(move, "↑", "to table", Key.Up);
        Cap(move, "↓", "back", Key.Down);
        Cap(move, "PgUp", "up", Key.Pageup);
        Cap(move, "PgDn", "down", Key.Pagedown);
        Cap(move, "Shift", "×5", Key.Shift);
        Cap(move, ",", "turn L (R)", Key.Comma);
        Cap(move, ".", "turn R (R)", Key.Period);

        HBoxContainer body = Row("BODY");
        Cap(body, "−", "gravity down", Key.Minus);
        Cap(body, "+", "gravity up", Key.Equal);
        Cap(body, "J", "body left", Key.J);
        Cap(body, "L", "body right", Key.L);
        Cap(body, "I", "to table", Key.I);
        Cap(body, "K", "body back", Key.K);
        var stepNote = Small("Shift+J/L/I/K: step, feet too");
        stepNote.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        body.AddChild(stepNote);

        HBoxContainer other = Row("OTHER");
        Cap(other, "Space", "play", Key.Space, on: () => playing);
        Cap(other, "Ctrl+Z", "undo", Key.Z, ctrl: true);
        Cap(other, "Ctrl+S", "save", Key.S, ctrl: true);
        Cap(other, "Ctrl+D", "duplicate step", Key.D, ctrl: true);
        Cap(other, "1", "front", Key.Key1);
        Cap(other, "2", "side", Key.Key2);
        Cap(other, "3", "behind", Key.Key3);
        Cap(other, "4", "above", Key.Key4);
        if (InGame)
            Cap(other, ToggleKey.ToString(), "to game", ToggleKey, click: Close);
    }

    // Each cap: held (yellow, sunk) while its key is down or just after a click; on (green)
    // while its mode or play is on; up otherwise.
    void UpdateKeycaps()
    {
        ulong now = Time.GetTicksMsec();
        bool ctrlDown = Input.IsKeyPressed(Key.Ctrl);
        foreach (Keycap k in keycaps)
        {
            bool held = (Input.IsKeyPressed(k.Key) && ctrlDown == k.Ctrl)
                        || (flashUntil.TryGetValue(k.Cap, out ulong until) && now < until);
            int look = held ? 2 : k.On != null && k.On() ? 1 : 0;
            if (capLook.TryGetValue(k.Cap, out int was) && was == look)
                continue;
            capLook[k.Cap] = look;
            StyleBoxFlat style = look == 2 ? CapHeld : look == 1 ? CapOn : CapUp;
            foreach (string name in new[] { "normal", "hover", "pressed", "hover_pressed" })
                k.Cap.AddThemeStyleboxOverride(name, style);
            Color text = look == 2 ? new Color(0.15f, 0.1f, 0f) : Colors.White;
            foreach (string name in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color" })
                k.Cap.AddThemeColorOverride(name, text);
        }
    }

    // The frame rate shown (fpsLabel): frames a second, and the lab's own work per frame (its
    // _Process, the posing and drawing it does), averaged over half a second.
    Label fpsLabel;
    double fpsWindow, workMs;
    int workFrames;
    readonly System.Diagnostics.Stopwatch frameWork = new();

    void ShowFps(double delta)
    {
        workMs += frameWork.Elapsed.TotalMilliseconds;
        workFrames++;
        if ((fpsWindow += delta / Mathf.Max(Engine.TimeScale, 1e-3)) < 0.5 || fpsLabel == null)
            return;
        double fps = Engine.GetFramesPerSecond(), work = workMs / workFrames;
        fpsLabel.Text = $"{fps:F0} fps   ·   Pose Lab work {work:F1} ms a frame";
        fpsLabel.AddThemeColorOverride("font_color", fps >= 50 ? new Color(0.6f, 0.95f, 0.6f) : fps >= 30 ? new Color(1f, 0.85f, 0.4f) : new Color(1f, 0.45f, 0.4f));
        fpsWindow = workMs = 0;
        workFrames = 0;
    }

    public override void _Process(double delta)
    {
        ShowFps(delta);
        frameWork.Restart();
        CheckVideoRun();
        UpdateFocus();
        if (holdReader != null && holdStage >= 0)
            StepNaturalHolds();
        if (InGame && !open)
            return;
        UpdateKeycaps();
        if (body != null && bones.Visible)
            DrawBones(); // the strips keep facing the camera as the view turns

        // Real time, whatever the match's slow motion.
        float dt = (float)(delta / Mathf.Max(Engine.TimeScale, 1e-3));
        if (playing)
        {
            playTime += dt * playSpeed;
            if (playTime > sequence.Length)
            {
                if (loop)
                    playTime = 0f;
                else
                {
                    playTime = sequence.Length;
                    TogglePlay();
                }
            }
            pose = sequence.Sample(playTime, ease);
            SyncFields();
            Apply();
            syncing = true;
            timeline.Value = playTime;
            syncing = false;
        }
        int at = 0;
        for (int i = 1; i < sequence.Steps.Count; i++)
        {
            if (playTime >= sequence.StartOf(i) - 1e-4f)
                at = i;
        }
        if (sequence.Steps.Count > 0)
            timeLabel.Text = $"{playTime:F2} s of {sequence.Length:F2} s   (step {at + 1}: {sequence.Steps[at].Name})   racket {sequence.RacketSpeed(playTime, ease):F1} m/s";

        Vector3 offset = new Basis(Vector3.Up, viewYaw) * new Basis(Vector3.Right, -viewPitch) * new Vector3(0f, 0f, viewDistance);
        camera.GlobalTransform = new Transform3D(Basis.Identity, viewTarget + offset).LookingAt(viewTarget, Vector3.Up);
        frameWork.Stop();
    }

    // ---------------------------------------------------------------- save / load

    void Save()
    {
        string name = string.Concat(sequenceName.Text.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_'));
        if (name.Length == 0)
        {
            Status("give the motion a name first");
            return;
        }
        sequence.Name = name;
        sequence.Grip = grip.ToString();
        sequence.Save();
        sequenceName.Text = name;
        RefreshSaved();
        Status($"saved {sequence.Steps.Count} steps to res://Poses/{name}.json");
    }


    void Load()
    {
        if (loadPicker.ItemCount == 0)
            return;
        string name = loadPicker.GetItemText(loadPicker.Selected);
        PoseSequence loaded = PoseSequence.Load(name);
        if (loaded == null)
        {
            Status($"could not read {name}");
            return;
        }
        sequence = loaded;
        sequenceName.Text = name;
        if (!InGame)
        {
            grip = loaded.Grip == "Penhold" ? GripStyle.Penhold : GripStyle.Shakehand;
            gripPicker.Selected = grip == GripStyle.Shakehand ? 0 : 1;
            MakePaddle();
            ReadNaturalHolds();
        }
        step = sequence.Steps.Count > 0 ? 0 : -1;
        playTime = 0f;
        RefreshSteps();
        if (step == 0)
            PickStep(0);
        Status($"loaded {name}: {sequence.Steps.Count} steps");
    }

    // ---------------------------------------------------------------- actions

    void ShowActionNotes(int index)
    {
        StrokeActions.Action a = StrokeActions.All[index];
        string file = FileOf(a);
        bool saved = PoseSequence.Saved().Contains(file);
        string role = StrokeActions.MethodOf(a) is not string method ? "Footwork: a first action, ticked for a method's first part in the method table."
            : StrokeActions.IsDefault(a) ? "The default for this method: the game plays it when nothing is ticked for the method in the method table."
            : $"A variant of {MotionMethods.Find(method)?.Name}: tick it in the method table to have the game play it.";
        actionNotes.Text = $"{a.Name} - {a.Vietnamese}\n{role}\n{a.Notes}\n{(saved ? $"Saved: res://Poses/{file}.json{(a.Id == StrokeActions.ReadyId ? " (in use in the game)" : "")}" : "Not made yet: Open gives a starting motion to shape, then Save.")}";
    }

    // The file an action is saved in: its id, or for the ready stance the one in use (ready_<name>).
    static string FileOf(StrokeActions.Action a) => a.Id == StrokeActions.ReadyId ? ReadyStance.Name ?? a.Id : a.Id;

    // Opens an action's motion: the one saved for it, or (if none, or asked) its starting motion,
    // each step's waist and hand turned so the wrist sits easy. Save then keeps it as the action's.
    void OpenAction(int index, bool fromTemplate)
    {
        if (index < 0)
            return;
        StrokeActions.Action a = StrokeActions.All[index];
        string file = FileOf(a);
        PoseSequence saved = fromTemplate ? null : PoseSequence.Load(file);
        playing = false;
        playButton.Text = "▶ Play";
        if (saved != null)
            sequence = saved;
        else
        {
            sequence = StrokeActions.Build(a, DefaultPose(), StartName == "" ? null : StartPose());
            // A step 1 taken from a saved pose (the ready stance, or the precondition) stays
            // exactly as saved, so the motion joins on without a jump.
            bool savedStart = StrokeActions.UsesReady(a) && (StartName != "" || StrokeActions.SavedReady() != null);
            posingOther = true;
            foreach (PoseSequence.Step s in sequence.Steps.Skip(savedStart ? 1 : 0))
            {
                pose = new Dictionary<string, float>(s.Values);
                Apply();
                EaseWrist();
                s.Values = new Dictionary<string, float>(pose);
            }
            posingOther = false;
        }
        sequenceName.Text = saved != null ? file : a.Id;
        step = 0;
        playTime = 0f;
        RefreshSteps();
        PickStep(0);
        ShowActionNotes(index);
        Status(saved != null ? $"{StrokeActions.Label(a)}: your saved motion (\"Add as step 1\" puts {StartLabel} in front)"
            : $"{StrokeActions.Label(a)}: starting motion from {StartLabel}; shape it, then Save (saves as res://Poses/{a.Id}.json)");
    }

    // How far the racket hand's wrist is bent off the grip's natural line (degrees).

    float WristBend() => paddle.WristOffGrip(rightArm.ElbowPoint, Forehand).Bend;


    // How far the wrist is past its range that way (degrees; 0 or less: within it).
    float WristPast()
    {
        var (bend, palmward) = paddle.WristOffGrip(rightArm.ElbowPoint, Forehand);
        return bend - PoseCheck.WristRange(palmward);
    }

    // Turns the chest at the waist and the hand about the vertical (the racket staying where it
    // is) to the pair that bends the wrist least, the turns kept small: the body lines the arm up
    // behind the racket, as a player does.
    void EaseWrist()
    {
        var start = new Dictionary<string, float>(pose);
        Dictionary<string, float> best = start;
        float bestScore = float.MaxValue;
        float waist0 = V("waist.turn"), turn0 = V("racket.turn");
        for (float dw = -40f; dw <= 40f; dw += 5f)
        {
            for (float dt = -40f; dt <= 40f; dt += 5f)
            {
                pose = new Dictionary<string, float>(start);
                Hold("waist.turn", waist0 + dw);
                Hold("racket.turn", turn0 + dt);
                Apply();
                float score = WristBend() + 0.25f * Mathf.Abs(dt) + 0.1f * Mathf.Abs(dw)
                              + (atFullReach.Any(r => r.Contains("arm straight")) ? 30f : 0f);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = new Dictionary<string, float>(pose);
                }
            }
        }
        pose = best;
        SyncFields();
        Apply();
    }

    void NewSequence()
    {
        sequence = new PoseSequence { Name = "my_motion" };
        sequenceName.Text = sequence.Name;
        step = -1;
        playTime = 0f;
        // Step 1 is the precondition (Starts from), where there is one saved.
        if (StartPose() is { } start)
        {
            sequence.Steps.Add(new PoseSequence.Step { Name = "start (precondition)", Duration = 0f, Values = start });
            RefreshSteps();
            PickStep(0);
            Status($"new motion from {StartLabel} (step 1): pose the next step and press \"Add as new step\"");
            return;
        }
        RefreshSteps();
        Status("new motion: pose the first step and press \"Add as new step\"");
    }

    void RefreshSaved()
    {
        loadPicker.Clear();
        foreach (string name in PoseSequence.Saved())
            loadPicker.AddItem(name);
        FillStartPicker(StartName);
    }

    // ---------------------------------------------------------------- precondition

    // What a motion starts from (its step 1, the precondition): the ready stance, or the end (last
    // step) of another saved motion, such as the footwork ticked as the method's first action.
    string StartName => startPicker != null && startPicker.Selected > 0 ? startPicker.GetItemMetadata(startPicker.Selected).AsString() : "";

    void FillStartPicker(string select)
    {
        if (startPicker == null)
            return;
        startPicker.Clear();
        startPicker.AddItem("the ready stance");
        startPicker.SetItemMetadata(0, "");
        foreach (string name in PoseSequence.Saved().Where(n => !ReadyStance.IsReadyName(n)))
        {
            startPicker.AddItem($"the end of {name}");
            startPicker.SetItemMetadata(startPicker.ItemCount - 1, name);
            if (name == select)
                startPicker.Select(startPicker.ItemCount - 1);
        }
        if (string.IsNullOrEmpty(select))
            startPicker.Select(0);
    }

    // For an action: the end of the first action ticked for its method in the method table (the
    // footwork before the stroke), or else the ready stance.
    static string DefaultStart(StrokeActions.Action a) =>
        MotionMethods.RowOf(a) is string row && MotionMethods.FirstInUse(row).FirstOrDefault() is string first ? first : "";

    // The precondition's pose (every channel), or null when there is none saved.
    Dictionary<string, float> StartPose()
    {
        string name = StartName;
        Dictionary<string, float> from = name == "" ? StrokeActions.SavedReady() : StrokeActions.EndOf(name);
        if (from == null)
            return null;
        var p = DefaultPose();
        foreach (var (key, value) in from)
            p[key] = value;
        return p;
    }

    string StartLabel => StartName == "" ? "the ready stance" : $"the end of {StartName}";

    // The precondition put in front of the motion as its step 1 (nothing of it replaced; the step
    // that was first gets a move time).
    void AddStartStep()
    {
        Dictionary<string, float> p = StartPose();
        if (p == null)
        {
            Status($"nothing saved to start from ({StartLabel})");
            return;
        }
        if (sequence.Steps.Count > 0 && sequence.Steps[0].Duration <= 0f)
            sequence.Steps[0].Duration = 0.25f;
        sequence.Steps.Insert(0, new PoseSequence.Step { Name = "start (precondition)", Duration = 0f, Values = p });
        RefreshSteps();
        PickStep(0);
        Status($"step 1 is now {StartLabel}; the motion's own steps follow");
    }

    // ---------------------------------------------------------------- mouse and keys

    public override void _UnhandledInput(InputEvent e)
    {
        if (InGame)
        {
            if (e is InputEventKey { Pressed: true, Echo: false } toggle && toggle.Keycode == ToggleKey)
            {
                if (open)
                    Close();
                else
                    Open();
                GetViewport().SetInputAsHandled();
                return;
            }
            if (!open)
                return;
        }
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                viewDistance = Mathf.Max(viewDistance * 0.9f, 0.4f);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                viewDistance = Mathf.Min(viewDistance * 1.1f, 12f);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } click:
                if (click.Pressed)
                    StartDrag(click.Position);
                else
                    dragging = rotating = turningWhole = false;
                break;
            case InputEventMouseMotion motion:
                bool pan = (motion.ButtonMask & MouseButtonMask.Middle) != 0
                           || ((motion.ButtonMask & MouseButtonMask.Right) != 0 && motion.ShiftPressed);
                if (pan)
                {
                    Basis view = camera.GlobalBasis;
                    viewTarget += (-view.X * motion.Relative.X + view.Y * motion.Relative.Y) * viewDistance * 0.0015f;
                }
                else if ((motion.ButtonMask & MouseButtonMask.Right) != 0)
                {
                    viewYaw -= motion.Relative.X * 0.008f;
                    viewPitch = Mathf.Clamp(viewPitch + motion.Relative.Y * 0.008f, -1.4f, 1.5f);
                }
                else if (dragging && (motion.ButtonMask & MouseButtonMask.Left) != 0)
                    Drag(motion.Position);
                break;
            case InputEventKey { Pressed: true } key:
                KeyPressed(key);
                break;
        }
        if (InGame)
            GetViewport().SetInputAsHandled(); // the frozen match takes no input
    }

    void KeyPressed(InputEventKey key)
    {
        float stepSize = key.ShiftPressed ? 0.05f : 0.01f;
        switch (key.Keycode)
        {
            case Key.Z when key.CtrlPressed: Undo(); return;
            case Key.S when key.CtrlPressed: Save(); return;
            case Key.D when key.CtrlPressed: DuplicateStep(); return;
            case Key.Space: TogglePlay(); return;
            case Key.Key1: View(-40f, 12f); return; // front right
            case Key.Key2: View(-90f, 5f); return;  // its right side
            case Key.Key3: View(180f, 8f); return;  // from behind
            case Key.Key4: View(0f, 80f); return;   // from above
        }
        if (!key.CtrlPressed && !key.AltPressed && !key.Echo)
            foreach (var (m, k, _, _) in Modes)
                if (key.Keycode == k)
                {
                    SetMode(m);
                    return;
                }

        // - / + (or the keypad's): the centre of gravity down / up, in any mode.
        switch (key.Keycode)
        {
            case Key.Minus:
            case Key.KpSubtract:
                RaiseBody(-stepSize);
                return;
            case Key.Equal:
            case Key.KpAdd:
                RaiseBody(stepSize);
                return;
            // J / L / I / K: the body left / right / to the table / back, in any mode.
            case Key.J: ShiftBody(stepSize, 0f, key.ShiftPressed); return; // the player's left is +X
            case Key.L: ShiftBody(-stepSize, 0f, key.ShiftPressed); return;
            case Key.I: ShiftBody(0f, stepSize, key.ShiftPressed); return;
            case Key.K: ShiftBody(0f, -stepSize, key.ShiftPressed); return;
        }

        float shiftOr(float small, float big) => key.ShiftPressed ? big : small;
        switch (mode)
        {
            case Mode.WholeBody:
                switch (key.Keycode)
                {
                    case Key.Left: MoveWhole(stepSize, 0f, 0f); return; // the player's left is +X
                    case Key.Right: MoveWhole(-stepSize, 0f, 0f); return;
                    case Key.Up: MoveWhole(0f, stepSize, 0f); return;
                    case Key.Down: MoveWhole(0f, -stepSize, 0f); return;
                    case Key.Pageup: MoveWhole(0f, 0f, stepSize); return;
                    case Key.Pagedown: MoveWhole(0f, 0f, -stepSize); return;
                    // , and . turn the whole player a degree a press (shift: 5), , to its left.
                    case Key.Comma: PushUndo(false); TurnWhole(shiftOr(1f, 5f)); return;
                    case Key.Period: PushUndo(false); TurnWhole(-shiftOr(1f, 5f)); return;
                }
                return;
            case Mode.Ball:
                if (Parts[part].Name != "Ball")
                    SelectPart(PartIndex("Ball"));
                break;
            case Mode.Rotate:
            {
                // The arrows turn the picked joint a degree a press (shift: 5).
                float by = shiftOr(1f, 5f);
                (float across, float down) turn = key.Keycode switch
                {
                    Key.Left => (-by, 0f),
                    Key.Right => (by, 0f),
                    Key.Up => (0f, -by),
                    Key.Down => (0f, by),
                    _ => (0f, 0f),
                };
                if (turn != (0f, 0f) && Parts[part].Spin is not (Spin.RacketHead or Spin.RacketHandle or Spin.RacketFace or Spin.HipsTurn or Spin.BodyLean or Spin.ChestTurn or Spin.LeftToes or Spin.RightToes))
                {
                    PushUndo(false);
                    if (BeginRotate())
                        RotateBy(turn.across, turn.down);
                    rotateFrom = null;
                    return;
                }
                break;
            }
        }
        // A handle with its own keys: the arrows turn it a degree a press (shift: 5), or move a
        // shoulder a centimetre (shift: 5).
        Part picked = Parts[part];
        if (picked.Spin != Spin.None)
        {
            float By(string channel) => channel == null ? 0f
                : ChannelOf[channel].Step < 0.1f ? stepSize : key.ShiftPressed ? 5f : 1f;
            switch (key.Keycode)
            {
                case Key.Left: Nudge(picked.KeysAcross, By(picked.KeysAcross)); return;
                case Key.Right: Nudge(picked.KeysAcross, -By(picked.KeysAcross)); return;
                case Key.Up: Nudge(picked.KeysUpDown, By(picked.KeysUpDown)); return;
                case Key.Down: Nudge(picked.KeysUpDown, -By(picked.KeysUpDown)); return;
            }
            return;
        }
        switch (key.Keycode)
        {
            case Key.Left: Nudge(Parts[part].X, stepSize); return;   // the player's left is +X
            case Key.Right: Nudge(Parts[part].X, -stepSize); return;
            case Key.Up: Nudge(Parts[part].Z, stepSize); return;
            case Key.Down: Nudge(Parts[part].Z, -stepSize); return;
            case Key.Pageup: Nudge(Parts[part].Y, stepSize); return;
            case Key.Pagedown: Nudge(Parts[part].Y, -stepSize); return;
        }
    }

    // Views round the player, degrees from straight in front of it (from the table), negative
    // round to its right.
    void View(float yaw, float pitch)
    {
        Vector3 facing = toWorld * Vector3.Back;
        viewYaw = Mathf.Atan2(facing.X, facing.Z) + Mathf.DegToRad(yaw);
        viewPitch = Mathf.DegToRad(pitch);
    }

    void Nudge(string key, float by)
    {
        if (key == null)
            return;
        SetValue(key, V(key) + by);
    }

    void StartDrag(Vector2 mouse)
    {
        // Whole body / ball: a drag anywhere moves it, by as much as the mouse moves.
        int turnHandle = PartIndex("Whole body turn");
        if (mode == Mode.WholeBody && camera.UnprojectPosition(MarkerAt(turnHandle)).DistanceTo(mouse) < 18f
            && !camera.IsPositionBehind(MarkerAt(turnHandle)))
        {
            // The turn handle: the player turns as the mouse goes round the ring.
            SelectPart(turnHandle);
            dragPlane = new Plane(Vector3.Up, WholeRingCentre);
            Vector3? onFloor = dragPlane.IntersectsRay(camera.ProjectRayOrigin(mouse), camera.ProjectRayNormal(mouse));
            if (onFloor == null)
                return;
            Vector3 v = O(onFloor.Value - WholeRingCentre);
            wholeTurnFrom = Mathf.Atan2(v.X, v.Z);
            rotateFrom = new Dictionary<string, float>(pose);
            turningWhole = dragging = true;
            PushUndo(true);
            return;
        }
        if (mode is Mode.WholeBody or Mode.Ball)
        {
            bool whole = mode == Mode.WholeBody;
            SelectPart(PartIndex(whole ? "Body (hips)" : "Ball"));
            if (!whole && V("ball.show") < 0.5f)
                pose["ball.show"] = 1f;
            Vector3 from = whole ? upper.GlobalPosition : W(BallAt);
            dragPlane = whole ? new Plane(Vector3.Up, from) : new Plane(-camera.GlobalBasis.Z, from);
            Vector3? grab = dragPlane.IntersectsRay(camera.ProjectRayOrigin(mouse), camera.ProjectRayNormal(mouse));
            if (grab == null)
                return;
            dragOffset = HandleValue(Parts[part]) - O(grab.Value);
            dragging = true;
            PushUndo(true);
            return;
        }

        int nearest = -1;
        float best = 16f; // pixels
        for (int i = 0; i < Parts.Length; i++)
        {
            if (!markers[i].Visible || camera.IsPositionBehind(markers[i].GlobalPosition))
                continue;
            float d = camera.UnprojectPosition(markers[i].GlobalPosition).DistanceTo(mouse);
            if (d < best)
            {
                best = d;
                nearest = i;
            }
        }
        if (nearest < 0)
            return;
        SelectPart(nearest);
        Part p = Parts[nearest];
        Vector3 at = MarkerAt(nearest);
        if (mode == Mode.Rotate && BeginRotate())
        {
            rotating = rotateFrom != null;
            rotateMouse = mouse;
            dragging = rotating;
            if (rotating)
                PushUndo(true);
            return;
        }
        if (p.Spin != Spin.None)
        {
            // A turning handle: the turns round the vertical follow the mouse across the floor,
            // the others in the plane facing the view.
            dragPlane = p.Spin is Spin.HipsTurn or Spin.ChestTurn or Spin.LeftToes or Spin.RightToes ? new Plane(Vector3.Up, at) : new Plane(-camera.GlobalBasis.Z, at);
            Vector3? spinHit = dragPlane.IntersectsRay(camera.ProjectRayOrigin(mouse), camera.ProjectRayNormal(mouse));
            if (spinHit == null)
                return;
            spinOffset = at - spinHit.Value;
            spinStart = paddle.GlobalTransform;
            elbowStart = p.Spin == Spin.LeftElbow ? leftArm.ElbowPoint : rightArm.ElbowPoint;
            handStart = W(LeftHandAt);
            palmStart = W(Palm);
            ballStart = W(BallAt);
            ballStartHeld = V("ball.show") > 0.5f && ballStart.DistanceTo(handStart) < BallInHand;
            dragging = true;
            PushUndo(true);
            return;
        }
        if (p.X == null)
            return; // a joint: its values are in the panel
        // On the floor parts move across it; the others in the plane facing the view.
        dragPlane = p.Y == null ? new Plane(Vector3.Up, at) : new Plane(-camera.GlobalBasis.Z, at);
        Vector3? hit = dragPlane.IntersectsRay(camera.ProjectRayOrigin(mouse), camera.ProjectRayNormal(mouse));
        if (hit == null)
            return;
        dragOffset = HandleValue(p) - O(hit.Value);
        dragging = true;
        PushUndo(true);
    }

    Vector3 HandleValue(Part p) => new(V(p.X), p.Y != null ? V(p.Y) : 0f, V(p.Z));

    void Drag(Vector2 mouse)
    {
        if (rotating)
        {
            Vector2 moved = mouse - rotateMouse;
            RotateBy(moved.X * DegreesPerPixel, moved.Y * DegreesPerPixel);
            return;
        }
        if (turningWhole)
        {
            // From the pose the turn began in, by how far the mouse has gone round the ring.
            Vector3? onFloor = dragPlane.IntersectsRay(camera.ProjectRayOrigin(mouse), camera.ProjectRayNormal(mouse));
            if (onFloor == null || rotateFrom == null)
                return;
            pose = new Dictionary<string, float>(rotateFrom);
            Apply();
            Vector3 v = O(onFloor.Value - WholeRingCentre);
            if (new Vector2(v.X, v.Z).LengthSquared() < 1e-4f)
                return;
            TurnWhole(Mathf.RadToDeg(Mathf.Wrap(Mathf.Atan2(v.X, v.Z) - wholeTurnFrom, -Mathf.Pi, Mathf.Pi)));
            return;
        }
        Vector3? hit = dragPlane.IntersectsRay(camera.ProjectRayOrigin(mouse), camera.ProjectRayNormal(mouse));
        if (hit == null)
            return;
        Part p = Parts[part];
        if (p.Spin != Spin.None)
        {
            SpinTo(p.Spin, hit.Value + spinOffset);
            return;
        }
        if (p.X == null)
            return;
        Vector3 to = O(hit.Value) + dragOffset;
        void Place()
        {
            pose[p.X] = Mathf.Clamp(to.X, ChannelOf[p.X].Min, ChannelOf[p.X].Max);
            if (p.Y != null)
                pose[p.Y] = Mathf.Clamp(to.Y, ChannelOf[p.Y].Min, ChannelOf[p.Y].Max);
            pose[p.Z] = Mathf.Clamp(to.Z, ChannelOf[p.Z].Min, ChannelOf[p.Z].Max);
        }
        // The body carries its arms (and with shift held, or in whole-body mode, its feet too);
        // the left hand its ball. Point only: nothing else moves.
        if (MovesShoulders(p.X))
            Trunk(Place, feetToo: mode == Mode.WholeBody || Input.IsKeyPressed(Key.Shift));
        else if (p.X == "lhand.x" && mode != Mode.PointOnly)
            MovingLeftHand(Place);
        else
        {
            Place();
            SyncFields();
            Apply();
        }
    }

    // ---------------------------------------------------------------- example and capture

    // A forehand pendulum serve, short with backspin, as an example of a motion in steps.
    static PoseSequence ExampleServe()
    {
        Dictionary<string, float> Pose(params (string Key, float Value)[] values)
        {
            var p = DefaultPose();
            foreach (var (key, value) in values)
                p[key] = value;
            return p;
        }
        var stance = new (string, float)[]
        {
            ("body.x", 0.25f), ("body.z", -1.88f), ("hips.height", 0.8f), ("hips.turn", -25f), ("hips.lean", 22f),
            ("lfoot.x", 0.55f), ("lfoot.z", -1.85f), ("lfoot.turn", 10f),
            ("rfoot.x", 0.08f), ("rfoot.z", -2.1f), ("rfoot.turn", -55f),
        };
        Dictionary<string, float> With(params (string, float)[] more) => Pose(stance.Concat(more).ToArray());

        return new PoseSequence
        {
            Name = "example_serve",
            Steps =
            {
                new() { Name = "ball shown on the palm", Duration = 0f, Values = With(
                    ("waist.turn", -15f), ("racket.x", -0.12f), ("racket.y", 1.0f), ("racket.z", -1.62f), ("racket.open", 20f), ("racket.noseForward", 0f),
                    ("relbow.swivel", 0f), ("lhand.x", 0.12f), ("lhand.y", 0.95f), ("lhand.z", -1.42f), ("lhand.palmUp", 90f), ("lhand.open", 1f),
                    ("lelbow.swivel", 15f), ("ball.x", 0.12f), ("ball.y", 0.975f), ("ball.z", -1.44f), ("ball.show", 1f)) },
                new() { Name = "toss: ball up, racket back", Duration = 0.35f, Values = With(
                    ("waist.turn", -35f), ("racket.x", -0.3f), ("racket.y", 1.12f), ("racket.z", -1.82f), ("racket.open", 50f), ("racket.noseForward", -20f),
                    ("relbow.swivel", 10f), ("lhand.x", 0.12f), ("lhand.y", 1.15f), ("lhand.z", -1.42f), ("lhand.palmUp", 90f), ("lhand.open", 1f),
                    ("lelbow.swivel", 10f), ("ball.x", 0.12f), ("ball.y", 1.4f), ("ball.z", -1.44f), ("ball.show", 1f)) },
                new() { Name = "contact: brushing under the ball", Duration = 0.45f, Values = With(
                    ("waist.turn", -5f), ("racket.x", 0.04f), ("racket.y", 0.93f), ("racket.z", -1.5f), ("racket.open", 70f), ("racket.noseForward", 10f), ("racket.turn", 35f),
                    ("relbow.swivel", 0f), ("lhand.x", 0.35f), ("lhand.y", 1.05f), ("lhand.z", -1.55f), ("lhand.palmUp", 20f), ("lhand.open", 0.4f),
                    ("lelbow.swivel", 40f), ("ball.x", 0.12f), ("ball.y", 0.96f), ("ball.z", -1.44f), ("ball.show", 1f)) },
                new() { Name = "finish", Duration = 0.2f, Values = With(
                    ("waist.turn", 5f), ("racket.x", 0.12f), ("racket.y", 0.97f), ("racket.z", -1.42f), ("racket.open", 60f), ("racket.noseForward", 25f), ("racket.turn", 50f),
                    ("relbow.swivel", 0f), ("lhand.x", 0.4f), ("lhand.y", 1.05f), ("lhand.z", -1.6f), ("lhand.palmUp", 0f), ("lhand.open", 0.3f),
                    ("lelbow.swivel", 45f), ("ball.x", 0.15f), ("ball.y", 0.93f), ("ball.z", -1.0f), ("ball.show", 1f)) },
            }
        };
    }


    // PoseLab.tscn with --actions=<folder>: opens every action's starting motion, prints each
    // step's body check, saves a picture of each at its contact (action_<id>.png) and quits.
    async void CaptureActions()
    {
        while (!HoldsKnown)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        for (int i = 0; i < StrokeActions.All.Length; i++)
        {
            StrokeActions.Action a = StrokeActions.All[i];
            actionPicker.Selected = i;
            OpenAction(i, true);
            int contact = 0;
            for (int s = 0; s < sequence.Steps.Count; s++)
            {
                PickStep(s);
                GD.Print($"{a.Id} | {sequence.Steps[s].Name}: {checks.Text.Replace("\n", " | ")}");
                if (sequence.Steps[s].Name.StartsWith("contact"))
                    contact = s;
            }
            PickStep(contact);
            SelectPart(2);
            viewTarget = upper.GlobalPosition + Vector3.Up * 0.25f;
            View(-55f, 10f);
            viewDistance = 2.6f;
            for (int f = 0; f < 5; f++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            GetViewport().GetTexture().GetImage().SavePng($"{actionsFolder}/action_{a.Id}.png");
        }
        GetTree().Quit();
    }
    async void CaptureExample()
    {
        while (!HoldsKnown)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        sequence = ExampleServe();
        sequenceName.Text = sequence.Name;
        sequence.Save();
        sequence = PoseSequence.Load("example_serve"); // what was saved is what plays
        RefreshSaved();
        RefreshSteps();
        View(-60f, 10f);
        viewDistance = 2.6f;
        for (int i = 0; i < sequence.Steps.Count; i++)
        {
            PickStep(i);
            SelectPart(2);
            for (int f = 0; f < 6; f++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            GetViewport().GetTexture().GetImage().SavePng($"{captureFolder}/pose_step{i + 1}.png");
            GD.Print($"step {i + 1} ({sequence.Steps[i].Name}): {checks.Text.Replace('\n', ' ')}");
        }
        // Halfway between the toss and the contact, played from the saved steps.
        Scrub((sequence.StartOf(1) + sequence.StartOf(2)) * 0.5f);
        for (int f = 0; f < 6; f++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetViewport().GetTexture().GetImage().SavePng($"{captureFolder}/pose_between.png");
        GetTree().Quit();
    }

    // ---------------------------------------------------------------- in-game test

    // Main.tscn with --pose-test=<folder>: lets the match run a moment, opens the lab on the far
    // NPC as F2 would, saves a picture, checks the pose read back from the game lays the NPC
    // exactly as it was, nudges the racket, saves another picture, closes and quits.
    public async void InGameTest(string folder)
    {
        for (int f = 0; f < 200; f++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Transform3D racketBefore = Targets[0].Rig.Paddle.GlobalTransform;
        Vector3 elbowBefore = Targets[0].Rig.RightArm.ElbowPoint;
        Open();
        while (gamePose == null)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        for (int f = 0; f < 4; f++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        float racketMoved = paddle.GlobalTransform.Origin.DistanceTo(racketBefore.Origin);
        float racketTurned = Mathf.RadToDeg((racketBefore.Basis.Inverse() * paddle.GlobalBasis).GetRotationQuaternion().GetAngle());
        float elbowMoved = rightArm.ElbowPoint.DistanceTo(elbowBefore);
        GD.Print($"POSETEST read back from the game: racket off by {racketMoved * 100f:F2} cm and {racketTurned:F2}°, elbow off by {elbowMoved * 100f:F2} cm");
        GetViewport().GetTexture().GetImage().SavePng($"{folder}/ingame_open.png");
        SetValue("racket.y", V("racket.y") + 0.15f);
        SetValue("racket.open", V("racket.open") + 40f);
        AddStep();
        for (int f = 0; f < 4; f++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetViewport().GetTexture().GetImage().SavePng($"{folder}/ingame_edited.png");
        Close();
        for (int f = 0; f < 30; f++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GD.Print($"POSETEST closed: paused {GetTree().Paused}");
        GetViewport().GetTexture().GetImage().SavePng($"{folder}/ingame_resumed.png");
        GetTree().Quit();
    }
}
