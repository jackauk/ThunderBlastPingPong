using Godot;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

// The method table (set up in the Pose Lab's "Method table" popup): for each method (a moment
// where the NPC plays a motion: its ready stance, its serve, its strokes) the saved motions
// ticked for it, one of which is picked at random each time. A rally stroke has two parts, each
// ticked on its own: the first action (usually footwork: getting to the ball, Footwork motions)
// and then the main action (the stroke itself: drive, push, flick, chop...). Kept in
// res://Config/motion_methods.json (outside res://Poses, so it is not taken for a motion; the
// first actions under "<method>.first") and read afresh when it changes, so the running game
// uses a change at once.
public static class MotionMethods
{
    // Live: the game plays saved motions for it now. The strokes in a rally are still the NPC's
    // own (worked out for each ball), so motions ticked for them are kept for when they are.
    // HasFirst: it is played in two parts (First, then Main).
    // Parent: for a variant's row, the method it is a variant of.
    public record Method(string Id, string Name, string InGame, bool Live, bool HasFirst = false, string Parent = null);

    public enum Part { First, Main }

    const string NotYet = "not yet: rally strokes are the NPC's own";

    public static readonly Method[] All =
    {
        new("ready", "Ready stance", "the NPC waiting between strokes and to receive", true),
        new("serve", "Serve", "the NPC serving", true),
        new("fh_drive", "Forehand drive / loop", NotYet, false, true),
        new("bh_drive", "Backhand drive", NotYet, false, true),
        new("fh_push", "Forehand push", NotYet, false, true),
        new("bh_push", "Backhand push", NotYet, false, true),
        new("fh_flick", "Forehand flick", NotYet, false, true),
        new("bh_flick", "Backhand flick", NotYet, false, true),
        new("fh_block", "Forehand block", NotYet, false, true),
        new("bh_block", "Backhand block", NotYet, false, true),
        new("fh_chop", "Forehand chop", NotYet, false, true),
        new("bh_chop", "Backhand chop", NotYet, false, true),
        new("smash", "Smash", NotYet, false, true),
    };

    static string Key(string method, Part part) => part == Part.First ? method + ".first" : method;

    public const string FilePath = "res://Config/motion_methods.json";
    static string Disk => ProjectSettings.GlobalizePath(FilePath);

    static Dictionary<string, List<string>> table = new();
    static System.DateTime readTime;
    static bool read;

    static Dictionary<string, List<string>> Table
    {
        get
        {
            System.DateTime time = File.Exists(Disk) ? File.GetLastWriteTimeUtc(Disk) : default;
            if (!read || time != readTime)
            {
                read = true;
                readTime = time;
                table = new Dictionary<string, List<string>>();
                if (time != default)
                {
                    try
                    {
                        table = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(Disk)) ?? new();
                    }
                    catch (System.Exception e) when (e is IOException or JsonException)
                    {
                        GD.PushWarning($"{FilePath}: {e.Message}");
                    }
                }
            }
            return table;
        }
    }

    // The motions ticked for a method's part, as set (some may no longer be saved).
    public static List<string> Ticked(string method, Part part = Part.Main) =>
        Table.TryGetValue(Key(method, part), out List<string> names) ? new List<string>(names) : new List<string>();

    // The ticked motions that are saved now.
    public static List<string> Chosen(string method, Part part = Part.Main)
    {
        HashSet<string> saved = PoseSequence.Saved().ToHashSet();
        return Ticked(method, part).Where(saved.Contains).ToList();
    }

    public static Method Find(string id) => All.FirstOrDefault(m => m.Id == id);

    // Every row of the method table: the same list as the Pose Lab's ACTION picker (footwork aside,
    // it is a first action), in the same order and words. Each method under its own name (its
    // default action), then each of its variants as a row of its own, keyed by the variant
    // action's id (such as "fh_vs_topspin"): it can have its own motions ticked.
    static Method[] rows;
    public static Method[] Rows => rows ??= StrokeActions.All
        .Where(a => StrokeActions.MethodOf(a) != null)
        .Select(a =>
        {
            Method m = Find(StrokeActions.MethodOf(a));
            return StrokeActions.IsDefault(a) ? m : m with
            {
                Id = a.Id,
                Name = StrokeActions.Label(a),
                InGame = m.Live ? $"with {m.Name}: one of its motions" : m.InGame,
                Parent = m.Id,
            };
        })
        .ToArray();

    public static Method Row(string id) => Rows.FirstOrDefault(r => r.Id == id);

    // The row an action is ticked under: a default action under its method, a variant its own.
    public static string RowOf(StrokeActions.Action action) =>
        StrokeActions.IsDefault(action) ? StrokeActions.MethodOf(action) : StrokeActions.MethodOf(action) != null ? action.Id : null;

    // A row's own motions: the ticked ones that are saved, or with none ticked, its action's own
    // motion (res://Poses/<id>.json: the method's default action, or the variant) once saved.
    public static List<string> Own(string id)
    {
        List<string> chosen = Chosen(id);
        if (chosen.Count > 0)
            return chosen;
        string fallback = DefaultMotion(id);
        return fallback != null && PoseSequence.Saved().Contains(fallback) ? new List<string> { fallback } : new List<string>();
    }

    // What the game plays for a row's main action: its own motions, or for a variant with none,
    // its method's.
    public static List<string> InUse(string id)
    {
        List<string> own = Own(id);
        return own.Count == 0 && Row(id)?.Parent is string parent ? InUse(parent) : own;
    }

    // The first action (footwork) for a row: ticked for it, or for a variant with none, its
    // method's.
    public static List<string> FirstInUse(string id)
    {
        List<string> chosen = Chosen(id, Part.First);
        return chosen.Count == 0 && Row(id)?.Parent is string parent ? FirstInUse(parent) : chosen;
    }

    // The file a row's action is saved as (res://Poses/<id>.json), or null.
    public static string DefaultMotion(string id) => Row(id)?.Parent != null ? id : StrokeActions.DefaultOf(id)?.Id;

    // A footwork motion (from the Pose Lab's Footwork templates): its name starts "move_".
    public static bool IsFootwork(string motion) => motion.StartsWith("move_");

    public static void Set(string method, IEnumerable<string> names, Part part = Part.Main)
    {
        Dictionary<string, List<string>> t = Table;
        List<string> list = names.Distinct().ToList();
        string key = Key(method, part);
        if (list.Count == 0)
            t.Remove(key);
        else
            t[key] = list;
        Directory.CreateDirectory(Path.GetDirectoryName(Disk));
        File.WriteAllText(Disk, JsonSerializer.Serialize(t, new JsonSerializerOptions { WriteIndented = true }));
        readTime = File.GetLastWriteTimeUtc(Disk);
    }
}
