using Godot;
using System.Collections.Generic;
using System.Linq;

// The racket collection's browser (Pose Lab "Rackets...", or F3 in a match): pick who to equip
// (the player or either NPC), a blade and a rubber for each side from the catalogue
// (RacketCatalog), the sponge thickness, topsheet colour and handle, see what the racket does in
// the game's own contact physics, and equip it (Equipment; the running game uses it at once).
// Names show as the game's invented ones or the real ones, which are reference only.
public partial class RacketLocker : Window
{
    static RacketLocker instance;

    // Opens the browser, made the first time.
    public static void Open(SceneTree tree)
    {
        // Deferred: the root may be busy adding its own children (a call from a _Ready).
        if (!IsInstanceValid(instance))
        {
            instance = new RacketLocker { Name = "RacketLocker" };
            tree.Root.CallDeferred(Node.MethodName.AddChild, instance);
        }
        Input.MouseMode = Input.MouseModeEnum.Visible;
        RacketLocker window = instance;
        Callable.From(() => window.PopupCenteredRatio(0.94f)).CallDeferred();
    }

    OptionButton holderPicker, namesPicker, handlePicker;
    LineEdit search;
    ItemList bladeList, forehandList, backhandList;
    OptionButton forehandMm, backhandMm, forehandColour, backhandColour;
    RichTextLabel details;
    Label current, status;
    bool filling;

    string holder = Equipment.Player;
    bool fictional = true;
    readonly RacketSetup pick = new();

    static readonly Color Head = new(1f, 0.82f, 0.45f);
    static readonly Color Dim = new(0.65f, 0.68f, 0.74f);

    public override void _Ready()
    {
        Title = "Racket collection";
        Size = new Vector2I(1200, 680);
        MinSize = new Vector2I(900, 520);
        Visible = false;
        Transient = true;
        CloseRequested += Hide;
        ProcessMode = ProcessModeEnum.Always; // the game may be frozen behind (Pose Lab)

        var back = new Panel();
        back.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        back.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.1f, 0.11f, 0.13f) });
        AddChild(back);
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride($"margin_{side}", 12);
        AddChild(margin);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        margin.AddChild(column);

        // Who, which names, search.
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 10);
        top.AddChild(Text("Racket for", 14, Head));
        holderPicker = new OptionButton();
        foreach (var (_, label) in Equipment.Holders)
            holderPicker.AddItem(label);
        holderPicker.ItemSelected += i => { holder = Equipment.Holders[(int)i].Key; LoadPick(); };
        top.AddChild(holderPicker);
        top.AddChild(Text("Names", 14, Head));
        namesPicker = new OptionButton();
        namesPicker.AddItem("Game names (invented, to ship)");
        namesPicker.AddItem("Real names (reference only, trademarks)");
        namesPicker.ItemSelected += i => { fictional = i == 0; FillLists(); };
        top.AddChild(namesPicker);
        top.AddChild(Text("Search", 14, Head));
        search = new LineEdit { PlaceholderText = "brand, model, carbon, long pips...", CustomMinimumSize = new Vector2(200f, 0f), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        search.TextChanged += _ => FillLists();
        top.AddChild(search);
        column.AddChild(top);

        current = Text("", 13, Dim, true);
        column.AddChild(current);

        // Blade | forehand rubber | backhand rubber | what it does.
        var lists = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        lists.AddThemeConstantOverride("separation", 10);
        column.AddChild(lists);

        bladeList = List(lists, "Blade", out VBoxContainer bladeBox);
        bladeList.ItemSelected += i => { pick.Blade = Id(bladeList, i); pick.Handle = null; FillOptions(); ShowPick(); };
        handlePicker = Option(bladeBox, "Handle", i => { pick.Handle = handlePicker.GetItemText((int)i).Split(' ')[0]; ShowPick(); });

        forehandList = List(lists, "Forehand rubber (red side as standard)", out VBoxContainer fhBox);
        forehandList.ItemSelected += i => { pick.Forehand = Id(forehandList, i); pick.ForehandMm = null; FillOptions(); ShowPick(); };
        forehandMm = Option(fhBox, "Sponge", i => { pick.ForehandMm = Thickness(forehandMm, i); ShowPick(); });
        forehandColour = Option(fhBox, "Colour", i => { pick.ForehandColor = forehandColour.GetItemText((int)i).Split(' ')[0]; ShowPick(); });

        backhandList = List(lists, "Backhand rubber", out VBoxContainer bhBox);
        backhandList.ItemSelected += i => { pick.Backhand = Id(backhandList, i); pick.BackhandMm = null; FillOptions(); ShowPick(); };
        backhandMm = Option(bhBox, "Sponge", i => { pick.BackhandMm = Thickness(backhandMm, i); ShowPick(); });
        backhandColour = Option(bhBox, "Colour", i => { pick.BackhandColor = backhandColour.GetItemText((int)i).Split(' ')[0]; ShowPick(); });

        details = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = false, ScrollActive = true, SelectionEnabled = true,
            CustomMinimumSize = new Vector2(330f, 0f), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        details.AddThemeFontSizeOverride("normal_font_size", 13);
        details.AddThemeFontSizeOverride("bold_font_size", 14);
        details.MetaClicked += meta => OS.ShellOpen(meta.AsString());
        lists.AddChild(details);

        // Equip.
        var bottom = new HBoxContainer();
        bottom.AddThemeConstantOverride("separation", 10);
        status = Text("", 13, Dim);
        status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        bottom.AddChild(status);
        var standard = new Button { Text = "Back to the standard racket" };
        standard.Pressed += () => { Equipment.Set(holder, null); status.Text = $"{HolderLabel}: standard racket."; ShowCurrent(); };
        bottom.AddChild(standard);
        var equip = new Button { Text = "Equip" };
        equip.Pressed += Equip;
        bottom.AddChild(equip);
        var close = new Button { Text = "Close" };
        close.Pressed += Hide;
        bottom.AddChild(close);
        column.AddChild(bottom);

        AboutToPopup += LoadPick;
    }

    string HolderLabel => Equipment.Holders.First(h => h.Key == holder).Label;

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

    static ItemList List(HBoxContainer parent, string title, out VBoxContainer box)
    {
        box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(190f, 0f) };
        box.AddChild(Text(title, 14, Head));
        var list = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        list.AddThemeFontSizeOverride("font_size", 13);
        box.AddChild(list);
        parent.AddChild(box);
        return list;
    }

    OptionButton Option(VBoxContainer parent, string title, System.Action<long> picked)
    {
        var row = new HBoxContainer();
        row.AddChild(Text(title, 13, Dim));
        var option = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        option.ItemSelected += i => { if (!filling) picked(i); };
        row.AddChild(option);
        parent.AddChild(row);
        return option;
    }

    static string Id(ItemList list, long index) => list.GetItemMetadata((int)index).AsString();
    static float? Thickness(OptionButton option, long index) =>
        float.Parse(option.GetItemText((int)index).Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture) is var t && t >= 0f ? t : null;

    // What the holder has now, as the starting pick (the first items when it has the standard).
    void LoadPick()
    {
        RacketSetup s = Equipment.Setup(holder);
        pick.Blade = s?.Blade ?? RacketCatalog.Blades.FirstOrDefault()?.Id;
        pick.Forehand = s?.Forehand ?? RacketCatalog.Rubbers.FirstOrDefault(r => r.Kind == RubberKind.InvertedTensor)?.Id ?? RacketCatalog.Rubbers.FirstOrDefault()?.Id;
        pick.Backhand = s?.Backhand ?? pick.Forehand;
        pick.ForehandMm = s?.ForehandMm;
        pick.BackhandMm = s?.BackhandMm;
        pick.ForehandColor = s?.ForehandColor ?? "red";
        pick.BackhandColor = s?.BackhandColor ?? "black";
        pick.Handle = s?.Handle;
        status.Text = RacketCatalog.Blades.Count == 0
            ? $"The catalogue is empty: {RacketCatalog.FilePath} is missing or unreadable."
            : $"{RacketCatalog.Blades.Count} blades and {RacketCatalog.Rubbers.Count} rubbers from {RacketCatalog.Blades.Select(b => b.Brand).Concat(RacketCatalog.Rubbers.Select(r => r.Brand)).Distinct().Count()} makers.";
        FillLists();
        ShowCurrent();
    }

    bool Matches(string text, params string[] fields)
    {
        string q = search.Text.Trim().ToLowerInvariant();
        return q.Length == 0 || q.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
            .All(word => fields.Any(f => f != null && f.ToLowerInvariant().Contains(word))) || text.ToLowerInvariant().Contains(q);
    }

    void FillLists()
    {
        filling = true;
        bladeList.Clear();
        foreach (BladeSpec b in RacketCatalog.Blades.OrderBy(b => fictional ? b.FictionalBrand : b.Brand).ThenBy(b => fictional ? b.FictionalModel : b.Model))
        {
            string text = $"{b.Name(fictional)}  ·  {b.SpeedClass ?? "?"}  {b.Construction ?? ""}{(b.Composite != null ? " " + b.Composite : "")}  {(b.WeightG is float w ? $"{w:F0} g" : "")}{(b.PenholdOnly ? "  penhold" : "")}";
            if (!Matches(text, b.Brand, b.Model, b.FictionalBrand, b.FictionalModel, b.Composite, b.Country, b.SpeedClass, b.OuterPly, string.Join(" ", b.Handles), b.PenholdOnly ? "penhold" : "shakehand"))
                continue;
            int i = bladeList.AddItem(text);
            bladeList.SetItemTooltip(i, text);
            bladeList.SetItemMetadata(i, b.Id);
            if (b.Id == pick.Blade)
                bladeList.Select(i);
        }
        foreach (ItemList list in new[] { forehandList, backhandList })
        {
            string chosen = list == forehandList ? pick.Forehand : pick.Backhand;
            list.Clear();
            foreach (RubberSpec r in RacketCatalog.Rubbers.OrderBy(r => r.Kind).ThenBy(r => fictional ? r.FictionalBrand : r.Brand).ThenBy(r => fictional ? r.FictionalModel : r.Model))
            {
                string text = $"{r.Name(fictional)}  ·  {KindWords(r.Kind)}{(r.Hardness is float h ? $"  {h:0.#}° {r.HardnessScale}" : "")}";
                if (!Matches(text, r.Brand, r.Model, r.FictionalBrand, r.FictionalModel, r.Type, KindWords(r.Kind)))
                    continue;
                int i = list.AddItem(text);
                list.SetItemTooltip(i, text);
                list.SetItemMetadata(i, r.Id);
                if (r.Id == chosen)
                    list.Select(i);
            }
        }
        foreach (ItemList list in new[] { bladeList, forehandList, backhandList })
            Callable.From(list.EnsureCurrentIsVisible).CallDeferred(); // after layout
        filling = false;
        FillOptions();
        ShowPick();
    }

    static string KindWords(RubberKind k) => k switch
    {
        RubberKind.InvertedTensor => "tensor inverted",
        RubberKind.InvertedTacky => "tacky inverted",
        RubberKind.InvertedClassic => "classic inverted",
        RubberKind.ShortPips => "short pips",
        RubberKind.MediumPips => "medium pips",
        RubberKind.LongPips => "long pips",
        _ => "anti-spin",
    };

    // Handle, thicknesses and colours offered for what is picked.
    void FillOptions()
    {
        filling = true;
        BladeSpec blade = RacketCatalog.Blade(pick.Blade);
        handlePicker.Clear();
        foreach (string h in blade?.Handles.DefaultIfEmpty("FL") ?? new[] { "FL" })
            handlePicker.AddItem(h + " " + h switch { "FL" => "(flared)", "ST" => "(straight)", "AN" => "(anatomic)", "CPEN" => "(Chinese penhold)", "JPEN" => "(Japanese penhold)", _ => "" });
        pick.Handle ??= blade?.Handles.FirstOrDefault() ?? "FL";
        SelectText(handlePicker, pick.Handle);

        FillRubberOptions(RacketCatalog.Rubber(pick.Forehand), forehandMm, forehandColour, pick.ForehandMm, pick.ForehandColor);
        FillRubberOptions(RacketCatalog.Rubber(pick.Backhand), backhandMm, backhandColour, pick.BackhandMm, pick.BackhandColor);
        filling = false;
    }

    static void FillRubberOptions(RubberSpec r, OptionButton mm, OptionButton colour, float? chosenMm, string chosenColour)
    {
        mm.Clear();
        colour.Clear();
        if (r == null)
            return;
        float shown = chosenMm ?? r.DefaultThickness;
        foreach (float t in r.ThicknessesMm.DefaultIfEmpty(r.DefaultThickness))
            mm.AddItem(t <= 0f ? "0 (OX, no sponge)" : $"{t:0.0#} mm");
        SelectText(mm, shown <= 0f ? "0" : $"{shown:0.0#}");
        foreach (string c in r.Colors.Union(new[] { "red", "black" }).Distinct())
            colour.AddItem(c);
        if (chosenColour != null && !r.Colors.Union(new[] { "red", "black" }).Contains(chosenColour))
            colour.AddItem(chosenColour + " (not listed by the maker)");
        SelectText(colour, chosenColour);
    }

    static void SelectText(OptionButton option, string start)
    {
        for (int i = 0; i < option.ItemCount; i++)
        {
            if (option.GetItemText(i).StartsWith(start ?? "\u0001"))
            {
                option.Select(i);
                return;
            }
        }
        if (option.ItemCount > 0)
            option.Select(0);
    }

    Racket Picked()
    {
        BladeSpec blade = RacketCatalog.Blade(pick.Blade);
        RubberSpec fh = RacketCatalog.Rubber(pick.Forehand), bh = RacketCatalog.Rubber(pick.Backhand);
        return blade == null || fh == null || bh == null ? null
            : new Racket(blade, fh, pick.ForehandMm, pick.ForehandColor, bh, pick.BackhandMm, pick.BackhandColor, pick.Handle);
    }

    void ShowCurrent()
    {
        Racket now = Equipment.For(holder);
        current.Text = $"{HolderLabel} plays with: " + (now == null ? "the standard racket (offensive 5+2 blade, tensor rubbers, about 180 g)"
            : $"{now.Name(fictional)}   ({now.Mass * 1000f:F0} g)");
    }

    // What the picked racket is and does.
    void ShowPick()
    {
        if (filling || details == null)
            return;
        Racket r = Picked();
        if (r == null)
        {
            details.Text = "Pick a blade and a rubber for each side.";
            return;
        }
        var t = new System.Text.StringBuilder();
        t.Append($"[b]{r.Name(fictional)}[/b]\n");
        if (fictional)
            t.Append($"[color=#9aa0aa]real: {r.Name(false)} (reference only)[/color]\n");
        t.Append($"\n[b]Weight[/b] {r.Mass * 1000f:F0} g  (blade {(r.Blade.WeightG is float bw ? $"{bw:F0} g" : "~85 g, not listed")}, rubbers {r.Forehand.Mass(r.ForehandMm) * 1000f:F0} + {r.Backhand.Mass(r.BackhandMm) * 1000f:F0} g cut)\n");
        string rule = r.ColourRuleBroken();
        if (rule != null)
            t.Append($"[color=#ffb060]Not legal in competition: {rule}.[/color]\n");

        t.Append("\n[b]In the game's contact physics[/b] (the standard racket in brackets)\n");
        t.Append(Performance("Forehand", r.Face(true), r.Forehand, r.ForehandMm));
        t.Append(Performance(r.Penhold ? "Backhand (penhold: forehand side)" : "Backhand", r.Face(r.Penhold), r.Penhold ? r.Forehand : r.Backhand, r.Penhold ? r.ForehandMm : r.BackhandMm));

        BladeSpec b = r.Blade;
        t.Append($"\n[b]Blade[/b] {b.Name(fictional)}\n");
        t.Append($"  {b.Country ?? "?"} · class {b.SpeedClass ?? "not listed"} · {b.Construction ?? $"{b.Plies} ply"}"
                 + (b.Composite != null ? $" with {b.Composite} ({b.CompositeLayer ?? "?"})" : " all wood")
                 + (b.OuterPly != null ? $" · outer ply {b.OuterPly}" : "")
                 + $" · {(b.ThicknessMm is float th ? $"{th:0.0#} mm" : "thickness not listed")} · handle {r.Handle}\n");
        t.Append(RubberLines("Forehand rubber", r.Forehand, r.ForehandMm, r.ForehandColor));
        t.Append(RubberLines("Backhand rubber", r.Backhand, r.BackhandMm, r.BackhandColor));
        details.Text = t.ToString();
    }

    string RubberLines(string title, RubberSpec x, float mm, string colour) =>
        $"\n[b]{title}[/b] {x.Name(fictional)}\n"
        + $"  {KindWords(x.Kind)} · sponge {(x.Hardness is float h ? $"{h:0.#}° {x.HardnessScale} (about {x.EsnHardness:0.#}° ESN)" : "hardness not listed")}"
        + $" · {(mm <= 0f ? "OX" : $"{mm:0.0#} mm")} · {colour}\n"
        + (x.Source != null ? $"  [url={x.Source}]source[/url]\n" : "");

    // A flat hit and a loop with this face, against the standard racket's.
    static string Performance(string side, RacketFace face, RubberSpec rubber, float mm)
    {
        var (flat, spin) = Probe(face);
        var (flat0, spin0) = Probe(RacketFace.Standard);
        var (block, block0) = (Blocked(face), Blocked(RacketFace.Standard));
        return $"  [b]{side}[/b]: rebound {face.Restitution:F2} ({Ball.RubberRestitution:F2}) · grip {face.Friction:F2} ({Ball.RubberFriction:F2}) · catapult {face.SpringBack:F2} ({Ball.RubberSpringBack:F2})\n"
            + $"    flat hit leaves {flat:F1} m/s ({flat0:F1}) · loop makes {spin:F0} rev/s ({spin0:F0})"
            + $" · blocking a 40 rev/s loop sends back {SpinName(block)} ({SpinName(block0)})\n";
    }

    static string SpinName(float topspin) => Mathf.Abs(topspin) < 0.5f ? "no spin" : $"{Mathf.Abs(topspin):F0} rev/s {(topspin > 0f ? "topspin" : "backspin")}";

    // Ball 8 m/s into a racket moving 10 m/s straight at it: how fast it leaves; and a loop, the
    // racket at 15 m/s 30 degrees to the face brushing up a 8 m/s ball: how much topspin.
    static (float Flat, float Spin) Probe(RacketFace face)
    {
        var hitter = new Striker(Human.DefaultBodyMass, face);
        Vector3 normal = Vector3.Back; // out of the rubber, towards the ball coming along -Z
        var ball = new BallState(Vector3.Zero, new Vector3(0f, 0f, -8f), Vector3.Zero);
        BallState flat = ball;
        Ball.HitByRacket(ref flat, normal, normal * 10f, Ball.RubberGrip, hitter);
        BallState loop = ball;
        float a = Mathf.DegToRad(30f);
        Ball.HitByRacket(ref loop, normal, (normal * Mathf.Sin(a) + Vector3.Up * Mathf.Cos(a)) * 15f, Ball.RubberGrip, hitter);
        return (flat.Velocity.Length(), loop.Spin.Length() / Mathf.Tau);
    }

    // A 10 m/s ball with 40 rev/s topspin blocked by a still, upright racket: the spin it leaves
    // with (rev/s; > 0 topspin, < 0 backspin, for its new way). A grippy face turns the spin
    // round with the ball; a slippery one (long pips) leaves it spinning as it was, which going
    // back the other way is backspin.
    static float Blocked(RacketFace face)
    {
        var hitter = new Striker(Human.DefaultBodyMass, face);
        float spin = 40f * Mathf.Tau;
        // Travelling -Z, topspin turns the top of the ball towards -Z: spin about -X.
        var ball = new BallState(Vector3.Zero, new Vector3(0f, 0f, -10f), new Vector3(-spin, 0f, 0f));
        Ball.HitByRacket(ref ball, Vector3.Back, Vector3.Zero, Ball.RubberGrip, hitter);
        return ball.Spin.X / Mathf.Tau; // travelling +Z, topspin is about +X
    }

    void Equip()
    {
        Racket r = Picked();
        if (r == null)
        {
            status.Text = "Pick a blade and both rubbers first.";
            return;
        }
        Equipment.Set(holder, new RacketSetup
        {
            Blade = pick.Blade, Forehand = pick.Forehand, ForehandMm = r.ForehandMm, ForehandColor = r.ForehandColor,
            Backhand = pick.Backhand, BackhandMm = r.BackhandMm, BackhandColor = r.BackhandColor, Handle = r.Handle,
        });
        status.Text = $"{HolderLabel}: equipped {r.Name(fictional)} ({r.Mass * 1000f:F0} g). The game uses it now.";
        ShowCurrent();
    }
}
