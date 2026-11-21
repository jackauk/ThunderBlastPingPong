using Godot;

// Grip Lab: every way the NPC holds its racket, one close-up each, showing only the forearm,
// wrist, hand and racket. Each station is a hitter (holder space: x right, y up, -z towards the
// opponent) with the game's own Paddle frozen at one moment of a stroke, and an arm built from
// the racket out: the wrist where the hand holds the handle, the forearm the way the grip sets
// it, the elbow, the upper arm. The label gives the wrist's bend from straight and turns red
// past what a wrist can do (bent back about 70 degrees, forward 80, sideways 30).
//
// Keys: G shakehand / penhold, V view (front, side, behind the hand, above).
// User argument --capture=<folder>: saves grip_<grip>_<view>.png there for both grips, from the
// front and from the side, and quits.
public partial class GripLab : Node3D
{
    record Moment(string Name, StrokeStyle Style, bool Forehand, float Windup, float Follow, Vector2 Face);

    static readonly Moment[] Moments =
    {
        new("Ready", StrokeStyle.Block, true, 0f, 0f, Vector2.Zero),
        new("Forehand drive: backswing", StrokeStyle.Drive, true, 1f, 0f, Vector2.Zero),
        new("Forehand drive: contact", StrokeStyle.Drive, true, 0f, 0f, new Vector2(-0.15f, 0f)),
        new("Forehand drive: finish", StrokeStyle.Drive, true, 0f, 0.3f, new Vector2(-0.15f, 0f)),
        new("Backhand drive: backswing", StrokeStyle.Drive, false, 1f, 0f, Vector2.Zero),
        new("Backhand drive: contact", StrokeStyle.Drive, false, 0f, 0f, new Vector2(-0.15f, 0f)),
        new("Backhand drive: finish", StrokeStyle.Drive, false, 0f, 0.3f, new Vector2(-0.15f, 0f)),
        new("Forehand push", StrokeStyle.Push, true, 0f, 0.12f, new Vector2(0.6f, 0f)),
        new("Backhand push", StrokeStyle.Push, false, 0f, 0.12f, new Vector2(0.6f, 0f)),
        new("Forehand block", StrokeStyle.Block, true, 0f, 0f, new Vector2(-0.3f, 0f)),
        new("Smash: backswing", StrokeStyle.Smash, true, 1f, 0f, Vector2.Zero),
        new("Serve: chop, start", StrokeStyle.Push, true, 1f, 0f, Vector2.Zero),
    };

    static readonly string[] ViewNames = { "front", "side", "behind the hand", "above" };
    // Camera directions from the station, holder space.
    static readonly Vector3[] ViewDirections =
    {
        new(0.45f, 0.35f, -1f), new(1f, 0.25f, -0.15f), new(0.35f, 0.45f, 1f), new(0.2f, 1f, -0.25f)
    };

    const int Columns = 4;
    const int Rows = 3;
    const float Spacing = 12f;
    const float ViewDistance = 1.0f;

    class Station
    {
        public Node3D Holder;
        public Paddle Paddle;
        public Arm Arm;
    }

    readonly Station[,] stations = new Station[2, 12];
    Camera3D[] cameras;
    Label[] labels;
    Label footer;
    int grip;
    int view;
    string captureFolder;
    int frame;

    public override void _Ready()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--capture="))
                captureFolder = arg["--capture=".Length..];
        }

        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.2f, 0.22f, 0.26f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.55f, 0.55f, 0.6f),
                AmbientLightEnergy = 0.8f
            }
        });
        AddChild(new DirectionalLight3D
        {
            Rotation = new Vector3(-0.9f, 0.6f, 0f),
            LightEnergy = 1.2f,
            ShadowEnabled = true
        });

        var skin = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.65f, 0.5f) };
        var sleeve = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.75f, 0.1f) };
        var paddleScene = GD.Load<PackedScene>("res://Paddle.tscn");
        for (int g = 0; g < 2; g++)
        {
            for (int i = 0; i < Moments.Length; i++)
            {
                var holder = new Node3D { Position = new Vector3(i * Spacing, 1f, g * Spacing * 2f) };
                AddChild(holder);
                var paddle = paddleScene.Instantiate<Paddle>();
                paddle.Grip = g == 0 ? GripStyle.Shakehand : GripStyle.Penhold;
                paddle.AvoidTable = false; // there is no table in the lab
                holder.AddChild(paddle);
                Moment m = Moments[i];
                paddle.Freeze(m.Style, m.Forehand, m.Windup, m.Follow, m.Face);
                var arm = new Arm { Skin = skin, Sleeve = sleeve };
                holder.AddChild(arm);
                stations[g, i] = new Station { Holder = holder, Paddle = paddle, Arm = arm };
            }
        }

        var layer = new CanvasLayer();
        AddChild(layer);
        cameras = new Camera3D[Moments.Length];
        labels = new Label[Moments.Length];
        Vector2 cell = new(1280f / Columns, 720f / Rows);
        for (int i = 0; i < Moments.Length; i++)
        {
            var at = new Vector2(i % Columns * cell.X, i / Columns * cell.Y);
            var container = new SubViewportContainer { Position = at, Size = cell - new Vector2(2f, 2f), Stretch = true };
            layer.AddChild(container);
            var viewport = new SubViewport { Size = (Vector2I)(cell - new Vector2(2f, 2f)), Msaa3D = Viewport.Msaa.Msaa4X };
            container.AddChild(viewport);
            cameras[i] = new Camera3D { Current = true, Fov = 40f };
            viewport.AddChild(cameras[i]);
            labels[i] = Caption(at + new Vector2(6f, 2f), 12);
            layer.AddChild(labels[i]);
        }
        footer = Caption(new Vector2(6f, 700f), 12);
        layer.AddChild(footer);
    }

    static Label Caption(Vector2 at, int size)
    {
        var label = new Label { Position = at };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", Colors.White);
        label.AddThemeColorOverride("font_outline_color", Colors.Black);
        label.AddThemeConstantOverride("outline_size", 4);
        return label;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        if (key.Keycode == Key.G)
            grip = 1 - grip;
        else if (key.Keycode == Key.V)
            view = (view + 1) % ViewNames.Length;
    }

    public override void _Process(double delta)
    {
        for (int i = 0; i < Moments.Length; i++)
        {
            for (int g = 0; g < 2; g++)
                BuildArm(stations[g, i]);

            Station st = stations[grip, i];
            Vector3 wrist = st.Paddle.WristPoint;
            Vector3 elbow = wrist + st.Paddle.ForearmDirection * Arm.ForearmSize;
            Vector3 centre = (st.Paddle.GlobalPosition + wrist + elbow) / 3f;
            Vector3 from = centre + st.Holder.GlobalBasis * ViewDirections[view].Normalized() * ViewDistance;
            cameras[i].GlobalTransform = new Transform3D(Basis.Identity, from).LookingAt(centre, Mathf.Abs(ViewDirections[view].Normalized().Y) > 0.9f ? Vector3.Back : Vector3.Up);

            Vector2 bend = st.Paddle.WristBend;
            string text = WristText(grip == 0 ? GripStyle.Shakehand : GripStyle.Penhold, bend, out bool beyond);
            labels[i].Text = $"{Moments[i].Name}\n{text}";
            labels[i].AddThemeColorOverride("font_color", beyond ? new Color(1f, 0.4f, 0.35f) : Colors.White);
        }
        footer.Text = $"{(grip == 0 ? "SHAKEHAND" : "PENHOLD")} grip, view from {ViewNames[view]}   |   G: shakehand / penhold   V: view";

        // Each shot is a few frames after the last change, once the views have rendered it.
        if (captureFolder != null && ++frame % 20 == 0)
        {
            string name = $"grip_{(grip == 0 ? "shakehand" : "penhold")}_{(view == 0 ? "front" : "side")}";
            GetViewport().GetTexture().GetImage().SavePng($"{captureFolder}/{name}.png");
            if (grip == 1 && view == 1)
                GetTree().Quit();
            else if (view == 0)
                view = 1;
            else
                (grip, view) = (1, 0);
        }
    }

    // The arm from the racket out: the elbow along the grip's forearm, the shoulder up the upper
    // arm from it; the arm is then solved to that shoulder with its elbow where it should be.
    static void BuildArm(Station st)
    {
        Vector3 wrist = st.Paddle.WristPoint;
        Vector3 elbow = wrist + st.Paddle.ForearmDirection * Arm.ForearmSize;
        Vector3 shoulder = elbow + st.Paddle.UpperArmDirection * Arm.UpperArmSize;
        st.Arm.Solve(shoulder, wrist, elbow - shoulder);
    }

    // Shakehand: the palm lies in the racket's edge plane, so bending in the racket's plane is
    // the wrist bent back (extension) or forward (flexion), out of it is sideways (deviation).
    // Penhold the other way round.
    static string WristText(GripStyle grip, Vector2 bend, out bool beyond)
    {
        float inPlane = Mathf.RadToDeg(bend.X);
        float outOfPlane = Mathf.RadToDeg(bend.Y);
        float flex = grip == GripStyle.Shakehand ? -inPlane : outOfPlane; // shakehand palm faces +X
        float side = grip == GripStyle.Shakehand ? outOfPlane : inPlane;
        beyond = flex < -70f || flex > 80f || Mathf.Abs(side) > 30f;
        string flexText = flex < 0f ? $"bent back {-flex:F0}°" : $"bent forward {flex:F0}°";
        return $"wrist {flexText}, sideways {Mathf.Abs(side):F0}°";
    }
}
