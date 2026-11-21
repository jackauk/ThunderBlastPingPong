using Godot;
using System.Collections.Generic;
using System.Linq;

// The method table popup (Pose Lab, "Method table"): the same rows as the Pose Lab's ACTION
// picker (MotionMethods.Rows: each method, then its variants), each with
// what it is used for in the game, the saved motions ticked for it, and lists to tick them from.
// A rally stroke is ticked in two parts: the first action (usually footwork, getting to the
// ball) and the main action (the stroke: drive, push, flick, chop...), each picked at random
// from its own ticks; the ready stance and the serve are one motion. A tick is saved at once, and
// the running game uses it.
public partial class MethodTable : Window
{
    GridContainer grid;
    Label status;
    readonly Dictionary<(string, MotionMethods.Part), Label> summaries = new();

    static readonly Color Live = new(0.55f, 0.9f, 0.6f);
    static readonly Color NotYet = new(0.6f, 0.62f, 0.68f);
    static readonly Color Warn = new(1f, 0.7f, 0.4f);
    static readonly Color Head = new(1f, 0.82f, 0.45f);

    public override void _Ready()
    {
        Title = "Method table: motions picked at random";
        Size = new Vector2I(1220, 640);
        MinSize = new Vector2I(760, 360);
        Visible = false;
        Exclusive = false;
        Transient = true;
        CloseRequested += Hide;
        ProcessMode = ProcessModeEnum.Always; // the game is frozen behind the Pose Lab

        var back = new Panel();
        back.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        back.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.1f, 0.11f, 0.13f) });
        AddChild(back);
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride($"margin_{side}", 14);
        AddChild(margin);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        margin.AddChild(column);

        column.AddChild(Text("Tick one or more saved motions for a method. A rally stroke has two parts, played one after the other: "
            + "the first action (usually footwork: getting to the ball) and the main action (the stroke itself). Each time the NPC needs "
            + "it, one ticked motion is picked at random for each part. A tick is saved at once (" + MotionMethods.FilePath
            + ") and the running game uses it. The rows are the Pose Lab's actions: each method (its default action) and then its variants. "
            + "With none ticked, a row plays its own action's motion once saved; a variant with none uses its method's.", 14, new Color(0.85f, 0.87f, 0.92f), true));

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        column.AddChild(scroll);
        grid = new GridContainer { Columns = 6, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 10);
        scroll.AddChild(grid);

        foreach (string head in new[] { "Method", "Used in the game", "1. First action (usually footwork)", "", "2. Main action (the stroke)", "" })
            grid.AddChild(Text(head, 14, Head));
        foreach (MotionMethods.Method m in MotionMethods.Rows)
            AddRow(m);

        var bottom = new HBoxContainer();
        status = Text("", 13, NotYet);
        status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        bottom.AddChild(status);
        var close = new Button { Text = "Close" };
        close.Pressed += Hide;
        bottom.AddChild(close);
        column.AddChild(bottom);

        AboutToPopup += Refresh;
    }

    static Label Text(string text, int size, Color color, bool wrap = false)
    {
        var label = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        if (wrap)
        {
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        }
        return label;
    }

    void AddRow(MotionMethods.Method m)
    {
        var name = Text(m.Name, m.Parent == null ? 15 : 13, m.Parent == null ? Colors.White : new Color(0.8f, 0.83f, 0.9f), true);
        name.CustomMinimumSize = new Vector2(190f, 0f);
        grid.AddChild(name);
        var inGame = Text(m.InGame, 13, m.Live ? Live : NotYet, true);
        inGame.CustomMinimumSize = new Vector2(190f, 0f);
        grid.AddChild(inGame);
        if (m.HasFirst)
            AddPart(m, MotionMethods.Part.First);
        else
        {
            grid.AddChild(Text("(one motion, no first action)", 13, NotYet));
            grid.AddChild(new Control());
        }
        AddPart(m, MotionMethods.Part.Main);
    }

    // A part's summary and the list to tick from: every saved motion, the likely ones first
    // (footwork for the first action, those named for the method for the main one).
    void AddPart(MotionMethods.Method m, MotionMethods.Part part)
    {
        var summary = Text("", 13, Colors.White, true);
        summary.CustomMinimumSize = new Vector2(250f, 0f);
        summaries[(m.Id, part)] = summary;
        grid.AddChild(summary);

        var choose = new MenuButton { Text = "Choose ▾", Flat = false };
        PopupMenu menu = choose.GetPopup();
        menu.HideOnCheckableItemSelection = false; // tick several in one go
        choose.AboutToPopup += () =>
        {
            menu.Clear();
            List<string> ticked = MotionMethods.Ticked(m.Id, part);
            string hint = m.Id.Contains('_') ? m.Id.Split('_')[1] : m.Id;
            bool Likely(string n) => part == MotionMethods.Part.First ? MotionMethods.IsFootwork(n) : n.Contains(hint) && !MotionMethods.IsFootwork(n);
            List<string> saved = PoseSequence.Saved();
            string fallback = part == MotionMethods.Part.Main ? MotionMethods.DefaultMotion(m.Id) : null;
            foreach (string name in saved.OrderBy(n => n == fallback ? 0 : Likely(n) ? 1 : 2).ThenBy(n => n))
            {
                menu.AddCheckItem(name);
                menu.SetItemChecked(menu.ItemCount - 1, ticked.Contains(name));
            }
            foreach (string gone in ticked.Where(n => !saved.Contains(n)))
            {
                menu.AddCheckItem(gone + "   (not saved)");
                menu.SetItemChecked(menu.ItemCount - 1, true);
                menu.SetItemMetadata(menu.ItemCount - 1, gone);
            }
            if (menu.ItemCount == 0)
            {
                menu.AddItem("no saved motions yet");
                menu.SetItemDisabled(0, true);
            }
        };
        menu.IndexPressed += index =>
        {
            int i = (int)index;
            if (!menu.IsItemCheckable(i))
                return;
            string name = menu.GetItemMetadata(i).VariantType == Variant.Type.String ? menu.GetItemMetadata(i).AsString() : menu.GetItemText(i);
            bool on = !menu.IsItemChecked(i);
            menu.SetItemChecked(i, on);
            List<string> ticked = MotionMethods.Ticked(m.Id, part);
            if (on)
                ticked.Add(name);
            else
                ticked.Remove(name);
            MotionMethods.Set(m.Id, ticked, part);
            Refresh();
            status.Text = $"{m.Name}, {(part == MotionMethods.Part.First ? "first action" : "main action")}: {(on ? "added" : "removed")} {name}; saved.";
        };
        grid.AddChild(choose);
    }

    // The summaries from the table as it is now, with what the game does with each.
    void Refresh()
    {
        HashSet<string> saved = PoseSequence.Saved().ToHashSet();
        foreach (MotionMethods.Method m in MotionMethods.Rows)
        {
            foreach (MotionMethods.Part part in new[] { MotionMethods.Part.First, MotionMethods.Part.Main })
            {
                if (!summaries.TryGetValue((m.Id, part), out Label summary))
                    continue;
                List<string> ticked = MotionMethods.Ticked(m.Id, part);
                var notes = new List<string>();
                if (m.Id == "serve" || m.Parent == "serve")
                {
                    // Which ticked ones can be served from (ServeMotion reads the toss and the contact).
                    foreach (string name in ticked.Where(saved.Contains))
                    {
                        ServeMotion.Read(name, PoseSequence.Load(name), out string why);
                        if (why != null)
                            notes.Add($"{name} is left out: {why}");
                    }
                }
                foreach (string gone in ticked.Where(n => !saved.Contains(n)))
                    notes.Add($"{gone} is not saved");
                // A footwork motion as the stroke, or a stroke as the footwork, is likely a slip.
                if (m.HasFirst)
                    foreach (string name in ticked.Where(saved.Contains))
                    {
                        if (part == MotionMethods.Part.Main && MotionMethods.IsFootwork(name))
                            notes.Add($"{name} is footwork: meant as the first action?");
                    }
                string fallback = part == MotionMethods.Part.Main ? DefaultFallback(m, saved)
                    : m.Parent != null ? $"none ticked: as {MotionMethods.Find(m.Parent).Name}"
                    : "none ticked: no footwork, the stroke from where it stands";
                summary.Text = (ticked.Count == 0 ? fallback : string.Join(", ", ticked) + (ticked.Count > 1 ? $"   ({ticked.Count}, one at random)" : ""))
                               + (notes.Count > 0 ? "\n" + string.Join("\n", notes) : "");
                summary.AddThemeColorOverride("font_color", notes.Count > 0 ? Warn : ticked.Count == 0 ? NotYet : Colors.White);
            }
        }
    }

    // With none ticked, the method's default action (the Pose Lab's action of the same name) is
    // played once saved; before that the ready stance and the serve have their own stand-ins.
    static string DefaultFallback(MotionMethods.Method m, HashSet<string> saved)
    {
        string file = MotionMethods.DefaultMotion(m.Id);
        if (m.Parent != null)
        {
            string method = MotionMethods.Find(m.Parent).Name;
            return file != null && saved.Contains(file) ? $"none ticked: its own motion ({file})"
                : m.Parent == "serve" ? $"none ticked; its own motion ({file}) is not made yet, so it adds no serve"
                : $"none ticked; its own motion ({file}) is not made yet, so as {method}";
        }
        if (file != null && saved.Contains(file))
            return $"none ticked: the default, {m.Name} ({file})";
        string missing = $"none ticked; the default, {m.Name} ({file}), is not made yet";
        return m.Id switch
        {
            "ready" => missing + ". " + ReadyStanceFallback(saved),
            "serve" => missing + ": every saved motion named \"...serve...\"",
            _ => missing,
        };
    }

    static string ReadyStanceFallback(HashSet<string> saved)
    {
        string newest = saved.Where(ReadyStance.IsReadyName)
            .OrderByDescending(n => System.IO.File.GetLastWriteTimeUtc(PoseSequence.PathOf(n))).FirstOrDefault();
        return newest == null ? "None saved: the NPC's own ready" : $"Meanwhile the newest ready_* ({newest})";
    }
}
