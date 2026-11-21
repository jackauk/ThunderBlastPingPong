using Godot;

// How tired a player is, and what that costs them. Stamina (0 = tires fast and recovers slowly,
// 1 = a well-trained athlete) sets how hard each bit of work hits and how quickly it wears off.
//
// Table tennis is short bursts and short breaks: a rally of a few seconds, then about 10 s
// between points and up to a minute between games (ITTF rules). Tiredness comes in two parts:
//  - Breath: the burst itself. It builds within a rally (strokes, pushes off the floor, lunges)
//    and mostly wears off in the break before the next point; a long, running rally leaves the
//    player out of breath for the next one too.
//  - Wear: what builds over the match, a little of every effort, and barely recovers before it
//    is over (a game break helps a bit).
// A tired player reacts later, reads the ball worse, pushes off the floor less hard, swings a
// little slower and controls the racket face less well. The sizes of these are the game's
// tuning, in line with the general finding that fatigue costs reaction, footwork, racket speed
// and above all accuracy; they are not measured values.
public class Fatigue
{
    // Rest the game counts for its own (shortened) breaks: what a real player gets.
    public const float PointBreak = 10f;   // s between points
    public const float GameBreak = 60f;    // s between games

    // Efforts, in units of one forehand drive.
    public const float SmashEffort = 1.6f;
    public const float DriveEffort = 1f;
    public const float PushEffort = 0.5f;
    public const float BlockEffort = 0.4f;
    public const float ServeEffort = 0.3f;
    public const float LungeEffort = 0.9f;
    public const float ShufflePerMetre = 0.4f; // pushes off both feet, per metre the feet travel
    public const float RallyEffort = 0.1f;     // per second of a ball in play: up on the toes

    // What tiredness costs at its worst (fatigue 1).
    const float SlowerReaction = 0.3f;   // reaction time up 30%
    const float WorseRead = 0.5f;        // read error up 50%
    const float WeakerLegs = 0.3f;       // floor push and top speed down 30%
    const float SlowerSwing = 0.15f;     // racket speed down 15%
    const float WorseControl = 0.6f;     // racket face wobble up 60%
    const float MoreMistakes = 1f;       // fumbles twice as likely

    public float Stamina = 0.6f;
    public float Breath { get; private set; }
    public float Wear { get; private set; }
    public float Level => Mathf.Clamp(Breath + Wear, 0f, 1f);

    // Breath gained per unit of effort, and its recovery time while resting (s).
    float BreathPerEffort => Mathf.Lerp(0.07f, 0.03f, Stamina);
    float BreathRecovery => Mathf.Lerp(14f, 6f, Stamina);
    // During a rally it recovers too, but far slower.
    const float RecoveryInPlay = 0.25f;
    float WearPerEffort => Mathf.Lerp(0.003f, 0.0008f, Stamina);
    float WearRecovery => Mathf.Lerp(400f, 200f, Stamina);
    const float MostWear = 0.8f;

    public void Work(float effort)
    {
        Breath = Mathf.Min(Breath + effort * BreathPerEffort, 1f);
        Wear = Mathf.Min(Wear + effort * WearPerEffort, MostWear);
    }

    // Time passing in play: on the toes, breathing a little.
    public void Play(float dt)
    {
        Work(RallyEffort * dt);
        Breath *= Mathf.Exp(-RecoveryInPlay * dt / BreathRecovery);
    }

    // A break of this many seconds (PointBreak, GameBreak).
    public void Rest(float seconds)
    {
        Breath *= Mathf.Exp(-seconds / BreathRecovery);
        Wear *= Mathf.Exp(-seconds / WearRecovery);
    }

    public void Clear() => Breath = Wear = 0f;

    public float ReactionFactor => 1f + SlowerReaction * Level;
    public float ReadFactor => 1f + WorseRead * Level;
    public float LegFactor => 1f - WeakerLegs * Level;
    public float SwingFactor => 1f - SlowerSwing * Level;
    public float ControlFactor => 1f + WorseControl * Level;
    public float MistakeFactor => 1f + MoreMistakes * Level;

    public override string ToString() =>
        $"tired {Level * 100f:F0}% (breath {Breath * 100f:F0}%, wear {Wear * 100f:F0}%, stamina {Stamina:F2})";
}
