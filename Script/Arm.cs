using Godot;

// A jointed arm: shoulder, upper arm, elbow, forearm and wrist, posed every frame by two-bone IK
// from the shoulder to a wrist target, with the elbow bending towards a pole direction. The
// hand belongs to whatever the wrist holds (see the grip in Paddle); WithHand adds a free left
// hand with a palm, four three-jointed fingers and a thumb, which Open bends from a relaxed curl
// to flat (to hold the ball on the open palm for a serve).
public partial class Arm : Node3D
{
    [Export] public Material Skin;
    [Export] public Material Sleeve;
    public const float UpperArmSize = 0.3f;
    public const float ForearmSize = 0.27f;

    [Export] public float UpperArmLength = UpperArmSize;
    [Export] public float ForearmLength = ForearmSize;
    [Export] public bool WithHand;
    [Export] public bool ForearmOnly; // the shoulder and upper arm hidden (no body to join)

    // Hand space: origin at the wrist, +Y along the fingers, +Z out of the back of the hand
    // (the palm faces -Z) and +X towards the thumb, which for a left hand is Y x Z.
    public const float PalmCentre = 0.045f;        // from the wrist along the fingers
    public const float PalmHalfThickness = 0.016f;

    public float Open; // 0 relaxed, fingers loosely curled; 1 flat and straight

    // Where the joints are now (world space), for keeping things out of the arm.
    public Vector3 ShoulderPoint { get; private set; }
    public Vector3 ElbowPoint { get; private set; }
    public Vector3 WristPoint { get; private set; }
    public Vector3 PalmFacing { get; private set; } = Vector3.Down; // the free hand's palm (world)

    // Per finger: knuckle across the palm, the three phalanges' lengths and the radius.
    static readonly (float X, float Y, float[] Lengths, float Radius)[] Fingers =
    {
        (0.029f, 0.09f, new[] { 0.039f, 0.024f, 0.019f }, 0.0085f),   // index
        (0.009f, 0.093f, new[] { 0.044f, 0.028f, 0.02f }, 0.0088f),   // middle
        (-0.011f, 0.09f, new[] { 0.041f, 0.027f, 0.02f }, 0.0082f),   // ring
        (-0.03f, 0.082f, new[] { 0.032f, 0.019f, 0.017f }, 0.0072f),  // little
    };
    // Joint bend (radians, towards the palm) relaxed and open, from the knuckle out.
    static readonly float[] RelaxedCurl = { 0.6f, 0.75f, 0.45f };
    static readonly float[] OpenCurl = { 0.04f, 0.05f, 0.03f };
    static readonly float[] ThumbLengths = { 0.034f, 0.03f, 0.024f };
    const float ThumbRadius = 0.0098f;

    // The arm's shape round the bones, as ellipsoids placed along them every frame (full sizes:
    // across, along, across): the biceps swelling the middle of the upper arm, the point of the
    // elbow (olecranon) sticking out behind the bend just past the end of the upper arm, and the
    // forearm's muscles making it widest just below the elbow, tapering to a slim wrist.
    static readonly Vector3 BicepsSize = new(0.098f, 0.2f, 0.098f);
    const float BicepsAt = 0.45f;                     // of the way from shoulder to elbow
    static readonly Vector3 OlecranonSize = new(0.05f, 0.06f, 0.05f);
    static readonly Vector3 ForearmMuscleSize = new(0.088f, 0.15f, 0.088f);
    const float ForearmMuscleAt = 0.28f;              // of the way from elbow to wrist

    MeshInstance3D shoulder;
    MeshInstance3D upperArm;
    MeshInstance3D biceps;
    MeshInstance3D elbow;
    MeshInstance3D olecranon;
    MeshInstance3D forearm;
    MeshInstance3D forearmMuscle;
    MeshInstance3D wrist;
    Node3D hand;
    Node3D[][] fingerJoints;
    Node3D[] thumbJoints;
    Node3D thumbBase;

    public override void _Ready()
    {
        TopLevel = true;
        shoulder = Limb.Joint(this, 0.062f, Sleeve);
        upperArm = Limb.Bone(this, 0.05f, 0.036f, Skin);
        biceps = Limb.Ellipsoid(this, Vector3.Zero, Vector3.One, Skin);
        elbow = Limb.Joint(this, 0.038f, Skin);
        olecranon = Limb.Ellipsoid(this, Vector3.Zero, Vector3.One, Skin);
        forearm = Limb.Bone(this, 0.037f, 0.026f, Skin);
        forearmMuscle = Limb.Ellipsoid(this, Vector3.Zero, Vector3.One, Skin);
        wrist = Limb.Joint(this, 0.028f, Skin);
        if (WithHand)
            BuildHand();
        // Only the forearm shows, ending at the rounded elbow (an arm with no body behind it).
        if (ForearmOnly)
            shoulder.Visible = upperArm.Visible = biceps.Visible = false;
    }

    // The palm is a flattened ellipsoid; each finger and the thumb is a chain of joints, every
    // joint carrying its phalanx, so bending a joint carries the rest of the finger with it.
    void BuildHand()
    {
        hand = new Node3D();
        AddChild(hand);
        Limb.Ellipsoid(hand, new Vector3(0f, PalmCentre, 0f), new Vector3(0.085f, 0.1f, 2f * PalmHalfThickness), Skin);

        fingerJoints = new Node3D[Fingers.Length][];
        for (int f = 0; f < Fingers.Length; f++)
            fingerJoints[f] = Chain(hand, new Vector3(Fingers[f].X, Fingers[f].Y, 0f), Fingers[f].Lengths, Fingers[f].Radius);

        // The thumb leaves the palm low on the thumb side, angled out and a little in front of it.
        thumbBase = new Node3D
        {
            Position = new Vector3(0.034f, 0.022f, -0.006f),
            Basis = Limb.AlongY(new Vector3(0.62f, 0.72f, -0.3f))
        };
        hand.AddChild(thumbBase);
        thumbJoints = Chain(thumbBase, Vector3.Zero, ThumbLengths, ThumbRadius);
    }

    Node3D[] Chain(Node3D parent, Vector3 start, float[] lengths, float radius)
    {
        var joints = new Node3D[lengths.Length];
        Node3D at = parent;
        Vector3 offset = start;
        for (int k = 0; k < lengths.Length; k++)
        {
            joints[k] = new Node3D { Position = offset };
            at.AddChild(joints[k]);
            Limb.Segment(joints[k], Vector3.Zero, new Vector3(0f, lengths[k], 0f), radius * (1f - 0.08f * k), Skin);
            at = joints[k];
            offset = new Vector3(0f, lengths[k], 0f);
        }
        return joints;
    }

    // palmFacing and fingers (world directions) turn the free hand: the way the palm faces, and
    // the way the fingers of the open hand point (as it relaxes they fall back in line with the
    // forearm); zero vectors leave the fingers along the forearm, palm down.
    public void Solve(Vector3 shoulderPos, Vector3 wristTarget, Vector3 pole, Vector3 palmFacing = default, Vector3 fingers = default)
    {
        if (wristTarget.DistanceTo(shoulderPos) < 0.001f)
            return;
        // The arm keeps its length: out of reach, the hand stops at full stretch (WristPoint) and
        // whatever it holds must come with it.
        wristTarget = Limb.Reach(shoulderPos, wristTarget, UpperArmLength, ForearmLength);
        Vector3 elbowPos = Limb.TwoBone(shoulderPos, wristTarget, UpperArmLength, ForearmLength, pole);

        shoulder.GlobalPosition = shoulderPos;
        ShoulderPoint = shoulderPos;
        ElbowPoint = elbowPos;
        WristPoint = wristTarget;
        Limb.PlaceBone(upperArm, shoulderPos, elbowPos);
        elbow.GlobalPosition = elbowPos;
        Limb.PlaceBone(forearm, elbowPos, wristTarget);
        wrist.GlobalPosition = wristTarget;
        ShapeArm(shoulderPos, elbowPos, wristTarget);
        if (hand != null)
            PoseHand(wristTarget, wristTarget - elbowPos, palmFacing, fingers);
    }

    void ShapeArm(Vector3 shoulderPos, Vector3 elbowPos, Vector3 wristPos)
    {
        Vector3 upper = elbowPos - shoulderPos;
        Vector3 lower = wristPos - elbowPos;
        Vector3 u = upper.Normalized();
        Vector3 f = lower.Normalized();
        Place(biceps, shoulderPos + upper * BicepsAt, u, BicepsSize);
        Place(forearmMuscle, elbowPos + lower * ForearmMuscleAt, f, ForearmMuscleSize);

        // Behind the bend: away from the forearm, square to the upper arm (straight down the upper
        // arm's line when the arm is straight).
        Vector3 behind = -(f - u * f.Dot(u));
        behind = behind.LengthSquared() > 1e-6f ? behind.Normalized() : Vector3.Zero;
        Place(olecranon, elbowPos + u * 0.012f + behind * 0.012f, u, OlecranonSize);
    }

    static void Place(MeshInstance3D part, Vector3 at, Vector3 along, Vector3 size) =>
        part.GlobalTransform = new Transform3D(Limb.AlongY(along) * Basis.FromScale(size), at);

    void PoseHand(Vector3 at, Vector3 forearmDir, Vector3 palmFacing, Vector3 fingers)
    {
        Vector3 back = palmFacing.LengthSquared() > 1e-6f ? -palmFacing.Normalized() : Vector3.Up;
        PalmFacing = -back;
        Vector3 y = fingers.LengthSquared() > 1e-6f
            ? forearmDir.Normalized().Lerp(fingers.Normalized(), Mathf.Clamp(Open, 0f, 1f))
            : forearmDir;
        y -= back * y.Dot(back);
        if (y.LengthSquared() < 1e-6f)
            y = Limb.AlongY(back).X;
        y = y.Normalized();
        hand.GlobalTransform = new Transform3D(new Basis(y.Cross(back), y, back), at);

        // Bending towards the palm (-Z) is a negative turn about X.
        float open = Mathf.Clamp(Open, 0f, 1f);
        foreach (Node3D[] finger in fingerJoints)
        {
            for (int k = 0; k < finger.Length; k++)
                finger[k].Basis = new Basis(Vector3.Right, -Mathf.Lerp(RelaxedCurl[k], OpenCurl[k], open));
        }
        // The thumb folds across in front of the palm when relaxed and lies out beside it when open.
        thumbBase.Basis = Limb.AlongY(new Vector3(Mathf.Lerp(0.35f, 0.62f, open), 0.72f, Mathf.Lerp(-0.55f, -0.25f, open)));
        for (int k = 0; k < thumbJoints.Length; k++)
            thumbJoints[k].Basis = new Basis(Vector3.Right, -Mathf.Lerp(0.35f, 0.05f, open) * (k == 0 ? 0.5f : 1f));
    }
}
