using Godot;
using System.Collections.Generic;

// The NPC player on the far side, right-handed. It predicts the ball's flight after it bounces
// on its half, picks forehand or backhand and the stroke (drive, push, block or smash), moves its
// blade (at limited speed) to meet the ball near the top of the bounce with a timed backswing,
// and returns it to a random spot with accuracy depending on Skill and on how hard the incoming
// ball is. It tires through rallies and the match and recovers in the breaks, at a rate set by
// Stamina (Fatigue).
//
// Its body follows basic table tennis technique: feet a little wider than the shoulders, knees
// always bent; for the forehand the right foot back and the feet at 45 degrees to the side of
// the table, for the backhand the right foot a little forward and the body nearly square; the
// torso turning back with the backswing and through after contact while the weight moves from
// the back foot to the front one, and back to the ready position after every stroke. It serves
// from behind the end line, the ball resting on the open free hand and tossed straight up.
//
// It thinks in its own frame, where it always plays from the far end (-Z). Turned half round
// the table's centre, the same NPC plays from the near end (NPC vs NPC mode).
public partial class Opponent : Node3D, IBallHitter
{
    const int OwnSide = -1;

    [Export] public Ball TargetBall;
    [Export] public string Tag = "cpu";       // its name in the log
    [Export(PropertyHint.Range, "0,1")] public float Skill = 0.6f;
    // How fast it tires and recovers, 0 .. 1 (Fatigue): a tired NPC reacts later, moves and
    // swings slower and misses more.
    [Export(PropertyHint.Range, "0,1")] public float Stamina = 0.6f;
    [Export(PropertyHint.Range, "35,140")] public float BodyMass = Human.DefaultBodyMass; // kg: the hand on the racket adds to what the ball meets (Human.ImpactShare)
    [Export] public float Speed = 3.2f;       // blade speed in m/s
    [Export] public float Reach = 0.15f;
    [Export] public float MinReachZ = -0.8f;  // how far over the table it takes the ball, leaning in
    [Export] public float ShortBallReachZ = -0.55f; // stretching in for a ball that would bounce twice
    [Export] public float TossSpeed = 2.4f;   // the ball rises about 30 cm
    [Export] public float ServeSwingSpeed = 5f;
    [Export] public float ForwardSwingTime = Human.ForwardSwingTime;
    [Export] public float BackhandFromX = 0.05f; // balls further to world +X (its left) are backhands

    public int Side { get; private set; } = -1;
    public Paddle Paddle => paddle;
    public bool HasServe = true; // this NPC serves the coming point
    // Where the server holds the ball and strikes it: in front of the body and a little to its
    // right, so the ball is in front of the chest within easy reach of the free hand. The ball is
    // carried with the body while it walks to its serving place (behind the end line, a little
    // left of the centre line) during the hold, and tossed from there.
    public Vector3 ServePoint
    {
        get
        {
            if (serveMotion != null)
                return World(serveMotion.BallAt(0f)); // on the palm in the serve motion's first step
            Vector3 at = Own(body.GlobalPosition) - BodyFromServePoint;
            return World(new Vector3(Mathf.Clamp(at.X, -0.5f, 0.5f), 1f, at.Z));
        }
    }
    const float ServeSpotX = 0.15f;
    static readonly Vector3 BodyFromServePoint = new(0.15f, 0f, -0.5f);
    static readonly Vector3 RacketWaitsFromServePoint = new(-0.36f, -0.05f, -0.05f);

    // Body pose. The right shoulder goes where the paddle's arm needs it (Paddle.RestShoulderPoint:
    // from the grip up the forearm and upper arm), so the wrist keeps a natural angle; hips drop
    // and the torso leans to bring the shoulder there.
    const float TorsoLength = 0.55f;      // hips to shoulders
    const float StandingHips = 0.86f;     // knees stay slightly bent even when upright (about 30 degrees)
    const float LowestHips = 0.78f;       // knees bent about 55 degrees for a low ball
    const float MinLean = 0.15f;          // radians forward
    const float MaxLean = 0.8f;
    // The body stays back from the end line far enough that the hips, however low, clear the
    // table's edge (the pelvis is 0.165 deep, the thighs leaning forward with the stance).
    const float ClosestBodyZ = -(TableSpec.HalfLength + 0.26f);
    const float HipHalfWidth = 0.1f;      // where the legs join the hips
    const float FootHalfWidth = 0.23f;    // feet a little wider than the shoulders
    const float WeightTravel = 0.06f;     // how far the hips move over a foot with the weight
    // Footwork: the feet stay planted, and the body moves in short, explosive pushes off both
    // feet together (side to side, towards or away from the table), not one foot after the
    // other, which would cost the balance. A ball too far away for that in the time left is
    // reached by a lunge: the leg on that side reaches out, the body leaning and dropping over
    // it, the other foot planted. The hips lead the feet only a little.
    const float MoveTrigger = 0.06f;      // the feet move once the stance is this far out of place
    const float ShuffleTime = 0.16f;      // one push off both feet
    const float MaxShuffle = 0.35f;       // how far one push carries
    const float FeetLead = 0.12f;         // how far a push may put the feet ahead of the body
    const float ShuffleLift = 0.025f;     // both feet just leave the floor
    const float LungeTime = 0.18f;
    const float MaxLunge = 0.6f;          // how far the reaching foot goes
    const float LungeHips = 0.55f;        // how low the hips may drop over a lunging leg
    const float MaxSpan = 1.1f;           // widest the feet ever stand apart
    const float HipsOverFeet = 0.4f;      // safety only: the feet step to stay under the hips
    const float LungeHipsOverFeet = 0.45f; // in a lunge the hips may hang back over the planted leg
    const float ToesOut = 0.27f;          // toes turned out about 15 degrees
    const float TurnPerMetre = 0.12f;     // a foot turned 0.5 rad off the stance counts as 6 cm out of place
    // Shoulders in the upper body's space (pivot at the hips) with the waist untwisted. The free
    // left hand is held up in front for balance and holds the ball on its open palm before a serve.
    static readonly Vector3 RightShoulder = new(-0.2f, TorsoLength, 0f);
    static readonly Vector3 LeftShoulder = new(0.2f, TorsoLength, 0f);
    // The waist joint between the hips and the chest. The stroke's body turn is mostly the chest
    // twisting there (about the vertical, so the shoulders stay level however far the body
    // leans); the hips, carrying the legs, follow with a little of it.
    const float WaistHeight = 0.22f;
    const float HipsShareOfTurn = 0.3f;
    static readonly Vector3 Waist = new(0f, WaistHeight, 0f);
    const float ArmReach = 0.76f;          // shoulder to the middle of the racket, the arm straight (0.57 + 0.2)

    // Where the racket can really be: where the NPC wants it (the blade), but no further than the
    // arm reaches from the shoulder. A ball further off needs the body to get there first.
    Vector3 Reachable(Vector3 wanted)
    {
        Vector3 shoulder = untwistedHips * RightShoulder;
        Vector3 reach = wanted - shoulder;
        return reach.Length() > ArmReach ? shoulder + reach.Normalized() * ArmReach : wanted;
    }
    // The free hand hangs from the left shoulder, below it and in front (facing space: x left of
    // the body, y up, z forward), the same whatever the lean, so it stays clear of the table.
    static readonly Vector3 LeftHandFromShoulder = new(0.1f, -0.34f, 0.24f);

    Basis toWorld = Basis.Identity; // own frame to world: turned half round for the near end
    Basis toOwn = Basis.Identity;
    Node3D blade;
    Paddle paddle;
    Node3D body;
    Node3D upperBody;
    Node3D chest;                 // above the waist joint: shoulders, arms and head
    float waistTurn;              // the stroke's turn at the waist and hips, radians, positive to the left
    Leg[] legs;
    Arm rightArm;
    Arm leftArm;
    float hipsHeight = StandingHips;
    float lean = MinLean;
    float hipsYaw;                // stance: hips turned to the right, radians
    Vector3 leftHand;             // world space; everything below is in the own frame
    readonly Vector3[] feet = new Vector3[2]; // on the ground; 0 is the left foot (+X)
    readonly Vector3[] toes = new Vector3[2]; // the way each foot points
    bool feetPlaced;
    enum Footwork { Planted, Shuffle, Lunge }
    Footwork footwork;
    float footProgress;
    readonly Vector3[] footFrom = new Vector3[2], footTo = new Vector3[2];
    readonly Vector3[] toesFrom = new Vector3[2], toesTo = new Vector3[2];
    Vector3 readyPosition;
    Vector3 bladeTarget;
    Vector3 tossPoint;
    bool tossing;
    bool topspinServe;
    // How much spin the serve is for: the serve motion's (its contact shaped in the Pose Lab), or
    // as it comes (the NPC's own mix) for its own serves.
    PoseSequence.SpinLevel serveSpin;
    PoseSequence.Brush serveBrush; // a sidespin brush turns the serve's spin mostly about the vertical

    // A serve from the motion database (ServeMotion), picked at random for each serve: the
    // body stands in its first step while holding the ball, then plays it (PoseRig lays each
    // moment on the body), tossing and striking the ball at the motion's moments.
    ServeMotion serveMotion;
    PoseRig poseRig;
    bool playingMotion;
    float motionTime;
    bool motionTossed, motionStruck;
    static readonly HashSet<string> reportedSkips = new();

    // The ready stance (ReadyStance, made in the Pose Lab): between strokes, from when the racket
    // comes back from the finish of the follow-through and while no ball is on its way, the body
    // eases into it (a player's recovery, about a quarter second) and its steps play round and
    // round; when a ball comes the body eases back out. The NPC's own body (feet, hips, arm)
    // keeps working underneath all along and the stance is laid over it, so it carries on from
    // where it was. The feet lift a little while they change place.
    const float ReadyInTime = 0.25f;
    const float ReadyOutTime = 0.15f;
    const float ReadyStepLift = 0.03f;
    float readyWeight;
    float readyClock;
    PoseSequence readyMotion;     // the ready motion picked (at random) this time
    bool readyLaid;
    (Transform3D Body, Transform3D Upper, Transform3D Chest, Transform3D Paddle, float LeftOpen) ownPose;

    float replanTimer;
    float interceptEta = -1f;
    float closestDistance = float.MaxValue;
    StrokeStyle? plannedStroke;   // decided when the ball's arrival is first predicted
    int rallyStrokes;             // its strokes in this point
    float reactTimer;             // still taking in a ball the other player has just hit
    bool ballWasComing;
    Vector3 misread;              // how far off its first read of where to meet the ball is, from pace and direction
    Vector3 spinMisread;          // how far off its first read of the ball's spin is (rad/s)
    float readEta = -1f;          // time to the meeting at that first read
    Vector3 bodyVelocity;        // own frame, across the floor

    // Its racket from the collection (Equipment, by this node's name), or the standard one: the
    // face a stroke is struck with is the side it plays (a serve is forehand).
    RacketFace forehandFace = RacketFace.Standard, backhandFace = RacketFace.Standard;
    int equipmentVersion = -1;
    Striker Hitter => new(BodyMass, paddle.Forehand ? forehandFace : backhandFace);
    Striker ServeHitter => new(BodyMass, forehandFace);

    void UseEquipment()
    {
        if (Equipment.Version == equipmentVersion)
            return;
        equipmentVersion = Equipment.Version;
        Racket racket = Equipment.For(Name);
        forehandFace = racket?.Face(true) ?? RacketFace.Standard;
        backhandFace = racket?.Face(racket.Penhold) ?? RacketFace.Standard;
        paddle.ShowRacket(racket);
    }

    // The stroke it played last and what it was for (the match's slow-motion captions).
    public enum StrokeRole { Serve, Attack, Defence, Control }
    public string LastStroke { get; private set; } = "";
    public StrokeRole LastRole { get; private set; }

    // How tired it is: work in a rally tires it, the breaks between points and games rest it.
    readonly Fatigue fatigue = new();
    public Fatigue Fatigue => fatigue;
    float LegPush => Human.PushAcceleration(Skill) * fatigue.LegFactor;
    float TopSpeed => Human.TopSpeed * fatigue.LegFactor;

    // The break before a point (Fatigue.PointBreak) or a game (Fatigue.GameBreak).
    public void Rest(float seconds)
    {
        fatigue.Stamina = Stamina;
        fatigue.Rest(seconds);
        if (fatigue.Level >= 0.01f)
            DebugOverlay.Log($"  {Tag} {fatigue}");
    }

    // Fresh, for a new match.
    public void Freshen() => fatigue.Clear();

    void Work(float effort)
    {
        fatigue.Stamina = Stamina;
        fatigue.Work(effort);
    }

    bool HoldingServe => HasServe && TargetBall != null && !TargetBall.Simulating && !tossing;

    // For the Pose Lab in the game (F2): the body's parts, and the turn from its own frame to the world.
    public (Node3D Body, Node3D Upper, Node3D Chest, Leg[] Legs, Arm RightArm, Arm LeftArm, Paddle Paddle) Rig =>
        (body, upperBody, chest, legs, rightArm, leftArm, paddle);
    public Basis ToWorld => GlobalBasis.Orthonormalized();

    // The table is the same turned half round, and so is the ball's flight and spin.
    Vector3 Own(Vector3 world) => toOwn * world;
    Vector3 World(Vector3 own) => toWorld * own;
    BallState Own(BallState s) => new(Own(s.Position), Own(s.Velocity), Own(s.Spin));

    public override void _Ready()
    {
        toWorld = GlobalBasis.Orthonormalized();
        toOwn = toWorld.Transposed();
        Side = toWorld.Z.Z > 0f ? OwnSide : -OwnSide;
        blade = GetNode<Node3D>("Blade");
        paddle = blade.GetNode<Paddle>("Paddle");
        paddle.Driven = true; // its arm places the racket
        paddle.BackswingRate = 1f / Human.BackswingTime; // a human backswing, not a snap
        body = GetNode<Node3D>("Body");
        upperBody = body.GetNode<Node3D>("Upper");
        chest = upperBody.GetNode<Node3D>("Chest");
        legs =new[] { body.GetNode<Leg>("LegLeft"), body.GetNode<Leg>("LegRight") };
        rightArm = GetNode<Arm>("ArmRight");
        leftArm = GetNode<Arm>("ArmLeft");
        readyPosition = Own(blade.GlobalPosition);
        bladeTarget = readyPosition;
        leftHand = upperBody.GlobalTransform * LeftShoulder; // the waist is untwisted at the start
        poseRig = new PoseRig(Rig, toWorld);
    }

    // Drops a toss or a planned stroke, for a new point.
    public void Reset()
    {
        poseLogged.Clear(); // the pose check logs afresh each point
        tossing = false;
        interceptEta = -1f;
        plannedStroke = null;
        bladeTarget = RestPoint;
        rallyStrokes = 0;
        if (playingMotion)
            EndServeMotion();
        serveMotion = HasServe && TargetBall != null ? PickServeMotion() : null;
        // The new serve waits in its first step (the ball on the palm), not where the last one
        // ended: the motion's clock starts again.
        motionTime = 0f;
        motionTossed = motionStruck = false;
    }

    // One of the database's serve motions at random (null when there is none: the built-in serve).
    ServeMotion PickServeMotion()
    {
        List<ServeMotion> all = ServeMotion.All(out List<string> skipped);
        foreach (string why in skipped)
            if (reportedSkips.Add(why))
                DebugOverlay.Log($"  serve motion left out: {why}");
        if (all.Count == 0)
            return null;
        ServeMotion picked = all[GD.RandRange(0, all.Count - 1)];
        DebugOverlay.Log($"  {Tag} serves with the motion {picked} (1 of {all.Count})");
        return picked;
    }

    // The serve motion on the body: its first step while the ball is held, then played at real
    // speed from BeginServe, the ball in the hand until the toss, struck at the contact. The toss
    // and the strike happen at the motion's own moments to the millisecond, not at the frame
    // after (a ball falling at 7 m/s goes 11 cm in a frame): the ball is put where the toss has
    // it by then.
    void PlayServeMotion(float dt)
    {
        if (playingMotion)
            motionTime += dt;
        poseRig.Apply(serveMotion.PoseAt(motionTime));
        untwistedHips = upperBody.GlobalTransform;
        if (!playingMotion)
            return; // holding: the match keeps the ball on the palm (ServePoint)
        if (!motionTossed)
        {
            if (motionTime >= serveMotion.TossAt - 1e-4f)
            {
                BallState flying = serveMotion.Flight(tossVelocity, Mathf.Max(motionTime - serveMotion.TossAt, 0f));
                TargetBall.Hold(World(flying.Position));
                TargetBall.Toss(World(flying.Velocity));
                motionTossed = true;
            }
            else
                TargetBall.Hold(World(serveMotion.BallAt(motionTime)));
        }
        if (motionTossed && !motionStruck && motionTime >= serveMotion.ContactAt - 1e-4f)
        {
            motionStruck = true;
            topspinServe = !serveMotion.Underspin;
            serveSpin = serveMotion.Spin;
            serveBrush = serveMotion.Brush;
            PlanServe();
            BallState atContact = serveMotion.Flight(tossVelocity, serveMotion.ContactAt - serveMotion.TossAt);
            Serve(TargetBall, serveMotion.Name, atContact);
        }
        if (motionTime >= serveMotion.Length && motionStruck)
            EndServeMotion();
    }

    Vector3 tossVelocity; // own frame, for the serve motion being played

    // Where the motion's racket meets the ball: on its face, a ball's radius (and the rubber) in
    // front of the racket's middle, on the side the motion's ball is (or the racket is moving
    // to). Read from the contact step laid on the body for a moment, then the toss is solved to
    // fall onto it.
    void AimServeMotion()
    {
        Dictionary<string, float> atContact = serveMotion.PoseAt(serveMotion.ContactAt);
        poseRig.Apply(atContact);
        Vector3 middle = Own(paddle.GlobalPosition);
        Vector3 normal = Own(paddle.GlobalBasis.Z).Normalized(); // the blade lies in its x-y plane
        Vector3 ballThere = serveMotion.BallAt(serveMotion.ContactAt);
        Vector3 before = PoseRig.RacketAt(serveMotion.PoseAt(serveMotion.ContactAt - 0.05f));
        float side = normal.Dot(ballThere - middle);
        if (Mathf.Abs(side) < 0.005f)
            side = normal.Dot(middle - before); // the face the racket is moving towards
        serveMotion.ContactPoint = middle + normal * Mathf.Sign(side == 0f ? 1f : side) * (Ball.Radius + 0.006f);
        tossVelocity = serveMotion.TossVelocity();
        poseRig.Apply(serveMotion.PoseAt(motionTime));
    }

    // The NPC's own play carries on from where the motion left the body: the feet where they
    // stand, the hips, lean and turn as they are, the racket where it is.
    void EndServeMotion()
    {
        playingMotion = false;
        serveMotion = null;
        for (int i = 0; i < 2; i++)
        {
            feet[i] = Own(legs[i].AnklePoint) - Vector3.Up * Leg.AnkleHeight;
            feet[i].Y = Mathf.Max(feet[i].Y, 0f);
            Vector3 way = Own(legs[i].Toes);
            toes[i] = new Vector3(way.X, 0f, way.Z).Normalized();
        }
        feetPlaced = true;
        footwork = Footwork.Planted;
        bodyVelocity = Vector3.Zero;
        hipsHeight = upperBody.Position.Y;
        lean = upperBody.Rotation.X;
        hipsYaw = -upperBody.Rotation.Y;
        leftHand = leftArm.WristPoint;
        blade.GlobalPosition = paddle.GlobalPosition;
        bladeTarget = RestPoint;
    }

    // Where the racket waits between strokes: the ready stance's, or where it started.
    Vector3 RestPoint => (readyMotion ?? ReadyStance.Current) is { } r ? PoseRig.RacketAt(r.Steps[0].Values) : readyPosition;

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        TakeBackOwnPose();
        UseEquipment();
        if (serveMotion != null && (playingMotion || HoldingServe))
        {
            readyWeight = 0f;
            ServeMotion playing = serveMotion; // (cleared when it ends)
            PlayServeMotion(dt);
            CheckPose(dt, $"serve motion {playing.Name}", playing.PoseAt(motionTime));
            return;
        }
        replanTimer -= dt;
        interceptEta = interceptEta >= 0f ? Mathf.Max(interceptEta - dt, 0f) : -1f;
        if (TargetBall != null && TargetBall.Simulating)
        {
            fatigue.Stamina = Stamina;
            fatigue.Play(dt);
        }

        // A ball just hit towards it takes a reaction time to read before it moves for it. In a
        // rally it reads the opponent's stroke for the spin; a serve is disguised. Its first read
        // is off, the more so when it is caught out of position, and is put right as the ball
        // comes: where to meet it fully, the spin only partly.
        // Only a live ball the other side struck: not one of its own dropping back off the net,
        // nor one whose point is already given.
        bool ballComing = Playable && Own(TargetBall.State.Velocity).Z < 0f;
        if (ballComing && !ballWasComing)
        {
            bool serve = !HasServe && rallyStrokes == 0;
            float caught = OutOfPosition();
            BallState hit = Own(TargetBall.State);
            reactTimer = Human.ReactionTime(Skill, serve) * fatigue.ReactionFactor + Human.OutOfPositionDelay * caught;
            misread = FirstReadError(hit) * (1f + Human.OutOfPositionRead * caught) * fatigue.ReadFactor;
            spinMisread = FirstSpinReadError(hit, serve) * (1f + Human.OutOfPositionRead * caught) * fatigue.ReadFactor;
            readEta = -1f;
        }
        ballWasComing = ballComing;
        reactTimer -= dt;

        if (tossing)
        {
            UpdateToss();
        }
        else if (replanTimer <= 0f && reactTimer <= 0f)
        {
            replanTimer = 0.1f;
            if (PredictIntercept(SpinMisreadNow(), out Vector3 intercept, out interceptEta, out BallState arriving))
            {
                if (readEta < 0f)
                    readEta = interceptEta;
                bladeTarget = intercept + misread * ReadShare(interceptEta);
                plannedStroke ??= StrategyRun.For(Side) is { } planned ? Strategy.StyleOf(planned.Method) : ChooseStroke(arriving);
                paddle.Style = plannedStroke.Value;
            }
            else
            {
                // Back to the ready position between strokes.
                bladeTarget = RestPoint;
                plannedStroke = null;
            }
            // The NPC's right is world -X: balls on that side of the split are forehands.
            paddle.ChooseSide(BackhandFromX - bladeTarget.X);
        }
        // Holding the serve, the racket waits beside the ball.
        if (HoldingServe)
            bladeTarget = Own(ServePoint) + RacketWaitsFromServePoint;

        if (HoldingServe || tossing)
            paddle.ChooseSide(1f); // serves are forehand
        if (HoldingServe)
            paddle.Windup = 1f; // racket held back while the free hand shows the ball
        else if (!tossing)
            paddle.Windup = interceptEta < 0f ? 0f : Mathf.Clamp(interceptEta / ForwardSwingTime, 0f, 1f);

        blade.GlobalPosition = World(Own(blade.GlobalPosition).MoveToward(bladeTarget, (tossing ? ServeSwingSpeed : Speed) * dt));
        UpdateBody(dt);
        UpdateArms(dt);
        LayReadyStance(dt);
        CheckPose(dt, readyLaid ? $"ready stance {readyMotion?.Name}" : $"its own {(plannedStroke ?? paddle.Style).ToString().ToLower()}", readyLaid ? lastLaid : null);
    }

    // The pose as shown, checked a few times a second (PoseCheck): a joint past its range or the
    // body off balance is logged once a point for each thing and what the NPC was playing. Balance
    // only standing still: a body crossing the floor is off its feet by nature.
    const float PoseCheckEvery = 0.2f;
    float poseCheckTimer;
    readonly HashSet<string> poseLogged = new();
    Dictionary<string, float> lastLaid;

    void CheckPose(float dt, string playing, Dictionary<string, float> laid)
    {
        if ((poseCheckTimer -= dt) > 0f || !DebugOverlay.Enabled && !OS.IsDebugBuild())
            return;
        poseCheckTimer = PoseCheckEvery;
        bool forehand = laid != null ? laid.GetValueOrDefault("racket.backhand") < 0.5f : paddle.Forehand;
        bool still = bodyVelocity.Length() < 0.3f && footwork == Footwork.Planted;
        foreach (PoseCheck.Issue issue in PoseCheck.Run(new PoseCheck.Body(Rig, toWorld), forehand, false))
        {
            if (issue.Kind == PoseCheck.Kind.Balance && !still)
                continue;
            if (poseLogged.Add($"{issue.Kind} {playing}"))
                DebugOverlay.Log($"  POSE CHECK {Tag} ({playing}): {(issue.Serious ? "✖" : "!")} {issue.Text}");
        }
    }

    // The ready stance laid over the NPC's own pose, by how far it has eased in.
    void LayReadyStance(float dt)
    {
        // Recovering straight out of the stroke: from when the racket comes back from the finish,
        // not once it is all the way back (in a rally the next ball is read 0.6-0.8 s after the hit).
        bool strokeDone = paddle.Striking ? paddle.Recovering : !followingThrough;
        bool waiting = !tossing && !HoldingServe && plannedStroke == null && interceptEta < 0f
                       && strokeDone && ReadyStance.Current != null;
        float before = readyWeight;
        readyWeight = Mathf.MoveToward(readyWeight, waiting ? 1f : 0f, dt / (waiting ? ReadyInTime : ReadyOutTime));
        if (readyWeight <= 0f)
            return;
        if (before <= 0f || readyMotion == null)
        {
            readyClock = 0f;
            readyMotion = ReadyStance.Pick() ?? ReadyStance.Current; // one of the ticked, each time
        }
        else if (TargetBall == null || !TargetBall.Dead)
            readyClock += dt; // held still once the point is over: nothing to get ready for
        Dictionary<string, float> own = poseRig.Read(), ready = ReadyStance.At(readyMotion, readyClock);
        if (ready == null)
            return;
        Dictionary<string, float> laid = PoseSequence.Blend(own, ready, Mathf.SmoothStep(0f, 1f, readyWeight), turnRacket: true);
        lastLaid = laid;
        // Feet changing place are lifted off the floor on the way, not slid along it.
        foreach (string foot in new[] { "lfoot", "rfoot" })
        {
            float apart = new Vector2(own[foot + ".x"] - ready.GetValueOrDefault(foot + ".x"), own[foot + ".z"] - ready.GetValueOrDefault(foot + ".z")).Length();
            laid[foot + ".lift"] = laid.GetValueOrDefault(foot + ".lift") + Mathf.Clamp(apart / 0.1f, 0f, 1f) * ReadyStepLift * Mathf.Sin(Mathf.Pi * readyWeight);
        }
        ownPose = (body.GlobalTransform, upperBody.Transform, chest.Transform, paddle.GlobalTransform, leftArm.Open);
        poseRig.Apply(laid);
        readyLaid = true;
    }


    // The NPC's own pose put back before it works out the next frame (the ready stance laid over
    // it is only shown).
    void TakeBackOwnPose()
    {
        if (!readyLaid)
            return;
        readyLaid = false;
        body.GlobalTransform = ownPose.Body;
        upperBody.Transform = ownPose.Upper;
        chest.Transform = ownPose.Chest;
        paddle.GlobalTransform = ownPose.Paddle;
        leftArm.Open = ownPose.LeftOpen;
    }

    // Against a low backspin ball often a push (brushing down under it, which returns backspin;
    // nearly always when very low); a block against a fast topspin ball, its energy redirected
    // with the face closed; a smash when the ball sits up high; otherwise a drive brushing up
    // the back of the ball for topspin.
    static StrokeStyle ChooseStroke(BallState ball)
    {
        float topspin = ball.Spin.X * Mathf.Sign(ball.Velocity.Z);
        float height = ball.Position.Y - TableSpec.Height;
        if (topspin < -40f && height < 0.35f && GD.Randf() < (height < 0.25f ? 0.85f : 0.5f))
            return StrokeStyle.Push;
        if (height > 0.42f && topspin > -40f)
            return StrokeStyle.Smash;
        if (topspin > 250f && ball.Velocity.Length() > 10f && GD.Randf() < 0.5f)
            return StrokeStyle.Block;
        return StrokeStyle.Drive;
    }

    // How far off the first read is, at random: a fast or spinning ball is read worse.
    Vector3 FirstReadError(BallState incoming)
    {
        float hard = 1f + incoming.Velocity.Length() / 20f + incoming.Spin.Length() / 800f;
        Vector3 spread = Human.ReadSpread(Skill) * hard;
        return new Vector3((float)GD.Randfn(0.0, spread.X), (float)GD.Randfn(0.0, spread.Y), (float)GD.Randfn(0.0, spread.Z));
    }

    // How far off the first read of the spin is, at random: a share of the spin (about each axis
    // by the whole spin, so topspin can be taken for less, more, or even backspin) and a little
    // for a ball with none.
    Vector3 FirstSpinReadError(BallState incoming, bool serve)
    {
        float spread = Human.SpinReadShare(Skill, serve) * incoming.Spin.Length() * 0.6f + Human.SpinReadFloor(Skill);
        return new Vector3((float)GD.Randfn(0.0, spread), (float)GD.Randfn(0.0, spread), (float)GD.Randfn(0.0, spread * 0.5));
    }

    // Top or back spin and side spin in rev/s, for the log.
    static string SpinWords(BallState b)
    {
        float top = b.Spin.X * Mathf.Sign(b.Velocity.Z) / Mathf.Tau;
        return $"{(top >= 0f ? "top" : "back")} {Mathf.Abs(top):F0} side {b.Spin.Y / Mathf.Tau:F0} rev/s";
    }

    // How caught out of position it is when the other player strikes, 0 (set, balanced) .. 1:
    // still crossing the floor, or still finishing its own stroke.
    float OutOfPosition() => Mathf.Clamp(bodyVelocity.Length() / 1.5f + (followingThrough || paddle.Striking ? 0.4f : 0f), 0f, 1f);

    // How much of the first read's error is left with eta to go: all of it at the first read,
    // shrinking as the ball comes, none by the commit time.
    float ReadShare(float eta) => readEta <= Human.CommitTime ? 0f
        : Mathf.Clamp((eta - Human.CommitTime) / (readEta - Human.CommitTime), 0f, 1f);

    // The spin misread now: all of it at the first read, down to what is left at contact.
    Vector3 SpinMisreadNow()
    {
        float left = Human.SpinReadLeft(Skill);
        return readEta < 0f ? spinMisread : spinMisread * (left + (1f - left) * ReadShare(interceptEta));
    }

    // A ball it may still have to play: in flight, struck by the other side (its own ball into the
    // net drops back towards it, but is never its to play), and its point not yet given.
    bool Playable => TargetBall != null && TargetBall.Simulating && !TargetBall.Dead && TargetBall.StruckBy != Side;

    // How far off the table a ball can land and still look as if it might clip the edge: one
    // going this near the side or end line is got ready for like a good one (see PredictIntercept).
    const float DoubtfulMargin = 0.25f;

    // Where and when to meet the ball: just below the top of its bounce on our half, or where it
    // passes our ready line if it is still rising there. The flight is worked out with the spin
    // as it reads it (the real spin plus spinError), so a misread spin puts it in the wrong place.
    // A ball flying long or wide but landing near the table: nobody can tell one that just misses
    // from one that clips the edge until it is there, so it is got ready for as if it were good
    // (its flight on a table without edges). It is only struck if it does bounce on our side (the
    // ball lets a side hit it once it has bounced there), so one that goes out is let go at the
    // last moment, the racket ready for it.
    bool PredictIntercept(Vector3 spinError, out Vector3 point, out float eta, out BallState arriving)
    {
        point = Vector3.Zero;
        eta = -1f;
        arriving = default;
        if (!Playable)
            return false;
        BallState s = Own(TargetBall.State);
        if (s.Velocity.Z >= 0f)
            return false;
        s.Spin += spinError;

        bool bounced = TargetBall.HittableBy == Side;
        if (Flight(s, bounced, false, out point, out eta, out arriving, out bool flewOff))
            return true;
        return flewOff && !bounced && LandsNearTable(s) && Flight(s, false, true, out point, out eta, out arriving, out _);
    }

    // The flight from s to where it is met (see PredictIntercept); infiniteTable: as if the table
    // had no edges (and no net). flewOff: it went off the table without bouncing on it.
    bool Flight(BallState s, bool bounced, bool infiniteTable, out Vector3 point, out float eta, out BallState arriving, out bool flewOff)
    {
        point = Vector3.Zero;
        eta = -1f;
        arriving = default;
        flewOff = false;
        float dt = Ball.SubStepTime;
        // A short ball that would bounce twice before then is taken at the top of its bounce,
        // leaning in over the table.
        BallState? shortBall = null;
        float shortEta = 0f;
        for (float t = dt; t < 2f; t += dt)
        {
            BallEvent evt = Ball.Step(ref s, dt, infiniteTable);
            if (evt == BallEvent.Floor || evt == BallEvent.Net)
            {
                flewOff = evt == BallEvent.Floor && !bounced;
                return false;
            }
            if (evt == BallEvent.Table)
            {
                if (bounced && shortBall is BallState top)
                {
                    point = top.Position + new Vector3(0f, -0.02f, 0f);
                    eta = shortEta;
                    arriving = top;
                    return true;
                }
                if (bounced || TableSpec.SideOf(s.Position.Z) != OwnSide)
                    return false;
                bounced = true;
            }
            if (bounced && s.Position.Z <= ShortBallReachZ && (shortBall == null || s.Position.Y > shortBall.Value.Position.Y))
            {
                shortBall = s;
                shortEta = t;
            }
            bool falling = s.Velocity.Y <= 0f;
            if (bounced && s.Position.Z <= MinReachZ && (falling || s.Position.Z <= readyPosition.Z))
            {
                point = s.Position + new Vector3(0f, -0.03f, 0f);
                eta = t;
                arriving = s;
                return true;
            }
        }
        return false;
    }

    // Whether a ball flying off the table would have come down on our half within DoubtfulMargin
    // of its edges (where it crosses the table's height, as on a table without edges).
    bool LandsNearTable(BallState s)
    {
        float dt = Ball.SubStepTime;
        for (float t = dt; t < 2f; t += dt)
        {
            if (Ball.Step(ref s, dt, true) != BallEvent.Table)
                continue;
            Vector3 at = s.Position;
            return TableSpec.SideOf(at.Z) == OwnSide
                && Mathf.Abs(at.X) <= TableSpec.HalfWidth + DoubtfulMargin
                && Mathf.Abs(at.Z) <= TableSpec.HalfLength + DoubtfulMargin;
        }
        return false;
    }

    public bool TryHit(Ball ball)
    {
        BallState incoming = Own(ball.State);
        Vector3 contact = incoming.Position;
        float distance = contact.DistanceTo(Own(Reachable(blade.GlobalPosition)));
        if (contact.Z > ShortBallReachZ || distance > Reach)
        {
            closestDistance = float.MaxValue;
            return false;
        }
        // Strike at the ball's closest approach to the blade, not where it enters reach.
        if (distance < closestDistance && contact.Z > readyPosition.Z - 0.1f)
        {
            closestDistance = distance;
            return false;
        }
        closestDistance = float.MaxValue;

        // The ball as it reads it at contact: the real one with what is left of its spin misread.
        // The stroke is chosen and the face angled for this ball; the real one answers to them.
        BallState seen = incoming;
        seen.Spin += spinMisread * Human.SpinReadLeft(Skill);

        // The racket's motion for the planned stroke: a push brushes down under the ball, a drive
        // up its back, a block barely moves, a smash drives hard and a little down. The contact
        // gives the pace and spin; the face angle aims.
        StrokeStyle stroke = plannedStroke ?? ChooseStroke(seen);
        float drive, brush;
        switch (stroke)
        {
            case StrokeStyle.Push:
                drive = (float)GD.RandRange(1.2, 2.2);
                brush = -(float)GD.RandRange(0.8, 1.8);
                break;
            case StrokeStyle.Block:
                drive = (float)GD.RandRange(0.6, 1.4);
                brush = (float)GD.RandRange(-0.8, 0.2); // face closed, brushing slightly down over the topspin
                break;
            case StrokeStyle.Smash:
            {
                // Swung as hard as the body can, nearly flat and a little down.
                float speed = Human.SmashRacketSpeed(Skill) * fatigue.SwingFactor * (float)GD.RandRange(0.85, 1.0);
                float dip = (float)GD.RandRange(0.0, 0.12);
                drive = speed * Mathf.Cos(dip);
                brush = -speed * Mathf.Sin(dip);
                break;
            }
            default:
            {
                // A real topspin drive: the racket at the speed players of this level measure
                // (Human), the backhand a little slower; rising steeply to lift a backspin ball,
                // more forward against topspin. A fast ball already brings pace: swing a little
                // less hard into it.
                float speed = Human.DriveRacketSpeed(Skill) * fatigue.SwingFactor * (paddle.Forehand ? 1f : 0.88f) * (float)GD.RandRange(0.7, 0.95);
                speed = Mathf.Max(speed - 0.3f * Mathf.Abs(incoming.Velocity.Z), 0.45f * speed);
                float topspinIn = seen.Spin.X * Mathf.Sign(seen.Velocity.Z);
                float rise = topspinIn < -40f ? (float)GD.RandRange(0.7, 1.05) : (float)GD.RandRange(0.45, 0.75);
                drive = speed * Mathf.Cos(rise);
                brush = speed * Mathf.Sin(rise);
                break;
            }
        }
        // Now and then a drive becomes an attack: harder, deep and wide towards a corner.
        bool attack = stroke == StrokeStyle.Drive && GD.Randf() < Mathf.Lerp(0.1f, 0.25f, Skill);
        var aim = new Vector2((float)GD.RandRange(-0.6, 0.6), (float)GD.RandRange(0.5, 1.2));
        if (attack)
        {
            drive *= 1.25f;
            brush *= 1.15f;
            aim = new Vector2((GD.Randf() < 0.5f ? -1f : 1f) * (float)GD.RandRange(0.45, 0.62), (float)GD.RandRange(1.0, 1.2));
        }
        var racket = new Vector3((float)GD.RandRange(-0.8, 0.8) * Mathf.Max(drive / 4f, 1f), brush, drive);
        // A strategy's shot: its method's stroke, at its pace and spin, to its spot.
        Strategy.Shot plannedShot = StrategyRun.For(Side);
        if (plannedShot != null && plannedShot.Method != "serve")
        {
            stroke = Strategy.StyleOf(plannedShot.Method);
            attack = false;
            aim = PlannedAim(plannedShot);
            racket = PlannedRacket(plannedShot, stroke);
            // Out of reach at this pace (a soft push to a deep spot): drive through it harder, a
            // little at a time, until some face angle sends it there.
            for (int harder = 0; harder < 8 && !ShotSolver.TrySolveFace(seen, racket, OwnSide, aim, 1, out _, Hitter); harder++)
                racket.Z += 0.5f;
            DebugOverlay.Log($"  {Tag} plays shot {StrategyRun.Struck + 1} of the strategy: {StrategyRun.Describe(plannedShot, Side == -1 ? "A" : "B")}");
        }

        // The waist turning swings the chest, and with it the arm and racket, round the spine:
        // racket speed the arm and shoulder do not have to make. With it the stroke comes out a
        // little faster than the arm alone would make it (WaistBonus), and it pulls the racket
        // across the body. How the racket speed is shared among the joints is the arm chain's
        // (ShareOut, at the strike).
        Vector3 waist = WaistVelocity(stroke, paddle.Forehand, contact);
        float forward = racket.Z * (1f + WaistBonus * Mathf.Clamp(waist.Z / Mathf.Max(racket.Z, 0.1f), 0f, 1f));
        racket = new Vector3(racket.X + 0.4f * waist.X, racket.Y, forward);
        // Never faster than this body can swing it: every joint at its top speed (Human).
        racket = racket.LimitLength(MostRacketSpeed(World(racket)));

        // A fast ball arcs over the net only to a deep target, so deep is the first fallback.
        Vector2[] aims = { aim, new(aim.X * 0.7f, 1.25f), new(aim.X * 0.7f, Mathf.Max(aim.Y - 0.3f, 0.45f)), new(0f, 0.6f), new(0f, 0.4f) };
        float wobble = Mathf.Lerp(0.04f, 0.008f, Skill) * fatigue.ControlFactor * (1f + incoming.Velocity.Length() / 30f + incoming.Spin.Length() / 2000f);
        var faceError = new Vector2((GD.Randf() * 2f - 1f) * wobble, (GD.Randf() * 2f - 1f) * wobble * 0.7f);

        // Sometimes it fumbles the stroke, more often against pace, when going all out, as the
        // pressure of a long rally builds and when tired: the face is well off, mostly too open or
        // too closed (long or into the net), sometimes turned (wide). Misjudging the spin is not
        // in this: it comes from the read (seen).
        float mistake = Mathf.Lerp(0.1f, 0.02f, Skill) * (1f + incoming.Velocity.Length() / 20f)
            * (attack ? 2f : 1f) * (1f + 0.15f * rallyStrokes++) * fatigue.MistakeFactor;
        Work(stroke switch
        {
            StrokeStyle.Smash => Fatigue.SmashEffort,
            StrokeStyle.Push => Fatigue.PushEffort,
            StrokeStyle.Block => Fatigue.BlockEffort,
            _ => Fatigue.DriveEffort
        } * (attack ? 1.3f : 1f));
        bool misjudged = GD.Randf() < mistake;
        if (misjudged)
        {
            float miss = (float)GD.RandRange(0.1, 0.22) * (GD.Randf() < 0.5f ? -1f : 1f);
            faceError += GD.Randf() < 0.65f ? new Vector2(miss, 0f) : new Vector2(0f, 1.5f * miss);
        }

        // Like a player, it takes pace off a ball that will not take a full swing (low, short, or
        // too fast to bring down on the table): softer, brushing more and driving less (a slower, spinnier
        // loop), until some face angle makes the shot.
        for (int easier = 0; easier < 6 && racket.Length() > 4f; easier++)
        {
            BallState tried = ShotSolver.Stroke(seen, racket, OwnSide, aims, 1, Vector2.Zero, out _, out bool makes, Hitter);
            bool flat = tried.Velocity.Y < 0.5f * tried.Velocity.Length(); // a drive, not a lob: leaving under 30 degrees up
            if (makes && flat && tried.Velocity.Length() <= Human.FastestBall)
                break;
            racket = new Vector3(racket.X * 0.85f, racket.Y * (racket.Y > 0f ? 0.97f : 0.85f), racket.Z * 0.8f); // brush more, drive less
        }
        LastStroke = (attack ? "attacking " : "") + stroke.ToString().ToLower() + (misjudged ? " (misjudged)" : "");
        LastRole = stroke switch
        {
            StrokeStyle.Drive or StrokeStyle.Smash => StrokeRole.Attack,
            StrokeStyle.Block => StrokeRole.Defence,
            _ => StrokeRole.Control, // a push keeps the ball short and low, neither
        };
        ShotSolver.PushedStroke(seen, ref racket, OwnSide, aims, faceError, out Vector2 face, Hitter);
        BallState after = ShotSolver.Hit(incoming, face, racket, OwnSide, Hitter);
        ball.Strike(Side, World(contact), World(after.Velocity), World(after.Spin));
        paddle.Strike(face, World(racket), stroke);
        string joints = ShareOut(racket, stroke, false);
        LogRacket((attack ? "attacking " : "") + stroke.ToString().ToLower() + (misjudged ? " (misjudged)" : "")
            + $" (racket speed: {joints})", racket, face);
        DebugOverlay.Log($"    read the spin as {SpinWords(seen)} (it was {SpinWords(incoming)})");
        DebugOverlay.Log("    " + Ball.Describe(Ball.Analyse(incoming, ShotSolver.FaceNormal(face, OwnSide), racket, Ball.RubberGrip, Hitter)));
        interceptEta = -1f;
        plannedStroke = null;
        return true;
    }

    // How fast the waist turns the chest at contact (rad/s): hard for a forehand drive or smash,
    // the body uncoiling from the backswing, half that on the backhand, little for a push or block.
    static float WaistRate(StrokeStyle stroke) => stroke switch
    {
        StrokeStyle.Drive => 9f,   // measured forehand trunk turn 9-10 rad/s (Human)
        StrokeStyle.Smash => 10f,
        StrokeStyle.Block => 1f,
        _ => 1.5f
    };
    const float BackhandWaistShare = Human.BackhandTrunkShare;
    const float WaistBonus = 0.15f;     // extra forward racket speed from the waist turn

    // The racket's velocity from the waist turn alone (own frame): the chest turning about the
    // vertical through the spine carries the racket round it. A forehand turns the chest to the
    // left (+ about Y in the own frame), a backhand to the right.
    Vector3 WaistVelocity(StrokeStyle stroke, bool forehand, Vector3 contact)
    {
        float rate = WaistRate(stroke) * (forehand ? 1f : -BackhandWaistShare);
        Vector3 spine = Own(chest.GlobalPosition);
        var fromSpine = new Vector3(contact.X - spine.X, 0f, contact.Z - spine.Z);
        Vector3 v = new Vector3(0f, rate, 0f).Cross(fromSpine);
        return v.Z > 0f ? v : Vector3.Zero; // only a turn that carries the racket forward helps
    }

    void LogRacket(string stroke, Vector3 racket, Vector2 face)
    {
        DebugOverlay.Log($"  {Tag} {stroke}: racket ({racket.X:F1}, {racket.Y:F1}, {racket.Z:F1}) m/s, " +
            $"face pitch {Mathf.RadToDeg(face.X):F0}° yaw {Mathf.RadToDeg(face.Y):F0}°");
    }

    // Tosses the ball straight up off the open palm from the serve point (well over the 16 cm the
    // rules ask for); the strike follows in UpdateToss. Serves are forehand, the backswing
    // already set for the brush: low for a topspin serve, high for a backspin one.
    public void BeginServe(Ball ball)
    {
        if (serveMotion != null)
        {
            playingMotion = true;
            // The clock starts a touch early so a frame falls exactly on the contact: the ball
            // shown is the ball struck.
            float frame = (float)GetPhysicsProcessDeltaTime(); // the step this callback gets (time scale included)
            motionTime = serveMotion.ContactAt - Mathf.Ceil(serveMotion.ContactAt / frame - 1e-3f) * frame;
            motionTossed = motionStruck = false;
            AimServeMotion();
            return;
        }
        tossPoint = Own(ball.State.Position);
        ball.Toss(new Vector3(0f, TossSpeed, 0f));
        tossing = true;
        interceptEta = -1f;
        topspinServe = GD.Randf() < 0.5f;
        serveSpin = PoseSequence.SpinLevel.AsShaped; // its own serve: its own mix
        serveBrush = PoseSequence.Brush.AsMade;
        PlanServe();
        paddle.ChooseSide(1f);
        paddle.Style = topspinServe ? StrokeStyle.Drive : StrokeStyle.Push;
    }

    // The paddle winds up fully while the ball rises, then swings forward to arrive at the
    // contact point just as the ball drops back to the toss height.
    void UpdateToss()
    {
        BallState s = Own(TargetBall.State);
        bladeTarget = tossPoint;
        if (s.Velocity.Y >= 0f)
        {
            paddle.Windup = 1f;
            return;
        }

        float timeToContact = (s.Position.Y - tossPoint.Y) / -s.Velocity.Y;
        paddle.Windup = Mathf.Clamp(timeToContact / ForwardSwingTime, 0f, 1f);
        if (s.Position.Y <= tossPoint.Y)
        {
            tossing = false;
            Serve(TargetBall);
        }
    }

    // A brushed serve with heavy top- or backspin plus sidespin. The NPC knows the serve it
    // wants: a legal flight (solved with the ball's physics) carrying that spin. It then works out
    // the brush that makes it: the racket face and velocity whose contact (push along the face,
    // rubber friction across it) produces exactly that flight and spin from the falling ball,
    // and the ball is struck by that contact. Falls back to a plain serve if nothing works.
    void Serve(Ball ball, string motion = null, BallState? at = null)
    {
        BallState toss = at ?? Own(ball.State);
        Work(Fatigue.ServeEffort);
        string kind = (serveSpin switch
        {
            PoseSequence.SpinLevel.None => "no-spin ",
            PoseSequence.SpinLevel.Light => "light ",
            PoseSequence.SpinLevel.Heavy => "heavy ",
            _ => "",
        }) + (serveBrush is PoseSequence.Brush.SideLeft or PoseSequence.Brush.SideRight ? "sidespin serve" : topspinServe ? "topspin serve" : "underspin serve");
        LastStroke = kind;
        LastRole = StrokeRole.Serve;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            // Ball travels +Z: positive X spin is topspin, negative is backspin.
            Vector3 wanted;
            Vector2 aim;
            float pace = plannedServe != null ? PlannedPace(plannedServe) : (float)GD.RandRange(3.2, 5.0);
            if (topspinServe)
            {
                wanted = new Vector3(Human.ServeSpin(Skill) * Mathf.Tau * ServeSpinShare(0.5, 1.1), (float)GD.RandRange(-150.0, 150.0) * SideShare(), 0f);
                aim = plannedServe != null ? PlannedAim(plannedServe) : new Vector2((float)GD.RandRange(-0.5, 0.5), (float)GD.RandRange(0.6, 1.1));
            }
            else
            {
                // Underspin: chopped low and short (left alone it would bounce again on the
                // receiver's half), with heavy backspin that checks the ball after its bounces.
                // The rubber's friction is capped by the push, so when no contact gives the
                // spin, ask a little less.
                float backspin = Human.ServeSpin(Skill) * Mathf.Tau * ServeSpinShare(1.0, 1.4) * Mathf.Pow(0.92f, attempt);
                wanted = new Vector3(-backspin, (float)GD.RandRange(-80.0, 80.0) * SideShare(), 0f);
                // A sidespin brush: the spin mostly sideways (brushing across to the left spins the
                // ball about - Y, to the right about + Y), with a little backspin under it.
                if (serveBrush is PoseSequence.Brush.SideLeft or PoseSequence.Brush.SideRight)
                    wanted = new Vector3(-backspin * 0.35f, (serveBrush == PoseSequence.Brush.SideLeft ? -1f : 1f) * backspin, 0f);
                aim = plannedServe != null ? PlannedAim(plannedServe) : new Vector2((float)GD.RandRange(-0.45, 0.45), (float)GD.RandRange(0.35, 0.6));
            }

            // Friction can only spin the ball about axes across its change of velocity, so settle
            // the spin and the flight together.
            Vector3 spin = wanted;
            Vector3 velocity = Vector3.Zero;
            bool found = true;
            for (int i = 0; i < (topspinServe ? 4 : 2) && found; i++)
            {
                if (topspinServe)
                    velocity = ShotSolver.PlanShot(toss.Position, spin, aim, pace, 2, Vector2.Zero);
                else
                    found = ShotSolver.SearchServe(toss.Position, spin, aim, out velocity);
                spin = toss.Spin + ShotSolver.SpinWithin(wanted - toss.Spin, velocity - toss.Velocity);
            }
            if (!found || !ShotSolver.RacketFor(toss, velocity, spin, out Vector3 normal, out Vector3 racket, ServeHitter))
                continue;

            Ball.RacketContact serveContact = Ball.Analyse(toss, normal, racket, Ball.RubberGrip, ServeHitter);
            BallState served = serveContact.After;

            ball.Strike(Side, World(toss.Position), World(served.Velocity), World(served.Spin));
            Vector2 face = ShotSolver.FaceAngles(normal, OwnSide);
            if (motion == null)
                paddle.Strike(face, World(racket), topspinServe ? StrokeStyle.Drive : StrokeStyle.Push); // a motion swings the racket itself
            LogRacket(kind + (motion != null
                ? $" from the motion {motion}" // the motion swung the racket, not the arm chain
                : $" (racket speed: {ShareOut(racket, StrokeStyle.Push, true)})"), racket, face);
            DebugOverlay.Log("    " + Ball.Describe(serveContact));
            return;
        }

        // No brushed serve: a plain one without spin, searched for like the underspin serve (a
        // low contact needs the ball struck down so it bounces up over the net), aimed deep then
        // short; only if none is legal from here is the ball just popped over.
        Vector3 plain = Vector3.Zero;
        bool legalPlain = false;
        foreach (float depth in new[] { 0.85f, 0.6f, 0.4f })
            if (ShotSolver.SearchServe(toss.Position, Vector3.Zero, new Vector2(0f, depth), out plain))
            {
                legalPlain = true;
                break;
            }
        if (!legalPlain)
            plain = ShotSolver.PlanShot(toss.Position, Vector3.Zero, new Vector2(0f, 0.85f), 4f, 2, Vector2.Zero);
        ball.Strike(Side, World(toss.Position), World(plain), Vector3.Zero);
        if (motion == null)
            paddle.Strike(Vector2.Zero, World(plain), StrokeStyle.Drive);
        DebugOverlay.Log(legalPlain
            ? $"  {Tag} plain serve (no brushed serve worked)"
            : $"  {Tag} no legal serve from the contact at ({toss.Position.X:F2}, {toss.Position.Y:F2}, {toss.Position.Z:F2}): "
              + $"{(toss.Position.Y - TableSpec.Height) * 100f:F0} cm above the table, {(-TableSpec.HalfLength - toss.Position.Z) * 100f:F0} cm behind the end line; the ball is popped over");
    }

    // ---------------------------------------------------------------- a strategy (StrategyRun)

    Strategy.Shot plannedServe; // the strategy's serve, when it is this NPC's

    // The strategy's serve, when this NPC has it: its spin (topspin or backspin, sidespin brushed
    // across), how much, where it lands and its pace, in place of the NPC's own choice.
    void PlanServe()
    {
        plannedServe = StrategyRun.For(Side) is { Method: "serve" } shot ? shot : null;
        if (plannedServe == null)
            return;
        topspinServe = plannedServe.Spin == "top";
        serveSpin = plannedServe.Spin == "none" ? PoseSequence.SpinLevel.None : plannedServe.Amount switch
        {
            "none" => PoseSequence.SpinLevel.None,
            "heavy" => PoseSequence.SpinLevel.Heavy,
            _ => PoseSequence.SpinLevel.Light,
        };
        serveBrush = plannedServe.Spin switch
        {
            "sideleft" => PoseSequence.Brush.SideLeft,
            "sideright" => PoseSequence.Brush.SideRight,
            _ => PoseSequence.Brush.AsMade,
        };
        if (serveBrush != PoseSequence.Brush.AsMade)
            topspinServe = false; // sidespin is served with a little backspin under it
        DebugOverlay.Log($"  {Tag} serves as the strategy says: {StrategyRun.Describe(plannedServe, Side == -1 ? "A" : "B")}");
    }

    // A planned spot (the table's x, z) as this NPC aims: in its own frame, on the far half.
    Vector2 PlannedAim(Strategy.Shot shot)
    {
        Vector3 own = Own(new Vector3(shot.LandX, TableSpec.Height, shot.LandZ));
        return new Vector2(own.X, own.Z);
    }

    static float PlannedPace(Strategy.Shot shot) => shot.Pace switch { "slow" => 3.4f, "fast" => 5.2f, _ => 4.2f };

    // The racket's motion (own frame: across, up, forward) for a planned shot: the method's own
    // speed (a block 1.3 m/s, a push 2, a chop 3, a flick 4.5, a drive as players of this level
    // swing it, a smash all out), slower or faster for its pace (x0.7, x1.3); brushing the more the
    // more spin it asks (none nearly straight through, heavy mostly brush): up the ball for
    // topspin, down under it for backspin, across it for sidespin (to the left: + x).
    Vector3 PlannedRacket(Strategy.Shot shot, StrokeStyle stroke)
    {
        float usual = shot.Method switch
        {
            "fh_block" or "bh_block" => 1.3f,
            "fh_push" or "bh_push" => 2f,
            "fh_chop" or "bh_chop" => 3f,
            "fh_flick" or "bh_flick" => 4.5f,
            "smash" => Human.SmashRacketSpeed(Skill),
            _ => Human.DriveRacketSpeed(Skill) * (paddle.Forehand ? 1f : 0.88f),
        };
        float speed = usual * fatigue.SwingFactor * (shot.Pace switch { "slow" => 0.7f, "fast" => 1.3f, _ => 1f });
        float ratio = shot.Spin == "none" ? 0.12f : shot.Amount switch { "none" => 0.12f, "heavy" => 1.6f, _ => 0.7f };
        float a = Mathf.Atan(ratio);
        float drive = speed * Mathf.Cos(a), brush = speed * Mathf.Sin(a);
        return shot.Spin switch
        {
            "back" => new Vector3(0f, -brush, drive),
            "sideleft" => new Vector3(brush, 0.2f * brush, drive),
            "sideright" => new Vector3(-brush, 0.2f * brush, drive),
            "none" => new Vector3(0f, brush, drive),
            _ => new Vector3(0f, brush, drive),
        };
    }

    // The share of the player's full serve spin (Human.ServeSpin) asked for: the motion's level, or
    // at random in the NPC's own range (from, to) for a contact left as made.
    float ServeSpinShare(double from, double to) => serveSpin switch
    {
        PoseSequence.SpinLevel.None => 0.04f,  // through the middle: next to none
        PoseSequence.SpinLevel.Light => (float)GD.RandRange(0.35, 0.5),
        PoseSequence.SpinLevel.Heavy => (float)GD.RandRange(1.2, 1.4),
        _ => (float)GD.RandRange(from, to),
    };

    // Sidespin goes with it: none on a dead ball, a little on a light one.
    float SideShare() => serveSpin switch { PoseSequence.SpinLevel.None => 0.05f, PoseSequence.SpinLevel.Light => 0.5f, _ => 1f };

    // Steps the feet, bends the knees and leans the torso so the right shoulder comes to its
    // place relative to the paddle hand's wrist at the hitting point. The backswing and
    // follow-through do not move the feet: the waist turns the chest with them (the arm chain),
    // and the weight shifts.
    void UpdateBody(float dt)
    {
        static (float Hips, float Lean) HipsAndLean(float shoulderHeight)
        {
            float hips = Mathf.Clamp(shoulderHeight - TorsoLength * Mathf.Cos(MinLean), LowestHips, StandingHips);
            float lean = Mathf.Acos(Mathf.Clamp((shoulderHeight - hips) / TorsoLength, Mathf.Cos(MaxLean), Mathf.Cos(MinLean)));
            return (hips, lean);
        }
        // The feet, knees and lean are placed for the paddle at rest; the stroke itself is the arm
        // chain's joints turning (PoseArm), the waist turning the chest with it.
        Vector3 shoulder = Own(paddle.RestShoulderPoint);
        (float hips, float targetLean) = HipsAndLean(shoulder.Y);

        // Stance, as a turn to the right (positive) of the line through the feet and of the hips:
        // for the forehand (and the forehand serve) the right foot drops back and the feet stand
        // at 45 degrees to the side of the table, the hips turned part of the way with them; for
        // the backhand the right foot is a little forward and the body almost square.
        bool serving = tossing || HoldingServe;
        float k = 1f - Mathf.Exp(-14f * dt);
        float feetYaw = serving || paddle.Forehand ? Mathf.Pi / 4f : -0.25f;
        float hipsTarget = serving || paddle.Forehand ? 0.35f : -0.1f;
        hipsYaw = Mathf.Lerp(hipsYaw, hipsTarget, k);

        // Place the body so the right shoulder, leaning and turned with the stance (but not with
        // the stroke, so the feet stay put through the swing), is where the wrist needs it.
        Vector3 shoulderOffset = Basis.FromEuler(new Vector3(targetLean, -hipsYaw, 0f)) * RightShoulder;
        Vector3 goal = new Vector3(
            Mathf.Clamp(shoulder.X - shoulderOffset.X, -1.3f, 1.3f),
            0f,
            Mathf.Min(shoulder.Z - shoulderOffset.Z, ClosestBodyZ));
        // Serving, the body stands behind the ball and a little to its left instead.
        if (serving)
        {
            Vector3 behind = new Vector3(ServeSpotX, 0f, readyPosition.Z + 0.08f) + BodyFromServePoint;
            goal = new Vector3(behind.X, 0f, Mathf.Min(behind.Z, ClosestBodyZ));
        }

        // The feet move towards their places in the stance at the goal (pushing off both feet, or
        // lunging); the body heads there too, leaning a little ahead of its feet towards the ball.
        var feetTurn = new Basis(Vector3.Up, -feetYaw);
        bool feetPlacedBefore = feetPlaced;
        MoveFeet(dt, goal, feetTurn);

        // The body is a mass the legs push: it speeds up and slows down no harder than a player's
        // push on the floor (Human.PushAcceleration), heading for the goal so as to stop on it.
        Vector3 bodyFrom = Own(body.GlobalPosition);
        Vector3 toGoal = new Vector3(goal.X - bodyFrom.X, 0f, goal.Z - bodyFrom.Z);
        float push = LegPush;
        float away = toGoal.Length();
        // (Braking distances look one frame ahead, so it never arrives too fast.)
        float stopIn = Mathf.Max(away - bodyVelocity.Length() * dt, 0f);
        Vector3 wanted = away > 1e-3f ? toGoal / away * Mathf.Min(TopSpeed, Mathf.Sqrt(2f * push * stopIn)) : Vector3.Zero;
        // Braking in time for the table, too.
        if (wanted.Z > 0f)
            wanted.Z = Mathf.Min(wanted.Z, Mathf.Sqrt(2f * push * Mathf.Max(ClosestBodyZ - bodyFrom.Z - Mathf.Max(bodyVelocity.Z, 0f) * dt, 0f)));
        bodyVelocity += (wanted - bodyVelocity).LimitLength(push * dt);
        Vector3 bodyAt = bodyFrom + bodyVelocity * dt;
        // The hips stay over the feet; in a lunge they may hang further back, the weight still
        // on the planted leg.
        Vector3 middle = (feet[0] + feet[1]) * 0.5f;
        Vector3 lead = new Vector3(bodyAt.X - middle.X, 0f, bodyAt.Z - middle.Z);
        float overFeet = footwork == Footwork.Lunge ? LungeHipsOverFeet : HipsOverFeet;
        if (lead.Length() > overFeet)
            bodyAt = new Vector3(middle.X, bodyAt.Y, middle.Z) + lead.Normalized() * overFeet;
        bodyAt.Z = Mathf.Min(bodyAt.Z, ClosestBodyZ);
        bodyVelocity = feetPlacedBefore
            ? new Vector3(bodyAt.X - bodyFrom.X, 0f, bodyAt.Z - bodyFrom.Z) / Mathf.Max(dt, 1e-4f)
            : Vector3.Zero; // the first frame stands the body on its feet, at rest
        body.GlobalPosition = World(bodyAt);

        // Feet spread wide (a lunge) bring the hips down over them, the knees bending rather
        // than the legs stretching.
        float legReach = 0.97f * (legs[0].ThighLength + legs[0].ShinLength);
        var stanceHips = new Basis(Vector3.Up, -hipsYaw);
        for (int i = 0; i < 2; i++)
        {
            Vector3 hipAt = bodyAt + stanceHips * new Vector3((i == 0 ? 1f : -1f) * HipHalfWidth, 0f, 0f);
            float across = new Vector2(feet[i].X - hipAt.X, feet[i].Z - hipAt.Z).Length();
            float highest = Leg.AnkleHeight + feet[i].Y + Mathf.Sqrt(Mathf.Max(legReach * legReach - across * across, 0.01f));
            hips = Mathf.Min(hips, Mathf.Max(highest, LungeHips));
        }
        hipsHeight = Mathf.Lerp(hipsHeight, hips, k);
        lean = Mathf.Lerp(lean, targetLean, k);
        // The hips ride over the foot carrying the weight (the NPC's right is -X) and stand turned
        // with the stance; lean tips the top towards +Z, the table.
        float hipsX = -paddle.WeightShift * WeightTravel;
        upperBody.Position = new Vector3(hipsX, hipsHeight, 0f);
        upperBody.Rotation = new Vector3(lean, -hipsYaw, 0f);
        untwistedHips = upperBody.GlobalTransform;
    }

    // Turns the waist (from the arm chain, see PoseArm): the chest turns about the vertical
    // through the spine, so the shoulders stay level, and the hips follow with a little of it,
    // the legs running from the turned hips to the planted feet.
    void TwistWaist(float waist)
    {
        waistTurn = waist;
        float hipsX = -paddle.WeightShift * WeightTravel;
        float hipsTurnAngle = hipsYaw - HipsShareOfTurn * waistTurn;
        upperBody.Rotation = new Vector3(lean, -hipsTurnAngle, 0f);
        Vector3 vertical = (upperBody.Basis.Inverse() * Vector3.Up).Normalized();
        chest.Basis = new Basis(vertical, (1f - HipsShareOfTurn) * waistTurn);

        // Each leg runs from its hip joint to its planted (or stepping) foot, the knee bending
        // forward over the toes; the lower the hips the more it bends.
        var hipsTurn = new Basis(Vector3.Up, -hipsTurnAngle);
        // The legs keep their length and the feet stay where they are put: where a leg could not
        // reach its foot from the hips (turned with the waist, shifted over a foot, still easing
        // down), the hips come down now until it can, the upper body with them.
        float drop = 0f;
        for (int i = 0; i < legs.Length; i++)
        {
            float sign = i == 0 ? 1f : -1f;
            Vector3 hipAt = Own(body.GlobalTransform * (hipsTurn * new Vector3(sign * HipHalfWidth, 0f, 0f) + new Vector3(hipsX, hipsHeight, 0f)));
            Vector3 ankle = feet[i] + Vector3.Up * Leg.AnkleHeight;
            float reach = 0.998f * (legs[i].ThighLength + legs[i].ShinLength);
            float across = new Vector2(hipAt.X - ankle.X, hipAt.Z - ankle.Z).Length();
            if (across < reach)
                drop = Mathf.Max(drop, hipAt.Y - (ankle.Y + Mathf.Sqrt(reach * reach - across * across)));
        }
        if (drop > 0f)
        {
            hipsHeight -= drop;
            upperBody.Position = new Vector3(upperBody.Position.X, hipsHeight, upperBody.Position.Z);
        }
        for (int i = 0; i < legs.Length; i++)
        {
            float sign = i == 0 ? 1f : -1f; // LegLeft is on +X
            Vector3 hip = hipsTurn * new Vector3(sign * HipHalfWidth, 0f, 0f) + new Vector3(hipsX, hipsHeight, 0f);
            legs[i].Solve(body.GlobalTransform * hip, World(feet[i]), World(toes[i]));
        }
    }

    // Moves the feet towards their places in the stance around the body's goal. Planted until the
    // stance is out of place; then a push off both feet together carries both a short way (each
    // turning to the stance), again and again for a longer move. When a ball is coming and those
    // pushes would not get there in time, the foot leading the way lunges out instead, the other
    // staying planted, and the body leans over it; the feet come back together with the next
    // pushes once the ball has gone.
    void MoveFeet(float dt, Vector3 goal, Basis feetTurn)
    {
        Vector3 Place(int i) => goal + feetTurn * new Vector3((i == 0 ? 1f : -1f) * FootHalfWidth, 0f, 0f);
        // Toes forward across the line of the feet, turned out a little.
        Vector3 Toes(int i) => feetTurn * new Vector3((i == 0 ? 1f : -1f) * ToesOut, 0f, 1f).Normalized();
        if (!feetPlaced)
        {
            for (int i = 0; i < 2; i++)
            {
                feet[i] = Place(i);
                toes[i] = Toes(i);
            }
            feetPlaced = true;
        }

        if (footwork == Footwork.Planted)
        {
            Vector3 move = (Place(0) - feet[0] + Place(1) - feet[1]) * 0.5f;
            move.Y = 0f;
            float off = Mathf.Max(move.Length(), Mathf.Max(
                Mathf.Max(feet[0].DistanceTo(Place(0)), feet[1].DistanceTo(Place(1))) - 0.1f,
                Mathf.Max(toes[0].AngleTo(Toes(0)), toes[1].AngleTo(Toes(1))) * TurnPerMetre));
            // A body carried off its feet (by its own speed) steps to get them back under it.
            Vector3 bodyOff = Own(body.GlobalPosition) - (feet[0] + feet[1]) * 0.5f;
            off = Mathf.Max(off, new Vector2(bodyOff.X, bodyOff.Z).Length() - 0.1f);
            if (off < MoveTrigger)
                return;

            for (int i = 0; i < 2; i++)
            {
                footFrom[i] = feet[i];
                toesFrom[i] = toes[i];
            }
            // How long the body takes to get there from rest, pushing as hard as a player can.
            float pushesNeeded = 2f * Mathf.Sqrt(move.Length() / LegPush);
            // A lunge only from the stance (not from one already spread), never wider than MaxSpan.
            float span =new Vector2(feet[0].X - feet[1].X, feet[0].Z - feet[1].Z).Length();
            bool late = interceptEta >= 0f && move.Length() > MaxShuffle && pushesNeeded > interceptEta
                && span < 2f * FootHalfWidth + 0.25f;
            if (late)
            {
                // The foot on the side the ball is reaches out; the middle of the feet, and the
                // body with it, goes half as far.
                Vector3 middle = (feet[0] + feet[1]) * 0.5f;
                int reaching = (feet[0] - middle).Dot(move) >= (feet[1] - middle).Dot(move) ? 0 : 1;
                Vector3 reach = (move * 2f).LimitLength(MaxLunge);
                Vector3 planted = feet[1 - reaching];
                Vector3 apart = feet[reaching] + reach - planted;
                apart.Y = 0f;
                if (apart.Length() > MaxSpan)
                    reach = planted + apart.Normalized() * MaxSpan - feet[reaching];
                footTo[reaching] = feet[reaching] + new Vector3(reach.X, 0f, reach.Z);
                footTo[1 - reaching] = feet[1 - reaching];
                toesTo[reaching] = Toes(reaching);
                toesTo[1 - reaching] = toes[1 - reaching];
                footwork = Footwork.Lunge;
                Work(Fatigue.LungeEffort);
            }
            else
            {
                // A push lands the feet round where the body itself will be by then (from its
                // speed that way and the legs' push), a short lead ahead of it, never past the goal.
                Vector3 bodyNow = Own(body.GlobalPosition);
                Vector3 toGoal = new Vector3(goal.X - bodyNow.X, 0f, goal.Z - bodyNow.Z);
                // Where its momentum and the push take the body in the push, then a short lead on.
                Vector3 bodyEnd = new Vector3(bodyNow.X, 0f, bodyNow.Z) + bodyVelocity * ShuffleTime
                    + (toGoal.Normalized() * 0.5f * LegPush * ShuffleTime * ShuffleTime).LimitLength(toGoal.Length());
                Vector3 onToGoal = new Vector3(goal.X - bodyEnd.X, 0f, goal.Z - bodyEnd.Z);
                Vector3 centre = bodyEnd + onToGoal.LimitLength(FeetLead);
                for (int i = 0; i < 2; i++)
                {
                    Vector3 way = centre + feetTurn * new Vector3((i == 0 ? 1f : -1f) * FootHalfWidth, 0f, 0f) - feet[i];
                    way.Y = 0f;
                    // Spread feet come back together in the push: the far one may travel further.
                    footTo[i] = feet[i] + way.LimitLength(MaxShuffle + Mathf.Max(span - 2f * FootHalfWidth, 0f));
                    toesTo[i] = Toes(i);
                }
                footwork = Footwork.Shuffle;
                // Pushing for a ball tires, by how far; stepping into place between points does not.
                if (TargetBall != null && TargetBall.Simulating)
                    Work(Fatigue.ShufflePerMetre * Mathf.Max(footTo[0].DistanceTo(footFrom[0]), footTo[1].DistanceTo(footFrom[1])));
            }
            footProgress = 0f;
        }

        bool lunge = footwork == Footwork.Lunge;
        footProgress = Mathf.Min(footProgress + dt / (lunge ? LungeTime : ShuffleTime), 1f);
        float eased = Mathf.SmoothStep(0f, 1f, footProgress);
        float lift = (lunge ? 0.04f : ShuffleLift) * Mathf.Sin(Mathf.Pi * footProgress);
        for (int i = 0; i < 2; i++)
        {
            bool moving = footFrom[i] != footTo[i];
            feet[i] = footFrom[i].Lerp(footTo[i], eased) + (moving ? Vector3.Up * lift : Vector3.Zero);
            toes[i] = toesFrom[i].Slerp(toesTo[i], eased);
        }
        if (footProgress >= 1f)
        {
            for (int i = 0; i < 2; i++)
                feet[i] = footTo[i];
            footwork = Footwork.Planted;
        }
    }

    // The right arm reaches the wrist of the hand gripping the paddle. The left hand holds the
    // ball on its palm before a serve, rises with it to release the toss, and otherwise is held
    // up in front for balance.
    void UpdateArms(float dt)
    {
        // The paddle arm is the joint chain driving the racket (PoseArm), which turns the waist;
        // the shoulders are on the chest, above the waist joint.
        PoseArm(dt);
        Transform3D upper = chest.GlobalTransform.TranslatedLocal(-Waist);
        Basis facing = body.GlobalBasis * new Basis(Vector3.Up, waistTurn - hipsYaw);
        Vector3 hand = upper * LeftShoulder + facing * LeftHandFromShoulder;
        // Clear of the table, fingers and all: touching it with the free hand loses the point.
        bool overTable = Mathf.Abs(hand.X) < TableSpec.HalfWidth + 0.15f && Mathf.Abs(hand.Z) < TableSpec.HalfLength + 0.15f;
        hand.Y = Mathf.Max(hand.Y, TableSpec.Height + (overTable ? 0.25f : 0.12f));

        // The ball rests in the middle of the flat, open palm, the fingers pointing forward over
        // the table and a little in across the body; the wrist is behind and below it.
        Vector3 fingersForward = World(new Vector3(-0.3f, 0f, 1f).Normalized());
        Vector3 WristUnder(Vector3 ball) =>
            ball - fingersForward * Arm.PalmCentre - Vector3.Up * (Arm.PalmHalfThickness + Ball.Radius);
        if (HoldingServe)
            hand = WristUnder(TargetBall.State.Position);
        else if (tossing && TargetBall.State.Velocity.Y > 0f && TargetBall.State.Position.Y < tossPoint.Y + 0.12f)
            hand = WristUnder(World(tossPoint)) + new Vector3(0f, 0.1f, 0f);
        leftHand = HoldingServe ? hand : leftHand.Lerp(hand, 1f - Mathf.Exp(-12f * dt));

        // Open and palm up from showing the ball until it is tossed and struck, then relaxing
        // and turning over (through palm in) to the balance position, palm down and in.
        leftArm.Open = HoldingServe ? 1f : Mathf.MoveToward(leftArm.Open, tossing ? 1f : 0f, 3f * dt);
        float open = leftArm.Open;
        Vector3 inward = World(Vector3.Left);
        Vector3 palmFacing = Vector3.Down * (1f - open) + Vector3.Up * open + inward * (0.6f + 0.6f * Mathf.Sin(Mathf.Pi * open)) * (1f - 0.9f * open);

        // The free arm never passes through the body, the racket arm or the racket: its hand
        // (unless it holds the ball for a serve) and forearm are moved clear, and its elbow,
        // bending down, outwards and back, goes round the outside.
        Vector3 leftShoulder = upper * LeftShoulder;
        Collide.Capsule[] around = FreeArmObstacles();
        Vector3 pole = upper.Basis * new Vector3(0.6f, -1f, -0.2f);
        for (int pass = 0; pass < 4; pass++)
        {
            Vector3 leftElbow = Limb.TwoBone(leftShoulder, leftHand, leftArm.UpperArmLength, leftArm.ForearmLength, pole);
            Vector3 handOut = Collide.OutOfAll(leftHand, 0.05f, around);
            Vector3 foreOut = Collide.OutOfAll((leftHand + leftElbow) * 0.5f, 0.04f, around);
            Vector3 elbowOut = Collide.OutOfAll(leftElbow, 0.045f, around, table: false);
            if (handOut.LengthSquared() + foreOut.LengthSquared() + elbowOut.LengthSquared() < 1e-6f)
                break;
            if (!HoldingServe)
                leftHand += foreOut.LengthSquared() > handOut.LengthSquared() ? foreOut : handOut;
            if (elbowOut.LengthSquared() > 1e-6f || (HoldingServe && foreOut.LengthSquared() > 1e-6f))
                pole = leftElbow + (elbowOut + foreOut) * 3f - leftShoulder;
        }
        leftArm.Solve(leftShoulder, leftHand, pole, palmFacing, fingersForward);
        ReclearRacket(); // and the racket once more, against where the free arm has gone
    }
}
