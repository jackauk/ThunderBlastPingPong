using Godot;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

// A motion made in the Pose Lab: steps, each a whole pose (every part's values) with the time the
// move into it takes. Saved as JSON in res://Poses/<name>.json, so the game can read it.
//
// Values are in the NPC's own frame, the same for either player: metres and degrees, the table
// centred at the origin, the player behind the -Z end line facing +Z, its right hand at -X. Turns
// are positive to the player's left (anticlockwise seen from above).
public class PoseSequence
{
    public class Step
    {
        public string Name { get; set; } = "";
        public float Duration { get; set; } = 0.3f; // seconds to move here from the step before
        public Dictionary<string, float> Values { get; set; } = new();
        // On the step after the contact: how the swing goes through the ball ("snap" or "soft";
        // none is normal). See FinishStyle.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Finish { get; set; }
    }

    // How the racket goes into and through the contact. The move into the contact step and the
    // one into the step after it (the finish) are timed by it, the other moves eased as before:
    //  - Normal: steady acceleration into the ball, then on through to the finish pose, slowing.
    //  - Snap: the speed held back and let go late, so it is three times the move's average at
    //    the ball; then the forearm brakes hard (SnapBrake) just after it, and the racket runs on
    //    a few centimetres by its own inertia and stops short of the finish pose.
    //  - Soft: a gentle start, the swing braked before the ball (0.6 of the average at contact),
    //    then a slow glide to rest.
    public enum FinishStyle { Normal, Snap, Soft }

    public const float SnapBrake = 500f; // m/s², the racket's deceleration as the forearm stops it

    // What the profile makes of the steps' places and move times (metres, seconds, m/s).
    public record Swing(FinishStyle Style, int Contact, float InDistance, float InTime, float ContactSpeed, float PeakSpeed,
                        float FinishDistance, float StopDistance, float StopTime);

    // The contact step: the first named "contact...", with a step before it.
    [JsonIgnore]
    public int ContactIndex => Steps.FindIndex(s => s.Name.ToLowerInvariant().Contains("contact"));

    // Whether there is a contact step with a move into it and a step after it to finish on.
    [JsonIgnore]
    public bool HasFinish
    {
        get
        {
            int c = ContactIndex;
            return c >= 1 && c + 1 < Steps.Count;
        }
    }

    [JsonIgnore]
    public FinishStyle Finish
    {
        get => HasFinish && System.Enum.TryParse(Steps[ContactIndex + 1].Finish, true, out FinishStyle f) ? f : FinishStyle.Normal;
        set
        {
            if (HasFinish)
                Steps[ContactIndex + 1].Finish = value == FinishStyle.Normal ? null : value.ToString().ToLowerInvariant();
        }
    }

    static Vector3 RacketOf(Step s) => PoseRig.RacketAt(s.Values);

    // Cubic from 0 to 1 over 0..1 with these slopes at the ends (monotone for slopes 0..3).
    static float Hermite(float s, float m0, float m1) => (m0 + m1 - 2f) * s * s * s + (3f - 2f * m0 - m1) * s * s + m0 * s;

    // How far into the contact move (0..1) at s (0..1 of its time); its slope at 1 is the
    // contact speed over the move's average.
    static float IntoContact(FinishStyle f, float s) => f switch
    {
        FinishStyle.Snap => s * s * s,
        FinishStyle.Soft => Hermite(s, 0f, 0.6f),
        _ => s * s,
    };

    static float ContactSlope(FinishStyle f) => f switch { FinishStyle.Snap => 3f, FinishStyle.Soft => 0.6f, _ => 2f };

    // The swing worked out for the contact and finish steps, or null without them (or when the
    // racket does not move into the contact).
    public Swing SwingInfo()
    {
        if (!HasFinish)
            return null;
        int c = ContactIndex;
        FinishStyle style = Finish;
        float d1 = RacketOf(Steps[c - 1]).DistanceTo(RacketOf(Steps[c])), t1 = Steps[c].Duration;
        float d2 = RacketOf(Steps[c]).DistanceTo(RacketOf(Steps[c + 1])), t2 = Steps[c + 1].Duration;
        if (d1 < 1e-3f || t1 < 1e-4f)
            return null;
        float average = d1 / t1, contact = ContactSlope(style) * average;
        float peak = contact;
        if (style == FinishStyle.Soft)
        {
            for (float s = 0f; s <= 1f; s += 0.01f)
                peak = Mathf.Max(peak, (Hermite(s + 0.005f, 0f, 0.6f) - Hermite(s - 0.005f, 0f, 0.6f)) / 0.01f * average);
        }
        float stop = style switch
        {
            FinishStyle.Snap => Mathf.Clamp(contact * contact / (2f * SnapBrake), Mathf.Min(0.02f, d2), d2),
            FinishStyle.Soft => Mathf.Min(d2, contact * Mathf.Max(t2, 1e-4f) / 2f),
            _ => d2,
        };
        float stopTime = style == FinishStyle.Normal ? t2 : Mathf.Min(contact > 1e-3f ? 2f * stop / contact : t2, t2);
        return new Swing(style, c, d1, t1, contact, peak, d2, stop, stopTime);
    }

    public string Name { get; set; } = "";
    public string Grip { get; set; } = "Shakehand";
    public string Frame { get; set; } =
        "own frame: metres and degrees; table centred at the origin; player behind the -Z end line facing +Z, right hand at -X; turns positive to the player's left";
    public List<Step> Steps { get; set; } = new();

    public const string Folder = "res://Poses";

    // Time from the first step to the last.
    public float Length
    {
        get
        {
            float t = 0f;
            for (int i = 1; i < Steps.Count; i++)
                t += Steps[i].Duration;
            return t;
        }
    }

    // When step i is reached.
    public float StartOf(int i)
    {
        float t = 0f;
        for (int k = 1; k <= i && k < Steps.Count; k++)
            t += Steps[k].Duration;
        return t;
    }

    // The pose at this time: each value moves from one step's to the next's, eased in and out
    // (or evenly); the moves into the contact and the finish follow the finish style.
    public Dictionary<string, float> Sample(float time, bool ease = true)
    {
        var pose = new Dictionary<string, float>();
        if (Steps.Count == 0)
            return pose;
        if (Steps.Count == 1 || time <= 0f)
            return new Dictionary<string, float>(Steps[0].Values);

        Swing swing = SwingInfo();
        float t = time;
        for (int i = 1; i < Steps.Count; i++)
        {
            float d = Steps[i].Duration;
            if (t <= d || i == Steps.Count - 1)
            {
                float f = d > 1e-5f ? Mathf.Clamp(t / d, 0f, 1f) : 1f;
                if (swing != null && i == swing.Contact)
                    return Blend(Steps[i - 1].Values, Steps[i].Values, IntoContact(swing.Style, f));
                if (swing != null && i == swing.Contact + 1)
                    return Blend(Steps[i - 1].Values, Steps[i].Values, ThroughFinish(swing, Mathf.Min(t, d)));
                if (ease)
                    f = Mathf.SmoothStep(0f, 1f, f);
                return Blend(Reached(i - 1, swing), Steps[i].Values, f);
            }
            t -= d;
        }
        return Reached(Steps.Count - 1, swing);
    }

    // How far from the contact to the finish pose (0..1) the racket is, t seconds after contact.
    static float ThroughFinish(Swing swing, float t)
    {
        if (swing.FinishDistance < 1e-4f || swing.StopDistance < 1e-5f)
            return swing.FinishDistance < 1e-4f ? 1f : 0f;
        float tau = swing.StopTime > 1e-5f ? Mathf.Clamp(t / swing.StopTime, 0f, 1f) : 1f;
        float m0 = Mathf.Min(swing.ContactSpeed * swing.StopTime / swing.StopDistance, 3f);
        return swing.StopDistance / swing.FinishDistance * Hermite(tau, m0, 0f);
    }

    // The pose step i really reaches: a snap or soft finish stops short of its pose.
    Dictionary<string, float> Reached(int i, Swing swing)
    {
        if (swing != null && i == swing.Contact + 1 && swing.Style != FinishStyle.Normal && swing.FinishDistance > 1e-4f)
            return Blend(Steps[i - 1].Values, Steps[i].Values, swing.StopDistance / swing.FinishDistance);
        return new Dictionary<string, float>(Steps[i].Values);
    }

    // The racket's speed (m/s) at this time: the faster of the moments just before and just
    // after (at a step the path turns a corner, which a chord across it would cut short).
    public float RacketSpeed(float time, bool ease = true)
    {
        const float h = 0.001f;
        Vector3 now = PoseRig.RacketAt(Sample(time, ease));
        float Over(float other) => Mathf.Abs(other - time) < 1e-5f ? 0f
            : PoseRig.RacketAt(Sample(other, ease)).DistanceTo(now) / Mathf.Abs(other - time);
        return Mathf.Max(Over(Mathf.Max(time - h, 0f)), Over(Mathf.Min(time + h, Length)));
    }

    // turnRacket: the racket turns the short way between the two (as always between a forehand and
    // a backhand), rather than each angle on its own: for blending two unrelated poses, such as the
    // NPC's own and its ready stance laid over it.
    public static Dictionary<string, float> Blend(Dictionary<string, float> a, Dictionary<string, float> b, float f, bool turnRacket = false)
    {
        var pose = new Dictionary<string, float>(a);
        foreach (var (key, to) in b)
        {
            if (!a.TryGetValue(key, out float from))
                pose[key] = to;
            else if (Wrapping.Contains(key))
                pose[key] = Mathf.Wrap(from + Mathf.Wrap(to - from, -180f, 180f) * f, -180f, 180f); // the short way round
            else
                pose[key] = Mathf.Lerp(from, to, f);
        }
        TurnFace(a, b, f, pose, turnRacket);
        return pose;
    }

    // Between a forehand and a backhand step the racket's angles are measured from different
    // holds (the face flips), so blending the numbers would twist the racket through poses no hand
    // makes, and flip it half way. Instead the racket turns the short way from the one step's
    // place to the other's, and that is put in the angles of the side in use at the moment.
    static void TurnFace(Dictionary<string, float> a, Dictionary<string, float> b, float f, Dictionary<string, float> pose, bool always = false)
    {
        if (!a.TryGetValue("racket.backhand", out float sideA) || !b.TryGetValue("racket.backhand", out float sideB)
            || (!always && (sideA < 0.5f) == (sideB < 0.5f)) || f <= 0f || f >= 1f)
            return;
        Basis turned = RacketTurn(a, sideA < 0.5f).Slerp(RacketTurn(b, sideB < 0.5f), f);
        bool forehand = pose.GetValueOrDefault("racket.backhand") < 0.5f;
        float turn = pose.GetValueOrDefault("racket.turn");
        Vector3 angles = (Hold(forehand).Inverse() * new Basis(Vector3.Up, -Mathf.DegToRad(turn)) * turned)
            .Orthonormalized().GetEuler(EulerOrder.Yzx);
        pose["racket.open"] = AngleSide(forehand) * Mathf.RadToDeg(angles.Y);
        pose["racket.noseUp"] = -Mathf.RadToDeg(angles.Z);
        pose["racket.noseForward"] = AngleSide(forehand) * Mathf.RadToDeg(angles.X);
    }

    // The racket's angles mean the same on either side: "face open" (+) turns the hitting face up
    // and "nose forward" (+) takes the nose towards the table, forehand or backhand. The backhand
    // hold has the other face out, so about it these two turn the other way: their sign is
    // mirrored there (racket.open and racket.noseForward times this; nose up is the same).
    public static float AngleSide(bool forehand) => forehand ? 1f : -1f;

    // The racket's turn for these values (as PoseRig and the Pose Lab build it).
    static Basis Hold(bool forehand) => new Basis(Vector3.Up, Mathf.Pi) * Paddle.StandardHold(forehand);
    static Basis RacketTurn(Dictionary<string, float> p, bool forehand)
    {
        float R(string key) => Mathf.DegToRad(p.GetValueOrDefault(key));
        float side = AngleSide(forehand);
        return (new Basis(Vector3.Up, R("racket.turn")) * Hold(forehand)
                * new Basis(Vector3.Up, side * R("racket.open"))
                * new Basis(Vector3.Back, -R("racket.noseUp"))
                * new Basis(Vector3.Right, side * R("racket.noseForward"))).Orthonormalized();
    }

    // Turns that go all the way round (-180..180): 170 to -170 is 20 degrees, not 340.
    static readonly HashSet<string> Wrapping = new() { "hips.turn", "lfoot.turn", "rfoot.turn" };

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string PathOf(string name) => ProjectSettings.GlobalizePath($"{Folder}/{name}.json");

    public void Save()
    {
        Directory.CreateDirectory(ProjectSettings.GlobalizePath(Folder));
        File.WriteAllText(PathOf(Name), JsonSerializer.Serialize(this, Json));
    }

    public static PoseSequence Load(string name)
    {
        string path = PathOf(name);
        if (!File.Exists(path))
            return null;
        var sequence = JsonSerializer.Deserialize<PoseSequence>(File.ReadAllText(path));
        if (sequence != null)
            sequence.Name = name;
        return sequence;
    }

    public static List<string> Saved()
    {
        var names = new List<string>();
        string folder = ProjectSettings.GlobalizePath(Folder);
        if (Directory.Exists(folder))
        {
            foreach (string file in Directory.GetFiles(folder, "*.json"))
                names.Add(System.IO.Path.GetFileNameWithoutExtension(file));
        }
        names.Sort();
        return names;
    }
}
