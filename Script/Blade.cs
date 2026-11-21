using Godot;
using System.Threading.Tasks;

// The player's blade. The mouse sets its height and sideways position; once the ball has
// bounced on the player's half the blade reaches in along the table to meet it, so the
// mouse effectively chooses where on the ball's arc it is taken. Holding the blade right of
// centre plays a forehand, left of centre a backhand.
//
// The mouse buttons choose the stroke, played at contact:
//  - no button: a block (the face set against the ball, its own pace sent back), or a push
//    against a low backspin ball;
//  - left: a topspin drive, or a flick when a low ball is taken over the table (a smash when
//    it sits up high and the button was held long);
//  - right: a cut (push) over the table, a chop from behind it.
// The stroke is decided at the moment of contact, from the button down then and how long it has
// been held: a short press is slow, a long one (up to FullChargeTime) fast. A button already let
// go counts for nothing; nothing waits for a release. The contact physics turns the racket's motion into the return's pace and spin; only
// the face angle is set to aim, like a player's wrist. Finding a swing that lands takes tens of
// milliseconds, too long for the frame of contact, so it is worked out on a worker thread while
// the ball comes, for the contact the flight predicts and the strength the press will have
// reached by then.
public partial class Blade : Node3D, IBallHitter
{
    [Export] public Ball TargetBall;
    [Export] public float Reach = 0.13f;       // forgiving hit radius around the blade centre
    [Export] public float MinReachZ = 0.5f;    // how far over the table the player can lean
    [Export] public Vector2 HorizontalRange = new(-1.1f, 1.1f);
    [Export] public Vector2 HeightRange = new(0.8f, 1.5f);
    [Export] public float BodyCentreX = -0.05f; // where the player stands; right of it is forehand
    [Export] public float ForwardSwingTime = 0.12f;
    [Export(PropertyHint.Range, "0,1")] public float Skill = 0.6f;      // racket speeds from Human, 0 recreational .. 1 elite
    [Export] public float FullChargeTime = 0.6f; // seconds of holding a button for the fastest stroke
    [Export(PropertyHint.Range, "35,140")] public float BodyMass = Human.DefaultBodyMass; // kg (Human.ImpactShare)

    public enum Shot { Block, Push, Drive, Flick, Smash, Cut, Chop }
    enum Button { None, Left, Right }

    public int Side => 1;
    public Vector3 SwingVelocity { get; private set; }
    public Paddle Paddle => paddle;

    Paddle paddle;
    // The equipped racket's two faces (Equipment), read on the main thread each frame, and the
    // one the stroke being planned is struck with: per thread, since a plan is worked out in the
    // background while the ball comes.
    RacketFace forehandFace = RacketFace.Standard, backhandFace = RacketFace.Standard;
    int equipmentVersion = -1;
    [System.ThreadStatic] static Striker? planning;
    Striker Hitter => planning ?? new Striker(BodyMass, paddle.Forehand ? forehandFace : backhandFace);
    Vector3 lastPosition;
    float restZ;
    float closestOffset = float.MaxValue;

    Button held;                // the button down now (left wins if both)
    ulong pressedAt;            // real-time ms, so slow motion does not charge faster
    BallState? predicted;       // the ball where it will meet the blade, while it comes

    // The stroke worked out ahead, and what for.
    record PlanFor(Shot Shot, float Power, BallState Ball, bool Forehand, float AimX);
    PlanFor planFor;
    Task<(Vector3 racket, Vector2 face)> planTask;

    CanvasLayer hud;
    Label shotLabel;
    ProgressBar chargeBar;
    StyleBoxFlat chargeFill;

    public override void _Ready()
    {
        paddle = GetNode<Paddle>("Paddle");
        // The player's forearm holding the racket (the player has no body): the hand runs into it.
        AddChild(new HeldArm
        {
            Name = "Arm",
            Paddle = paddle,
            Skin = paddle.Skin,
            Sleeve = paddle.Skin,
        });
        lastPosition = Position;
        restZ = Position.Z;
        BuildHud();
        VisibilityChanged += () => hud.Visible = Visible;
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        UseEquipment();
        ReadButtons();
        Vector2 mouse = GetViewport().GetMousePosition() / GetViewport().GetVisibleRect().Size;
        float reachZ = restZ;
        if (TargetBall != null && TargetBall.HittableBy == Side)
            reachZ = Mathf.Clamp(TargetBall.State.Position.Z, MinReachZ, restZ);
        Position = new Vector3(
            Mathf.Lerp(HorizontalRange.X, HorizontalRange.Y, Mathf.Clamp(mouse.X, 0f, 1f)),
            Mathf.Lerp(HeightRange.Y, HeightRange.X, Mathf.Clamp(mouse.Y, 0f, 1f)),
            Mathf.Lerp(Position.Z, reachZ, 1f - Mathf.Exp(-20f * dt)));

        // The mouse's sideways motion, measured in real time, steers the stroke a little.
        Vector3 velocity = (Position - lastPosition) / (dt / (float)Engine.TimeScale);
        velocity.Z = 0f;
        SwingVelocity = SwingVelocity.Lerp(velocity, 0.5f);
        lastPosition = Position;

        // Back-swing for the stroke the buttons ask for while the ball comes, swinging forward
        // to arrive with it; a longer press swings further back.
        float eta = TimeToContact(out predicted);
        var (button, power) = Asked();
        Shot shot = Choose(button, power, predicted ?? TargetBall?.State ?? default);
        paddle.ChooseSide(Position.X - BodyCentreX);
        float backswing = shot is Shot.Block or Shot.Push ? 0.5f : Mathf.Lerp(0.5f, 1f, power);
        bool ours = TargetBall != null && TargetBall.HittableBy == Side;
        paddle.Windup = eta < 0f || !ours ? 0f : Mathf.Clamp(eta / ForwardSwingTime, 0f, 1f) * backswing;
        paddle.FaceTilt = 0f;
        paddle.Style = StyleOf(shot);
        ShowHud(button, power, shot);
        PlanAhead(eta);
    }

    // Starts working out the stroke for the coming contact on a worker thread, unless the one
    // worked out or under way is for the same contact, stroke and strength. One at a time: a
    // newer one starts when the last is done.
    void PlanAhead(float eta)
    {
        if (predicted is not BallState at || eta < 0f)
            return;
        var (button, power) = AskedAt(eta);
        var want = new PlanFor(Choose(button, power, at), power, at, paddle.Forehand, Mathf.Clamp(at.Position.X * 0.4f, -0.6f, 0.6f));
        if ((planFor != null && Matches(planFor, want)) || (planTask != null && !planTask.IsCompleted))
            return;
        planFor = want;
        planTask = Task.Run(() => Plan(want.Ball, want.Shot, want.Power, 0f, want.AimX, want.Forehand));
    }

    bool FinishedPlan()
    {
        try
        {
            planTask.Wait();
            return true;
        }
        catch (System.AggregateException)
        {
            return false;
        }
    }

    static bool Matches(PlanFor a, PlanFor b) =>
        a.Shot == b.Shot && a.Forehand == b.Forehand && Mathf.Abs(a.Power - b.Power) < 0.08f
        && a.Ball.Position.DistanceTo(b.Ball.Position) < 0.15f // a frame's flight either side
        && a.Ball.Velocity.DistanceTo(b.Ball.Velocity) < 1f
        && a.Ball.Spin.DistanceTo(b.Ball.Spin) < 40f;

    void ReadButtons()
    {
        Button now = Input.IsMouseButtonPressed(MouseButton.Left) ? Button.Left
                   : Input.IsMouseButtonPressed(MouseButton.Right) ? Button.Right
                   : Button.None;
        if (now == held)
            return;
        held = now;
        pressedAt = Time.GetTicksMsec();
    }

    float PowerSince(ulong from, ulong to) => Mathf.Clamp((to - from) / 1000f / FullChargeTime, 0f, 1f);

    // The stroke the buttons ask for right now and its strength 0..1: the button down and how
    // long it has been held.
    (Button button, float power) Asked() => AskedAt(0f);

    // The same, eta seconds of play from now if the buttons stay as they are (slow motion
    // stretches it in real time, which is what the press is timed in).
    (Button button, float power) AskedAt(float eta)
    {
        if (held == Button.None)
            return (Button.None, 0f);
        ulong ms = Time.GetTicksMsec() + (ulong)(Mathf.Max(eta, 0f) / (float)Engine.TimeScale * 1000f);
        return (held, PowerSince(pressedAt, ms));
    }

    // Topspin of a ball coming at the player (rad/s, negative for backspin).
    static float TopspinOf(BallState ball) => ball.Spin.X * Mathf.Sign(ball.Velocity.Z);

    // Which stroke the button makes against this ball, met where it is.
    static Shot Choose(Button button, float power, BallState ball)
    {
        float topspin = TopspinOf(ball);
        float height = ball.Position.Y - TableSpec.Height;
        bool overTable = ball.Position.Z < TableSpec.HalfLength - 0.05f;
        return button switch
        {
            Button.Left when height > 0.42f && topspin > -40f && power > 0.5f => Shot.Smash,
            Button.Left when overTable && height < 0.3f => Shot.Flick,
            Button.Left => Shot.Drive,
            Button.Right => overTable ? Shot.Cut : Shot.Chop,
            _ => topspin < -40f && height < 0.35f ? Shot.Push : Shot.Block,
        };
    }

    static StrokeStyle StyleOf(Shot shot) => shot switch
    {
        Shot.Block => StrokeStyle.Block,
        Shot.Push or Shot.Cut or Shot.Chop => StrokeStyle.Push,
        Shot.Smash => StrokeStyle.Smash,
        _ => StrokeStyle.Drive,
    };

    // The racket's motion at contact for the stroke: speed towards the opponent and upwards
    // (negative brushes down), m/s, and how deep to aim past the net (m).
    (float drive, float up, float depth) Swing(Shot shot, float power, BallState ball, bool forehand)
    {
        float topspin = TopspinOf(ball);
        // Taken below the net's height, a topspin stroke has to rise more steeply to clear it.
        float low = Mathf.Clamp((TableSpec.NetTop + 0.1f - ball.Position.Y) / 0.2f, 0f, 1f);
        switch (shot)
        {
            case Shot.Block:
                // The face set against the ball, closed over topspin: its own pace comes back.
                return (1.0f, topspin > 40f ? -0.4f : 0f, 0.85f);
            case Shot.Push:
                return (1.6f, -1.2f, 0.6f);
            case Shot.Cut:
                // Under the ball over the table: short and soft, or long and heavy.
                return (Mathf.Lerp(1.2f, 2.6f, power), -Mathf.Lerp(0.8f, 2.4f, power), Mathf.Lerp(0.5f, 1.0f, power));
            case Shot.Chop:
            {
                // From behind the table, steeply down and forward under the ball.
                float speed = Mathf.Lerp(3.5f, 6.5f, power) * Mathf.Lerp(0.85f, 1.15f, Skill);
                const float down = 0.8f; // about 46 degrees below level
                return (speed * Mathf.Cos(down), -speed * Mathf.Sin(down), Mathf.Lerp(0.8f, 1.0f, power));
            }
            case Shot.Flick:
            {
                // Mostly wrist over the table: well short of a full drive, rising to lift backspin.
                float speed = Mathf.Lerp(3.5f, 8f, power) * Mathf.Lerp(0.85f, 1.15f, Skill);
                float rise = (topspin < -40f ? 0.85f : 0.55f) + 0.3f * low;
                return (speed * Mathf.Cos(rise), speed * Mathf.Sin(rise), Mathf.Lerp(0.65f, 1.0f, power));
            }
            case Shot.Smash:
            {
                float speed = Human.SmashRacketSpeed(Skill) * Mathf.Lerp(0.7f, 1f, power);
                const float dip = 0.06f;
                return (speed * Mathf.Cos(dip), -speed * Mathf.Sin(dip), 1.0f);
            }
            default:
            {
                // A topspin drive at the racket speed players of this level measure (Human), the
                // backhand a little slower; steeper against backspin, and a little less hard into
                // a fast ball, which brings its own pace; flatter against heavy topspin (a
                // counter-drive, the closed face riding over the spin).
                float full = Human.DriveRacketSpeed(Skill) * (forehand ? 1f : 0.88f);
                float speed = full * Mathf.Lerp(0.4f, 1f, power);
                speed = Mathf.Max(speed - 0.3f * Mathf.Abs(ball.Velocity.Z), 0.45f * speed);
                float rise = (topspin < -40f ? 0.9f : topspin > 150f ? 0.35f : 0.6f) + 0.35f * low;
                return (speed * Mathf.Cos(rise), speed * Mathf.Sin(rise), Mathf.Lerp(0.75f, 1.05f, power));
            }
        }
    }

    // The racket's motion and face for a stroke against this ball: the stroke's swing, made one
    // that can land (Attack, Landable), with the face aimed at the stroke's depth or, when no
    // aim lands, at the middle of the faces that do, the angle with the most room for error.
    // Touches no nodes, so it can run on a worker thread.
    // The player's racket from the collection (Equipment), or the standard one: its faces for
    // the contact, its colours on the paddle.
    void UseEquipment()
    {
        if (Equipment.Version == equipmentVersion)
            return;
        equipmentVersion = Equipment.Version;
        Racket racket = Equipment.For(Equipment.Player);
        forehandFace = racket?.Face(true) ?? RacketFace.Standard;
        backhandFace = racket?.Face(racket.Penhold) ?? RacketFace.Standard;
        paddle.ShowRacket(racket);
    }

    (Vector3 racket, Vector2 face) Plan(BallState ball, Shot shot, float power, float across, float aimX, bool forehand)
    {
        Striker? was = planning;
        planning = new Striker(BodyMass, forehand ? forehandFace : backhandFace);
        try
        {
            return PlanWith(ball, shot, power, across, aimX, forehand);
        }
        finally
        {
            planning = was;
        }
    }

    (Vector3 racket, Vector2 face) PlanWith(BallState ball, Shot shot, float power, float across, float aimX, bool forehand)
    {
        var (drive, up, depth) = Swing(shot, power, ball, forehand);
        var racket = new Vector3(across, up, -Side * drive);
        // The depth the stroke wants, then a little shorter and a little longer.
        Vector2[] aims =
        {
            new(aimX, -Side * Mathf.Clamp(depth, 0.4f, 1.1f)),
            new(aimX, -Side * Mathf.Clamp(depth - 0.25f, 0.4f, 1.1f)),
            new(aimX, -Side * Mathf.Clamp(depth + 0.15f, 0.4f, 1.1f)),
        };
        float yaw = Mathf.Clamp(Mathf.Atan2(-Side * (aims[0].X - ball.Position.X), -Side * (aims[0].Y - ball.Position.Z)), -0.9f, 0.9f);
        bool attack = shot is Shot.Drive or Shot.Flick or Shot.Smash;
        float slowest = Slowest(shot, power, ball, drive, up, forehand);
        // An attack that cannot land with topspin at all is played back any way that lands.
        Vector3? attacking = attack ? Attack(ball, racket, slowest, yaw) : null;
        attack = attacking != null;
        racket = attacking ?? Landable(ball, racket, slowest, yaw);
        BallState aimed = ShotSolver.Stroke(ball, racket, Side, aims, 1, Vector2.Zero, out Vector2 face, out bool legal, Hitter);
        if (attack && aimed.Spin.X * Side >= 0f)
            legal = false; // an attack keeps its topspin
        if (!legal && MiddleLanding(ball, racket, yaw, attack, out Vector2 middle))
            face = middle;
        return (racket, face);
    }

    // The middle of the widest run of face angles that land a racket moving like this legally
    // (with topspin if asked).
    bool MiddleLanding(BallState ball, Vector3 racket, float yaw, bool topspin, out Vector2 face)
    {
        const float step = 0.04f;
        float runStart = 0f, bestStart = 0f, bestLength = -1f;
        bool inRun = false;
        for (float pitch = -1.25f; pitch <= 1.25f + step * 0.5f; pitch += step)
        {
            BallState after = ShotSolver.Hit(ball, new Vector2(pitch, yaw), racket, Side, Hitter);
            bool lands = (!topspin || after.Spin.X * Side < 0f) && ShotSolver.IsLegal(after.Position, after.Velocity, after.Spin, 1);
            if (lands && !inRun)
                runStart = pitch;
            inRun = lands;
            if (lands && pitch - runStart > bestLength)
            {
                bestLength = pitch - runStart;
                bestStart = runStart;
            }
        }
        face = new Vector2(bestStart + bestLength * 0.5f, yaw);
        return bestLength >= 0f;
    }

    // An attacking stroke goes as fast as it can and still land with topspin. When the planned
    // swing cannot (a ball taken low or close, or against heavy topspin, where the only face
    // left would open and send the incoming spin back as backspin), the player brushes up more
    // steeply, as for a loop, or holds back, never below a short press's swing: of those, the
    // one that sends the ball fastest. Where the ball allows, a longer press sends it faster;
    // where only a steep loop lands it, the extra racket speed goes into spin. Null when no
    // swing lands it with topspin.
    Vector3? Attack(BallState ball, Vector3 racket, float slowest, float yaw)
    {
        float speed = new Vector2(racket.Y, racket.Z).Length();
        float planned = Mathf.Atan2(racket.Y, -Side * racket.Z);
        Vector3 best = racket;
        float bestSpeed = -1f;
        int eases = slowest < 0.999f ? 4 : 1;
        for (int e = 0; e < eases; e++)
        {
            float eased = speed * Mathf.Lerp(1f, slowest, e / 3f);
            for (float rise = planned; rise <= 1.17f; rise += 0.08f)
            {
                var r = new Vector3(racket.X, eased * Mathf.Sin(rise), -Side * eased * Mathf.Cos(rise));
                float found = FastestLanding(ball, r, yaw, true);
                if (found < 0f)
                    continue;
                if (found > bestSpeed)
                {
                    best = r;
                    bestSpeed = found;
                }
                break; // steeper only takes pace off
            }
        }
        return bestSpeed > 0f ? best : null;
    }

    // A stroke no face angle can land is changed the way a player would: a swing too fast for
    // the ball taken this low or close holds back, in steps, never below a short press's swing;
    // one too soft to carry over the net drives through harder, by up to 3 m/s, brushing less
    // if it must.
    Vector3 Landable(BallState ball, Vector3 racket, float slowest, float yaw)
    {
        if (FastestLanding(ball, racket, yaw, false) > 0f)
            return racket;
        for (int step = 1; step <= 6 && slowest < 0.999f; step++)
        {
            Vector3 eased = racket * Mathf.Lerp(1f, slowest, step / 6f);
            if (FastestLanding(ball, eased, yaw, false) > 0f)
                return eased;
        }
        for (float extra = 0.5f; extra <= 3f; extra += 0.5f)
        {
            foreach (float brush in new[] { 1f, 0.5f })
            {
                var harder = new Vector3(racket.X, racket.Y * brush, racket.Z - Side * extra);
                if (FastestLanding(ball, harder, yaw, false) > 0f)
                    return harder;
            }
        }
        return racket;
    }

    // How fast the fastest legal return a racket moving like this can make leaves, over the
    // face angles (only those giving topspin if asked); -1 if no face lands it. A face counts
    // only if the next one lands too: a narrower window than that no player could hit. A scan,
    // much cheaper than solving the face for each aim, and it skips any face that could not
    // beat the best so far before flying it.
    float FastestLanding(BallState ball, Vector3 racket, float yaw, bool topspin)
    {
        const float step = 0.05f;
        bool Lands(float pitch, out float leaving)
        {
            BallState after = ShotSolver.Hit(ball, new Vector2(pitch, yaw), racket, Side, Hitter);
            leaving = after.Velocity.Length();
            return (!topspin || after.Spin.X * Side < 0f) // topspin: Spin.X against the play
                && ShotSolver.IsLegal(after.Position, after.Velocity, after.Spin, 1);
        }
        float best = -1f;
        bool lastLanded = false;
        for (float pitch = -1.25f; pitch <= 1.25f; pitch += step)
        {
            BallState after = ShotSolver.Hit(ball, new Vector2(pitch, yaw), racket, Side, Hitter);
            float leaving = after.Velocity.Length();
            if (leaving <= best && !lastLanded)
                continue;
            bool landed = Lands(pitch, out leaving);
            if (landed && leaving > best && (lastLanded || Lands(pitch + step, out _)))
                best = leaving;
            lastLanded = landed;
        }
        return best;
    }

    // How far a swing may hold back: to a short press's swing for the same stroke.
    float Slowest(Shot shot, float power, BallState ball, float drive, float up, bool forehand)
    {
        if (power <= 0f || shot is Shot.Block or Shot.Push)
            return 1f;
        var (slowDrive, slowUp, _) = Swing(shot, 0f, ball, forehand);
        return Mathf.Min(new Vector2(slowDrive, slowUp).Length() / new Vector2(drive, up).Length(), 1f);
    }

    // Seconds until the ball passes closest to the blade after bouncing on our half, replaying
    // its flight from as soon as it comes our way, with the ball there; -1 if it is not coming.
    float TimeToContact(out BallState? at)
    {
        at = null;
        if (TargetBall == null || !TargetBall.Simulating || TargetBall.State.Velocity.Z * Side <= 0f)
            return -1f;

        BallState s = TargetBall.State;
        bool bounced = TargetBall.HittableBy == Side;
        float dt = Ball.SubStepTime;
        float closest = float.MaxValue;
        float time = -1f;
        for (float t = 0f; t < 1.2f; t += dt)
        {
            if (bounced && s.Position.Z * Side >= MinReachZ)
            {
                float offset = new Vector2(s.Position.X - Position.X, s.Position.Y - Position.Y).Length();
                if (offset < closest)
                {
                    closest = offset;
                    time = t;
                    at = s;
                }
            }
            BallEvent happened = Ball.Step(ref s, dt, false);
            if (happened is BallEvent.Floor or BallEvent.Net || s.Position.Z * Side > restZ + 0.1f)
                break;
            if (happened == BallEvent.Table && TableSpec.SideOf(s.Position.Z) == Side) // a serve bounces on the far half first
            {
                if (bounced)
                    break; // a second bounce: not ours to play
                bounced = true;
            }
        }
        return time;
    }

    public bool TryHit(Ball ball)
    {
        Vector3 contact = ball.State.Position;
        float offset = new Vector2(contact.X - Position.X, contact.Y - Position.Y).Length();
        if (contact.Z < MinReachZ || offset > Reach)
        {
            closestOffset = float.MaxValue;
            return false;
        }
        // Strike at the ball's closest approach to the blade centre, not where it enters reach.
        if (offset < closestOffset && contact.Z < restZ + 0.1f)
        {
            closestOffset = offset;
            return false;
        }
        offset = closestOffset;
        closestOffset = float.MaxValue;

        // Decided now, from the buttons as they are at the touch.
        ReadButtons();
        var (button, power) = Asked();
        Shot shot = Choose(button, power, ball.State);
        // The mouse's sideways motion swings the racket across a little (less on a block or push)
        // and turns the aim.
        float across = Mathf.Clamp(SwingVelocity.X, -3f, 3f) * (shot is Shot.Block or Shot.Push ? 0.3f : 0.6f);
        float aimX = Mathf.Clamp(contact.X * 0.4f + SwingVelocity.X * 0.08f, -0.6f, 0.6f);
        // The stroke worked out while the ball came, if it was for this contact (waiting for the
        // rest of it if it is not quite done, still sooner than starting again); otherwise now.
        Vector3 racket;
        Vector2 face;
        var want = new PlanFor(shot, power, ball.State, paddle.Forehand, aimX);
        if (planTask != null && planFor != null && Matches(planFor, want) && FinishedPlan())
        {
            (racket, face) = planTask.Result;
            racket.X = across;
            // Turned for the mouse's aim, which the plan left out: a sideways shift over the
            // ball's way to the landing.
            face.Y = Mathf.Clamp(face.Y - Side * (aimX - planFor.AimX) / (Mathf.Abs(contact.Z) + 0.9f), -0.9f, 0.9f);
        }
        else
            (racket, face) = Plan(ball.State, shot, power, across, aimX, paddle.Forehand);
        planFor = null;
        planTask = null;

        // The face is less exact on hard strokes (attacks more than the cut and chop, which are
        // played for control), off-centre contact and heavy incoming spin.
        float effort = shot switch { Shot.Block or Shot.Push => 0f, Shot.Cut or Shot.Chop => 0.6f * power, _ => power };
        float wobble = 0.012f + 0.035f * effort * effort * effort + 0.04f * offset / Reach + ball.State.Spin.Length() / 25000f;
        face += new Vector2((GD.Randf() * 2f - 1f) * wobble, (GD.Randf() * 2f - 1f) * wobble * 0.7f);

        BallState before = ball.State;
        BallState after = ShotSolver.Hit(ball.State, face, racket, Side, Hitter);
        ball.Strike(Side, contact, after.Velocity, after.Spin);
        paddle.Strike(face, racket, StyleOf(shot));
        DebugOverlay.Log($"  you {shot.ToString().ToLower()}{(button == Button.None ? "" : $" ({button.ToString().ToLower()} button, {power * 100f:F0}%)")}: racket ({racket.X:F1}, {racket.Y:F1}, {racket.Z:F1}) m/s, face pitch {Mathf.RadToDeg(face.X):F0}° yaw {Mathf.RadToDeg(face.Y):F0}°, ball {after.Velocity.Length():F1} m/s {-after.Spin.X / Mathf.Tau:F0} rev/s");
        DebugOverlay.Log("    " + Ball.Describe(Ball.Analyse(before, ShotSolver.FaceNormal(face, Side), racket, Ball.RubberGrip, Hitter)));

        // A stroke uses up the press: a button still held starts charging again.
        pressedAt = Time.GetTicksMsec();
        return true;
    }


    // The stroke the buttons will play and how hard, at the bottom of the screen.
    void BuildHud()
    {
        hud = new CanvasLayer { Name = "StrokeHud", Layer = 5 };
        AddChild(hud);
        var box = new VBoxContainer
        {
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 1f, AnchorBottom = 1f,
            OffsetLeft = -110f, OffsetRight = 110f, OffsetTop = -128f, OffsetBottom = -72f,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        hud.AddChild(box);
        shotLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        shotLabel.AddThemeFontSizeOverride("font_size", 20);
        shotLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        shotLabel.AddThemeConstantOverride("outline_size", 6);
        box.AddChild(shotLabel);
        chargeBar = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = false, CustomMinimumSize = new Vector2(220f, 12f), MouseFilter = Control.MouseFilterEnum.Ignore };
        chargeBar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0f, 0f, 0f, 0.5f), CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4 });
        chargeFill = new StyleBoxFlat { CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4 };
        chargeBar.AddThemeStyleboxOverride("fill", chargeFill);
        box.AddChild(chargeBar);
    }

    void ShowHud(Button button, float power, Shot shot)
    {
        Color colour = button switch
        {
            Button.Left => new Color(1f, 0.6f, 0.2f),   // topspin, as the ball's orange glow
            Button.Right => new Color(0.35f, 0.65f, 1f), // backspin, as its blue glow
            _ => new Color(1f, 1f, 1f, 0.6f),
        };
        shotLabel.Text = button == Button.None ? "block / push" : $"{shot.ToString().ToUpper()}  {(power < 0.34f ? "slow" : power < 0.67f ? "medium" : "fast")}";
        shotLabel.AddThemeColorOverride("font_color", colour);
        chargeBar.Value = power * 100f;
        chargeBar.Visible = button != Button.None;
        chargeFill.BgColor = colour;
    }
}
