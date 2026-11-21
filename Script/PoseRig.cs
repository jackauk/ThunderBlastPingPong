using Godot;
using System.Collections.Generic;

// Lays a pose (the Pose Lab's values, in the NPC's own frame: metres and degrees, the table
// centred at the origin, the player behind the -Z end line facing +Z, its right hand at -X) on an
// NPC's body, the way the Pose Lab does (PoseLab.Apply), so the game can play saved motions:
// the trunk (hips, lean, waist), the legs to the planted feet (the hips coming down where a leg
// could not reach), the racket in the hand from its natural hold, the arms (keeping their
// length), the free hand and its palm. The ball is the game's, not posed here.
public sealed class PoseRig
{
    // Body geometry, as the Pose Lab's and the NPC's.
    const float HipHalfWidth = 0.1f;
    static readonly Vector3 RightShoulderOnChest = new(-0.2f, 0.33f, 0f);
    static readonly Vector3 LeftShoulderOnChest = new(0.2f, 0.33f, 0f);

    readonly Node3D body, upper, chest;
    readonly Leg[] legs;
    readonly Arm rightArm, leftArm;
    readonly Paddle paddle;
    readonly Basis toWorld;
    Dictionary<string, float> pose = new();

    public PoseRig((Node3D Body, Node3D Upper, Node3D Chest, Leg[] Legs, Arm RightArm, Arm LeftArm, Paddle Paddle) rig, Basis toWorld)
    {
        (body, upper, chest, legs, rightArm, leftArm, paddle) = rig;
        this.toWorld = toWorld;
    }

    Vector3 W(Vector3 own) => toWorld * own;
    Vector3 O(Vector3 world) => toWorld.Transposed() * world;
    float V(string key) => pose.TryGetValue(key, out float v) ? v : PoseLab.DefaultValue(key);
    float R(string key) => Mathf.DegToRad(V(key));
    Vector3 FootAt(int i) => new(V(i == 0 ? "lfoot.x" : "rfoot.x"), V(i == 0 ? "lfoot.lift" : "rfoot.lift"), V(i == 0 ? "lfoot.z" : "rfoot.z"));
    Vector3 ToesOf(int i)
    {
        float turn = R(i == 0 ? "lfoot.turn" : "rfoot.turn");
        return new Vector3(Mathf.Sin(turn), 0f, Mathf.Cos(turn));
    }
    Vector3 LeftHandAt => new(V("lhand.x"), V("lhand.y"), V("lhand.z"));
    Vector3 Palm
    {
        get
        {
            float up = R("lhand.palmUp"), turn = R("lhand.palmTurn");
            return new Vector3(Mathf.Sin(turn) * Mathf.Cos(up), Mathf.Sin(up), Mathf.Cos(turn) * Mathf.Cos(up));
        }
    }
    bool Forehand => V("racket.backhand") < 0.5f;
    // From the natural hold (the paddle's, its holder space turned to face the table).
    // (Face open and nose forward mean the same on either side: PoseSequence.AngleSide.)
    Basis RacketBasis => new Basis(Vector3.Up, R("racket.turn"))
        * new Basis(Vector3.Up, Mathf.Pi) * paddle.NaturalHold(Forehand)
        * new Basis(Vector3.Up, PoseSequence.AngleSide(Forehand) * R("racket.open"))
        * new Basis(Vector3.Back, -R("racket.noseUp"))
        * new Basis(Vector3.Right, PoseSequence.AngleSide(Forehand) * R("racket.noseForward"));

    public Vector3 ShoulderAt(bool right)
    {
        string s = right ? "rshoulder" : "lshoulder";
        Vector3 rest = right ? RightShoulderOnChest : LeftShoulderOnChest;
        return chest.GlobalTransform * (rest + new Vector3(0f, V(s + ".up"), V(s + ".forward")));
    }

    // Where the racket's middle is for these values (own frame), the arm's reach aside.
    public static Vector3 RacketAt(Dictionary<string, float> values) =>
        new(values.GetValueOrDefault("racket.x"), values.GetValueOrDefault("racket.y"), values.GetValueOrDefault("racket.z"));

    public void Apply(Dictionary<string, float> values)
    {
        pose = new Dictionary<string, float>(values);
        PlaceTrunk();

        for (int i = 0; i < 2; i++)
        {
            Vector3 hip = upper.GlobalTransform * new Vector3((i == 0 ? 1f : -1f) * HipHalfWidth, 0f, 0f);
            legs[i].Solve(hip, W(FootAt(i)), W(ToesOf(i)), KneeToward(i, hip));
            // Out of reach: the foot slides in along the floor towards the hips, never up off it.
            Vector3 footGot = O(legs[i].AnklePoint) - Vector3.Up * Leg.AnkleHeight;
            if (footGot.DistanceTo(FootAt(i)) > 1e-4f)
            {
                string side = i == 0 ? "lfoot" : "rfoot";
                Vector3 hipOwn = O(hip), foot = FootAt(i);
                float reach = LegReach(i), high = hipOwn.Y - (foot.Y + Leg.AnkleHeight);
                float across = high < reach ? Mathf.Sqrt(reach * reach - high * high) : 0f;
                var way = new Vector2(foot.X - hipOwn.X, foot.Z - hipOwn.Z).Normalized();
                pose[side + ".x"] = hipOwn.X + way.X * across;
                pose[side + ".z"] = hipOwn.Z + way.Y * across;
                legs[i].Solve(hip, W(FootAt(i)), W(ToesOf(i)), KneeToward(i, hip));
            }
        }

        Vector3 rightShoulder = ShoulderAt(true), leftShoulder = ShoulderAt(false);
        var racket = new Transform3D(toWorld * RacketBasis, W(RacketAt(pose)));
        paddle.GlobalTransform = racket;
        Vector3 rightWrist = racket * paddle.WristLocal;
        // The elbow starts where the grip puts it (up the forearm's natural line), the swivel
        // turning it out or in from there.
        Vector3 gripElbow = rightWrist + (racket.Basis * paddle.NaturalForearm(Forehand)).Normalized() * Arm.ForearmSize;
        rightArm.Solve(rightShoulder, rightWrist, Pole(rightShoulder, rightWrist, gripElbow - rightShoulder, R("relbow.swivel"), W(Vector3.Left)));
        // The arm keeps its length: a racket out of reach comes in with the hand at full stretch.
        if (rightArm.WristPoint.DistanceTo(rightWrist) > 1e-4f)
            paddle.GlobalPosition += rightArm.WristPoint - rightWrist;

        leftArm.Open = V("lhand.open");
        leftArm.Solve(leftShoulder, W(LeftHandAt), Pole(leftShoulder, W(LeftHandAt), Vector3.Down, R("lelbow.swivel"), W(Vector3.Right)), W(Palm));
    }

    // The body as it stands, read back into values (as the Pose Lab reads the game's pose): the
    // hips' place (their shift over the feet included) as the body's, the feet, the racket's
    // place and its turn from the natural hold, the elbows' swivels, the free hand.
    public Dictionary<string, float> Read()
    {
        var p = new Dictionary<string, float>();
        static float Deg(float radians) => Mathf.RadToDeg(radians);

        Vector3 hipsAt = O(upper.GlobalPosition);
        p["body.x"] = hipsAt.X;
        p["body.z"] = hipsAt.Z;
        p["hips.height"] = upper.Position.Y;
        p["hips.lean"] = Deg(upper.Rotation.X);
        p["hips.turn"] = Deg(upper.Rotation.Y);
        p["hips.side"] = Deg(upper.Rotation.Z);
        Basis hips = upper.Basis;
        p["waist.turn"] = Deg((hips * chest.Basis * hips.Inverse()).GetEuler().Y);

        for (int i = 0; i < 2; i++)
        {
            string side = i == 0 ? "lfoot" : "rfoot";
            Vector3 foot = O(legs[i].AnklePoint) - Vector3.Up * Leg.AnkleHeight;
            Vector3 toes = O(legs[i].Toes);
            p[$"{side}.x"] = foot.X;
            p[$"{side}.z"] = foot.Z;
            p[$"{side}.lift"] = Mathf.Max(foot.Y, 0f);
            p[$"{side}.turn"] = Deg(Mathf.Atan2(toes.X, toes.Z));
        }

        Basis racket = toWorld.Transposed() * paddle.GlobalBasis.Orthonormalized();
        Vector3 racketAt = O(paddle.GlobalPosition);
        p["racket.x"] = racketAt.X;
        p["racket.y"] = racketAt.Y;
        p["racket.z"] = racketAt.Z;
        bool forehand = paddle.Forehand;
        p["racket.backhand"] = forehand ? 0f : 1f;
        Basis natural = new Basis(Vector3.Up, Mathf.Pi) * paddle.NaturalHold(forehand);
        Vector3 turns = (natural.Inverse() * racket).Orthonormalized().GetEuler(EulerOrder.Yzx);
        p["racket.open"] = PoseSequence.AngleSide(forehand) * Deg(turns.Y);
        p["racket.noseUp"] = -Deg(turns.Z);
        p["racket.noseForward"] = PoseSequence.AngleSide(forehand) * Deg(turns.X);
        p["racket.turn"] = 0f;

        Vector3 rightShoulder = chest.GlobalTransform * RightShoulderOnChest, leftShoulder = chest.GlobalTransform * LeftShoulderOnChest;
        Vector3 wrist = paddle.GlobalTransform * paddle.WristLocal;
        Vector3 gripElbow = wrist + (paddle.GlobalBasis * paddle.NaturalForearm(forehand)).Normalized() * Arm.ForearmSize;
        p["relbow.swivel"] = Deg(Swivel(rightShoulder, wrist, gripElbow - rightShoulder, rightArm.ElbowPoint, W(Vector3.Left)));

        Vector3 hand = O(leftArm.WristPoint);
        p["lhand.x"] = hand.X;
        p["lhand.y"] = hand.Y;
        p["lhand.z"] = hand.Z;
        Vector3 palm = O(leftArm.PalmFacing).Normalized();
        p["lhand.palmUp"] = Deg(Mathf.Asin(Mathf.Clamp(palm.Y, -1f, 1f)));
        p["lhand.palmTurn"] = Deg(Mathf.Atan2(palm.X, palm.Z));
        p["lhand.open"] = leftArm.Open;
        p["lelbow.swivel"] = Deg(Swivel(leftShoulder, leftArm.WristPoint, Vector3.Down, leftArm.ElbowPoint, W(Vector3.Right)));
        return p;
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

    void PlaceTrunk()
    {
        body.GlobalTransform = new Transform3D(toWorld, W(new Vector3(V("body.x"), 0f, V("body.z"))));
        upper.Position = new Vector3(0f, V("hips.height"), 0f);
        upper.Rotation = new Vector3(R("hips.lean"), R("hips.turn"), R("hips.side"));
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
            pose["hips.height"] = V("hips.height") - drop;
            upper.Position = new Vector3(0f, V("hips.height"), 0f);
        }
        Basis hips = upper.Basis;
        chest.Basis = hips.Inverse() * new Basis(Vector3.Up, R("waist.turn")) * hips
                      * Basis.FromEuler(new Vector3(R("waist.bend"), 0f, R("waist.side")));
    }

    Vector3 KneeToward(int i, Vector3 hip) =>
        Pole(hip, W(FootAt(i)) + Vector3.Up * Leg.AnkleHeight, W(ToesOf(i)), R(i == 0 ? "lknee.swivel" : "rknee.swivel"), W(i == 0 ? Vector3.Right : Vector3.Left));

    float LegReach(int i) => 0.998f * (legs[i].ThighLength + legs[i].ShinLength);

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
}
