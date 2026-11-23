using Godot;
using System.Linq;

public enum GripStyle
{
    Shakehand, // cầm vợt ngang: handle in the palm like a handshake
    Penhold    // cầm vợt dọc: handle up, held like a pen
}

public enum StrokeStyle
{
    Drive, // low to high and back to front, brushing up the back of the ball (topspin, loop)
    Push,  // open face, high to low under the ball (backspin)
    Block, // short and firm, the face set against the incoming ball
    Smash  // racket raised high, swung down through a high ball
}

// The visible paddle, the right hand holding it and its strokes. It sits under a hitter node
// whose origin is the hitting point, with local -Z towards the opponent and +X to the holder's
// right, so the same animation serves the player and the NPC.
//
// Shakehand: forehand shows the red rubber with the handle down towards the wrist on the left;
// backhand turns the paddle over on its handle so the black rubber faces the ball.
// Penhold: the handle points up and the red rubber plays both wings; for a backhand the head
// swings across in front of the body instead of turning over.
//
// A stroke is a backswing driven by Windup (1 = fully back, easing to 0 exactly at contact),
// then Strike() plays the follow-through and brings the paddle back to the ready position.
// The wrist locks the paddle to the forearm, so hand and paddle swing as one rigid piece round
// the stroke's swing centre (the body and shoulder for a forehand, the elbow for a backhand):
// the paddle travels a curved path and its face turns with the arc. The paddle also reports how
// the holder's torso turns and where their weight goes, for a visible body to follow.
public partial class Paddle : Node3D
{
    [Export] public GripStyle Grip = GripStyle.Shakehand;
    [Export] public bool ShowRubbers = true;        // off shows the bare wooden blade
    [Export] public float GripRoll = 1.5f;          // shakehand: handle tilt from vertical, radians (nose out to the side, level)
    [Export] public float PenholdRoll = 0.9f;       // penhold: handle tilt from vertical, radians (nose down and out)
    [Export] public float SideHysteresis = 0.08f;   // metres either side before switching wing
    [Export] public float FlipTime = 0.12f;         // seconds to turn between forehand and backhand
    [Export] public float WindupRate = 12f;         // per second
    [Export] public float BackswingRate = 12f;      // per second, taking the racket back (0 = as WindupRate)
    [Export] public float FollowThroughTime = 0.5f; // out to the finish and back to ready

    [Export] public Material Skin;

    // A stroke as an arc in forehand holder space (x to the holder's right, -z towards the
    // opponent, origin at the contact point); the backhand mirrors it sideways. The paddle turns
    // about an axis through Pivot, the swing centre. The axis leans from vertical by Lean (x, z):
    // leaning back (z > 0) makes the arc rise from back to front (low to high, a drive), leaning
    // forward makes it fall (high to low, a push or smash), and leaning right (x > 0) lifts the
    // finish across the body. Back and Follow are the arc's angles (radians) behind and past
    // contact. The torso turns Turn times the arc angle, and FaceOpen tilts the face on the
    // backswing (positive opens it for a push, negative rolls it closed for a drive).
    // NoseDrop is the shakehand wrist tipping the paddle's nose down (radians) on the backswing
    // and up as much through the follow-through: a drive starts low with the nose below the
    // handle and finishes with it up; a smash is the other way round.
    record StrokePath(Vector3 Pivot, Vector2 Lean, float Back, float Follow, float Turn, float FaceOpen, float NoseDrop);

    // Forehand: the swing centre is the body, behind and to the left of the contact point. The
    // racket starts behind the right hip with the torso turned back, sweeps round through
    // contact in front of the right side and finishes in front of the left shoulder.
    // Backhand: the swing centre is the elbow, held close in front of the body; the racket starts
    // at the left of the belly and the forearm swings it out forwards to the right, with only a
    // little torso turn.
    static StrokePath PathFor(StrokeStyle style, bool forehand) => (style, forehand) switch
    {
        (StrokeStyle.Drive, true) => new(new(-0.35f, 0.3f, 0.35f), new(0.25f, 0.45f), -1.2f, 1.4f, 0.65f, -0.15f, 0.55f),
        (StrokeStyle.Drive, false) => new(new(-0.15f, 0.1f, 0.3f), new(0.1f, 0.35f), -1.4f, 1.05f, 0.35f, -0.15f, 0.45f),
        (StrokeStyle.Push, true) => new(new(-0.35f, 0.3f, 0.35f), new(0f, -0.5f), -0.45f, 0.5f, 0.25f, 0.4f, 0f),
        (StrokeStyle.Push, false) => new(new(-0.12f, 0.1f, 0.3f), new(0f, -0.5f), -0.6f, 0.5f, 0.1f, 0.4f, 0f),
        (StrokeStyle.Block, true) => new(new(-0.35f, 0.3f, 0.35f), new(0f, 0.1f), -0.25f, 0.3f, 0.2f, -0.1f, 0f),
        (StrokeStyle.Block, false) => new(new(-0.15f, 0.1f, 0.3f), new(0f, 0.1f), -0.3f, 0.35f, 0.1f, -0.1f, 0f),
        (StrokeStyle.Smash, true) => new(new(-0.35f, 0.5f, 0.3f), new(0.3f, -0.7f), -1.4f, 1.6f, 0.6f, -0.2f, -0.5f),
        _ => new(new(-0.15f, 0.2f, 0.3f), new(0.1f, -0.5f), -1.2f, 1.1f, 0.35f, -0.1f, -0.4f),
    };

    // A right hand in paddle space before the grip pose: handle along -Y, forehand (red) rubber
    // on -Z, backhand (black) rubber on +Z, head centre at the origin.
    // ForearmForehand and ForearmBackhand are where the forearm runs from the wrist with the wrist
    // at its natural angle for that wing (before the stroke's own wrist movement). PalmTilt is
    // how far the palm slopes about X from the handle back towards the wrist (the wrist's angles
    // are measured from the palm). The hand's flesh: Palm and Pads are ellipsoids (the palm, the
    // ball of the thumb, the heel of the hand), Bones rounded rods under the skin (the bones of
    // the back of the hand, the knuckles), Fingers chains of joints.
    record struct Pad(Vector3 Centre, Vector3 Along, Vector3 Back, Vector3 Size);
    record HandShape(Pad Palm, Vector3 Wrist, (float Radius, Vector3[] Joints)[] Fingers,
        Vector3 ForearmForehand, Vector3 ForearmBackhand, float PalmTilt = 0f,
        Pad[] Pads = null, (Vector3 From, Vector3 To, float Radius)[] Bones = null);

    // An ellipsoid sloped about X by tilt (Along is its Y, Back its Z).
    static Pad Tilted(Vector3 centre, Vector3 size, float tilt) =>
        new(centre, new Basis(Vector3.Right, tilt) * Vector3.Up, new Basis(Vector3.Right, tilt) * Vector3.Back, size);

    // Shakehand, as a handshake: the palm is against the handle's black (+Z) face with the back
    // of the hand outwards, its little-finger side cupped round the +X edge; the V between thumb
    // and index finger sits on the -X edge at the throat. The index finger runs up the throat and
    // lies slanting across the bottom of the black rubber; the thumb, from its ball low on the -X
    // edge, lies slanting up the red side to the shoulder of the blade.
    // Middle, ring and little finger wrap round the handle side by side, pressed together: the
    // knuckles in a row above the +X edge, the first bone down the +X side of the handle, the
    // middle joint just past its corner and the rest across the red face, sloping a little down
    // towards the ball of the thumb, the middle fingertip tucked under the thumb. Their joints
    // were placed by wrapping each finger's real bone lengths (middle 44/28/20 mm, ring 41/27/20,
    // little 32/19/17) round the handle's rounded section with every bone straight and touching
    // it, so none cuts into the wood, floats or sticks out past the edge.
    // The wrist stays where the arm is built to (the forearm directions are tuned to it).
    static readonly HandShape ShakehandHand = new(
        new Pad(new Vector3(-0.002f, -0.145f, 0.0235f), new Vector3(0.3f, 1f, 0.1f), new Vector3(0f, -0.1f, 1f), new Vector3(0.052f, 0.1f, 0.031f)),
        new Vector3(-0.021f, -0.2f, 0.002f),
        new[]
        {
            // Thumb: from deep in its ball (the joint at the wrist), round the -X edge and up the red side.
            (0.0102f, new[] { new Vector3(-0.03f, -0.165f, 0.006f), new Vector3(-0.027f, -0.128f, -0.0165f), new Vector3(-0.017f, -0.101f, -0.0235f), new Vector3(-0.004f, -0.082f, -0.021f) }),
            // Index finger: up the throat on the black side, then along the rubber's bottom edge.
            (0.0092f, new[] { new Vector3(0.004f, -0.1f, 0.026f), new Vector3(0.0233f, -0.071f, 0.019f), new Vector3(0.04f, -0.06f, 0.0165f), new Vector3(0.052f, -0.05f, 0.016f) }),
            // Middle, ring, little: knuckle, middle joint, end joint, tip.
            (0.0097f, new[] { new Vector3(0.019f, -0.11f, 0.031f), new Vector3(0.0246f, -0.1216f, -0.0111f), new Vector3(0.003f, -0.1298f, -0.0268f), new Vector3(-0.0143f, -0.1359f, -0.0189f) }),
            (0.0093f, new[] { new Vector3(0.021f, -0.128f, 0.029f), new Vector3(0.0238f, -0.1389f, -0.0104f), new Vector3(0.0058f, -0.1465f, -0.029f), new Vector3(-0.0115f, -0.1522f, -0.0206f) }),
            (0.0085f, new[] { new Vector3(0.02f, -0.146f, 0.026f), new Vector3(0.0256f, -0.1547f, -0.0043f), new Vector3(0.0191f, -0.16f, -0.0214f), new Vector3(0.0028f, -0.1648f, -0.0211f) }),
        },
        // The palm lies along the black face (back of the hand outwards), so the wrist bends about
        // Z: laid back a little for the forehand (the forearm coming from behind the racket,
        // nearly level), neutral for the backhand.
        new Vector3(-0.2f, -0.95f, 0.3f), new Vector3(-0.08f, -1f, -0.05f),
        Pads: new[]
        {
            new Pad(new Vector3(0.014f, -0.158f, 0.017f), new Vector3(0.25f, 1f, 0.05f), new Vector3(0.6f, 0f, 0.8f), new Vector3(0.034f, 0.06f, 0.028f)), // little-finger side, cupped round the +X edge
            new Pad(new Vector3(-0.033f, -0.163f, 0f), new Vector3(0.1f, 1f, -0.2f), new Vector3(-0.85f, 0f, 0.5f), new Vector3(0.03f, 0.058f, 0.03f)), // ball of the thumb, round the -X edge
            new Pad(new Vector3(-0.012f, -0.19f, 0.008f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f), new Vector3(0.056f, 0.036f, 0.04f)), // heel of the hand into the wrist
            new Pad(new Vector3(-0.021f, -0.15f, 0.002f), new Vector3(0.05f, 1f, 0f), new Vector3(-1f, 0f, 0.15f), new Vector3(0.036f, 0.06f, 0.02f)), // the web between thumb and index finger over the -X edge, low (into the ball of the thumb)
            new Pad(new Vector3(-0.014f, -0.11f, 0.008f), new Vector3(0.3f, 1f, 0f), new Vector3(-1f, 0f, 0.35f), new Vector3(0.03f, 0.05f, 0.016f)), // and high, up to the index knuckle
        },
        Bones: new[]
        {
            // The bones of the back of the hand from the wrist to each knuckle, just showing
            // through the palm, and the knuckles themselves.
            (new Vector3(-0.022f, -0.168f, 0.021f), new Vector3(0.004f, -0.1f, 0.026f), 0.0095f),
            (new Vector3(-0.013f, -0.17f, 0.022f), new Vector3(0.019f, -0.11f, 0.03f), 0.0098f),
            (new Vector3(-0.005f, -0.172f, 0.021f), new Vector3(0.021f, -0.128f, 0.028f), 0.0094f),
            (new Vector3(0.002f, -0.174f, 0.018f), new Vector3(0.02f, -0.146f, 0.025f), 0.0088f),
            (new Vector3(0.004f, -0.1f, 0.026f), new Vector3(0.004f, -0.1f, 0.026f), 0.0108f),
            (new Vector3(0.019f, -0.11f, 0.031f), new Vector3(0.019f, -0.11f, 0.031f), 0.0112f),
            (new Vector3(0.021f, -0.128f, 0.029f), new Vector3(0.021f, -0.128f, 0.029f), 0.0108f),
            (new Vector3(0.02f, -0.146f, 0.026f), new Vector3(0.02f, -0.146f, 0.026f), 0.0098f),
        });

    // Penhold, held like a pen: thumb and index finger ring the throat on the red side, the
    // handle's end rests in the web between them, and middle, ring and little finger curl behind
    // the head to press on the black rubber. The hand slopes back from the throat to the wrist,
    // which sits behind the racket on the player's side, and the forearm comes from there.
    static readonly HandShape PenholdHand = new(
        Tilted(new Vector3(-0.002f, -0.178f, 0.082f), new Vector3(0.075f, 0.095f, 0.03f), -0.62f),
        new Vector3(-0.002f, -0.228f, 0.118f),
        new[]
        {
            (0.0095f, new[] { new Vector3(-0.03f, -0.165f, 0.07f), new Vector3(-0.036f, -0.13f, 0.03f), new Vector3(-0.026f, -0.106f, -0.006f), new Vector3(-0.01f, -0.096f, -0.016f) }),
            (0.0085f, new[] { new Vector3(0.026f, -0.152f, 0.06f), new Vector3(0.036f, -0.122f, 0.024f), new Vector3(0.027f, -0.1f, -0.008f), new Vector3(0.011f, -0.09f, -0.016f) }),
            (0.0085f, new[] { new Vector3(0.013f, -0.14f, 0.066f), new Vector3(0.016f, -0.104f, 0.058f), new Vector3(0.015f, -0.072f, 0.036f), new Vector3(0.012f, -0.056f, 0.016f) }),
            (0.0085f, new[] { new Vector3(-0.006f, -0.14f, 0.068f), new Vector3(-0.006f, -0.104f, 0.06f), new Vector3(-0.007f, -0.074f, 0.037f), new Vector3(-0.008f, -0.058f, 0.016f) }),
            (0.0075f, new[] { new Vector3(-0.024f, -0.142f, 0.064f), new Vector3(-0.025f, -0.114f, 0.054f), new Vector3(-0.024f, -0.088f, 0.033f), new Vector3(-0.022f, -0.072f, 0.016f) }),
        },
        // Forearm carrying on back from the sloping hand, a little flatter (wrist slightly bent).
        new Vector3(0f, -0.5f, 0.87f), new Vector3(-0.1f, -0.5f, 0.86f),
        -0.62f);

    HandShape Hand => Grip == GripStyle.Penhold ? PenholdHand : ShakehandHand;

    public bool Forehand { get; private set; } = true;
    public float Windup;     // target, set by the owner every frame
    public float FaceTilt;   // extra face angle in radians, negative closes the face
    public StrokeStyle Style = StrokeStyle.Drive; // the stroke being prepared, set by the owner

    // Where the gripping hand's wrist is, for an arm to reach, and where it would be with the
    // paddle at rest in the hitting point (no backswing or follow-through), for placing the body.
    public Vector3 WristPoint => GlobalTransform * Hand.Wrist;
    public Vector3 RestWristPoint => GetParent<Node3D>().GlobalTransform * restPose * Hand.Wrist;

    // The arm the hand belongs to, worked out from the racket: the forearm runs from the wrist
    // the way the grip sets it (ForearmDirection, unit, world space), so a wrist movement in the
    // stroke turns the hand and racket away from the forearm rather than dragging it along. For
    // placing the body, RestShoulderPoint carries on from the paddle at rest up the forearm to the
    // elbow and up the upper arm to the shoulder: beside the body with the elbow down for the
    // forehand, the elbow in front of the body for the backhand.
    public Vector3 ForearmDirection { get; private set; } = Vector3.Down;
    public Vector3 RestShoulderPoint { get; private set; }
    public Vector3 UpperArmDirection { get; private set; } = Vector3.Up; // elbow to shoulder, world space
    // The wrist's angles away from straight in line with the forearm, in radians: bent in the
    // racket's plane (flexion/extension for the shakehand) and out of it.
    public Vector2 WristBend { get; private set; }

    // Driven: the owner's arm places the racket (see Opponent's arm chain) instead of the stroke
    // arcs here. The paddle still plays the stroke's timing and face, and tells the arm the
    // racket's orientation at contact in holder space (NeutralBasis: the face, no wrist movement),
    // where the hand's wrist is and where the grip's forearm runs (paddle space).
    public bool Driven;
    [Export] public bool AvoidTable = true; // the paddle may not go into the table (off where there is none)

    // Points round the racket's rim and the handle's end (paddle space), for keeping it out
    // of the table and bodies.
    public static readonly Vector3[] Outline =
    {
        new(0f, 0.09f, 0f), new(0.053f, 0.064f, 0f), new(-0.053f, 0.064f, 0f), new(0.075f, 0f, 0f), new(-0.075f, 0f, 0f),
        new(0.06f, -0.06f, 0f), new(-0.06f, -0.06f, 0f), new(0f, -0.1f, 0f), new(0f, -0.172f, 0f), Vector3.Zero,
    };
    public Basis NeutralBasis { get; private set; } = Basis.Identity;
    public Vector3 WristLocal => Hand.Wrist;
    public Vector3 ForearmLocal { get; private set; } = Vector3.Down;
    public float WindupNow => windup;              // 1 full backswing .. 0 at contact
    public float FollowNow { get; private set; }   // 0 at contact .. 1 at the finish and back to 0
    public bool Striking => followThrough >= 0f;   // the follow-through of a struck ball is playing
    public bool Recovering => followThrough >= 0.5f; // past the finish, coming back to ready

    // Upper arm from the elbow to the shoulder, holder space (x right, y up, z back).
    static readonly Vector3 UpperArmForehand = new(-0.2f, 0.93f, 0.3f);
    static readonly Vector3 UpperArmBackhand = new(0.3f, 0.8f, 0.5f);

    // Torso turn in radians, positive turned to the holder's right (right shoulder back), and
    // weight from +1 on the right foot to -1 on the left foot.
    public float BodyTurn { get; private set; }
    public float WeightShift { get; private set; }

    float side;              // 0 forehand .. 1 backhand, eased
    float windup;
    float followThrough = -1f; // progress 0..1 while the follow-through plays
    Vector2 strikeFace;        // face angle at contact, held through the follow-through
    StrokeStyle strikeStyle;
    Vector3 swingDirection;    // the racket's motion at contact, in holder space
    StrokePath prepared = PathFor(StrokeStyle.Drive, true); // eased towards the stroke being prepared
    Transform3D restPose = Transform3D.Identity;
    float? frozenFollow;       // set by Freeze: the pose is held instead of played
    readonly System.Collections.Generic.Dictionary<string, Material> standardRubbers = new();

    // Shows an equipped racket's topsheet colours (forehand on the forehand side), or the
    // standard red and black for null.
    public void ShowRacket(Racket racket)
    {
        foreach (var (node, colour) in new[] { ("ForehandRubber", racket?.ForehandColor), ("BackhandRubber", racket?.BackhandColor) })
        {
            if (!standardRubbers.TryGetValue(node, out Material standard))
                continue;
            var sheet = GetNode<MeshInstance3D>(node);
            if (colour == null || standard is not StandardMaterial3D look)
            {
                sheet.MaterialOverride = standard;
                continue;
            }
            var own = (StandardMaterial3D)look.Duplicate();
            own.AlbedoColor = Racket.Colour(colour);
            sheet.MaterialOverride = own;
        }
    }

    public override void _Ready()
    {
        GetNode<MeshInstance3D>("Blade").Mesh = BladeShape.Blade;
        GetNode<MeshInstance3D>("Handle").Mesh = BladeShape.Handle;
        foreach (string rubber in new[] { "ForehandRubber", "BackhandRubber" })
        {
            var sheet = GetNode<MeshInstance3D>(rubber);
            sheet.Mesh = BladeShape.Rubber;
            sheet.Visible = ShowRubbers;
            standardRubbers[rubber] = sheet.MaterialOverride;
        }

        var hand = new Node3D { Name = "Hand" };
        AddChild(hand);
        foreach (Pad pad in new[] { Hand.Palm }.Concat(Hand.Pads ?? System.Array.Empty<Pad>()))
        {
            Vector3 along = pad.Along.Normalized();
            Vector3 across = along.Cross(pad.Back).Normalized();
            Limb.Ellipsoid(hand, pad.Centre, Vector3.One, Skin, smooth: true).Basis =
                new Basis(across * pad.Size.X, along * pad.Size.Y, across.Cross(along) * pad.Size.Z);
        }
        foreach (var (from, to, radius) in Hand.Bones ?? System.Array.Empty<(Vector3, Vector3, float)>())
            Limb.Segment(hand, from, to, radius, Skin, smooth: true);
        // Each bone a little slimmer than the one before (towards the fingertip).
        foreach (var (radius, joints) in Hand.Fingers)
        {
            for (int i = 1; i < joints.Length; i++)
                Limb.Segment(hand, joints[i - 1], joints[i], radius * (1f - 0.07f * (i - 1)), Skin, smooth: true);
        }
    }

    // lateral: where the ball is relative to the body, positive on the holder's right.
    public void ChooseSide(float lateral)
    {
        if (followThrough >= 0f)
            return;
        if (Forehand && lateral < -SideHysteresis)
            Forehand = false;
        else if (!Forehand && lateral > SideHysteresis)
            Forehand = true;
    }

    // Plays the follow-through, starting from the face angle (pitch, yaw) the ball was hit with
    // and carrying on in the direction the racket was moving (world space, m/s).
    public void Strike(Vector2 face, Vector3 racketVelocity, StrokeStyle style)
    {
        followThrough = 0f;
        windup = 0f;
        strikeFace = face;
        strikeStyle = style;
        swingDirection = (GetParent<Node3D>().GlobalBasis.Inverse() * racketVelocity).Normalized();
    }

    // Holds one moment of a stroke still, for looking at the grip (the Grip Lab): windup 1 is
    // the full backswing, 0 contact; follow (0 .. 0.3) is the way through to the finish, with
    // the face at the given contact angle (pitch, yaw).
    public void Freeze(StrokeStyle style, bool forehand, float windupAmount, float follow, Vector2 face)
    {
        Style = style;
        strikeStyle = style;
        Forehand = forehand;
        side = forehand ? 0f : 1f;
        windup = windupAmount;
        prepared = PathFor(style, forehand);
        followThrough = follow > 0f ? 0.5f : -1f;
        strikeFace = face;
        swingDirection = Vector3.Zero;
        frozenFollow = follow;
    }

    public override void _Process(double delta)
    {
        Pose(frozenFollow ?? Advance((float)delta));
    }

    // Plays the stroke on: the turn between wings, the backswing and the follow-through.
    // Returns how far through to the finish the follow-through is.
    float Advance(float dt)
    {
        side = Mathf.MoveToward(side, Forehand ? 0f : 1f, dt / FlipTime);

        // Out to the finish quickly, a moment there, then back to the ready position.
        float follow = 0f;
        if (followThrough >= 0f)
        {
            followThrough += dt / FollowThroughTime;
            if (followThrough >= 1f)
                followThrough = -1f;
            else
                follow = Mathf.SmoothStep(0f, 0.3f, followThrough) * (1f - Mathf.SmoothStep(0.5f, 1f, followThrough));
        }
        float windupGoal = followThrough >= 0f ? 0f : Windup;
        float windupRate = windupGoal > windup && BackswingRate > 0f ? BackswingRate : WindupRate;
        windup = Mathf.MoveToward(windup, windupGoal, windupRate * dt);

        // The backswing eases between strokes, so changing plans does not make the paddle jump.
        StrokePath target = PathFor(Style, Forehand);
        float ease = 1f - Mathf.Exp(-10f * dt);
        prepared = new StrokePath(
            prepared.Pivot.Lerp(target.Pivot, ease), prepared.Lean.Lerp(target.Lean, ease),
            Mathf.Lerp(prepared.Back, target.Back, ease), target.Follow,
            Mathf.Lerp(prepared.Turn, target.Turn, ease), Mathf.Lerp(prepared.FaceOpen, target.FaceOpen, ease),
            Mathf.Lerp(prepared.NoseDrop, target.NoseDrop, ease));
        return follow;
    }

    void Pose(float follow)
    {
        float s = Mathf.SmoothStep(0f, 1f, side);
        float wing = Mathf.Lerp(1f, -1f, s); // +1 forehand, -1 backhand: sideways sense of the stroke
        var mirror = new Vector3(wing, 1f, 1f);

        // Before contact the paddle sits back along the prepared stroke's arc; from contact on it
        // sweeps through along the struck stroke's arc, rising or falling with the racket's real
        // swing. Both arcs pass through the contact point at angle 0.
        bool striking = followThrough >= 0f;
        StrokePath path = striking ? PathFor(strikeStyle, Forehand) : prepared;
        float lean = path.Lean.Y;
        if (striking && swingDirection != Vector3.Zero)
            lean = Mathf.Lerp(lean, Mathf.Clamp(0.9f * swingDirection.Y, -0.8f, 0.9f), 0.5f);
        float angle = striking ? path.Follow * follow : path.Back * windup;

        // Mirrored for the backhand: the pivot and the axis lean flip sideways and the arc runs
        // the other way round.
        Vector3 pivot = path.Pivot * mirror;
        Vector3 axis = new Vector3(path.Lean.X * wing, 1f, lean).Normalized();
        var swing = new Basis(axis, angle * wing);

        // From contact on, the face starts at the angle the ball was hit with; before it the
        // face opens for a push. The wrist holds that angle while the arc turns the paddle.
        float yaw = striking ? strikeFace.Y : 0f;
        float pitch = striking ? strikeFace.X : FaceTilt + path.FaceOpen * windup;

        // The torso turns with the arc: back with the backswing, through after contact. The
        // weight goes onto the back foot, then across to the front one (less on the backhand).
        BodyTurn = -angle * wing * path.Turn;
        WeightShift = wing * (windup - follow) * Mathf.Lerp(1f, 0.4f, s);

        // The handle leans towards the wrist, carrying on the line of the forearm: shakehand,
        // nearly level with the nose out to the side away from the hand, tipped by the wrist
        // through the stroke; penhold, up and left. Mirrored for the backhand.
        float nose = path.NoseDrop * (windup - follow);
        Basis gripPose = GripPose(s, nose);
        restPose = new Transform3D(new Basis(Vector3.Right, FaceTilt) * GripPose(s, 0f), Vector3.Zero);
        Basis face = new Basis(Vector3.Up, yaw) * new Basis(Vector3.Right, pitch);
        Basis basis = face * gripPose;
        // Hand and paddle turn rigidly about the swing centre (unless an arm drives them).
        if (!Driven)
        {
            Transform = new Transform3D(swing * basis, pivot - swing * pivot);
            if (AvoidTable)
                LiftOffTable();
        }

        // The forearm keeps the grip's natural angle to the hand; the nose's tipping is the wrist.
        Vector3 forearm = Hand.ForearmForehand.Normalized().Lerp(Hand.ForearmBackhand.Normalized(), s).Normalized();
        NeutralBasis = face * GripPose(s, 0f);
        ForearmLocal = forearm;
        FollowNow = follow;
        Basis holder = GetParent<Node3D>().GlobalBasis;
        ForearmDirection = (holder * (swing * face * GripPose(s, 0f) * forearm)).Normalized();
        // Measured from the hand's own axis (the palm, which may slope off the handle).
        Vector3 inPaddle = new Basis(Vector3.Right, -Hand.PalmTilt) * gripPose.Inverse() * GripPose(s, 0f) * forearm;
        WristBend = new Vector2(Mathf.Atan2(inPaddle.X, -inPaddle.Y), Mathf.Atan2(inPaddle.Z, -inPaddle.Y));

        Vector3 elbow = restPose * Hand.Wrist + restPose.Basis * forearm * Arm.ForearmSize;
        Vector3 upperArm = UpperArmForehand.Normalized().Lerp(UpperArmBackhand.Normalized(), s).Normalized();
        RestShoulderPoint = GetParent<Node3D>().GlobalTransform * (elbow + upperArm * Arm.UpperArmSize);
        UpperArmDirection = (holder * upperArm).Normalized(); // the upper arm hangs from the body, not carried round by the racket's swing
    }

    // An undriven paddle (the player's) lifts straight up out of the table where its swing would
    // take it in.
    void LiftOffTable()
    {
        Transform3D world = GetParent<Node3D>().GlobalTransform * Transform;
        float lift = 0f;
        foreach (Vector3 point in Outline)
            lift = Mathf.Max(lift, Collide.OutOfTable(world * point, RimClearance).Y);
        if (lift > 0f)
            Transform = Transform.Translated(GetParent<Node3D>().GlobalBasis.Inverse() * (Vector3.Up * lift));
    }

    public const float RimClearance = 0.012f; // the rubber's edge and a little air

    // The racket's turn in holder space held at rest in the forehand or backhand grip, the face
    // as at contact and no wrist movement (NeutralBasis of a paddle frozen at contact): what the
    // pose motions' racket angles are measured from (PoseLab, PoseRig).
    public Basis NaturalHold(bool forehand) => GripPose(forehand ? 0f : 1f, 0f);
    // The same for a shakehand grip at the standard roll, without a paddle (PoseSequence.Blend).
    public static Basis StandardHold(bool forehand)
    {
        float s = forehand ? 0f : 1f;
        return new Basis(Vector3.Back, Mathf.Lerp(-1.5f, 1.5f, s)) * new Basis(Vector3.Up, s * Mathf.Pi);
    }

    // The hand's palm, out of it (world): for the shakehand hand (null for the penhold's).
    public Vector3? PalmOut() =>
        Grip == GripStyle.Penhold ? null : (GlobalBasis.Orthonormalized() * new Vector3(0f, 0.12f, -1f)).Normalized();

    // How far the wrist is bent off the grip's line (degrees: the forearm against NaturalForearm),
    // and how much of it is towards the palm (1 all of it, -1 all back, 0 sideways). On that line
    // the wrist is already bent back about 25° (a shakehand grip's ready wrist), so it has far more
    // room towards the palm than back or sideways.
    public (float Bend, float Palmward) WristOffGrip(Vector3 elbow, bool forehand)
    {
        Vector3 forearm = (elbow - GlobalTransform * WristLocal).Normalized();
        Vector3 line = (GlobalBasis * NaturalForearm(forehand)).Normalized();
        float bend = Mathf.RadToDeg(forearm.AngleTo(line));
        Vector3 off = forearm - line * forearm.Dot(line);
        if (PalmOut() is not Vector3 palm || off.LengthSquared() < 1e-8f)
            return (bend, 0f);
        // Bent towards the palm, the forearm comes round to the palm's side of the hand.
        Vector3 palmSide = palm - line * palm.Dot(line);
        return (bend, palmSide.LengthSquared() < 1e-8f ? 0f : off.Normalized().Dot(palmSide.Normalized()));
    }

    // The forearm's line from the wrist in that hold (paddle space): ForearmLocal at rest.
    public Vector3 NaturalForearm(bool forehand) => (forehand ? Hand.ForearmForehand : Hand.ForearmBackhand).Normalized();

    // s: 0 forehand .. 1 backhand; noseDrop: radians the shakehand nose tips below its rest.
    Basis GripPose(float s, float noseDrop)
    {
        if (Grip == GripStyle.Penhold)
            return new Basis(Vector3.Back, Mathf.Pi + Mathf.Lerp(PenholdRoll, -PenholdRoll, s));
        float roll = GripRoll + noseDrop;
        return new Basis(Vector3.Back, Mathf.Lerp(-roll, roll, s)) * new Basis(Vector3.Up, s * Mathf.Pi);
    }
}
