using Godot;
using System.Collections.Generic;
using System.Linq;

// A serve the NPC can play from the motion database: every motion saved in res://Poses (made in
// the Pose Lab) with "serve" in its name. The NPC picks one at random for each serve, stands in
// its first step holding the ball, then plays it at real speed:
//  - the toss leaves the free hand as the motion moves from the step before the ball's highest
//    one (TossAt, from TossFrom), thrown so that it falls onto the racket at the contact;
//  - the contact is the step named "contact" or, failing that, the one after the toss where the
//    racket is nearest the ball (ContactAt, at ContactPoint on the racket);
//  - the ball is served there by the game's contact physics, underspin when the face is open or
//    the racket comes down into the ball, topspin when it brushes up;
//  - the motion plays on to its last step, then the NPC's own play takes over.
// Motions without a ball tossed and met (no ball shown, no rise, no step after the toss) are
// left out, with the reason.
public sealed class ServeMotion
{
    public string Name;
    public PoseSequence Sequence;
    public float TossAt;
    public Vector3 TossFrom;
    public float ContactAt;
    public Vector3 ContactPoint;
    public bool Underspin;
    public PoseSequence.SpinLevel Spin; // how much spin, as the Pose Lab's contact is shaped for
    public PoseSequence.Brush Brush;    // which way the racket brushes the ball (the kind of spin)
    public float Length => Sequence.Length;

    public Dictionary<string, float> PoseAt(float time) => Sequence.Sample(Mathf.Clamp(time, 0f, Length));
    public Vector3 BallAt(float time)
    {
        Dictionary<string, float> p = PoseAt(time);
        return new Vector3(p.GetValueOrDefault("ball.x"), p.GetValueOrDefault("ball.y"), p.GetValueOrDefault("ball.z"));
    }

    const float NearRacket = 0.15f; // a ball this near the racket's middle is met where it is

    // The serve motions in the database, read afresh (so ones saved in the Pose Lab while the
    // game runs are in it): those of every serve row of the method table (the serve and its
    // variants; each row's ticked motions, or with none, its action's saved motion), or with none
    // at all, every one named "...serve..."; skipped lists the left-out ones with why.
    public static List<ServeMotion> All(out List<string> skipped)
    {
        var all = new List<ServeMotion>();
        skipped = new List<string>();
        // Every serve row of the method table: the serve and its variants (such as the backhand serve).
        string[] serveRows = MotionMethods.Rows.Where(r => r.Id == "serve" || r.Parent == "serve").Select(r => r.Id).ToArray();
        List<string> ticked = serveRows.SelectMany(r => MotionMethods.Ticked(r)).Distinct().ToList();
        HashSet<string> saved = PoseSequence.Saved().ToHashSet();
        foreach (string gone in ticked.Where(n => !saved.Contains(n)))
            skipped.Add($"{gone}: ticked for serve in the method table, but not saved");
        // Each serve row's own motions (ticked, or with none its action's saved motion); with
        // none at all, every saved motion named "...serve...".
        List<string> own = serveRows.SelectMany(MotionMethods.Own).Distinct().ToList();
        IEnumerable<string> names = own.Count > 0 ? own : saved.Where(n => n.ToLowerInvariant().Contains("serve"));
        foreach (string name in names.OrderBy(n => n))
        {
            PoseSequence sequence;
            try
            {
                sequence = PoseSequence.Load(name);
            }
            catch (System.Exception e)
            {
                skipped.Add($"{name}: cannot be read ({e.Message})");
                continue;
            }
            ServeMotion motion = Read(name, sequence, out string why);
            if (motion != null)
                all.Add(motion);
            else
                skipped.Add($"{name}: {why}");
        }
        return all;
    }

    public static ServeMotion Read(string name, PoseSequence sequence, out string why)
    {
        why = null;
        List<PoseSequence.Step> steps = sequence?.Steps;
        if (steps == null || steps.Count < 3)
        {
            why = "fewer than 3 steps";
            return null;
        }
        static Vector3 Ball(Dictionary<string, float> v) =>
            new(v.GetValueOrDefault("ball.x"), v.GetValueOrDefault("ball.y"), v.GetValueOrDefault("ball.z"));
        bool Shown(int i) => steps[i].Values.GetValueOrDefault("ball.show") > 0.5f;
        if (!Shown(0))
        {
            why = "no ball shown at the start";
            return null;
        }

        // The toss: the ball's highest step (not the first or the last); it leaves the hand on
        // the move into it.
        int apex = -1;
        for (int i = 1; i < steps.Count - 1; i++)
            if (Shown(i) && (apex < 0 || Ball(steps[i].Values).Y > Ball(steps[apex].Values).Y))
                apex = i;
        if (apex < 0 || Ball(steps[apex].Values).Y < Ball(steps[apex - 1].Values).Y + 0.05f)
        {
            why = "the ball is never tossed up";
            return null;
        }
        int toss = apex - 1;

        // The contact: the step marked as meeting the ball (the Pose Lab's contact lock), a step
        // named so, or the racket nearest the ball after the toss.
        int contact = steps.FindIndex(st => st.Meets);
        if (contact < apex)
            contact = -1;
        for (int i = apex; i < steps.Count && contact < 0; i++)
            if (steps[i].Name.ToLowerInvariant().Contains("contact"))
            {
                contact = i;
                break;
            }
        if (contact < 0)
        {
            float nearest = float.MaxValue;
            for (int i = apex + 1; i < steps.Count; i++)
            {
                float d = PoseRig.RacketAt(steps[i].Values).DistanceTo(Ball(steps[i].Values));
                if (d < nearest)
                {
                    nearest = d;
                    contact = i;
                }
            }
        }
        if (contact <= toss)
        {
            why = "no step meets the ball after the toss";
            return null;
        }

        var motion = new ServeMotion { Name = name, Sequence = sequence };
        motion.TossAt = sequence.StartOf(toss);
        motion.TossFrom = Ball(steps[toss].Values);
        motion.ContactAt = sequence.StartOf(contact);
        Vector3 racket = PoseRig.RacketAt(steps[contact].Values), ball = Ball(steps[contact].Values);
        motion.ContactPoint = racket.DistanceTo(ball) <= NearRacket ? ball : racket;
        if (motion.ContactAt - motion.TossAt < 0.15f)
        {
            why = "under 0.15 s from the toss to the contact";
            return null;
        }
        // Brushing under (face open past 45 degrees, or the racket coming down into the ball)
        // gives underspin; brushing up the back, topspin.
        Vector3 before = PoseRig.RacketAt(steps[contact - 1].Values);
        motion.Underspin = steps[contact].Values.GetValueOrDefault("racket.open") > 45f || racket.Y < before.Y;
        motion.Spin = sequence.ContactIndex == contact ? sequence.ContactSpin : PoseSequence.SpinLevel.AsShaped;
        motion.Brush = sequence.ContactIndex == contact ? sequence.ContactBrush : PoseSequence.Brush.AsMade;
        if (motion.Brush == PoseSequence.Brush.Top)
            motion.Underspin = false;
        else if (motion.Brush != PoseSequence.Brush.AsMade)
            motion.Underspin = true; // backspin, or sidespin served with a little backspin under it
        return motion;
    }

    // The velocity (own frame) that tosses the ball from TossFrom to ContactPoint in the time
    // between, with the game's ball physics (gravity and air drag).
    public Vector3 TossVelocity()
    {
        float time = ContactAt - TossAt;
        Vector3 v = (ContactPoint - TossFrom) / time + Vector3.Up * (0.5f * Human.G * time);
        for (int pass = 0; pass < 6; pass++)
            v += (ContactPoint - Flight(v, time).Position) / time;
        return v;
    }

    // The tossed ball (own frame) this long after leaving the hand at this velocity.
    public BallState Flight(Vector3 velocity, float seconds)
    {
        var s = new BallState(TossFrom, velocity, Vector3.Zero);
        float dt = Ball.SubStepTime;
        for (float t = 0f; t < seconds - 1e-6f; t += dt)
            Ball.Step(ref s, Mathf.Min(dt, seconds - t), false);
        return s;
    }

    public override string ToString() =>
        $"{Name} ({(Spin == PoseSequence.SpinLevel.AsShaped ? "" : Spin.ToString().ToLower() + " ")}{(Underspin ? "underspin" : "topspin")}; toss at {TossAt:F2} s, contact at {ContactAt:F2} s, ends {Length:F2} s)";
}
