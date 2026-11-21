using Godot;
using System.Collections.Generic;

// Finds launch velocities that land the ball on a chosen spot, using the same physics as Ball.
// Targets are Vector2(x, z) positions on the table surface.
public static class ShotSolver
{
    const float MaxFlightTime = 3f;
    const float MinPace = 2.5f;
    const float PaceStep = 0.75f;
    const float NetClearance = 0.07f; // so a small aiming error does not put a planned shot in the net
    const float ServeBounceInset = 0.2f; // a serve's first bounce lands this far inside the end line

    // Legal shot to aim at the given pace (speed along Z) or, failing that, the nearest slower
    // and then faster pace; then aimed again at aim + error at that pace so a sloppy stroke can
    // still miss the table or hit the net. bounces is 1 for a rally shot and 2 for a serve.
    public static Vector3 PlanShot(Vector3 from, Vector3 spin, Vector2 aim, float pace, int bounces, Vector2 error)
    {
        var paces = new List<float>();
        for (float speed = pace; speed >= MinPace; speed -= PaceStep)
            paces.Add(speed);
        for (float speed = pace + PaceStep; speed <= pace + 4f * PaceStep; speed += PaceStep)
            paces.Add(speed);

        foreach (float speed in paces)
        {
            if (!TrySolve(from, spin, aim, speed, bounces, out Vector3 velocity) || !IsLegal(from, velocity, spin, bounces))
                continue;
            if (error != Vector2.Zero && TrySolve(from, spin, aim + error, speed, bounces, out Vector3 sloppy))
                return sloppy;
            return velocity;
        }

        // Nothing legal: pop the ball up towards the other side and hope.
        return new Vector3(0f, 2.5f, -Mathf.Sign(from.Z) * 3.5f);
    }

    // Face angles are (pitch, yaw): pitch > 0 opens the face upwards, yaw > 0 turns it towards +X.
    const float MaxFacePitch = 1.3f;
    const float MaxFaceYaw = 0.9f;

    // The hitting face's normal for a hitter on side (+1 player, facing -Z; -1 NPC, facing +Z).
    public static Vector3 FaceNormal(Vector2 face, int side) =>
        new Basis(Vector3.Up, face.Y) * new Vector3(0f, Mathf.Sin(face.X), -side * Mathf.Cos(face.X));

    // The ball after a racket with this face angle and velocity strikes it.
    public static BallState Hit(BallState ball, Vector2 face, Vector3 racketVelocity, int side, Striker? hitter = null)
    {
        Ball.HitByRacket(ref ball, FaceNormal(face, side), racketVelocity, Ball.RubberGrip, hitter);
        return ball;
    }

    // The face angle with which a racket moving at racketVelocity sends the ball to land on
    // target, the way a player's wrist sets the angle for a stroke: force and spin come from
    // the contact physics, only the angle is chosen. Newton iteration against an endless table.
    public static bool TrySolveFace(BallState ball, Vector3 racketVelocity, int side, Vector2 target, int bounces, out Vector2 face, Striker? hitter = null)
    {
        const float eps = 0.004f;
        float yaw = Mathf.Atan2(-side * (target.X - ball.Position.X), -side * (target.Y - ball.Position.Z));
        face = new Vector2(ScanPitch(ball, racketVelocity, side, target, yaw, bounces, hitter), Mathf.Clamp(yaw, -MaxFaceYaw, MaxFaceYaw));

        for (int i = 0; i < 14; i++)
        {
            if (!TryLandFace(ball, face, racketVelocity, side, bounces, out Vector2 land, hitter))
                return false;
            Vector2 error = target - land;
            if (error.Length() < 0.01f)
                return true;

            if (!TryLandFace(ball, face + new Vector2(eps, 0f), racketVelocity, side, bounces, out Vector2 landPitch, hitter) ||
                !TryLandFace(ball, face + new Vector2(0f, eps), racketVelocity, side, bounces, out Vector2 landYaw, hitter))
                return false;
            Vector2 dPitch = (landPitch - land) / eps;
            Vector2 dYaw = (landYaw - land) / eps;
            float det = dPitch.X * dYaw.Y - dYaw.X * dPitch.Y;
            if (Mathf.Abs(det) < 1e-6f)
                return false;
            face.X = Mathf.Clamp(face.X + Mathf.Clamp((error.X * dYaw.Y - dYaw.X * error.Y) / det, -0.15f, 0.15f), -MaxFacePitch, MaxFacePitch);
            face.Y = Mathf.Clamp(face.Y + Mathf.Clamp((dPitch.X * error.Y - error.X * dPitch.Y) / det, -0.15f, 0.15f), -MaxFaceYaw, MaxFaceYaw);
        }
        return false;
    }

    // A physical stroke with the racket moving at racketVelocity. The face is angled for the
    // first aim that gives a legal shot. If none does, it takes the first aim it can reach at
    // all, and failing that the angle that lands nearest the first aim (a soft ball met by a
    // still racket cannot go far): either way the shot may go wrong. The face is then off by
    // faceError radians. Returns the ball leaving the racket; legal tells whether the angle
    // before the error gives a legal shot.
    public static BallState Stroke(BallState ball, Vector3 racketVelocity, int side, Vector2[] aims, int bounces, Vector2 faceError, out Vector2 face, out bool legal, Striker? hitter = null)
    {
        Vector2? reachable = null;
        legal = false;
        foreach (Vector2 aim in aims)
        {
            if (!TrySolveFace(ball, racketVelocity, side, aim, bounces, out Vector2 candidate, hitter))
                continue;
            reachable ??= candidate;
            BallState after = Hit(ball, candidate, racketVelocity, side, hitter);
            if (IsLegal(after.Position, after.Velocity, after.Spin, bounces))
            {
                reachable = candidate;
                legal = true;
                break;
            }
        }
        face = (reachable ?? Nearest(ball, racketVelocity, side, aims[0], bounces, hitter)) + faceError;
        return Hit(ball, face, racketVelocity, side, hitter);
    }

    // A stroke that, when the ball is too soft to carry over the net with the racket's motion as
    // given, drives through it harder (up to 3 m/s more), as a player pushes a slow ball over.
    public static BallState PushedStroke(BallState ball, ref Vector3 racketVelocity, int side, Vector2[] aims, Vector2 faceError, out Vector2 face, Striker? hitter = null)
    {
        const float extraDrive = 0.75f;
        const float shortOfNet = 0.3f; // landing less than this past the net counts as too soft
        for (int k = 0; ; k++)
        {
            BallState after = Stroke(ball, racketVelocity, side, aims, 1, faceError, out face, out bool legal, hitter);
            bool tooSoft = !legal && (!TryLandFace(ball, face - faceError, racketVelocity, side, 1, out Vector2 land, hitter)
                || -side * land.Y < shortOfNet);
            if (!tooSoft || k == 4)
                return after;
            racketVelocity.Z -= side * extraDrive;
        }
    }

    // The face angle whose landing comes nearest to target, for targets out of reach.
    static Vector2 Nearest(BallState ball, Vector3 racketVelocity, int side, Vector2 target, int bounces, Striker? hitter)
    {
        float yaw = Mathf.Clamp(Mathf.Atan2(-side * (target.X - ball.Position.X), -side * (target.Y - ball.Position.Z)), -MaxFaceYaw, MaxFaceYaw);
        var best = new Vector2(0f, yaw);
        float bestMiss = float.MaxValue;
        for (float pitch = -1.2f; pitch <= 1.2f; pitch += 0.05f)
        {
            var face = new Vector2(pitch, yaw);
            if (TryLandFace(ball, face, racketVelocity, side, bounces, out Vector2 land, hitter) && land.DistanceTo(target) < bestMiss)
            {
                bestMiss = land.DistanceTo(target);
                best = face;
            }
        }
        return best;
    }

    // The face angle a player would use for this racket motion: closed when brushing up the
    // ball, open when chopping down under it.
    static float NaturalPitch(Vector3 racketVelocity) =>
        -(racketVelocity.Y > 0f ? 1f : 0.6f) * Mathf.Atan2(racketVelocity.Y, Mathf.Abs(racketVelocity.Z) + 1f);

    // A serve found by searching launch velocities: the legal one whose second bounce lands
    // nearest aim. Slower than PlanShot, but PlanShot's Newton steps lose their way when heavy
    // backspin makes a short serve float. Fails when no legal serve exists.
    public static bool SearchServe(Vector3 from, Vector3 spin, Vector2 aim, out Vector3 velocity)
    {
        velocity = Vector3.Zero;
        float bestMiss = float.MaxValue;
        float dz = aim.Y - from.Z;
        for (float forward = 2f; forward <= 6.5f; forward += 0.3f)
        {
            for (float vy = -5f; vy <= 1f; vy += 0.3f)
            {
                var v = new Vector3((aim.X - from.X) * forward / Mathf.Abs(dz), vy, Mathf.Sign(dz) * forward);
                if (!TryLand(from, v, spin, 2, out Vector2 land))
                    continue;
                float miss = land.DistanceTo(aim);
                if (miss < bestMiss && IsLegal(from, v, spin, 2))
                {
                    bestMiss = miss;
                    velocity = v;
                }
            }
        }
        return bestMiss < float.MaxValue;
    }

    // Face angles (pitch, yaw) of a face normal; the inverse of FaceNormal.
    public static Vector2 FaceAngles(Vector3 normal, int side) =>
        new(Mathf.Asin(Mathf.Clamp(normal.Y, -1f, 1f)), Mathf.Atan2(-side * normal.X, -side * normal.Z));

    // The racket face and velocity that turn the ball before into the ball after, by the same
    // contact as Ball.HitByRacket run backwards. Friction acts in the face, so the spin change
    // fixes the face's tilt to the velocity change (spinChange = -1.5/R * normal x velocityChange);
    // the push along the normal and the friction then give the racket's velocity. The spin
    // change must be perpendicular to the velocity change (use SpinWithin). Fails when no face
    // can do it: more spin than the rubber's friction allows for that push.
    public static bool RacketFor(BallState before, Vector3 velocityAfter, Vector3 spinAfter, out Vector3 normal, out Vector3 racketVelocity, Striker? hitter = null)
    {
        normal = Vector3.Zero;
        racketVelocity = Vector3.Zero;
        Striker h = hitter ?? Striker.Default;
        RacketFace face = h.Face;
        Vector3 dv = velocityAfter - before.Velocity;
        Vector3 c = (spinAfter - before.Spin) * (-Ball.Radius / 1.5f); // = normal x dv
        float dv2 = dv.LengthSquared();
        if (dv2 < 1e-6f || c.LengthSquared() >= dv2)
            return false;

        normal = dv.Cross(c) / dv2 + dv * (Mathf.Sqrt(1f - c.LengthSquared() / dv2) / Mathf.Sqrt(dv2));
        normal = normal.Normalized();
        float push = normal.Dot(dv);
        Vector3 friction = dv - normal * push;
        if (push < 0.6f || friction.Length() > face.Friction * push)
            return false;

        Vector3 r = -normal * Ball.Radius;
        Vector3 ballAlongFace = before.Velocity - normal * before.Velocity.Dot(normal);
        float share = h.ImpactShare; // the ball gets this share of an endlessly heavy racket's push
        Vector3 racketAlongFace = ballAlongFace + friction / (face.SlipTransfer * share) + before.Spin.Cross(r);
        float racketAlongNormal = before.Velocity.Dot(normal) + push / ((1f + face.Restitution) * share);
        racketVelocity = racketAlongFace + normal * racketAlongNormal;
        return true;
    }

    // The part of a wanted spin change that a single contact can give alongside this velocity
    // change: friction can only spin the ball about axes perpendicular to it.
    public static Vector3 SpinWithin(Vector3 spinChange, Vector3 velocityChange) =>
        spinChange - velocityChange * (spinChange.Dot(velocityChange) / velocityChange.LengthSquared());

    // A starting pitch for Newton: along the range, the one landing closest in depth, with a
    // preference for the natural face angle so that where two angles reach the target (a
    // closed face brushing up, or an open one under the ball) it takes the one a player would.
    static float ScanPitch(BallState ball, Vector3 racketVelocity, int side, Vector2 target, float yaw, int bounces, Striker? hitter)
    {
        const float naturalWeight = 0.25f; // metres of landing error per radian away from natural
        float natural = NaturalPitch(racketVelocity);
        float best = natural;
        float bestCost = float.MaxValue;
        for (float pitch = -1.2f; pitch <= 1.2f; pitch += 0.1f)
        {
            if (!TryLandFace(ball, new Vector2(pitch, yaw), racketVelocity, side, bounces, out Vector2 land, hitter))
                continue;
            float cost = Mathf.Abs(land.Y - target.Y) + naturalWeight * Mathf.Abs(pitch - natural);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = pitch;
            }
        }
        return best;
    }

    static bool TryLandFace(BallState ball, Vector2 face, Vector3 racketVelocity, int side, int bounces, out Vector2 land, Striker? hitter)
    {
        BallState after = Hit(ball, face, racketVelocity, side, hitter);
        return TryLand(after.Position, after.Velocity, after.Spin, bounces, out land);
    }

    // Newton iteration on (vx, vy) with vz fixed, against an endless table without a net.
    public static bool TrySolve(Vector3 from, Vector3 spin, Vector2 target, float forwardSpeed, int bounces, out Vector3 velocity)
    {
        const float eps = 0.02f;
        float dz = target.Y - from.Z;
        float vz = Mathf.Sign(dz) * forwardSpeed;
        float time = Mathf.Abs(dz) / forwardSpeed;
        float vx = (target.X - from.X) / time;
        float vy = bounces == 1 ? (TableSpec.Height + Ball.Radius - from.Y) / time + 0.5f * 9.81f * time : ScanVerticalSpeed(from, spin, target, vx, vz, bounces);

        for (int i = 0; i < 12; i++)
        {
            if (!TryLand(from, new Vector3(vx, vy, vz), spin, bounces, out Vector2 land))
                break;
            Vector2 error = target - land;
            if (error.Length() < 0.01f)
            {
                velocity = new Vector3(vx, vy, vz);
                return true;
            }

            if (!TryLand(from, new Vector3(vx + eps, vy, vz), spin, bounces, out Vector2 landX) ||
                !TryLand(from, new Vector3(vx, vy + eps, vz), spin, bounces, out Vector2 landY))
                break;
            Vector2 dx = (landX - land) / eps;
            Vector2 dy = (landY - land) / eps;
            float det = dx.X * dy.Y - dy.X * dx.Y;
            if (Mathf.Abs(det) < 1e-5f)
                break;
            vx += Mathf.Clamp((error.X * dy.Y - dy.X * error.Y) / det, -3f, 3f);
            vy += Mathf.Clamp((dx.X * error.Y - error.X * dx.Y) / det, -3f, 3f);
        }

        velocity = Vector3.Zero;
        return false;
    }

    // Replays the shot against the real table and net: every bounce but the last must be on
    // the hitter's half (a serve) and well inside its end line, the last on the other half,
    // and the ball must pass over the net with some clearance.
    public static bool IsLegal(Vector3 from, Vector3 velocity, Vector3 spin, int bounces)
    {
        int hitterSide = TableSpec.SideOf(from.Z);
        var s = new BallState(from, velocity, spin);
        float dt = Ball.SubStepTime;
        int seen = 0;
        for (float t = 0f; t < MaxFlightTime; t += dt)
        {
            Vector3 before = s.Position;
            BallEvent evt = Ball.Step(ref s, dt, false);
            if (before.Z * s.Position.Z <= 0f && before.Z != 0f &&
                before.Lerp(s.Position, before.Z / (before.Z - s.Position.Z)).Y < TableSpec.NetTop + Ball.Radius + NetClearance)
                return false;

            switch (evt)
            {
                case BallEvent.Net:
                case BallEvent.Floor:
                    return false;
                case BallEvent.Table:
                    seen++;
                    int expected = seen < bounces ? hitterSide : -hitterSide;
                    if (TableSpec.SideOf(s.Position.Z) != expected)
                        return false;
                    if (seen < bounces && Mathf.Abs(s.Position.Z) > TableSpec.HalfLength - ServeBounceInset)
                        return false;
                    if (seen == bounces)
                        return true;
                    break;
            }
        }
        return false;
    }

    // Multi-bounce shots have no simple ballistic guess: try a range of launch angles and
    // start Newton from the one that lands closest along Z.
    static float ScanVerticalSpeed(Vector3 from, Vector3 spin, Vector2 target, float vx, float vz, int bounces)
    {
        float best = -2f;
        float bestError = float.MaxValue;
        for (float vy = -7f; vy <= 2f; vy += 0.25f)
        {
            if (TryLand(from, new Vector3(vx, vy, vz), spin, bounces, out Vector2 land) && Mathf.Abs(land.Y - target.Y) < bestError)
            {
                bestError = Mathf.Abs(land.Y - target.Y);
                best = vy;
            }
        }
        return best;
    }

    static bool TryLand(Vector3 from, Vector3 velocity, Vector3 spin, int bounces, out Vector2 land)
    {
        var s = new BallState(from, velocity, spin);
        float dt = Ball.SubStepTime;
        int seen = 0;
        for (float t = 0f; t < MaxFlightTime; t += dt)
        {
            if (Ball.Step(ref s, dt, true) == BallEvent.Table && ++seen == bounces)
            {
                land = new Vector2(s.Position.X, s.Position.Z);
                return true;
            }
        }
        land = Vector2.Zero;
        return false;
    }
}
