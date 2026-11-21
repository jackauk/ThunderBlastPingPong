using Godot;

// The paddle arm as a chain of joints that drives the racket: the waist (the chest turning about
// the vertical through the spine), the shoulder, the elbow and the wrist. A stroke turns each
// joint by its own angle, within what a body can do, and the racket goes where the chain puts
// it, so the arm never takes a shape a person could not.
//
// The racket's speed at contact is the sum of what each joint gives, its turning speed times its
// lever to the racket: the waist round the spine, the elbow opening or closing, the wrist
// snapping, and the shoulder the rest. How a stroke shares it out follows the stroke (a drive
// comes from the body and shoulder, a push from the forearm and wrist). The backswing winds each
// joint back along its own speed and the follow-through carries it on.
public partial class Opponent
{
    // Share of the racket's speed the waist, elbow and wrist give (the shoulder gives the rest).
    record JointShare(float Waist, float Elbow, float Wrist);

    static JointShare ShareFor(StrokeStyle style, bool serve) => serve
        ? new(0.05f, 0.3f, 0.45f)
        : style switch
        {
            StrokeStyle.Drive => new(0.35f, 0.2f, 0.15f),
            StrokeStyle.Smash => new(0.4f, 0.25f, 0.1f),
            StrokeStyle.Push => new(0.05f, 0.4f, 0.3f),
            _ => new(0.1f, 0.2f, 0.1f), // block
        };

    // Joint speeds (rad/s): the waist about the vertical, the elbow about its hinge (positive
    // opens it), the wrist about the racket face's normal; the shoulder turns about any axis.
    // The parts are each joint's share of the racket's velocity (m/s, world space).
    struct JointSpeeds
    {
        public float Waist, Elbow, Wrist;
        public Vector3 Shoulder;
        public Vector3 WaistPart, ShoulderPart, ElbowPart, WristPart;
    }

    // The arm at contact (world space): the joints, their axes and the racket.
    struct Chain
    {
        public Vector3 Hit, Spine, Shoulder, Elbow, Wrist, ElbowAxis, WristAxis;
        public float ElbowAngle; // inside angle between upper arm and forearm
        public Transform3D Racket;
    }

    // A real player's top joint speeds (see Human).
    const float MaxWaistSpeed = Human.TrunkTurn;
    const float MaxShoulderSpeed = Human.ShoulderTurn;
    const float MaxElbowSpeed = Human.ElbowTurn;
    const float MaxWristSpeed = Human.WristTurn;
    const float BackswingTime = Human.ForwardSwingTime;  // the backswing winds each joint back this long at its stroke speed
    const float FollowTime = 0.16f;      // and the follow-through carries it on this long
    const float MaxWaistAngle = 0.9f;    // radians either way from the contact pose
    const float WaistEase = 12f;         // fastest the waist turns, rad/s
    const float MaxShoulderAngle = 1.3f;
    const float MaxWristAngle = 0.65f;
    const float MinElbowAngle = 0.6f;    // inside angle: fully bent (about 35 degrees) .. nearly straight
    const float MaxElbowAngle = 3.0f;
    const float WristGiveFrom = 0.35f;   // radians the wrist bends off the grip's forearm before the hand gives
    const float ChestClearance = 0.22f;  // the elbow stays this far out from the middle of the chest

    JointSpeeds strokeSpeeds;            // the struck stroke's joint speeds, for its follow-through
    bool followingThrough;
    float giveWeight = 1f;
    Transform3D untwistedHips;           // hips frame with the waist untwisted, from UpdateBody

    // The racket velocity the coming stroke is likely to need (own frame), for the backswing
    // before the real one is known at contact.
    Vector3 PlannedVelocity()
    {
        if (tossing || HoldingServe)
            return topspinServe ? new Vector3(0f, 1f, 6f) : new Vector3(0.3f, -6f, 5f);
        return paddle.Style switch
        {
            StrokeStyle.Drive => new Vector3(0f, 0.5f, 0.85f) * 0.8f * Human.DriveRacketSpeed(Skill),
            StrokeStyle.Smash => new Vector3(0f, -0.05f, 1f) * 0.9f * Human.SmashRacketSpeed(Skill),
            StrokeStyle.Push => new Vector3(0f, -1.3f, 1.8f),
            _ => new Vector3(0f, -0.3f, 1f),
        };
    }

    // The arm at contact, built from the racket out: the racket in the hitting point with the
    // stroke's face, the wrist where the hand holds it, the elbow up the grip's forearm as near as
    // the shoulder (on the untwisted chest) allows, kept out of the chest.
    Chain ContactChain()
    {
        Vector3 hit = Reachable(blade.GlobalPosition);
        var c = new Chain
        {
            Hit = hit,
            Spine = untwistedHips * Waist,
            Shoulder = untwistedHips * RightShoulder,
            Racket = new Transform3D(blade.GlobalBasis * paddle.NeutralBasis, hit),
        };
        // Serving, the racket is turned about the vertical so its forearm points back at the
        // shoulder: the face turned to brush the ball, the arm in front of the body.
        if (tossing || HoldingServe)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                Vector3 runs = c.Racket.Basis * paddle.ForearmLocal;
                Vector3 wristToShoulder = c.Shoulder - c.Racket * paddle.WristLocal;
                var from = new Vector2(runs.X, runs.Z);
                var to = new Vector2(wristToShoulder.X, wristToShoulder.Z);
                if (from.LengthSquared() < 1e-6f || to.LengthSquared() < 1e-6f)
                    break;
                float turn = Mathf.Atan2(from.Y * to.X - from.X * to.Y, from.Dot(to));
                c.Racket = About(c.Hit, Vector3.Up, turn) * c.Racket;
            }
        }
        c.Wrist = c.Racket * paddle.WristLocal;
        Vector3 forearm = (c.Racket.Basis * paddle.ForearmLocal).Normalized();
        Vector3 wanted = c.Wrist + forearm * Arm.ForearmSize;
        c.Elbow = Limb.TwoBone(c.Shoulder, c.Wrist, Arm.UpperArmSize, Arm.ForearmSize, wanted - c.Shoulder);

        // An elbow inside the chest goes round the outside of it instead, pushed further out
        // each try until it is clear.
        Transform3D chestFrame = untwistedHips * new Transform3D(Basis.Identity, Waist);
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            Vector3 inChest = chestFrame.AffineInverse() * c.Elbow;
            var across = new Vector2(inChest.X, inChest.Z);
            if (!InChest(chestFrame, c.Elbow))
                break;
            Vector2 outward = across.LengthSquared() > 1e-6f ? across.Normalized() : new Vector2(-1f, 0.3f).Normalized();
            Vector3 push = chestFrame.Basis * new Vector3(outward.X, 0f, outward.Y);
            c.Elbow = Limb.TwoBone(c.Shoulder, c.Wrist, Arm.UpperArmSize, Arm.ForearmSize,
                c.Elbow + push * ChestClearance * attempt - c.Shoulder);
        }

        // Where the arm cannot follow the grip's forearm, the wrist bends as far as it naturally
        // can and the hand and racket turn about the wrist with the arm for the rest; not as the
        // ball arrives, so the racket meets it where the stroke struck it.
        Vector3 actual = (c.Elbow - c.Wrist).Normalized();
        float off = actual.AngleTo(forearm);
        Vector3 giveAxis = forearm.Cross(actual);
        if (off > WristGiveFrom && giveAxis.LengthSquared() > 1e-8f)
        {
            var give = new Basis(giveAxis.Normalized(), (off - WristGiveFrom) * giveWeight);
            c.Racket = new Transform3D(give, c.Wrist - give * c.Wrist) * c.Racket;
        }

        Vector3 toShoulder = c.Shoulder - c.Elbow;
        Vector3 toWrist = c.Wrist - c.Elbow;
        c.ElbowAngle = toShoulder.AngleTo(toWrist);
        Vector3 hinge = toShoulder.Cross(toWrist);
        c.ElbowAxis = hinge.LengthSquared() > 1e-8f ? hinge.Normalized() : chestFrame.Basis.X;
        c.WristAxis = c.Racket.Basis.Z.Normalized();
        return c;
    }

    // Shares a racket velocity (world space) out among the joints: the waist, elbow and wrist
    // each give their share of what they can (their speed times their lever to the racket, within
    // their top speed), and the shoulder turns to make the rest.
    static JointSpeeds Decompose(in Chain c, Vector3 velocity, JointShare share)
    {
        Vector3 waistLever = Vector3.Up.Cross(c.Hit - c.Spine);
        Vector3 elbowLever = c.ElbowAxis.Cross(c.Hit - c.Elbow);
        Vector3 wristLever = c.WristAxis.Cross(c.Hit - c.Wrist);
        // A joint giving `part` of the racket speed along its path: its speed times its lever,
        // projected on the path, makes that share, within the joint's top speed. A joint whose
        // motion runs nearly across the path gives less (it would mostly throw the racket sideways).
        float Speed(Vector3 lever, float part, float max)
        {
            float along = velocity.Dot(lever);
            if (Mathf.Abs(along) < 1e-4f)
                return 0f;
            float lined = Mathf.Abs(along) / (velocity.Length() * lever.Length());
            return Mathf.Clamp(part * Mathf.SmoothStep(0.15f, 0.4f, lined) * velocity.LengthSquared() / along, -max, max);
        }

        var j = new JointSpeeds
        {
            Waist = Speed(waistLever, share.Waist, MaxWaistSpeed),
            Elbow = Speed(elbowLever, share.Elbow, MaxElbowSpeed),
            Wrist = Speed(wristLever, share.Wrist, MaxWristSpeed),
        };
        j.WaistPart = j.Waist * waistLever;
        j.ElbowPart = j.Elbow * elbowLever;
        j.WristPart = j.Wrist * wristLever;
        Vector3 rest = velocity - j.WaistPart - j.ElbowPart - j.WristPart;
        Vector3 arm = c.Hit - c.Shoulder;
        j.Shoulder = (arm.Cross(rest) / Mathf.Max(arm.LengthSquared(), 1e-4f)).LimitLength(MaxShoulderSpeed);
        j.ShoulderPart = j.Shoulder.Cross(arm);
        return j;
    }

    // The fastest this body can move the racket along a direction (world space) from the arm as
    // it is now: every joint turning at its top speed, each giving its lever along that way. A
    // backhand turns the trunk far less than a forehand (Human.BackhandTrunkShare).
    float MostRacketSpeed(Vector3 direction)
    {
        Chain c = ContactChain();
        Vector3 along = direction.Normalized();
        float waistTop = MaxWaistSpeed * (paddle.Forehand ? 1f : Human.BackhandTrunkShare);
        Vector3 arm = c.Hit - c.Shoulder;
        float across = Mathf.Sqrt(Mathf.Max(1f - Mathf.Pow(arm.Normalized().Dot(along), 2f), 0f));
        return waistTop * Mathf.Abs(Vector3.Up.Cross(c.Hit - c.Spine).Dot(along))
            + MaxShoulderSpeed * arm.Length() * across
            + MaxElbowSpeed * Mathf.Abs(c.ElbowAxis.Cross(c.Hit - c.Elbow).Dot(along))
            + MaxWristSpeed * Mathf.Abs(c.WristAxis.Cross(c.Hit - c.Wrist).Dot(along));
    }

    // Inside the chest: within the torso's round body (a little wider, for the arm's own
    // thickness) between the waist and the shoulders.
    static bool InChest(Transform3D chestFrame, Vector3 point)
    {
        Vector3 local = chestFrame.AffineInverse() * point;
        return local.Y > -0.15f && local.Y < 0.5f && new Vector2(local.X, local.Z).Length() < ChestClearance;
    }

    static Transform3D About(Vector3 pivot, Vector3 axis, float angle) =>
        Mathf.IsZeroApprox(angle) || axis.LengthSquared() < 1e-8f
            ? Transform3D.Identity
            : new Transform3D(new Basis(axis.Normalized(), angle), pivot) * new Transform3D(Basis.Identity, -pivot);

    // Places the racket and the arm for this moment of the stroke, turning the waist with it.
    // Each joint is turned from the contact pose by its speed times the time to or from contact
    // (wound back through the backswing, carried on through the follow-through), within its
    // range, the wrist first and the waist last, so each turn carries everything beyond it.
    void PoseArm(float dt)
    {
        bool beforeBall = interceptEta >= 0f && !followingThrough;
        giveWeight = Mathf.MoveToward(giveWeight, beforeBall ? Mathf.Clamp((interceptEta - 0.03f) / 0.12f, 0f, 1f) : 1f, 8f * dt);
        Chain c = ContactChain();

        if (followingThrough && !paddle.Striking)
            followingThrough = false;
        bool serve = tossing || HoldingServe;
        JointSpeeds j = followingThrough
            ? strokeSpeeds
            : Decompose(c, World(PlannedVelocity()), ShareFor(paddle.Style, serve));
        // A serve is a short, compact swing.
        float t = followingThrough ? FollowTime * paddle.FollowNow : -BackswingTime * paddle.WindupNow * (serve ? 0.5f : 1f);

        // The waist first (eased, so a change of plan turns the body rather than snapping it),
        // and the arm hangs from the chest it has turned.
        float waist = Mathf.MoveToward(waistTurn, Mathf.Clamp(j.Waist * t, -MaxWaistAngle, MaxWaistAngle), WaistEase * dt);
        TwistWaist(waist);
        Vector3 shoulderOnChest = chest.GlobalTransform.TranslatedLocal(-Waist) * RightShoulder;
        Vector3 shoulder = (j.Shoulder * t).LimitLength(MaxShoulderAngle);
        float elbow = Mathf.Clamp(j.Elbow * t, MinElbowAngle - c.ElbowAngle, MaxElbowAngle - c.ElbowAngle);
        float wrist = Mathf.Clamp(j.Wrist * t, -MaxWristAngle, MaxWristAngle);

        Transform3D atWaist = About(c.Spine, Vector3.Up, waist);
        Transform3D chestNow = atWaist * untwistedHips * new Transform3D(Basis.Identity, Waist);

        // The arm's own turns, cut short (to 3/4, 1/2, 1/4, none) where even moving clear cannot
        // keep the racket and arm out of the table and the body: the stroke is made smaller
        // rather than passing through anything.
        Transform3D racket = c.Racket;
        Vector3 elbowNow = c.Elbow, wristNow = c.Wrist;
        for (int cut = 0; cut <= 4; cut++)
        {
            float scale = 1f - 0.25f * cut;
            Transform3D atWrist = About(c.Wrist, c.WristAxis, wrist * scale);
            Transform3D atElbow = About(c.Elbow, c.ElbowAxis, elbow * scale);

            // The shoulder turns no further than keeps the elbow out of the chest (as the waist
            // has turned it).
            Transform3D atShoulder = Transform3D.Identity;
            for (float reach = 1f; reach >= 0f; reach -= 0.25f)
            {
                atShoulder = About(c.Shoulder, shoulder, shoulder.Length() * reach * scale);
                if (!InChest(chestNow, atWaist * atShoulder * c.Elbow))
                    break;
            }

            racket = atWaist * atShoulder * atElbow * atWrist * c.Racket;
            elbowNow = atWaist * atShoulder * c.Elbow;
            wristNow = atWaist * atShoulder * atElbow * c.Wrist;
            if (KeepClear(shoulderOnChest, ref racket, ref elbowNow, ref wristNow) < 0.005f)
                break;
        }
        paddle.GlobalTransform = racket;
        rightArm.Solve(shoulderOnChest, wristNow, elbowNow - shoulderOnChest);
        KeepInHand(wristNow);
    }

    // The hard rule: the racket and the arm holding it never pass through the table or a body
    // (the trunk, head, hips, legs, the other arm). Where they would, the arm moves its deepest
    // point clear a little at a time, turning at the joint that can move it that way: the
    // shoulder when it can (the whole arm goes with it), else the elbow (a racket low under the
    // shoulder is lifted by bending the elbow) or the wrist.
    float KeepClear(Vector3 shoulder, ref Transform3D racket, ref Vector3 elbow, ref Vector3 wrist)
    {
        Collide.Capsule[] body = BodyCapsules(withFreeArm: true);
        for (int pass = 0; ; pass++)
        {
            Vector3 deepestAt = Vector3.Zero, push = Vector3.Zero;
            int part = 0; // 0 racket, 1 wrist or forearm, 2 elbow or upper arm
            void Check(Vector3 point, float radius, int which)
            {
                Vector3 clear = Collide.OutOfAll(point, radius, body);
                if (clear.LengthSquared() > push.LengthSquared())
                {
                    push = clear;
                    deepestAt = point;
                    part = which;
                }
            }
            foreach (Vector3 point in Paddle.Outline)
                Check(racket * point, Paddle.RimClearance, 0);
            Check(wrist, 0.03f, 1);
            Check((wrist + elbow) * 0.5f, 0.035f, 1);
            Check(elbow, 0.04f, 2);
            Check((elbow + shoulder) * 0.5f, 0.03f, 2);
            if (push.LengthSquared() < 1e-6f)
                return 0f;
            if (pass == 16)
                return push.Length(); // as clear as it gets: the caller makes the stroke smaller

            // The joints that carry this point, nearest the body first; take the first whose turn
            // moves it well along the push (a joint straight above or below it mostly cannot).
            Vector3[] joints = part == 0 ? new[] { shoulder, elbow, wrist } : part == 1 ? new[] { shoulder, elbow } : new[] { shoulder };
            int chosen = 0;
            float best = -1f;
            for (int k = 0; k < joints.Length; k++)
            {
                Vector3 lever = deepestAt - joints[k];
                if (lever.LengthSquared() < 1e-4f)
                    continue;
                float across = (push - lever * (push.Dot(lever) / lever.LengthSquared())).Length() / push.Length();
                if (across > 0.6f)
                {
                    chosen = k;
                    break;
                }
                if (across > best)
                {
                    best = across;
                    chosen = k;
                }
            }
            Vector3 pivot = joints[chosen];
            Vector3 arm = deepestAt - pivot;
            Vector3 axis = arm.Cross(push);
            if (axis.LengthSquared() < 1e-10f)
                return push.Length();
            Vector3 sideways = push - arm * (push.Dot(arm) / arm.LengthSquared());
            float angle = Mathf.Min(1.1f * Mathf.Max(sideways.Length(), 0.2f * push.Length()) / Mathf.Max(arm.Length(), 0.05f), 0.5f);
            Transform3D turn = About(pivot, axis, angle);
            racket = turn * racket;
            wrist = chosen < 2 ? turn * wrist : wrist;
            elbow = chosen < 1 ? turn * elbow : elbow;
        }
    }

    // After the free arm has moved: keeps the racket and its arm clear of where it went.
    void ReclearRacket()
    {
        Transform3D racket = paddle.GlobalTransform;
        Vector3 shoulder = rightArm.ShoulderPoint, elbow = rightArm.ElbowPoint, wrist = rightArm.WristPoint;
        if (KeepClear(shoulder, ref racket, ref elbow, ref wrist) >= 0f)
        {
            paddle.GlobalTransform = racket;
            rightArm.Solve(shoulder, wrist, elbow - shoulder);
            KeepInHand(wrist);
        }
    }

    // The arm keeps its length: where it could not reach the racket's grip it stops at full
    // stretch, and the racket comes with the hand rather than floating off it.
    void KeepInHand(Vector3 wantedWrist)
    {
        Vector3 gap = rightArm.WristPoint - wantedWrist;
        if (gap.LengthSquared() > 1e-8f)
            paddle.GlobalPosition += gap;
    }

    // What the free arm must keep out of: the body, the racket arm and the racket.
    Collide.Capsule[] FreeArmObstacles()
    {
        var parts = new System.Collections.Generic.List<Collide.Capsule>(BodyCapsules(withFreeArm: false))
        {
            new(rightArm.ShoulderPoint, rightArm.ElbowPoint, 0.045f),
            new(rightArm.ElbowPoint, rightArm.WristPoint, 0.035f),
        };
        Transform3D racket = paddle.GlobalTransform;
        parts.Add(new(racket * new Vector3(0f, 0.02f, 0f), racket * new Vector3(0f, -0.03f, 0f), 0.08f)); // head
        parts.Add(new(racket * new Vector3(0f, -0.1f, 0f), racket * new Vector3(0f, -0.172f, 0f), 0.02f)); // handle
        return parts.ToArray();
    }

    // The body as capsules (world space): trunk, head, hips, thighs and shins, and with
    // withFreeArm the free arm and hand.
    Collide.Capsule[] BodyCapsules(bool withFreeArm)
    {
        Transform3D chestNow = chest.GlobalTransform;
        Transform3D hips = upperBody.GlobalTransform;
        var parts = new System.Collections.Generic.List<Collide.Capsule>
        {
            new(chestNow * new Vector3(0f, 0.13f, 0f), chestNow * new Vector3(0f, 0.27f, 0f), 0.17f),  // trunk
            new(chestNow * new Vector3(0f, 0.53f, 0f), chestNow * new Vector3(0f, 0.53f, 0f), 0.11f),  // head
            new(hips * new Vector3(0f, 0.085f, 0f), hips * new Vector3(0f, 0.115f, 0f), 0.165f),       // hips
        };
        foreach (Leg leg in legs)
        {
            parts.Add(new(leg.HipPoint, leg.KneePoint, 0.075f));
            parts.Add(new(leg.KneePoint, leg.AnklePoint, 0.055f));
        }
        if (withFreeArm)
        {
            parts.Add(new(leftArm.ShoulderPoint, leftArm.ElbowPoint, 0.045f));
            parts.Add(new(leftArm.ElbowPoint, leftArm.WristPoint, 0.035f));
            Vector3 hand = leftArm.WristPoint + (leftArm.WristPoint - leftArm.ElbowPoint).Normalized() * 0.08f;
            parts.Add(new(leftArm.WristPoint, hand, 0.045f));
        }
        return parts.ToArray();
    }

    // At contact: shares the stroke's racket velocity (own frame) among the joints, keeps it for
    // the follow-through and says what each joint gave.
    string ShareOut(Vector3 racket, StrokeStyle style, bool serve)
    {
        Vector3 v = World(racket);
        strokeSpeeds = Decompose(ContactChain(), v, ShareFor(style, serve));
        followingThrough = true;
        Vector3 along = v.Normalized();
        float waist = strokeSpeeds.WaistPart.Dot(along), shoulder = strokeSpeeds.ShoulderPart.Dot(along);
        float elbow = strokeSpeeds.ElbowPart.Dot(along), wrist = strokeSpeeds.WristPart.Dot(along);
        return $"waist {waist:F1} + shoulder {shoulder:F1} + elbow {elbow:F1} + wrist {wrist:F1}" +
               $" = {waist + shoulder + elbow + wrist:F1} of {v.Length():F1} m/s";
    }
}
