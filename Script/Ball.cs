using Godot;
using System;
using System.Collections.Generic;

public enum BallEvent
{
    None,
    Table,
    Net,
    Floor
}

public struct BallState
{
    public Vector3 Position;
    public Vector3 Velocity;
    public Vector3 Spin; // angular velocity in rad/s

    public BallState(Vector3 position, Vector3 velocity, Vector3 spin)
    {
        Position = position;
        Velocity = velocity;
        Spin = spin;
    }
}

// Anything that can strike the ball: the player's blade and the NPC.
public interface IBallHitter
{
    int Side { get; }

    // Called every sub-step while this side may hit.
    bool TryHit(Ball ball);
}

// The ball runs its own integrator (gravity, air drag, Magnus effect, spin-aware bounces)
// instead of the physics engine: a 2 cm ball at 15 m/s needs sub-stepping, and the shot
// solver and the NPC must replay exactly the same physics to predict where it goes.
public partial class Ball : Node3D
{
    public const float Radius = 0.02f;
    public const int SubSteps = 8;

    const float Gravity = 9.81f;
    const float DragFactor = 0.14f;        // 0.5 * air density * Cd * area / mass
    const float MagnusFactor = 0.0023f;    // lift coefficient about 0.33 at a loop's spin (rw/v about 0.8), as measured for table tennis balls
    const float SpinDamping = 0.3f;
    const float TableRestitution = 0.9f;
    const float TableGrip = 0.6f;          // 0 = frictionless table, 1 = ball leaves it rolling
    const float TableFriction = 0.3f;      // Coulomb limit at a bounce: tangential <= this * normal impulse
    const float FloorRestitution = 0.6f;
    const float MinImpactSpeed = 0.05f;    // slower contacts just rest instead of bouncing
    // The standard racket's rubber (RacketFace.Standard); an equipped racket brings its own
    // values for each side (RacketFace, from its blade and rubbers).
    public const float RubberRestitution = 0.85f; // ball against an inverted rubber, along the face normal
    public const float RubberGrip = 0.95f;        // how close the rubber comes to stopping the contact slip
    public const float RubberSpringBack = 0.3f;   // the rubber's surface springs back, reversing some slip
    public const float RubberFriction = 1.2f;     // Coulomb limit of a tacky rubber: tangential <= this * normal impulse
    // Share of the contact slip the rubber's friction takes out of the ball's velocity
    // (2/5 stops a thin shell's slip; the spring-back pushes past that).
    public const float RubberSlipTransfer = 0.4f * RubberGrip * (1f + RubberSpringBack);

    static readonly Color TopspinGlow = new(1f, 0.45f, 0.1f);
    static readonly Color BackspinGlow = new(0.2f, 0.6f, 1f);
    const float FullGlowSpin = 400f;       // rad/s

    public static float SubStepTime => 1f / (Engine.PhysicsTicksPerSecond * SubSteps);

    public BallState State;
    public bool Simulating;
    // Side allowed to strike the ball right now: +1 player, -1 opponent, 0 nobody.
    public int HittableBy;
    // The side that struck it last (0 for a toss or a held ball): a ball you struck is never yours
    // to play, even one that drops back off the net towards you.
    public int StruckBy;
    // The point is over (the match has given it): nobody plays this ball any more.
    public bool Dead;
    public readonly List<IBallHitter> Hitters = new();

    public event Action<BallEvent, Vector3> Contact;
    public event Action<int> Struck;

    MeshInstance3D mesh;
    ShaderMaterial material;
    MeshInstance3D shadow;
    Vector3 previousPosition;
    float accumulator;

    public override void _Ready()
    {
        mesh = GetNode<MeshInstance3D>("Mesh");
        material = (ShaderMaterial)mesh.MaterialOverride;
        shadow = GetNode<MeshInstance3D>("Shadow");
        State.Position = Position;
        previousPosition = Position;
    }

    public override void _PhysicsProcess(double delta)
    {
        // Always integrate in fixed sub-steps, also in slow motion (where delta shrinks), so the
        // flight is exactly the one the shot solver and the NPC predicted.
        float dt = SubStepTime;
        if (Simulating)
        {
            accumulator += (float)delta;
            while (accumulator >= dt && Simulating)
            {
                accumulator -= dt;
                previousPosition = State.Position;
                BallEvent evt = Step(ref State, dt, false);
                if (evt != BallEvent.None)
                    Contact?.Invoke(evt, State.Position);

                foreach (IBallHitter hitter in Hitters)
                {
                    if (hitter.Side == HittableBy && hitter.TryHit(this))
                        break;
                }
            }
        }

        Position = Simulating ? previousPosition.Lerp(State.Position, accumulator / dt) : State.Position;
        UpdateSpin((float)delta);
        UpdateShadow();
    }

    public void Hold(Vector3 position)
    {
        Simulating = false;
        HittableBy = 0;
        StruckBy = 0;
        Dead = false;
        State = new BallState(position, Vector3.Zero, Vector3.Zero);
        Position = position;
        previousPosition = position;
        accumulator = 0f;
    }

    // Free flight that is not a stroke, such as the toss before a serve.
    public void Toss(Vector3 velocity)
    {
        State = new BallState(State.Position, velocity, Vector3.Zero);
        previousPosition = State.Position;
        Simulating = true;
        HittableBy = 0;
        StruckBy = 0;
        Dead = false;
    }

    public void Strike(int side, Vector3 position, Vector3 velocity, Vector3 spin)
    {
        State = new BallState(position, velocity, spin);
        previousPosition = position;
        Simulating = true;
        HittableBy = 0;
        StruckBy = side;
        Dead = false;
        Struck?.Invoke(side);
    }

    // Advances a ball state by dt. With infiniteTable the table surface is an endless plane and
    // the net is ignored, which keeps the shot solver's search smooth.
    public static BallEvent Step(ref BallState s, float dt, bool infiniteTable)
    {
        Vector3 from = s.Position;
        Vector3 v = s.Velocity;
        Vector3 accel = Vector3.Down * Gravity - DragFactor * v.Length() * v + MagnusFactor * s.Spin.Cross(v);
        v += accel * dt;
        Vector3 p = from + v * dt;
        s.Spin *= 1f - SpinDamping * dt;
        BallEvent evt = BallEvent.None;

        if (!infiniteTable && from.Z != 0f && from.Z * p.Z <= 0f)
        {
            Vector3 cross = from.Lerp(p, from.Z / (from.Z - p.Z));
            bool inNetSpan = Mathf.Abs(cross.X) <= TableSpec.NetHalfWidth + Radius;
            if (inNetSpan && cross.Y > TableSpec.Height && cross.Y - Radius < TableSpec.NetTop)
            {
                evt = BallEvent.Net;
                if (cross.Y < TableSpec.NetTop)
                {
                    // Into the mesh: the ball drops back on the hitter's side.
                    p.Z = Mathf.Sign(from.Z) * Radius;
                    v = new Vector3(v.X * 0.3f, Mathf.Min(v.Y, 0f), -v.Z * 0.1f);
                    s.Spin *= 0.2f;
                }
                else
                {
                    // Clipped the tape: it carries on, slower and popped up.
                    v.Z *= 0.6f;
                    v.Y = Mathf.Abs(v.Y) * 0.3f + 0.4f;
                }
            }
        }

        bool overTable = infiniteTable ||
            (Mathf.Abs(p.X) <= TableSpec.HalfWidth && Mathf.Abs(p.Z) <= TableSpec.HalfLength);
        if (v.Y < 0f && overTable && p.Y - Radius < TableSpec.Height && from.Y - Radius >= TableSpec.Height - 0.01f)
        {
            float impact = -v.Y;
            p.Y = TableSpec.Height + Radius;
            v.Y = impact > MinImpactSpeed ? impact * TableRestitution : 0f;
            ApplyContactFriction(ref v, ref s.Spin, impact * (1f + TableRestitution));
            if (impact > MinImpactSpeed)
                evt = BallEvent.Table;
        }
        else if (v.Y < 0f && p.Y < Radius)
        {
            float impact = -v.Y;
            p.Y = Radius;
            v = new Vector3(v.X * 0.8f, impact > MinImpactSpeed ? impact * FloorRestitution : 0f, v.Z * 0.8f);
            if (impact > MinImpactSpeed)
                evt = BallEvent.Floor;
        }

        s.Position = p;
        s.Velocity = v;
        return evt;
    }

    // A racket face (unit normal pointing out of the rubber towards the ball) moving at
    // racketVelocity strikes the ball. Along the normal the racket transfers force: the relative
    // approach speed comes back scaled by the rubber's restitution. Along the face the rubber's
    // friction grips the ball's contact point; that impulse acts off the ball's centre and spins
    // it, so the spin's direction follows how the face brushes the ball (up: topspin, down:
    // backspin, sideways: sidespin) together with the spin it already had. When the grip would
    // need more friction than the rubber has, the ball slides and gets less.
    // grip is how close the rubber comes to stopping the slip in the time the ball stays on it
    // (see GripForDwell); the default is a normal stroke's. bodyMass is the hitter's (kg): the
    // racket and hand are heavy but not endlessly so, and the ball gets Human.ImpactShare of
    // what an endlessly heavy racket would give it (the racket barely slows).
    // Never faster than the fastest ball ever hit (Human.FastestBall, 32 m/s): an impact hard
    // enough to beat it presses the rubber and sponge down to the blade, and they give back less
    // (measured restitution falls with impact speed); the push is then only what sends the ball
    // off at that speed, the friction on it (and so the spin) with it.
    // hitter is who strikes it and with which racket face (Striker; the standard racket and a
    // 70 kg player when not given): the face's restitution, friction and spring-back and the
    // racket's mass replace the standard rubber's.
    public static void HitByRacket(ref BallState s, Vector3 normal, Vector3 racketVelocity, float grip = RubberGrip, Striker? hitter = null) =>
        s = Hit(s, normal, racketVelocity, grip, hitter ?? Striker.Default, out _);

    // The hit, and the share of the full push it gave (1, or less when held to the record).
    static BallState Hit(BallState s, Vector3 normal, Vector3 racketVelocity, float grip, Striker hitter, out float pushShare)
    {
        RacketFace face = hitter.Face;
        float share = hitter.ImpactShare;
        Vector3 relative = s.Velocity - racketVelocity;
        float approach = Mathf.Min(relative.Dot(normal), -0.3f);
        Vector3 r = -normal * Radius;  // contact point from the ball's centre
        Vector3 slip = relative - normal * relative.Dot(normal) + s.Spin.Cross(r);

        BallState With(float pushed)
        {
            float pushImpulse = -(1f + face.Restitution) * approach * share * pushed;
            Vector3 friction = slip * (-face.SlipTransfer * grip / RubberGrip * share);
            float limit = face.Friction * pushImpulse;
            if (friction.Length() > limit)
                friction = friction.Normalized() * limit;
            BallState after = s;
            after.Velocity += normal * pushImpulse + friction;
            after.Spin += r.Cross(friction) * (1.5f / (Radius * Radius));
            return after;
        }

        pushShare = 1f;
        BallState full = With(1f);
        if (full.Velocity.Length() <= Human.FastestBall)
            return full;
        // Held to the record: the share of the push that sends it off at exactly that speed.
        float low = 0f, high = 1f;
        for (int i = 0; i < 30; i++)
        {
            float mid = 0.5f * (low + high);
            if (With(mid).Velocity.Length() > Human.FastestBall)
                high = mid;
            else
                low = mid;
        }
        pushShare = low;
        return With(low);
    }

    // ---------------------------------------------------------------- the contact, in time

    // How long the ball is on the rubber: measured 1.3-1.8 ms on inverted rubbers, about the
    // same at any impact speed (the rubber and sponge act as a spring with damping, whose
    // half-period does not depend on how hard it is struck) [Rinaldi et al. 2016, Non linearity
    // of the ball/rubber impact in table tennis].
    public const float ContactTime = 0.0015f;
    // A hollow ball rolls without slipping once friction has taken this share of the slip out of
    // its velocity (1 / (1 + m R^2 / I), I = 2/3 m R^2 for a thin shell): the same threshold as
    // the measured bounce models' alpha = mu (1 + e) v_normal / v_slip >= 0.4 [Gossard 2026,
    // arXiv:2604.11349, after Nakashima et al.].
    public const float RollingShare = 0.4f;

    // What happens in a racket's contact with the ball, worked out exactly as HitByRacket does
    // it (the ball after is HitByRacket's), with when it happens in the contact time:
    //  - the push along the face's normal, from the speed the ball and racket close at; the
    //    force rises and falls as a half sine over ContactTime;
    //  - the slip: how fast the ball's contact point slides across the rubber (the racket's
    //    brush along the face, the ball's own movement across it, and its spin at that point);
    //  - friction, up to RubberFriction times the push, works the slip down. If it can take out
    //    RollingShare of it the ball grips: from then on it rolls on the rubber (the rubber's
    //    surface, stretched, then springs back and adds a little spin); GripsAt is when. If not,
    //    it slides the whole contact and gets what friction gives.
    //  - an impact hard enough to beat the fastest ball ever hit gives back less push (HeldToRecord;
    //    Restitution is the rubber's bounce for it, 0.85 otherwise).
    public readonly record struct RacketContact(
        float Approach, float Slip, float Push, float FrictionNeeded, float FrictionAvailable,
        bool Grips, float GripsAt, float AngleToFace, BallState After, bool HeldToRecord, float Restitution,
        float FaceRestitution = RubberRestitution);

    public static RacketContact Analyse(BallState s, Vector3 normal, Vector3 racketVelocity, float grip = RubberGrip, Striker? hitter = null)
    {
        Striker h = hitter ?? Striker.Default;
        RacketFace face = h.Face;
        BallState after = Hit(s, normal, racketVelocity, grip, h, out float pushShare);
        float share = h.ImpactShare;
        Vector3 relative = s.Velocity - racketVelocity;
        float approach = Mathf.Min(relative.Dot(normal), -0.3f);
        float push = -(1f + face.Restitution) * approach * share * pushShare;
        Vector3 r = -normal * Radius;
        Vector3 slip = relative - normal * relative.Dot(normal) + s.Spin.Cross(r);
        float needed = RollingShare * share * slip.Length();
        float available = face.Friction * push;
        bool grips = needed <= available;
        // The friction impulse grows with the push's: by time t of a half-sine force, the share
        // (1 - cos(pi t / T)) / 2 of it has acted.
        float gripsAt = grips ? ContactTime / Mathf.Pi * Mathf.Acos(Mathf.Clamp(1f - 2f * needed / Mathf.Max(available, 1e-6f), -1f, 1f)) : ContactTime;
        // The racket's path against the face: 90 degrees drives straight through the ball's
        // centre, near 0 brushes along the face.
        float pathSpeed = racketVelocity.Length();
        float angle = pathSpeed > 1e-3f ? Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(Mathf.Abs(racketVelocity.Dot(normal)) / pathSpeed, 0f, 1f))) : 90f;
        float restitution = (1f + face.Restitution) * pushShare - 1f;
        return new RacketContact(-approach, slip.Length(), push, needed, available, grips, gripsAt, angle, after, pushShare < 1f, restitution, face.Restitution);
    }

    // One line for the log: the stroke's contact, in time.
    public static string Describe(RacketContact c) =>
        $"contact {ContactTime * 1000f:F1} ms: path {c.AngleToFace:F0}° to the face, closing {c.Approach:F1} m/s, slip {c.Slip:F1} m/s; "
        + (c.Slip < 0.05f ? "nothing slips (straight through the centre, no spin)"
           : c.Grips ? $"slides {c.GripsAt * 1000f:F2} ms then grips and rolls {(ContactTime - c.GripsAt) * 1000f:F2} ms"
                   : $"slides all {ContactTime * 1000f:F1} ms (friction {c.FrictionAvailable:F1} of the {c.FrictionNeeded:F1} m/s needed to grip)")
        + (c.HeldToRecord ? $"; held to the {Human.FastestBall:F0} m/s record (restitution {c.FaceRestitution:F2} -> {c.Restitution:F2})" : "");

    // The longer the ball dwells on the rubber, the closer the rubber's grip comes to stopping
    // the contact slip. RubberGrip (0.95: how fully a grip stops the slip) is GripForDwell of
    // 3.6 ms: a fitted curve for the Serve Lab's brush lengths, not the measured time the ball is
    // on the rubber (ContactTime, 1.3-1.8 ms).
    const float DwellTimeConstant = 0.0012f;
    public static float GripForDwell(float seconds) => 1f - Mathf.Exp(-seconds / DwellTimeConstant);

    // Friction impulse at the contact point of a thin-shell sphere. Full grip removes 2/5 of
    // the contact slip from the linear velocity and turns it into spin, so topspin kicks the
    // ball forward off the table and backspin holds it back. The friction cannot exceed the
    // table's Coulomb limit for how hard the ball lands, so a low, slow ball with heavy spin
    // (a short underspin serve) slides through its bounce and keeps much of its spin.
    static void ApplyContactFriction(ref Vector3 v, ref Vector3 spin, float normalImpulse)
    {
        Vector3 r = new Vector3(0f, -Radius, 0f);
        Vector3 slip = new Vector3(v.X, 0f, v.Z) + spin.Cross(r);
        Vector3 dv = slip * (-0.4f * TableGrip);
        float limit = TableFriction * normalImpulse;
        if (dv.Length() > limit)
            dv = dv.Normalized() * limit;
        v += dv;
        spin += r.Cross(dv) * (1.5f / (Radius * Radius));
    }

    // Turns the mesh with the spin and feeds the ball shader its blur and glow.
    void UpdateSpin(float dt)
    {
        Vector3 spin = State.Spin;
        float rate = spin.Length();
        if (rate > 0.01f)
        {
            Vector3 axis = spin / rate;
            mesh.Basis = (new Basis(axis, rate * dt) * mesh.Basis).Orthonormalized();
            material.SetShaderParameter("spin_axis", mesh.Basis.Inverse() * axis);
        }

        // Topspin turns the ball's top towards its direction of travel (see TableSpec axes).
        bool topspin = spin.X * State.Velocity.Z >= 0f;
        material.SetShaderParameter("blur_angle", Mathf.Min(rate * dt, 4f));
        material.SetShaderParameter("glow_strength", Mathf.Clamp(rate / FullGlowSpin, 0f, 1f));
        material.SetShaderParameter("glow_color", topspin ? TopspinGlow : BackspinGlow);
    }

    // A soft blob under the ball: far easier to judge its height than with real shadows.
    void UpdateShadow()
    {
        Vector3 p = Position;
        bool overTable = Mathf.Abs(p.X) <= TableSpec.HalfWidth && Mathf.Abs(p.Z) <= TableSpec.HalfLength
            && p.Y >= TableSpec.Height;
        float ground = overTable ? TableSpec.Height + 0.002f : 0.002f;
        shadow.GlobalPosition = new Vector3(p.X, ground, p.Z);
        float size = Mathf.Clamp(1.2f - (p.Y - ground) * 0.8f, 0.4f, 1.2f);
        shadow.Scale = new Vector3(size, 1f, size);
    }
}
