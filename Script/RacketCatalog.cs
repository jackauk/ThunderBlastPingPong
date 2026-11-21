using Godot;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

// The racket collection: blades and rubbers from makers round the world (Config/racket_catalog.json),
// each with its real name and published specs (reference only) and an invented name for the game.
// Read again when the file changes.
public static class RacketCatalog
{
    public const string FilePath = "res://Config/racket_catalog.json";
    static string Disk => ProjectSettings.GlobalizePath(FilePath);

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public class Data
    {
        public List<BladeSpec> Blades { get; set; } = new();
        public List<RubberSpec> Rubbers { get; set; } = new();
    }

    static Data data = new();
    static System.DateTime readTime;
    static bool read;

    public static Data Current
    {
        get
        {
            System.DateTime time = File.Exists(Disk) ? File.GetLastWriteTimeUtc(Disk) : default;
            if (!read || time != readTime)
            {
                read = true;
                readTime = time;
                data = new Data();
                if (time != default)
                {
                    try
                    {
                        data = JsonSerializer.Deserialize<Data>(File.ReadAllText(Disk), Json) ?? new Data();
                    }
                    catch (System.Exception e) when (e is IOException or JsonException)
                    {
                        GD.PushWarning($"{FilePath}: {e.Message}");
                    }
                }
            }
            return data;
        }
    }

    public static List<BladeSpec> Blades => Current.Blades;
    public static List<RubberSpec> Rubbers => Current.Rubbers;
    public static BladeSpec Blade(string id) => Blades.FirstOrDefault(b => b.Id == id);
    public static RubberSpec Rubber(string id) => Rubbers.FirstOrDefault(r => r.Id == id);
}

// Who plays with which racket (Config/equipment.json): the player and each NPC, by key. No entry
// (or one naming something no longer in the catalogue) means the game's standard racket. Read
// again when the file changes; Version moves on each time, for the paddles to repaint.
public static class Equipment
{
    public const string FilePath = "res://Config/equipment.json";
    static string Disk => ProjectSettings.GlobalizePath(FilePath);

    public const string Player = "player";
    public static readonly (string Key, string Label)[] Holders =
    {
        (Player, "You (the player)"),
        ("Opponent", "Far NPC (cpu / yellow)"),
        ("OpponentNear", "Near NPC (blue, NPC vs NPC)"),
    };

    static Dictionary<string, RacketSetup> table = new();
    static System.DateTime readTime, catalogTime;
    static bool read;
    static int version;
    static readonly Dictionary<string, Racket> built = new();

    static Dictionary<string, RacketSetup> Table
    {
        get
        {
            System.DateTime time = File.Exists(Disk) ? File.GetLastWriteTimeUtc(Disk) : default;
            string catalog = ProjectSettings.GlobalizePath(RacketCatalog.FilePath);
            System.DateTime ctime = File.Exists(catalog) ? File.GetLastWriteTimeUtc(catalog) : default;
            if (!read || time != readTime || ctime != catalogTime)
            {
                read = true;
                readTime = time;
                catalogTime = ctime;
                version++;
                built.Clear();
                table = new Dictionary<string, RacketSetup>();
                if (time != default)
                {
                    try
                    {
                        table = JsonSerializer.Deserialize<Dictionary<string, RacketSetup>>(File.ReadAllText(Disk), RacketCatalog.Json) ?? new();
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

    // Changes whenever the equipment or the catalogue is read again.
    public static int Version
    {
        get
        {
            _ = Table;
            return version;
        }
    }

    public static RacketSetup Setup(string holder) => Table.TryGetValue(holder, out RacketSetup s) ? s : null;

    // The holder's racket, or null for the standard one.
    public static Racket For(string holder)
    {
        RacketSetup s = Setup(holder);
        if (s == null)
            return null;
        if (built.TryGetValue(holder, out Racket racket))
            return racket;
        BladeSpec blade = RacketCatalog.Blade(s.Blade);
        RubberSpec fh = RacketCatalog.Rubber(s.Forehand), bh = RacketCatalog.Rubber(s.Backhand);
        racket = blade == null || fh == null || bh == null ? null
            : new Racket(blade, fh, s.ForehandMm, s.ForehandColor, bh, s.BackhandMm, s.BackhandColor, s.Handle);
        built[holder] = racket;
        return racket;
    }

    // Saves the holder's racket (null: back to the standard one).
    public static void Set(string holder, RacketSetup setup)
    {
        var t = new Dictionary<string, RacketSetup>(Table);
        if (setup == null)
            t.Remove(holder);
        else
            t[holder] = setup;
        Directory.CreateDirectory(Path.GetDirectoryName(Disk));
        File.WriteAllText(Disk, JsonSerializer.Serialize(t, RacketCatalog.Json));
        read = false;
    }

    // The face a holder strikes with: its racket's forehand or backhand side (a penhold racket
    // plays one side on both wings), or the standard racket's.
    public static Striker Striker(string holder, float bodyMass, bool forehand)
    {
        Racket r = For(holder);
        return r == null ? new Striker(bodyMass, RacketFace.Standard) : new Striker(bodyMass, r.Face(forehand || r.Penhold));
    }
}
