using Godot;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// The ready stance made in the Pose Lab (the "Ready stance" action), applied to the game by
// itself. Which motions: those ticked for "ready" in the method table (MotionMethods), one
// picked at random each time the NPC settles into it; with none ticked, the saved motion named
// "ready" or "ready_<anything>" (the most recently saved one when there are several). The NPCs
// stand in it between strokes, its steps playing round and round (a bounce on the knees, say),
// and the Pose Lab's stroke templates start from its first step. A save from the Pose Lab, in
// the game or outside it, is picked up within half a second.
public static class ReadyStance
{
    const ulong CheckEveryMs = 500;
    static List<string> names = new();
    static ulong checkedAt;
    static bool everChecked;
    static string reported;
    static readonly Dictionary<string, (System.DateTime Time, PoseSequence Motion)> loaded = new();

    // The ready motions in use (file names without .json): the ticked ones, or the newest ready_*.
    public static List<string> Names
    {
        get
        {
            ulong now = Time.GetTicksMsec();
            if (!everChecked || now - checkedAt >= CheckEveryMs)
            {
                everChecked = true;
                checkedAt = now;
                names = Find();
                string say = string.Join(", ", names);
                if (say != reported && names.Count > 0)
                    DebugOverlay.Log($"  ready stance {(reported == null ? "in use" : "now")}: {say}{(names.Count > 1 ? " (one at random each time)" : "")}");
                reported = say;
            }
            return names;
        }
    }

    // The first of them (for the Pose Lab's templates, and where the racket rests), or null.
    public static PoseSequence Current => Names.Select(Load).FirstOrDefault(s => s != null);

    public static string Name => Current?.Name;

    // One of them at random, or null.
    public static PoseSequence Pick()
    {
        List<PoseSequence> all = Names.Select(Load).Where(s => s != null).ToList();
        return all.Count == 0 ? null : all[GD.RandRange(0, all.Count - 1)];
    }

    public static bool IsReadyName(string name) =>
        name == StrokeActions.ReadyId || name.StartsWith(StrokeActions.ReadyId + "_");

    static List<string> Find()
    {
        // Ticked in the method table, or the default action's own (ready.json), or the newest ready_*.
        List<string> ticked = MotionMethods.InUse("ready");
        if (ticked.Count > 0)
            return ticked;
        string newest = null;
        System.DateTime newestTime = default;
        foreach (string name in PoseSequence.Saved().Where(IsReadyName))
        {
            System.DateTime time = File.GetLastWriteTimeUtc(PoseSequence.PathOf(name));
            if (newest == null || time > newestTime)
                (newest, newestTime) = (name, time);
        }
        return newest == null ? new List<string>() : new List<string> { newest };
    }

    // A ready motion, read again when its file has changed (the last good read kept while a
    // file is being written).
    static PoseSequence Load(string name)
    {
        string path = PoseSequence.PathOf(name);
        if (!File.Exists(path))
            return null;
        System.DateTime time = File.GetLastWriteTimeUtc(path);
        if (loaded.TryGetValue(name, out var had) && had.Time == time)
            return had.Motion;
        try
        {
            PoseSequence s = PoseSequence.Load(name);
            if (s != null && s.Steps.Count > 0)
            {
                loaded[name] = (time, s);
                return s;
            }
        }
        catch (System.Exception e) when (e is IOException or System.Text.Json.JsonException)
        {
            GD.PushWarning($"ready stance {name}: {e.Message}");
        }
        return had.Motion;
    }

    // The first ready motion's first step (a copy), or null.
    public static Dictionary<string, float> FirstStep() =>
        Current is { } s ? new Dictionary<string, float>(s.Steps[0].Values) : null;

    // A ready motion at this time, its steps played round and round: after the last step it
    // moves back to the first in the time of the first move, so the loop has no jump.
    public static Dictionary<string, float> At(PoseSequence s, float time)
    {
        if (s == null)
            return null;
        if (s.Steps.Count == 1)
            return new Dictionary<string, float>(s.Steps[0].Values);
        float wrap = s.Steps[1].Duration > 1e-3f ? s.Steps[1].Duration : 0.3f;
        float cycle = s.Length + wrap;
        float t = Mathf.PosMod(time, cycle);
        if (t <= s.Length)
            return s.Sample(t);
        return PoseSequence.Blend(s.Steps[^1].Values, s.Steps[0].Values, Mathf.SmoothStep(0f, 1f, (t - s.Length) / wrap));
    }
}
