using Godot;
using System.Collections.Generic;
using System.Linq;

// The pose check over everything (Main.tscn with the user argument --pose-check, or
// --pose-check=fix): every saved motion and every template, each step and the playback between
// (every 10 ms), checked as the Pose Lab checks a pose (PoseCheck, and nothing through the table
// or the body). The findings go to res://Review/pose_check.log and the output (lines starting
// "POSECHECK"). With fix, a saved motion's steps are put right where that is safe: the wrist eased
// (and the racket's nose turned a little if that is not enough), an over-bent elbow opened by
// moving the hand out from the shoulder, the hips moved back over the feet for balance, an over-bent knee straightened by raising the hips, the waist and lean brought
// into range; the motion is saved back, the old one kept in res://Review/pose_backups. What
// cannot be fixed without changing the stroke (a wrong face, a collision) is
// only reported. Only problems are fixed, not warnings (at the edge of the range). Templates are
// code (StrokeActions): reported, and with fix the values that would put them right are
// suggested, to change there.
public partial class PoseLab
{
    public async void CheckAll(bool fix)
    {
        for (int f = 0; f < 120; f++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Open();
        while (gamePose == null)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        mode = Mode.Carry;
        posingOther = true; // every step and moment is posed in turn: none is being edited

        var log = new List<string> { $"Pose check {Time.GetDatetimeStringFromSystem()} ({(fix ? "fix" : "report only")})", "" };
        int problems = 0, warnings = 0, fixedSteps = 0;
        void Count(Findings f) { problems += f.Problems; warnings += f.Warnings; }

        foreach (string name in PoseSequence.Saved())
        {
            PoseSequence s = PoseSequence.Load(name);
            if (s == null)
                continue;
            Findings found = CheckSequence(s);
            if (fix && (found.Fixable.Count > 0 || found.TwistedMoves.Count > 0))
            {
                int done = FixSequence(s, found.Fixable, log, name);
                fixedSteps += done;
                Findings after = CheckSequence(s);
                log.Add($"{name}: fixed {done} step(s); now:");
                found = after;
            }
            Count(found);
            log.Add($"{name} (saved motion): {found.Summary}");
            log.AddRange(found.Lines.Select(l => "    " + l));
        }
        foreach (StrokeActions.Action a in StrokeActions.All)
        {
            PoseSequence s = StrokeActions.Build(a, DefaultPose());
            // As Open gives it: each step but a saved start eased at the wrist.
            bool savedStart = StrokeActions.UsesReady(a) && StrokeActions.SavedReady() != null;
            foreach (PoseSequence.Step st in s.Steps.Skip(savedStart ? 1 : 0))
            {
                pose = new Dictionary<string, float>(st.Values);
                Apply();
                EaseWrist();
                st.Values = new Dictionary<string, float>(pose);
            }
            Findings found = CheckSequence(s);
            Count(found);
            log.Add($"template {a.Id} ({a.Name}): {found.Summary}");
            log.AddRange(found.Lines.Select(l => "    " + l));
            if (fix && found.Fixable.Count > 0)
                SuggestForTemplate(s, found.Fixable, log, a.Id);
            if (fix && found.TwistedMoves.Count > 0)
            {
                var said = new List<string>();
                if (FixTwistedMoves(s, found.TwistedMoves, said) > 0)
                    log.AddRange(said.Select(l => "    suggested:" + l.TrimStart().Insert(0, " ")));
            }
        }
        string summary = $"POSECHECK done: {problems} problem(s), {warnings} warning(s){(fix ? $", {fixedSteps} step(s) fixed" : "")}";
        log.Insert(1, summary);
        string path = ProjectSettings.GlobalizePath("res://Review/pose_check.log");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
        System.IO.File.WriteAllLines(path, log);
        foreach (string line in log.Where(l => l.Contains('✖') || l.Contains("fixed") || l.Contains("suggested") || l.StartsWith("template") || !l.StartsWith(" ")))
            GD.Print("POSECHECK " + line);
        GD.Print(summary + $"  (log: {path})");
        GetTree().Quit();
    }

    class Findings
    {
        public readonly List<string> Lines = new();
        public readonly HashSet<int> Fixable = new();   // steps with something FixSequence can put right
        public readonly HashSet<int> TwistedMoves = new(); // moves (into step i) where the wrist goes past its range
        public int Problems, Warnings;
        public string Summary => Problems + Warnings == 0 ? "ok" : $"{Problems} problem(s), {Warnings} warning(s)";
    }

    static readonly HashSet<PoseCheck.Kind> FixableKinds = new() { PoseCheck.Kind.Wrist, PoseCheck.Kind.Balance, PoseCheck.Kind.Knee, PoseCheck.Kind.Waist, PoseCheck.Kind.Lean, PoseCheck.Kind.Elbow };

    Findings CheckSequence(PoseSequence s)
    {
        var f = new Findings();
        for (int i = 0; i < s.Steps.Count; i++)
        {
            pose = new Dictionary<string, float>(s.Steps[i].Values);
            Apply();
            List<PoseCheck.Issue> issues = RunPoseCheck(s.Steps[i].Name.Contains("contact"));
            foreach (PoseCheck.Issue issue in issues)
            {
                f.Lines.Add($"step {i + 1} \"{s.Steps[i].Name}\": {(issue.Serious ? "✖" : "!")} {issue.Text}");
                if (issue.Serious) f.Problems++; else f.Warnings++;
                if (issue.Serious && FixableKinds.Contains(issue.Kind))
                    f.Fixable.Add(i);
            }
            foreach (string c in Collisions())
            {
                f.Lines.Add($"step {i + 1} \"{s.Steps[i].Name}\": ✖ {c}");
                f.Problems++;
            }
        }
        // Between the steps: joints past their range (balance and the face are for the steps:
        // a body in motion is off its feet for a moment, and the face turns on its way).
        var spans = new Dictionary<string, (float From, float To, float Worst)>();
        for (float t = 0f; t <= s.Length + 1e-4f; t += 0.01f)
        {
            pose = s.Sample(t, true);
            Apply();
            foreach (PoseCheck.Issue issue in RunPoseCheck(false).Where(x => x.Serious && x.Kind is not PoseCheck.Kind.Balance))
            {
                if (issue.Kind == PoseCheck.Kind.Wrist)
                    f.TwistedMoves.Add(MoveAt(s, t));
                string key = issue.Text.Split(' ')[0] + " " + issue.Kind;
                spans[key] = spans.TryGetValue(key, out var sp) ? (sp.From, t, Mathf.Max(sp.Worst, issue.Amount)) : (t, t, issue.Amount);
            }
        }
        foreach (var (key, sp) in spans)
        {
            f.Lines.Add($"playback {sp.From:F2}-{sp.To:F2} s: ✖ {key.Split(' ')[1].ToLower()} past its range ({key.Split(' ')[0]}; worst {sp.Worst:F0} past)");
            f.Problems++;
        }
        return f;
    }

    // The move (into step i) playing at time t.
    static int MoveAt(PoseSequence s, float t)
    {
        int at = 1;
        for (int i = 1; i < s.Steps.Count; i++)
            if (t > s.StartOf(i - 1) + 1e-4f)
                at = i;
        return at;
    }

    // The worst the wrist goes past its range on the move into step i (degrees; 0 within it).
    float WristPastOn(PoseSequence s, int i)
    {
        float worst = 0f;
        for (float t = s.StartOf(i - 1); t <= s.StartOf(i) + 1e-4f; t += 0.01f)
        {
            pose = s.Sample(t, true);
            Apply();
            foreach (PoseCheck.Issue issue in RunPoseCheck(false).Where(x => x.Serious && x.Kind == PoseCheck.Kind.Wrist))
                worst = Mathf.Max(worst, issue.Amount);
        }
        return worst;
    }

    // Moves whose wrist goes past its range: the racket turned the short way round instead (one
    // turn rather than each angle on its own, Step.Turn), kept where that takes the wrist past it
    // less or not at all. Returns the moves changed.
    int FixTwistedMoves(PoseSequence s, IEnumerable<int> moves, List<string> log)
    {
        int changed = 0;
        foreach (int i in moves.OrderBy(i => i))
        {
            if (s.Steps[i].ShortTurn)
                continue;
            float before = WristPastOn(s, i);
            s.Steps[i].ShortTurn = true;
            float after = WristPastOn(s, i);
            if (after < before - 0.5f)
            {
                changed++;
                log.Add($"    move into step {i + 1} \"{s.Steps[i].Name}\": the racket turns the short way round (wrist {before:F0}° past its range -> {(after <= 0f ? "within it" : $"{after:F0}° past")})");
            }
            else
                s.Steps[i].ShortTurn = false;
        }
        return changed;
    }

    // Puts a saved motion's steps right where that is safe; the old motion is kept first.
    // Returns how many steps changed.
    int FixSequence(PoseSequence s, HashSet<int> steps, List<string> log, string name)
    {
        string backup = ProjectSettings.GlobalizePath($"res://Review/pose_backups/{name}_{Time.GetDatetimeStringFromSystem().Replace(":", "-")}.json");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(backup));
        System.IO.File.Copy(PoseSequence.PathOf(name), backup, true);
        int changed = FixSteps(s, steps, log, name);
        Findings now = CheckSequence(s);
        changed += FixTwistedMoves(s, now.TwistedMoves, log);
        if (changed > 0)
            s.Save();
        log.Add($"{name}: old motion kept as {backup}");
        return changed;
    }

    // A template's steps put right on a copy: what to change in its code (StrokeActions), as
    // the racket's place from the body and the values that changed.
    void SuggestForTemplate(PoseSequence s, HashSet<int> steps, List<string> log, string id)
    {
        var before = s.Steps.Select(st => new Dictionary<string, float>(st.Values)).ToList();
        if (FixSteps(s, steps, new List<string>(), id) == 0)
            return;
        for (int i = 0; i < s.Steps.Count; i++)
        {
            Dictionary<string, float> a = before[i], b = s.Steps[i].Values;
            var changes = b.Where(kv => kv.Key is not ("racket.x" or "racket.z" or "body.x" or "body.z") && !kv.Key.StartsWith("lfoot") && !kv.Key.StartsWith("rfoot")
                                        && System.Math.Abs(kv.Value - a.GetValueOrDefault(kv.Key)) > 0.005f)
                .Select(kv => $"{kv.Key} {a.GetValueOrDefault(kv.Key):F2} -> {kv.Value:F2}").ToList();
            float rx = b["racket.x"] - b["body.x"], rz = b["racket.z"] - b["body.z"];
            float ax = a["racket.x"] - a["body.x"], az = a["racket.z"] - a["body.z"];
            if (System.Math.Abs(rx - ax) > 0.005f || System.Math.Abs(rz - az) > 0.005f)
                changes.Insert(0, $"racket from the body ({ax:F2}, {a["racket.y"]:F2}, {az:F2}) -> ({rx:F2}, {b["racket.y"]:F2}, {rz:F2})");
            if (changes.Count > 0)
                log.Add($"    suggested for step {i + 1} \"{s.Steps[i].Name}\": {string.Join(", ", changes)}");
        }
    }

    int FixSteps(PoseSequence s, HashSet<int> steps, List<string> log, string name)
    {
        int changed = 0;
        foreach (int i in steps.OrderBy(i => i))
        {
            pose = new Dictionary<string, float>(s.Steps[i].Values);
            Apply();
            var did = new List<string>();
            for (int round = 0; round < 6; round++)
            {
                List<PoseCheck.Issue> issues = RunPoseCheck(s.Steps[i].Name.Contains("contact"));
                PoseCheck.Issue issue = issues.FirstOrDefault(x => x.Serious && FixableKinds.Contains(x.Kind));
                if (issue == null)
                    break;
                switch (issue.Kind)
                {
                    case PoseCheck.Kind.Wrist:
                        EaseWrist();
                        if (WristBend() > PoseCheck.WristMost)
                            TurnNoseForWrist();
                        did.Add("wrist eased");
                        break;
                    case PoseCheck.Kind.Balance:
                        BalanceOverFeet();
                        did.Add("hips moved over the feet");
                        break;
                    case PoseCheck.Kind.Knee:
                        Trunk(() => Hold("hips.height", V("hips.height") + 0.02f));
                        did.Add("hips raised");
                        break;
                    case PoseCheck.Kind.Waist:
                        Trunk(() => Hold("waist.turn", V("waist.turn") * 0.8f));
                        did.Add("waist turned back");
                        break;
                    case PoseCheck.Kind.Lean:
                        Trunk(() => Hold("hips.lean", Mathf.Clamp(V("hips.lean"), -PoseCheck.LeanBackMost + 5f, PoseCheck.LeanForwardMost - 10f)));
                        did.Add("lean brought into range");
                        break;
                    case PoseCheck.Kind.Elbow:
                        // The hand out from the shoulder, 3 cm at a time, till the elbow opens.
                        bool right = issue.Text.StartsWith("right");
                        Arm arm = right ? rightArm : leftArm;
                        Vector3 outward = O(arm.WristPoint - arm.ShoulderPoint).Normalized() * 0.03f;
                        string at = right ? "racket" : "lhand";
                        Hold(at + ".x", V(at + ".x") + outward.X);
                        Hold(at + ".y", V(at + ".y") + outward.Y);
                        Hold(at + ".z", V(at + ".z") + outward.Z);
                        Apply();
                        EaseWrist();
                        did.Add($"{(right ? "racket hand" : "free hand")} moved out from the shoulder");
                        break;
                }
            }
            if (did.Count > 0)
            {
                s.Steps[i].Values = new Dictionary<string, float>(pose);
                changed++;
                log.Add($"{name}: step {i + 1} \"{s.Steps[i].Name}\" fixed: {string.Join(", ", did.Distinct())}");
            }
        }
        return changed;
    }

    // The racket's nose turned (up to 20° each way) to the least wrist bend, after easing.
    void TurnNoseForWrist()
    {
        var start = new Dictionary<string, float>(pose);
        Dictionary<string, float> best = start;
        float bestBend = WristBend();
        for (float nf = -20f; nf <= 20f; nf += 10f)
            for (float nu = -20f; nu <= 20f; nu += 10f)
            {
                pose = new Dictionary<string, float>(start);
                Hold("racket.noseForward", start.GetValueOrDefault("racket.noseForward") + nf);
                Hold("racket.noseUp", start.GetValueOrDefault("racket.noseUp") + nu);
                Apply();
                EaseWrist();
                if (WristBend() < bestBend)
                {
                    bestBend = WristBend();
                    best = new Dictionary<string, float>(pose);
                }
            }
        pose = best;
        SyncFields();
        Apply();
    }

    // The hips moved (the feet planted, the arms coming along) until the centre of gravity is
    // back over the feet.
    void BalanceOverFeet()
    {
        for (int k = 0; k < 8; k++)
        {
            PoseCheck.Issue off = RunPoseCheck(false).FirstOrDefault(x => x.Kind == PoseCheck.Kind.Balance);
            if (off == null)
                return;
            Vector3 cog = O(CentreOfGravity());
            Vector3 feet = (FootAt(0) + FootAt(1)) * 0.5f;
            var way = new Vector2(feet.X - cog.X, feet.Z - cog.Z).Normalized() * (off.Amount + 0.01f);
            Carrying(() =>
            {
                Hold("body.x", V("body.x") + way.X);
                Hold("body.z", V("body.z") + way.Y);
            });
        }
    }
}
