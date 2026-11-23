using Godot;
using System.Linq;
using System.Collections.Generic;

// Checks a body's pose for what a person cannot do, read off the body itself (the joints the
// arms and legs were solved to, the racket in the hand), so the same check serves the Pose Lab
// and the NPCs in the game:
//  - joints past their normal range: an elbow or knee bent further than a body bends, the waist
//    twisted or the trunk leaned too far, the wrist bent off the grip's line;
//  - wrong angles: at a contact, the racket's hitting face turned away from the table;
//  - balance: the centre of gravity (body segments weighted as Winter's tables, as the Pose Lab's
//    red ball) outside the area the planted feet stand on.
// The ranges are the usual active ranges of motion for a fit adult, a little generous.
public static class PoseCheck
{
    public enum Kind { Elbow, Knee, Wrist, Waist, Lean, Balance, Face }

    // Serious: a person cannot do it at all (a problem); otherwise at the edge (a warning).
    public record Issue(Kind Kind, string Text, bool Serious, float Amount);

    // Inner angles (180 = straight): an elbow bends to about 145° of flexion, a knee to about 140°.
    public const float ElbowMostBent = 35f;
    public const float KneeMostBent = 40f;
    // The wrist off the grip's line (Paddle.WristOffGrip): about 70° back or sideways, but about 110°
    // towards the palm (the grip's line has it bent back already; a wrist flexes to about 80°).
    public const float WristMost = 70f;
    public const float WristPalmMost = 110f;

    public static float WristRange(float palmward) =>
        WristMost + (WristPalmMost - WristMost) * Mathf.Max(palmward, 0f) * Mathf.Max(palmward, 0f);

    public static string WristWay(float palmward) =>
        palmward > 0.7f ? "towards the palm" : palmward < -0.7f ? "back" : "sideways";
    public const float WaistMost = 60f;        // chest turned from the hips
    public const float LeanForwardMost = 65f;  // trunk from upright
    public const float LeanBackMost = 20f;
    public const float BalanceRoom = 0.03f;    // m the centre of gravity may be past the feet's edge (a warning up to 0.08)
    public const float BalanceFall = 0.08f;

    // The foot's sole round the ankle (the toes' way forward): heel back, toes forward, half width.
    const float Heel = 0.06f, Toe = 0.19f, HalfFoot = 0.05f;
    const float StepLift = 0.1f; // a foot lower than this is stepping, not off the floor

    public readonly struct Body
    {
        public readonly Node3D Upper, Chest;
        public readonly Leg[] Legs;
        public readonly Arm RightArm, LeftArm;
        public readonly Paddle Paddle;
        public readonly Basis ToWorld;
        public Body((Node3D Body, Node3D Upper, Node3D Chest, Leg[] Legs, Arm RightArm, Arm LeftArm, Paddle Paddle) rig, Basis toWorld)
        {
            (_, Upper, Chest, Legs, RightArm, LeftArm, Paddle) = rig;
            ToWorld = toWorld;
        }
    }

    // forehand: the side the racket plays (the hitting face); atContact: check the face's way.
    public static List<Issue> Run(Body b, bool forehand, bool atContact)
    {
        var issues = new List<Issue>();
        static float Inner(Vector3 a, Vector3 joint, Vector3 c) => Mathf.RadToDeg((a - joint).AngleTo(c - joint));
        Basis toOwn = b.ToWorld.Transposed();

        foreach (Arm arm in new[] { b.RightArm, b.LeftArm })
        {
            float elbow = Inner(arm.ShoulderPoint, arm.ElbowPoint, arm.WristPoint);
            if (elbow < ElbowMostBent)
                issues.Add(new(Kind.Elbow, $"{(arm == b.RightArm ? "right" : "left")} elbow bent to {elbow:F0}° (it bends to about {ElbowMostBent:F0}°)", true, ElbowMostBent - elbow));
        }
        for (int i = 0; i < b.Legs.Length; i++)
        {
            Leg leg = b.Legs[i];
            float knee = Inner(leg.HipPoint, leg.KneePoint, leg.AnklePoint);
            if (knee < KneeMostBent)
                issues.Add(new(Kind.Knee, $"{(i == 0 ? "left" : "right")} knee bent to {knee:F0}° (it bends to about {KneeMostBent:F0}°)", true, KneeMostBent - knee));
        }

        // The wrist: the forearm off the line the grip holds it on, as far as a wrist goes that way.
        var (bend, palmward) = b.Paddle.WristOffGrip(b.RightArm.ElbowPoint, forehand);
        float range = WristRange(palmward);
        if (bend > range)
            issues.Add(new(Kind.Wrist, $"wrist bent {bend:F0}° off the grip's line, {WristWay(palmward)} (a wrist goes about {range:F0}° that way)", true, bend - range));

        // The waist: the chest's facing against the hips', about the vertical.
        Vector3 Flat(Vector3 v) => new Vector3(v.X, 0f, v.Z).Normalized();
        Vector3 hipsAcross = Flat(b.Upper.GlobalBasis.X), chestAcross = Flat(b.Chest.GlobalBasis.X);
        float twist = Mathf.RadToDeg(hipsAcross.AngleTo(chestAcross));
        if (twist > WaistMost)
            issues.Add(new(Kind.Waist, $"waist twisted {twist:F0}° (about {WaistMost:F0}° at most)", twist > WaistMost + 15f, twist - WaistMost));

        // The trunk's lean: the spine (hips to shoulders) from upright, forward towards the table.
        Vector3 spine = toOwn * (b.Chest.GlobalTransform * new Vector3(0f, 0.33f, 0f) - b.Upper.GlobalPosition);
        float lean = Mathf.RadToDeg(Mathf.Atan2(spine.Z, spine.Y));
        float leanSide = Mathf.RadToDeg(Mathf.Atan2(Mathf.Abs(spine.X), spine.Y));
        if (lean > LeanForwardMost)
            issues.Add(new(Kind.Lean, $"trunk leaning {lean:F0}° forward (about {LeanForwardMost:F0}° at most)", lean > LeanForwardMost + 15f, lean - LeanForwardMost));
        else if (lean < -LeanBackMost)
            issues.Add(new(Kind.Lean, $"trunk leaning {-lean:F0}° back (about {LeanBackMost:F0}° at most)", true, -lean - LeanBackMost));
        if (leanSide > 40f)
            issues.Add(new(Kind.Lean, $"trunk leaning {leanSide:F0}° to the side", leanSide > 55f, leanSide - 40f));

        // Balance: the centre of gravity over the soles of the feet. A foot stepping (just off the
        // floor) counts where it is going down: in a step the body moves over from one foot to
        // the other, between them. Feet well off the floor (a jump) are not stood on.
        Vector3 cog = toOwn * CentreOfGravity(b);
        var sole = new List<Vector2>();
        foreach (Leg leg in b.Legs)
        {
            Vector3 ankle = toOwn * leg.AnklePoint;
            if (ankle.Y - Leg.AnkleHeight > StepLift)
                continue; // off the floor
            Vector3 toes = toOwn * leg.Toes;
            var ahead = new Vector2(toes.X, toes.Z).Normalized();
            var side = new Vector2(-ahead.Y, ahead.X);
            var at = new Vector2(ankle.X, ankle.Z);
            foreach (float along in new[] { -Heel, Toe })
                foreach (float across in new[] { -HalfFoot, HalfFoot })
                    sole.Add(at + ahead * along + side * across);
        }
        if (sole.Count > 0)
        {
            float outside = OutsideHull(sole, new Vector2(cog.X, cog.Z));
            if (outside > BalanceRoom)
                issues.Add(new(Kind.Balance, $"off balance: centre of gravity {outside * 100f:F0} cm outside the feet", outside > BalanceFall, outside));
        }

        // The hitting face (the forehand rubber is on the paddle's -Z side, the backhand one on +Z)
        // must look towards the table at the contact.
        if (atContact)
        {
            Vector3 face = (toOwn * (b.Paddle.GlobalBasis.Z * (forehand ? -1f : 1f))).Normalized();
            if (face.Z < 0.1f)
                issues.Add(new(Kind.Face, $"racket face turned away from the table at the contact ({Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(face.Z, -1f, 1f))):F0}° off)", true, 0.1f - face.Z));
        }
        return issues;
    }

    // The centre of gravity (world), body segments weighted as the Pose Lab's (Winter).
    public static Vector3 CentreOfGravity(Body b)
    {
        static Vector3 Along(Vector3 a, Vector3 c, float f) => a + (c - a) * f;
        Vector3 shoulders = (b.RightArm.ShoulderPoint + b.LeftArm.ShoulderPoint) * 0.5f;
        Vector3 sum = 0.497f * Along(b.Upper.GlobalPosition, shoulders, 0.5f);
        sum += 0.081f * (b.Chest.GlobalTransform * new Vector3(0f, 0.53f, 0f));
        foreach (Arm arm in new[] { b.RightArm, b.LeftArm })
        {
            sum += 0.028f * Along(arm.ShoulderPoint, arm.ElbowPoint, 0.436f);
            sum += 0.016f * Along(arm.ElbowPoint, arm.WristPoint, 0.43f);
            sum += 0.006f * (arm.WristPoint + (arm.WristPoint - arm.ElbowPoint).Normalized() * 0.05f);
        }
        foreach (Leg leg in b.Legs)
        {
            sum += 0.1f * Along(leg.HipPoint, leg.KneePoint, 0.433f);
            sum += 0.0465f * Along(leg.KneePoint, leg.AnklePoint, 0.433f);
            sum += 0.0145f * (leg.AnklePoint + leg.Toes * 0.07f - Vector3.Up * 0.035f);
        }
        return sum;
    }

    // How far the point is outside the convex hull of these points (0 inside).
    static float OutsideHull(List<Vector2> points, Vector2 p)
    {
        Vector2[] hull = Geometry2D.ConvexHull(points.ToArray());
        if (hull.Length < 3)
            return 0f;
        bool inside = Geometry2D.IsPointInPolygon(p, hull);
        if (inside)
            return 0f;
        float best = float.MaxValue;
        for (int i = 0; i < hull.Length; i++)
        {
            Vector2 a = hull[i], c = hull[(i + 1) % hull.Length];
            best = Mathf.Min(best, p.DistanceTo(Geometry2D.GetClosestPointToSegment(p, a, c)));
        }
        return best;
    }

    public static string Describe(IEnumerable<Issue> issues) =>
        string.Join("; ", System.Linq.Enumerable.Select(issues, i => (i.Serious ? "✖ " : "! ") + i.Text));
}
