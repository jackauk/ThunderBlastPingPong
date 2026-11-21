using Godot;
using System.Collections.Generic;

// Serve laboratory (run ServeLab.tscn): a bare paddle and the ball play every kind of serve so
// the physics of each can be studied, with nobody holding the paddle.
//
// The server stands behind the -Z end line, right-handed. A forehand serve swings the paddle
// from the right of the body (world -X) across to the left, a backhand serve from the left of
// the body across to the right. The swing starts low (the blade's nose below the handle,
// brushing up), at medium height (blade level, brushing across) or high (nose up, brushing
// down). The ball is tossed high and struck as it falls back to the palm's height.
//
// How the paddle meets the ball decides the spin: brushing it thinly (a small contact angle
// between the swing and the face) lets the rubber's friction spin it hard, a flat hit (90
// degrees) pushes it with no spin, and the longer the ball stays on the rubber the more of the
// slip the rubber's grip turns into spin. The flight follows from the same physics as the game:
// the Magnus curve in the air and the kick or check of each bounce from the table's friction.
//
// For each serve only the paddle speed and a small turn of the swing are solved, so that the
// ball bounces once on the server's half and lands on a target on the receiver's half; the
// height over the net, the bounce angles, the spin and the curve are what the method produces.
public partial class ServeLab : Node3D, IBallHitter
{
    enum Kind { Topspin, TopSide, Sidespin, NoSpin, Underspin, UnderSide }

    // Swing direction in the server's frame (across in the swing's sense, up, forward towards
    // the net), where the swing starts, the usual contact angle (degrees between swing and face;
    // 90 is a flat hit with no friction), how far the wrist snaps the hand round the joint and
    // rolls the face about the handle through the swing (degrees), and whether such a serve is
    // usually short.
    // An underspin serve slices forwards under the falling ball with a very open face and a very
    // thin contact: chopping steeply down would go slower than the ball falls and roll it into
    // topspin, and a thick contact on a short, slow serve leaves no speed to rub with.
    record Method(string Name, string Start, Vector3 Swing, float ContactAngle, float Snap, float Roll, bool Short);

    static readonly Dictionary<Kind, Method> Methods = new()
    {
        [Kind.Topspin] = new("topspin", "low", new(0.2f, 0.75f, 0.65f), 20f, 120f, 60f, false),
        [Kind.TopSide] = new("topspin + sidespin", "low", new(0.8f, 0.5f, 0.35f), 20f, 140f, 70f, false),
        [Kind.Sidespin] = new("sidespin", "medium", new(0.9f, 0.05f, 0.4f), 20f, 150f, 80f, false),
        [Kind.NoSpin] = new("no spin (flat hit)", "medium", new(0.15f, -0.05f, 1f), 90f, 0f, 0f, false),
        [Kind.Underspin] = new("underspin", "high", new(0.15f, -0.35f, 0.9f), 10f, 110f, 60f, true),
        [Kind.UnderSide] = new("underspin + sidespin", "high", new(0.55f, -0.3f, 0.8f), 12f, 130f, 70f, true),
    };

    // A table bounce of the planned flight: where, the ball's angle to the table coming in and
    // going out, and its state either side.
    record Bounce(Vector3 Position, float AngleIn, float AngleOut, BallState Before, BallState After);

    enum Phase { Ready, Toss, Flight, Done }

    const float ContactHeight = 0.92f;   // struck as it falls back to the palm's height
    const float ContactZ = -1.5f;        // behind the end line
    const float TossHeight = 0.5f;       // thrown high
    const float SwingBefore = 0.45f;     // metres of swing before contact
    const float SwingAfter = 0.25f;
    const float RubberSurface = 0.0064f; // from the blade's centre plane
    const float WristToBlade = 0.2f;     // wrist joint to the blade's centre, along the handle
    const float SlowMotion = 0.15f;      // toss and contact
    const float FlightMotion = 0.35f;
    const float DoneTime = 2.5f;         // seconds to look at the result before the next serve

    public int Side => 0; // strikes the ball while nobody else may: the toss

    bool forehand = true;
    Kind kind;
    readonly Dictionary<Kind, float> contactAngles = new(); // adjusted per method, else its usual
    float ContactAngle => contactAngles.TryGetValue(kind, out float angle) ? angle : Methods[kind].ContactAngle;
    readonly Dictionary<Kind, float> wristSnaps = new();
    readonly Dictionary<Kind, float> wristRolls = new();
    float WristSnap => wristSnaps.TryGetValue(kind, out float angle) ? angle : Methods[kind].Snap;
    float WristRoll => wristRolls.TryGetValue(kind, out float angle) ? angle : Methods[kind].Roll;
    float dwell = 0.0036f;     // seconds the ball stays on the rubber
    bool? shortServe;          // null: the method's usual length
    bool auto = true;
    int serveIndex;

    Ball ball;
    Node3D paddle;
    Node3D marks;
    Label info;
    Camera3D closeUp;
    MeshInstance3D wristMarker;

    // The current serve.
    Phase phase;
    float timer;
    Vector3 tossFrom;
    float tossSpeed;
    int contactStep;           // ball sub-steps from the toss to contact
    int step;
    bool struck;
    Vector3 swing, normal;
    float speed, grip;
    BallState launched;
    readonly List<Bounce> bounces = new();
    float? netClearance;
    bool netTouched;
    string verdict;
    int liveBounces;
    bool solved;

    public override void _Ready()
    {
        foreach (string barrier in new[] { "BarrierFar", "BarrierNear", "BarrierLeft", "BarrierRight" })
            GetNode<Node3D>("Arena/" + barrier).Visible = false;

        ball = GetNode<Ball>("Ball");
        ball.Hitters.Add(this);
        ball.Contact += OnBallContact;

        paddle = GD.Load<PackedScene>("res://Paddle.tscn").Instantiate<Node3D>();
        AddChild(paddle);
        paddle.SetProcess(false);                       // posed here, not by its stroke animation
        paddle.GetNode<Node3D>("Hand").Visible = false; // bare paddle

        // The wrist joint, a small grey ball (the hand itself is left out).
        wristMarker = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.012f, Height = 0.024f, Material = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.55f, 0.6f) } }
        };
        AddChild(wristMarker);

        marks = new Node3D { Name = "Marks" };
        AddChild(marks);

        // User argument --capture=<folder>: save each serve's result as serve_NN.png there and
        // quit after the twelfth.
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--capture="))
                captureFolder = arg["--capture=".Length..];
        }

        BuildOverlay();
        StartServe();
    }

    public override void _ExitTree()
    {
        Engine.TimeScale = 1.0;
    }

    // The 3D view from behind the server on the left; on the right the serve's numbers, then
    // side and top views (which leave out the 3D labels).
    void BuildOverlay()
    {
        var layer = new CanvasLayer();
        AddChild(layer);

        AddView(layer, "", new Rect2(0, 0, 800, 690), camera =>
        {
            camera.Fov = 48f;
            camera.LookAtFromPosition(new Vector3(-2.5f, 1.85f, -2.3f), new Vector3(0.1f, 0.8f, -0.15f), Vector3.Up);
        });

        info = new Label { Position = new Vector2(806, 4), Size = new Vector2(470, 0), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        info.AddThemeFontSizeOverride("font_size", 12);
        info.AddThemeColorOverride("font_color", new Color(0.92f, 0.95f, 1f));
        info.AddThemeStyleboxOverride("normal", new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0.6f), ContentMarginLeft = 8, ContentMarginRight = 8,
            ContentMarginTop = 6, ContentMarginBottom = 6
        });
        layer.AddChild(info);

        var help = new Label
        {
            Text = "Space next   R replay   1-6 method   F fore/backhand   Up/Down contact angle   Left/Right time on rubber   " +
                   "W/S wrist snap   Q/E wrist roll   L short/long   A auto",
            Position = new Vector2(12, 696)
        };
        help.AddThemeFontSizeOverride("font_size", 13);
        help.AddThemeColorOverride("font_color", new Color(0.8f, 0.82f, 0.88f));
        layer.AddChild(help);

        AddView(layer, "Paddle close-up, from the server's right", new Rect2(6, 456, 300, 228), camera =>
        {
            camera.Fov = 38f;
            camera.CullMask = 1;
            closeUp = camera;
        });
        AddView(layer, "Side view (server on the left)", new Rect2(806, 342, 470, 172), camera =>
        {
            camera.Projection = Camera3D.ProjectionType.Orthogonal;
            camera.Size = 1.25f;
            camera.CullMask = 1;
            camera.LookAtFromPosition(new Vector3(-5f, 0.95f, 0f), new Vector3(0f, 0.95f, 0f), Vector3.Up);
        });
        AddView(layer, "Top view (server on the left)", new Rect2(806, 518, 470, 172), camera =>
        {
            camera.Projection = Camera3D.ProjectionType.Orthogonal;
            camera.Size = 1.9f;
            camera.CullMask = 1;
            camera.LookAtFromPosition(new Vector3(0f, 5f, 0f), Vector3.Zero, Vector3.Right);
        });
    }

    // A view of the scene in its own viewport, sharing the scene's world.
    static void AddView(CanvasLayer layer, string title, Rect2 rect, System.Action<Camera3D> setUp)
    {
        var container = new SubViewportContainer { Position = rect.Position, Size = rect.Size, Stretch = true };
        layer.AddChild(container);
        var viewport = new SubViewport { Size = (Vector2I)rect.Size, Msaa3D = Viewport.Msaa.Msaa2X };
        container.AddChild(viewport);
        var camera = new Camera3D { Current = true };
        viewport.AddChild(camera);
        setUp(camera);
        if (title == "")
            return;

        Vector2 at = rect.Position;
        var label = new Label { Text = title, Position = at + new Vector2(6, 2) };
        label.AddThemeFontSizeOverride("font_size", 13);
        label.AddThemeColorOverride("font_color", Colors.White);
        label.AddThemeColorOverride("font_outline_color", Colors.Black);
        label.AddThemeConstantOverride("outline_size", 4);
        layer.AddChild(label);
    }

    void StartServe()
    {
        foreach (Node child in marks.GetChildren())
            child.QueueFree();
        Engine.TimeScale = 1.0;
        Plan();
        ball.Hold(tossFrom);
        phase = Phase.Ready;
        timer = 0.6f;
        liveBounces = 0;
        netShown = false;

        // The paddle's path, dotted, and where it starts.
        for (float travel = -SwingBefore; travel <= SwingAfter; travel += 0.02f)
        {
            PlacePaddle(travel);
            AddDot(paddle.GlobalPosition, 0.004f, new Color(1f, 0.9f, 0.3f));
            AddDot(paddle.GlobalPosition - paddle.GlobalBasis.Y * WristToBlade, 0.003f, new Color(0.6f, 0.6f, 0.65f));
        }
        PlacePaddle(-SwingBefore);
        // Close-up from the server's right (-X), level with the contact: the paddle in profile,
        // its nose and its face tilt, along the path in from the start.
        Vector3 contactAt = launched.Position;
        closeUp.LookAtFromPosition(contactAt + new Vector3(-1.05f, 0.08f, -0.1f), contactAt + new Vector3(0f, -0.04f, -0.05f), Vector3.Up);
        AddLabel(paddle.GlobalPosition + new Vector3(0f, -0.13f, 0f), $"start {Methods[kind].Start}");
        UpdateInfo();
    }

    // Predicts the toss, solves the swing and plays the planned flight through once to know its
    // bounces, net clearance and verdict.
    void Plan()
    {
        Method method = Methods[kind];
        float across = forehand ? 1f : -1f; // forehand swings from the right (-X) across to +X
        tossFrom = new Vector3(forehand ? -0.3f : 0.1f, ContactHeight, ContactZ);
        tossSpeed = Mathf.Sqrt(2f * 9.81f * TossHeight);

        // The ball tossed straight up from the palm, until it falls back to the palm's height.
        float dt = Ball.SubStepTime;
        var contact = new BallState(tossFrom, new Vector3(0f, tossSpeed, 0f), Vector3.Zero);
        contactStep = 0;
        do
        {
            Ball.Step(ref contact, dt, false);
            contactStep++;
        } while (contact.Velocity.Y > 0f || contact.Position.Y > ContactHeight);

        Vector3 swing0 = new Vector3(method.Swing.X * across, method.Swing.Y, method.Swing.Z).Normalized();
        Vector3 normal0 = FaceNormal(swing0, ContactAngle);
        grip = Ball.GripForDwell(dwell);
        bool isShort = shortServe ?? method.Short;
        float clearance = isShort ? 0.03f : 0.05f;
        var landing = new Vector2(Mathf.Clamp(across * method.Swing.X * 0.4f, -0.5f, 0.5f), isShort ? 0.45f : 1.1f);

        flat = ContactAngle >= 90f;
        snap = flat ? 0f : Mathf.DegToRad(WristSnap);
        roll = flat ? 0f : Mathf.DegToRad(WristRoll);
        solved = Solve(contact, swing0, normal0, clearance, landing, out Vector3 best);
        var turn = new Basis(Vector3.Up, best.Z) * new Basis(Vector3.Right, best.Y);
        normal = turn * normal0;
        swing = turn * swing0;
        speed = best.X;
        racketVelocity = Racket(contact, swing, normal, speed);
        if (flat)
        {
            swing = racketVelocity.Normalized();
            speed = racketVelocity.Length();
        }
        wrist = WristFor(contact.Position, normal, swing, speed);

        launched = contact;
        Ball.HitByRacket(ref launched, normal, racketVelocity, grip);
        BallState armOnly = contact;
        Ball.HitByRacket(ref armOnly, normal, swing * speed, grip);
        armOnlySpin = armOnly.Spin.Length();
        PlayThrough();
    }

    bool flat;
    float snap, roll;          // the wrist's sweep through the swing, radians
    Vector3 racketVelocity;    // of the rubber where it touches the ball: arm plus wrist
    WristMotion wrist;
    float armOnlySpin;         // what the same arm swing would give without the wrist, rad/s

    // The wrist during a swing: the joint on the handle line behind the blade, the nose
    // direction, the axis the hand snaps about, which way the face rolls about the handle, and
    // the time scale over which both sweep through contact.
    record struct WristMotion(Vector3 Joint, Vector3 Nose, Vector3 SnapAxis, float RollSense, float Time);

    // The snap swings the blade's head round the wrist within the face's own plane (about the
    // face normal), the way the arm brushes: all of its speed rubs along the ball rather than
    // pushing into it, so a bigger snap brushes harder and spins more. The face rolls over the
    // ball the way it brushes: closing further over a topspin brush, opening further under an
    // underspin slice. Both sweep evenly either side of contact, cocked back when the arm
    // starts and fastest at contact.
    WristMotion WristFor(Vector3 ball, Vector3 face, Vector3 arm, float armSpeed)
    {
        Vector3 nose = NoseFor(face, arm);
        Vector3 joint = ball - face * (Ball.Radius + RubberSurface) - nose * WristToBlade;
        Vector3 reach = ball - face * Ball.Radius - joint;
        Vector3 brush = arm - face * arm.Dot(face);
        Vector3 axis = face.Cross(reach).Dot(brush) >= 0f ? face : -face;
        Vector3 sweep = brush.LengthSquared() > 1e-6f ? brush.Normalized() : axis.Cross(reach).Normalized();
        float rollSense = nose.Cross(face).Dot(sweep) > 0f ? -1f : 1f;
        float time = 2f * SwingBefore / (Mathf.Pi * Mathf.Max(armSpeed, 0.5f));
        return new WristMotion(joint, nose, axis, rollSense, time);
    }

    // The rubber's velocity at the contact point: the arm's swing plus the wrist turning the
    // paddle about the joint at its fastest. A flat hit has no friction and no wrist: the paddle
    // also moves with the ball along the face (here, dropping with it), so nothing slips and
    // nothing spins.
    Vector3 Racket(BallState contact, Vector3 arm, Vector3 face, float armSpeed)
    {
        if (flat)
            return face * armSpeed + (contact.Velocity - face * contact.Velocity.Dot(face));
        WristMotion w = WristFor(contact.Position, face, arm, armSpeed);
        Vector3 reach = contact.Position - face * Ball.Radius - w.Joint;
        Vector3 turning = w.SnapAxis * (snap / (2f * w.Time)) + w.Nose * (w.RollSense * roll / (2f * w.Time));
        return arm * armSpeed + turning.Cross(reach);
    }

    // The blade's nose, in the face's plane: mostly across the brush (the wrist's snap can only
    // move the blade's centre across the handle, so a handle along the brush could not add to
    // it), tilted down below the handle from a low or medium start (a pendulum serve) and up
    // from a high one.
    Vector3 NoseFor(Vector3 face, Vector3 arm)
    {
        Vector3 hint = Methods[kind].Start == "high" ? Vector3.Up : Vector3.Down;
        Vector3 brush = arm - face * arm.Dot(face);
        Vector3 across = brush.LengthSquared() > 1e-6f ? face.Cross(brush).Normalized() : Vector3.Zero;
        if (across.Dot(hint + 0.3f * Vector3.Back) < 0f)
            across = -across;
        Vector3 tilt = hint - face * hint.Dot(face);
        tilt = tilt.LengthSquared() > 1e-6f ? tilt.Normalized() : Vector3.Zero;
        Vector3 nose = across + 0.7f * tilt;
        if (nose.LengthSquared() < 1e-4f)
            nose = Vector3.Right - face * face.X;
        return nose.Normalized();
    }

    // The face normal at contactAngle to the swing, turned from the swing towards the net: a
    // swing upwards gets a closed face, downwards an open one, across a face to the net.
    static Vector3 FaceNormal(Vector3 swing, float contactAngle)
    {
        Vector3 forward = Vector3.Back - swing * swing.Dot(Vector3.Back);
        forward = forward.LengthSquared() > 1e-6f ? forward.Normalized() : Vector3.Up;
        float a = Mathf.DegToRad(contactAngle);
        return (swing * Mathf.Sin(a) + forward * Mathf.Cos(a)).Normalized();
    }

    // Newton's method on (paddle speed, swing pitch, swing yaw) so the ball skims the net by
    // clearance and then lands at landing on the receiver's half, on an endless table without
    // the net (smooth to search). Where the first bounce falls is up to the method; the real
    // flight then checks it is on the server's half. Returns false if it found no solution.
    bool Solve(BallState contact, Vector3 swing0, Vector3 normal0, float clearance, Vector2 landing, out Vector3 best)
    {
        Vector3 Residual(Vector3 p)
        {
            var turn = new Basis(Vector3.Up, p.Z) * new Basis(Vector3.Right, p.Y);
            Vector3 face = turn * normal0;
            BallState s = contact;
            Ball.HitByRacket(ref s, face, Racket(contact, turn * swing0, face, p.X), grip);
            float? overNet = null;
            for (float t = 0f; t < 4f; t += Ball.SubStepTime)
            {
                BallState before = s;
                BallEvent evt = Ball.Step(ref s, Ball.SubStepTime, true);
                if (overNet == null && before.Position.Z < 0f && s.Position.Z >= 0f)
                {
                    float y = Mathf.Lerp(before.Position.Y, s.Position.Y, -before.Position.Z / (s.Position.Z - before.Position.Z));
                    overNet = y - Ball.Radius - TableSpec.NetTop;
                }
                if (overNet != null && evt == BallEvent.Table)
                    return new Vector3(overNet.Value - clearance, s.Position.X - landing.X, s.Position.Z - landing.Y);
            }
            return new Vector3(9f, 9f, 9f);
        }

        best = new Vector3(5f, 0f, 0f);
        // Different starting speeds can find different serves landing the same way (a slow
        // pop or a fast brush); keep the one that spins most, which is what the method is for.
        // A serve must bounce on the server's half before crossing: solutions that do not are
        // only kept if nothing better turns up.
        float SpinOf(Vector3 p)
        {
            var turn = new Basis(Vector3.Up, p.Z) * new Basis(Vector3.Right, p.Y);
            Vector3 face = turn * normal0;
            BallState s = contact;
            Ball.HitByRacket(ref s, face, Racket(contact, turn * swing0, face, p.X), grip);
            float spin = s.Spin.Length();
            for (float t = 0f; t < 3f; t += Ball.SubStepTime)
            {
                if (Ball.Step(ref s, Ball.SubStepTime, true) == BallEvent.Table)
                    return s.Position.Z < 0f ? spin : spin - 10000f;
            }
            return spin - 10000f;
        }

        float bestError = float.MaxValue;
        float bestSpin = -1f;
        foreach (Vector2 start in new Vector2[] { new(2.5f, 0f), new(4f, 0f), new(7f, 0f), new(11f, 0f), new(16f, 0f),
                                                  new(5f, 0.3f), new(9f, 0.3f), new(5f, -0.3f), new(9f, -0.3f) })
        {
            var p = new Vector3(start.X, start.Y, 0f);
            for (int i = 0; i < 40; i++)
            {
                Vector3 r = Residual(p);
                if (r.Length() < 0.005f)
                {
                    float spin = SpinOf(p);
                    if (bestError >= 0.005f || spin > bestSpin)
                    {
                        bestSpin = spin;
                        best = p;
                    }
                    bestError = 0f;
                    break;
                }
                if (bestError >= 0.005f && r.Length() < bestError)
                {
                    bestError = r.Length();
                    best = p;
                }

                var h = new Vector3(0.02f * Mathf.Max(p.X, 1f), 0.003f, 0.003f);
                var jacobian = new Basis(
                    (Residual(p + new Vector3(h.X, 0f, 0f)) - r) / h.X,
                    (Residual(p + new Vector3(0f, h.Y, 0f)) - r) / h.Y,
                    (Residual(p + new Vector3(0f, 0f, h.Z)) - r) / h.Z);
                if (Mathf.Abs(jacobian.Determinant()) < 1e-9f)
                    break;
                Vector3 dp = jacobian.Inverse() * -r;
                dp = new Vector3(Mathf.Clamp(dp.X, -3f, 3f), Mathf.Clamp(dp.Y, -0.15f, 0.15f), Mathf.Clamp(dp.Z, -0.15f, 0.15f));
                p = new Vector3(Mathf.Clamp(p.X + dp.X, 1f, 30f), Mathf.Clamp(p.Y + dp.Y, -0.7f, 0.7f), Mathf.Clamp(p.Z + dp.Z, -0.7f, 0.7f));
            }
        }
        return bestError < 0.005f;
    }

    // The planned flight over the real table and net, the same steps the ball will take.
    void PlayThrough()
    {
        bounces.Clear();
        netClearance = null;
        netTouched = false;
        BallState s = launched;
        for (float t = 0f; t < 3f && bounces.Count < 3; t += Ball.SubStepTime)
        {
            BallState before = s;
            BallEvent evt = Ball.Step(ref s, Ball.SubStepTime, false);
            if (netClearance == null && before.Position.Z < 0f && s.Position.Z >= 0f)
            {
                float y = Mathf.Lerp(before.Position.Y, s.Position.Y, -before.Position.Z / (s.Position.Z - before.Position.Z));
                netClearance = y - Ball.Radius - TableSpec.NetTop;
            }
            if (evt == BallEvent.Net)
                netTouched = true;
            else if (evt == BallEvent.Table)
                bounces.Add(new Bounce(s.Position, AngleToTable(before.Velocity, -1f), AngleToTable(s.Velocity, 1f), before, s));
            else if (evt == BallEvent.Floor)
                break;
        }

        if (bounces.Count == 0 || bounces[0].Position.Z >= 0f)
            verdict = "FAULT: the first bounce must be on the server's half";
        else if (netTouched)
            verdict = "NET: the ball touched the net";
        else if (bounces.Count < 2 || bounces[1].Position.Z <= 0f)
            verdict = "FAULT: missed the receiver's half";
        else
            verdict = "LEGAL SERVE";
    }

    static float AngleToTable(Vector3 v, float sense) =>
        Mathf.RadToDeg(Mathf.Atan2(sense * v.Y, new Vector2(v.X, v.Z).Length()));

    // Puts the paddle along its swing, the arm travel metres from contact (negative before it):
    // the wrist joint moves with the arm while the paddle snaps and rolls about it, so the
    // blade follows a curve. The hitting rubber faces along the face normal at contact: red for
    // the forehand, black for the backhand.
    void PlacePaddle(float travel)
    {
        travel = Mathf.Clamp(travel, -SwingBefore, SwingAfter);
        Vector3 z = forehand ? -normal : normal;
        var atContact = new Basis(wrist.Nose.Cross(z), wrist.Nose, z);
        Vector3 centre = launched.Position - normal * (Ball.Radius + RubberSurface);

        float sweep = 0.5f * Mathf.Sin(Mathf.Clamp(travel / speed / wrist.Time, -Mathf.Pi / 2f, Mathf.Pi / 2f));
        Basis turn = new Basis(wrist.SnapAxis, snap * sweep) * new Basis(wrist.Nose, wrist.RollSense * roll * sweep);
        Vector3 joint = wrist.Joint + swing * travel;
        paddle.GlobalTransform = new Transform3D(turn * atContact, joint + turn * (centre - wrist.Joint));
        wristMarker.GlobalPosition = joint;
    }

    public bool TryHit(Ball b)
    {
        step++;
        if (struck || step != contactStep)
            return false;

        // The paddle reaches the ball: push along the face and friction across it.
        BallState s = b.State;
        Ball.HitByRacket(ref s, normal, racketVelocity, grip);
        b.Strike(0, s.Position, s.Velocity, s.Spin);
        struck = true;
        phase = Phase.Flight;

        // Leave a copy of the paddle where it met the ball, to show the face at contact.
        PlacePaddle(0f);
        var atContact = (Node3D)paddle.Duplicate((int)DuplicateFlags.Groups);
        marks.AddChild(atContact);
        atContact.GlobalTransform = paddle.GlobalTransform;
        AddLabel(s.Position + new Vector3(0f, 0.22f, 0f), $"contact\nface {FaceText()}\ncontact angle {ContactAngle:F0}°");
        GD.Print($"SERVE {serveIndex + 1}: {(forehand ? "forehand" : "backhand")} {Methods[kind].Name}, {verdict}, " +
                 $"speed {speed:F1} m/s, ball {s.Velocity.Length():F1} m/s, {SpinText(s)}, over net {(netClearance ?? -1f) * 100f:F1} cm");
        return true;
    }

    void OnBallContact(BallEvent evt, Vector3 at)
    {
        if (!struck || evt != BallEvent.Table || liveBounces >= bounces.Count)
            return;
        Bounce bounce = bounces[liveBounces++];
        string which = liveBounces == 1 ? "1st bounce" : liveBounces == 2 ? "2nd bounce" : "3rd bounce";
        AddRing(bounce.Position, liveBounces == 2 ? new Color(0.2f, 1f, 0.4f) : new Color(1f, 0.85f, 0.2f));
        AddLabel(bounce.Position + new Vector3(0f, 0.09f, 0f),
            $"{which}\nin {bounce.AngleIn:F0}°  out {bounce.AngleOut:F0}°\n{Rpm(bounce.Before.Spin.Length()):F0} → {Rpm(bounce.After.Spin.Length()):F0} rpm");
    }

    public override void _Process(double delta)
    {
        float real = (float)(delta / Engine.TimeScale);
        float sinceContact = (step - contactStep) * Ball.SubStepTime;
        PlacePaddle(phase == Phase.Ready ? -SwingBefore : sinceContact * speed);

        switch (phase)
        {
            case Phase.Ready:
                if ((timer -= real) <= 0f)
                {
                    step = 0;
                    struck = false;
                    ball.Toss(new Vector3(0f, tossSpeed, 0f));
                    phase = Phase.Toss;
                    Engine.TimeScale = SlowMotion;
                }
                break;
            case Phase.Flight:
                if (sinceContact > 0.1f)
                    Engine.TimeScale = FlightMotion;
                if (sinceContact > 2.2f || ball.State.Position.Y < 0.3f || liveBounces >= 3)
                {
                    phase = Phase.Done;
                    timer = DoneTime;
                    Engine.TimeScale = 1.0;
                    GD.Print($"SERVE {serveIndex + 1} DONE frame {Engine.GetProcessFrames()}");
                }
                break;
            case Phase.Done:
                timer -= real;
                if (captureFolder != null && !captured && timer < DoneTime - 0.3f)
                {
                    captured = true;
                    GetViewport().GetTexture().GetImage().SavePng($"{captureFolder}/serve_{serveIndex + 1:D2}.png");
                    if (serveIndex == 11)
                        GetTree().Quit();
                }
                if (auto && timer <= 0f)
                {
                    captured = false;
                    serveIndex = (serveIndex + 1) % 12;
                    forehand = serveIndex < 6;
                    kind = (Kind)(serveIndex % 6);
                    StartServe();
                }
                break;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (phase != Phase.Toss && phase != Phase.Flight)
            return;

        // Trail: white on the toss, then coloured by the spin's main kind.
        Color colour = Colors.White;
        if (struck)
        {
            var (top, side, _) = SpinParts(ball.State);
            float rpm = Rpm(ball.State.Spin.Length());
            colour = rpm < 300f ? new Color(0.85f, 0.85f, 0.85f)
                : Mathf.Abs(side) > Mathf.Abs(top) ? new Color(0.3f, 1f, 0.4f)
                : top > 0f ? new Color(1f, 0.5f, 0.1f) : new Color(0.25f, 0.6f, 1f);
        }
        AddDot(ball.State.Position, 0.006f, colour);

        if (struck && netClearance is float clearance && ball.State.Position.Z >= 0f && !netShown)
        {
            netShown = true;
            float top = TableSpec.NetTop;
            var line = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.003f, BottomRadius = 0.003f, Height = Mathf.Max(clearance, 0.002f), Material = Unshaded(new Color(1f, 0.3f, 0.6f)) },
                Position = new Vector3(ball.State.Position.X, top + clearance * 0.5f, 0f)
            };
            marks.AddChild(line);
            AddLabel(new Vector3(ball.State.Position.X, top + clearance + 0.06f, 0f), $"{clearance * 100f:F1} cm over the net");
        }
    }

    bool netShown;
    string captureFolder;
    bool captured;

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        switch (key.Keycode)
        {
            case Key.Space:
                serveIndex = (serveIndex + 1) % 12;
                forehand = serveIndex < 6;
                kind = (Kind)(serveIndex % 6);
                break;
            case Key.R:
                break;
            case >= Key.Key1 and <= Key.Key6:
                kind = (Kind)(key.Keycode - Key.Key1);
                break;
            case Key.F:
                forehand = !forehand;
                break;
            case Key.Up:
                contactAngles[kind] = Mathf.Min(ContactAngle + 5f, 90f);
                break;
            case Key.Down:
                contactAngles[kind] = Mathf.Max(ContactAngle - 5f, 5f);
                break;
            case Key.Right:
                dwell = Mathf.Min(dwell + 0.0005f, 0.008f);
                break;
            case Key.Left:
                dwell = Mathf.Max(dwell - 0.0005f, 0.0005f);
                break;
            case Key.W:
                wristSnaps[kind] = Mathf.Min(WristSnap + 10f, 170f);
                break;
            case Key.S:
                wristSnaps[kind] = Mathf.Max(WristSnap - 10f, 0f);
                break;
            case Key.E:
                wristRolls[kind] = Mathf.Min(WristRoll + 10f, 90f);
                break;
            case Key.Q:
                wristRolls[kind] = Mathf.Max(WristRoll - 10f, 0f);
                break;
            case Key.L:
                shortServe = !(shortServe ?? Methods[kind].Short);
                break;
            case Key.A:
                auto = !auto;
                UpdateInfo();
                return;
            default:
                return;
        }
        serveIndex = (forehand ? 0 : 6) + (int)kind;
        StartServe();
    }

    void UpdateInfo()
    {
        Method method = Methods[kind];
        float alpha = ContactAngle;
        string face = FaceText();
        string thickness = alpha < 30f ? "thin brush" : alpha < 60f ? "half brush" : alpha < 89f ? "thick" : "flat hit";
        string nose = method.Start switch { "low" => "nose below the handle", "high" => "nose up", _ => "nose down, pendulum" };
        string legal = solved ? "" : "  (no exact solution, closest shown)";

        var (top, side, _) = SpinParts(launched);
        string firstBounce = bounces.Count > 0 ? $"z {bounces[0].Position.Z:F2} m, in {bounces[0].AngleIn:F0}° out {bounces[0].AngleOut:F0}°" : "none";
        string secondBounce = bounces.Count > 1
            ? $"x {bounces[1].Position.X:F2} z {bounces[1].Position.Z:F2} m, in {bounces[1].AngleIn:F0}° out {bounces[1].AngleOut:F0}°, " +
              $"speed {new Vector2(bounces[1].Before.Velocity.X, bounces[1].Before.Velocity.Z).Length():F1} → {new Vector2(bounces[1].After.Velocity.X, bounces[1].After.Velocity.Z).Length():F1} m/s"
            : "none";
        float peak = 0f;
        BallState s = launched;
        for (float t = 0f; t < 2f && s.Position.Z < (bounces.Count > 1 ? bounces[1].Position.Z : 1.4f); t += Ball.SubStepTime)
        {
            Ball.Step(ref s, Ball.SubStepTime, false);
            peak = Mathf.Max(peak, s.Position.Y - Ball.Radius - TableSpec.Height);
        }

        info.Text =
            $"SERVE LAB   {serveIndex + 1} / 12   {(auto ? "auto" : "manual")}\n" +
            $"{(forehand ? "Forehand serve: paddle from the RIGHT of the body" : "Backhand serve: paddle from the LEFT of the body")}\n" +
            $"Start {method.Start.ToUpper()} ({nose}), method: {method.Name.ToUpper()}\n\n" +
            $"PADDLE  arm {speed:F1} m/s, swing {Mathf.Abs(Mathf.RadToDeg(Mathf.Asin(swing.Y))):F0}° {(swing.Y >= 0f ? "up" : "down")}, " +
            $"{Mathf.RadToDeg(Mathf.Atan2(Mathf.Abs(swing.X), swing.Z)):F0}° across\n" +
            $"  WRIST snap {WristSnap:F0}° round the joint, roll {WristRoll:F0}° about the handle: " +
            $"+{(racketVelocity - swing * speed).Length():F1} m/s, rubber at the ball {racketVelocity.Length():F1} m/s\n" +
            $"  face {face}, contact angle {alpha:F0}° ({thickness})\n" +
            $"  on the rubber {dwell * 1000f:F1} ms: grip {grip * 100f:F0}%{legal}\n" +
            $"BALL  {launched.Velocity.Length():F1} m/s, {SpinText(launched)}\n" +
            $"  the arm alone (no wrist) would give {Rpm(armOnlySpin):F0} rpm\n" +
            $"FLIGHT  1st bounce {firstBounce}\n" +
            $"  {(netClearance is float c ? $"{c * 100f:F1} cm over the net" : "does not reach the net")}, peak {peak * 100f:F0} cm over the table\n" +
            $"  2nd bounce {secondBounce}\n" +
            $"{verdict}";
    }

    // Spin about the axis across the flight (positive topspin), about the vertical (positive
    // curves the ball to the server's left, +X), and about the flight direction.
    static (float Top, float Side, float Cork) SpinParts(BallState s)
    {
        Vector3 forward = new Vector3(s.Velocity.X, 0f, s.Velocity.Z).Normalized();
        return (s.Spin.Dot(Vector3.Up.Cross(forward)), s.Spin.Y, s.Spin.Dot(forward));
    }

    static string SpinText(BallState s)
    {
        var (top, side, _) = SpinParts(s);
        string sideText = Rpm(Mathf.Abs(side)) < 100f ? "no sidespin"
            : $"sidespin {Rpm(Mathf.Abs(side)):F0} rpm (curves to the server's {(side > 0f ? "left" : "right")})";
        string topText = Rpm(Mathf.Abs(top)) < 100f ? "no top/underspin"
            : $"{(top > 0f ? "topspin" : "underspin")} {Rpm(Mathf.Abs(top)):F0} rpm";
        return $"{Rpm(s.Spin.Length()):F0} rpm: {topText}, {sideText}";
    }

    // The face's tilt from vertical: open faces up, closed faces down.
    string FaceText()
    {
        float tilt = Mathf.RadToDeg(Mathf.Asin(normal.Y));
        return Mathf.Abs(tilt) < 3f ? "vertical" : tilt > 0f ? $"open {tilt:F0}°" : $"closed {-tilt:F0}°";
    }

    static float Rpm(float radiansPerSecond) => radiansPerSecond * 60f / Mathf.Tau;

    static StandardMaterial3D Unshaded(Color colour) =>
        new() { AlbedoColor = colour, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };

    void AddDot(Vector3 at, float radius, Color colour)
    {
        marks.AddChild(new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = radius, Height = radius * 2f, RadialSegments = 6, Rings = 3, Material = Unshaded(colour) },
            Position = at,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        });
    }

    void AddRing(Vector3 at, Color colour)
    {
        marks.AddChild(new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = 0.035f, OuterRadius = 0.045f, Material = Unshaded(colour) },
            Position = new Vector3(at.X, TableSpec.Height + 0.002f, at.Z)
        });
    }

    void AddLabel(Vector3 at, string text)
    {
        marks.AddChild(new Label3D
        {
            Text = text, Position = at, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true,
            FontSize = 22, PixelSize = 0.0007f, FixedSize = true, OutlineSize = 7, Modulate = Colors.White,
            Layers = 2 // main view only
        });
    }
}
