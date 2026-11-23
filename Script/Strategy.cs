using Godot;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

// A rally plan, made on the strategy map (StrategyMap): who serves and from where, then each shot
// in turn, the two players taking turns: the method it is played with (the method table's: a
// serve, a forehand drive, a backhand push...), where it lands on the other half, its spin, how
// much, and its pace. Saved in res://Strategies/<name>.json.
//
// Places are the table's (world): x across, z along the table, + towards the near end. Player A
// plays from the far end (-z: the CPU, or the yellow NPC), player B from the near end (+z: you, or
// the near NPC).
public class Strategy
{
    public const string Folder = "res://Strategies";

    public string Name { get; set; } = "";
    public string Server { get; set; } = "A";   // "A" the far end, "B" the near end
    public float ServerX { get; set; } = 0.25f; // where the server stands across its end line (m)
    public List<Shot> Shots { get; set; } = new();

    public class Shot
    {
        public string Method { get; set; } = "serve"; // a MotionMethods id
        public string Action { get; set; }            // a variant (StrokeActions id), or null: the method's default
        public float LandX { get; set; }
        public float LandZ { get; set; }               // where it lands on the other half (its second bounce for a serve)
        public string Spin { get; set; } = "back";    // top, back, sideleft, sideright, none
        public string Amount { get; set; } = "light"; // none, light, heavy
        public string Pace { get; set; } = "medium";  // slow, medium, fast
    }

    public static readonly string[] Spins = { "top", "back", "sideleft", "sideright", "none" };
    public static readonly string[] Amounts = { "none", "light", "heavy" };
    public static readonly string[] Paces = { "slow", "medium", "fast" };

    public string HitterOf(int shot) => shot % 2 == 0 ? Server : Other(Server);
    public static string Other(string player) => player == "A" ? "B" : "A";
    // The end a player plays from: -1 the far (A), +1 the near (B), as the game's sides.
    public static int SideOf(string player) => player == "A" ? -1 : 1;

    // A forehand: the method's (fh_..., a serve, a smash); a backhand bh_....
    public static bool Forehand(string method) => !method.StartsWith("bh_");

    // The stroke the game plays a method with.
    public static StrokeStyle StyleOf(string method) => method switch
    {
        "fh_push" or "bh_push" or "fh_chop" or "bh_chop" => StrokeStyle.Push,
        "fh_block" or "bh_block" => StrokeStyle.Block,
        "smash" => StrokeStyle.Smash,
        _ => StrokeStyle.Drive, // drives, loops and flicks brush up the ball
    };

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string PathOf(string name) => ProjectSettings.GlobalizePath($"{Folder}/{name}.json");

    public void Save()
    {
        Directory.CreateDirectory(ProjectSettings.GlobalizePath(Folder));
        File.WriteAllText(PathOf(Name), JsonSerializer.Serialize(this, Json));
    }

    public static Strategy Load(string name)
    {
        string path = PathOf(name);
        if (!File.Exists(path))
            return null;
        Strategy s = JsonSerializer.Deserialize<Strategy>(File.ReadAllText(path));
        if (s != null)
            s.Name = name;
        return s;
    }

    public static List<string> Saved()
    {
        string folder = ProjectSettings.GlobalizePath(Folder);
        return Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.json").Select(Path.GetFileNameWithoutExtension).OrderBy(n => n).ToList()
            : new List<string>();
    }

    // A new plan: A serves short backspin to B's forehand, B pushes it back long to A's backhand,
    // A loops it.
    public static Strategy Example() => new()
    {
        Name = "my_strategy",
        Shots =
        {
            new Shot { Method = "serve", LandX = -0.35f, LandZ = 0.45f, Spin = "back", Amount = "heavy", Pace = "slow" },
            new Shot { Method = "fh_push", LandX = 0.45f, LandZ = -1.0f, Spin = "back", Amount = "light", Pace = "medium" },
            new Shot { Method = "fh_drive", LandX = -0.5f, LandZ = 1.05f, Spin = "top", Amount = "heavy", Pace = "fast" },
        },
    };
}

// The plan being played in the game: the NPCs play their planned shots, in turn, until the rally
// leaves the plan (a missed or extra shot), then their own game. Struck counts the shots of the
// rally (the serve the first), set back at each serve by the match.
public static class StrategyRun
{
    public static Strategy Active;
    public static int Struck;
    public static bool StartNpcMatch; // the game to open in NPC vs NPC (from the Pose Lab's map)

    // The shot the player at this end (-1 far, +1 near) is to play now, or null.
    public static Strategy.Shot For(int side)
    {
        if (Active == null || Struck >= Active.Shots.Count)
            return null;
        return Strategy.SideOf(Active.HitterOf(Struck)) == side ? Active.Shots[Struck] : null;
    }

    public static string Describe(Strategy.Shot s, string hitter) =>
        $"{hitter} {(MotionMethods.Find(s.Method)?.Name ?? s.Method).ToLower()}, {(s.Spin == "none" || s.Amount == "none" ? "no spin" : $"{s.Amount} {SpinWords(s.Spin)}")}, {s.Pace}";

    public static string SpinWords(string spin) => spin switch
    {
        "top" => "topspin",
        "back" => "backspin",
        "sideleft" => "sidespin (brushed left)",
        "sideright" => "sidespin (brushed right)",
        _ => "no spin",
    };
}
