using Godot;
using System.Collections.Generic;
using System.Linq;

// The strategy map: a rally planned from above, as from the roof. Only the table's surface, its
// lines and the net are drawn; each player is a circle (A at the far end, B at the near end). The
// rally is a list of shots in turn, the serve first: for each, the method it is played with,
// where it lands (drag its numbered spot on the other half), its spin, how much and its pace. The
// players place themselves: each stands where it meets the ball coming to it (past its bounce, as
// far back as the method takes: a chop well back, a push close in), on its forehand or backhand
// side; the server where it is dragged along its end. Play shows the rally: the circles move, the
// ball flies each shot (bigger as it rises). "Play in the game" has the NPCs play it (StrategyRun).
//
// Opened with F4 in the game, or from the Pose Lab.
public partial class StrategyMap : Window
{
    static StrategyMap instance;
    Match match; // the game it was opened from, or null (the Pose Lab)

    public static void Open(SceneTree tree, Match from = null)
    {
        if (!IsInstanceValid(instance))
        {
            instance = new StrategyMap { Name = "StrategyMap" };
            tree.Root.CallDeferred(Node.MethodName.AddChild, instance);
        }
        instance.match = from;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        StrategyMap window = instance;
        Callable.From(() => window.PopupCenteredRatio(0.94f)).CallDeferred();
    }

    Strategy plan = Strategy.Example();
    int selected;
    MapView map;
    ItemList shotList;
    OptionButton serverPicker, methodPicker, variantPicker, spinPicker, amountPicker, pacePicker, loadPicker;
    LineEdit nameEdit;
    Label status;
    Button playButton;
    bool filling;

    static readonly Color A = new(1f, 0.6f, 0.2f), B = new(0.35f, 0.78f, 1f);
    static readonly Color Head = new(1f, 0.82f, 0.45f);

    public override void _Ready()
    {
        Title = "Strategy map: a rally planned from above";
        MinSize = new Vector2I(900, 560);
        Visible = false;
        Exclusive = false;
        Transient = true;
        ProcessMode = ProcessModeEnum.Always;
        CloseRequested += Hide;

        var split = new HBoxContainer { AnchorRight = 1f, AnchorBottom = 1f };
        split.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(split);
        map = new MapView { Owner_ = this, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        split.AddChild(map);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(380f, 0f) };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.08f, 0.09f, 0.11f), ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 8, ContentMarginBottom = 8 });
        split.AddChild(panel);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        panel.AddChild(scroll);
        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(box);

        box.AddChild(Heading("STRATEGY"));
        var nameRow = new HBoxContainer();
        nameEdit = new LineEdit { Text = plan.Name, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        nameRow.AddChild(nameEdit);
        nameRow.AddChild(Button("Save", Save));
        nameRow.AddChild(Button("New", () => { plan = Strategy.Example(); selected = 0; Refresh(); Say("a new plan to shape"); }));
        box.AddChild(nameRow);
        var loadRow = new HBoxContainer();
        loadPicker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true, CustomMinimumSize = new Vector2(120f, 0f) };
        loadRow.AddChild(loadPicker);
        loadRow.AddChild(Button("Load", Load));
        box.AddChild(loadRow);
        var serverRow = new HBoxContainer();
        var servesLabel = Small("Serves");
        servesLabel.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        servesLabel.CustomMinimumSize = new Vector2(80f, 0f);
        serverRow.AddChild(servesLabel);
        serverPicker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        serverPicker.AddItem("A: the far end (the CPU / yellow NPC)");
        serverPicker.AddItem("B: the near end (you / the near NPC)");
        serverPicker.ItemSelected += i => { if (!filling) { plan.Server = i == 0 ? "A" : "B"; KeepLandingsOnTheirHalf(); Refresh(); } };
        serverRow.AddChild(serverPicker);
        box.AddChild(serverRow);

        box.AddChild(Heading("SHOTS  (in turn, the serve first)"));
        shotList = new ItemList { CustomMinimumSize = new Vector2(0f, 150f), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        shotList.ItemSelected += i => { selected = (int)i; Refresh(); };
        box.AddChild(shotList);
        var shotButtons = new HFlowContainer();
        shotButtons.AddChild(Button("Add a shot", AddShot));
        shotButtons.AddChild(Button("Delete", DeleteShot));
        box.AddChild(shotButtons);

        box.AddChild(Heading("THE PICKED SHOT"));
        methodPicker = Picker(box, "Method", i => { plan.Shots[selected].Method = MethodsFor(selected)[i].Id; plan.Shots[selected].Action = null; Refresh(); });
        variantPicker = Picker(box, "Variant", i => { var v = VariantsFor(plan.Shots[selected].Method); plan.Shots[selected].Action = i == 0 ? null : v[i - 1].Id; Refresh(); });
        spinPicker = Picker(box, "Spin", i => { plan.Shots[selected].Spin = Strategy.Spins[i]; Refresh(); });
        foreach (string s in Strategy.Spins)
            spinPicker.AddItem(StrategyRun.SpinWords(s));
        amountPicker = Picker(box, "How much", i => { plan.Shots[selected].Amount = Strategy.Amounts[i]; Refresh(); });
        foreach (string s in Strategy.Amounts)
            amountPicker.AddItem(s);
        pacePicker = Picker(box, "Pace", i => { plan.Shots[selected].Pace = Strategy.Paces[i]; Refresh(); });
        foreach (string s in Strategy.Paces)
            pacePicker.AddItem(s);
        box.AddChild(Small("Drag a shot's numbered spot to where it lands (on the other half); drag the server along its end line."));

        box.AddChild(Heading("PLAY"));
        var playRow = new HFlowContainer();
        playButton = Button("▶ Play on the map", TogglePlay);
        playRow.AddChild(playButton);
        playRow.AddChild(Button("Play in the game: NPC vs NPC", () => PlayInGame(true)));
        playRow.AddChild(Button("Play it against me", () => PlayInGame(false)));
        playRow.AddChild(Button("Stop following", () => { StrategyRun.Active = null; Say("the NPCs play their own game again"); }));
        box.AddChild(playRow);
        status = Small("");
        status.AddThemeColorOverride("font_color", new Color(0.7f, 0.85f, 0.75f));
        box.AddChild(status);
        box.AddChild(Button("Close", Hide));

        AboutToPopup += () => { FillLoad(); Refresh(); };
        Refresh();
    }

    // ---------------------------------------------------------------- the panel

    static Label Heading(string text)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", 15);
        l.AddThemeColorOverride("font_color", Head);
        return l;
    }

    static Label Small(string text)
    {
        var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        l.AddThemeFontSizeOverride("font_size", 12);
        return l;
    }

    static Button Button(string text, System.Action action)
    {
        var b = new Button { Text = text };
        b.AddThemeFontSizeOverride("font_size", 12);
        b.Pressed += action;
        return b;
    }

    OptionButton Picker(VBoxContainer box, string label, System.Action<int> picked)
    {
        var row = new HBoxContainer();
        var name = Small(label);
        name.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        name.CustomMinimumSize = new Vector2(80f, 0f);
        row.AddChild(name);
        var picker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true, CustomMinimumSize = new Vector2(120f, 0f) };
        picker.ItemSelected += i => { if (!filling && selected < plan.Shots.Count) picked((int)i); };
        row.AddChild(picker);
        box.AddChild(row);
        return picker;
    }

    // The methods a shot can be: the serve only first, the rally's strokes after it.
    static List<MotionMethods.Method> MethodsFor(int shot) =>
        MotionMethods.All.Where(m => shot == 0 ? m.Id == "serve" : m.Id is not ("serve" or "ready")).ToList();

    // A method's variants: the Pose Lab's actions under it other than its default.
    static List<StrokeActions.Action> VariantsFor(string method) =>
        StrokeActions.All.Where(a => StrokeActions.MethodOf(a) == method && !StrokeActions.IsDefault(a)).ToList();

    void Say(string text) => status.Text = text;

    void FillLoad()
    {
        loadPicker.Clear();
        foreach (string n in Strategy.Saved())
            loadPicker.AddItem(n);
    }

    void Refresh()
    {
        filling = true;
        nameEdit.Text = plan.Name;
        serverPicker.Selected = plan.Server == "A" ? 0 : 1;
        selected = Mathf.Clamp(selected, 0, Mathf.Max(plan.Shots.Count - 1, 0));
        shotList.Clear();
        for (int i = 0; i < plan.Shots.Count; i++)
        {
            string hitter = plan.HitterOf(i);
            shotList.AddItem($"{i + 1}. {StrategyRun.Describe(plan.Shots[i], hitter)}");
            shotList.SetItemCustomFgColor(i, hitter == "A" ? A : B);
        }
        if (plan.Shots.Count > 0)
        {
            shotList.Select(selected);
            Strategy.Shot s = plan.Shots[selected];
            methodPicker.Clear();
            List<MotionMethods.Method> methods = MethodsFor(selected);
            foreach (MotionMethods.Method m in methods)
                methodPicker.AddItem(m.Name);
            methodPicker.Selected = Mathf.Max(methods.FindIndex(m => m.Id == s.Method), 0);
            variantPicker.Clear();
            variantPicker.AddItem($"{MotionMethods.Find(s.Method)?.Name ?? s.Method} (the default)");
            List<StrokeActions.Action> variants = VariantsFor(s.Method);
            foreach (StrokeActions.Action a in variants)
                variantPicker.AddItem(a.Name);
            variantPicker.Selected = s.Action == null ? 0 : Mathf.Max(variants.FindIndex(a => a.Id == s.Action) + 1, 0);
            spinPicker.Selected = System.Array.IndexOf(Strategy.Spins, s.Spin);
            amountPicker.Selected = System.Array.IndexOf(Strategy.Amounts, s.Amount);
            pacePicker.Selected = System.Array.IndexOf(Strategy.Paces, s.Pace);
        }
        filling = false;
        map.QueueRedraw();
    }

    void AddShot()
    {
        int i = plan.Shots.Count;
        string hitter = plan.HitterOf(i);
        // Back to where the shot before came from, crossing over: a start to shape.
        Strategy.Shot last = plan.Shots.LastOrDefault();
        float landZ = -Strategy.SideOf(hitter) * 0.9f;
        var shot = new Strategy.Shot
        {
            Method = i == 0 ? "serve" : "fh_drive",
            LandX = last != null ? -last.LandX * 0.8f : 0.3f,
            LandZ = landZ,
            Spin = i == 0 ? "back" : "top",
            Amount = "light",
            Pace = "medium",
        };
        plan.Shots.Add(shot);
        selected = i;
        Refresh();
        Say($"shot {i + 1} added for {hitter}: pick its method and spin, drag where it lands");
    }

    void DeleteShot()
    {
        if (plan.Shots.Count == 0)
            return;
        plan.Shots.RemoveAt(selected);
        if (plan.Shots.Count > 0 && plan.Shots[0].Method != "serve")
            plan.Shots[0].Method = "serve";
        KeepLandingsOnTheirHalf();
        Refresh();
    }

    // Each shot lands on the half across from its hitter (turns change when a shot goes).
    void KeepLandingsOnTheirHalf()
    {
        for (int i = 0; i < plan.Shots.Count; i++)
        {
            Strategy.Shot s = plan.Shots[i];
            int towards = -Strategy.SideOf(plan.HitterOf(i));
            s.LandZ = towards * Mathf.Clamp(Mathf.Abs(s.LandZ), 0.15f, TableSpec.HalfLength - 0.05f);
            s.LandX = Mathf.Clamp(s.LandX, -TableSpec.HalfWidth + 0.05f, TableSpec.HalfWidth - 0.05f);
        }
    }

    void Save()
    {
        string name = string.Concat(nameEdit.Text.Trim().Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'));
        if (name.Length == 0)
        {
            Say("give the strategy a name first");
            return;
        }
        plan.Name = name;
        plan.Save();
        FillLoad();
        Say($"saved to res://Strategies/{name}.json");
    }

    void Load()
    {
        if (loadPicker.Selected < 0)
            return;
        Strategy s = Strategy.Load(loadPicker.GetItemText(loadPicker.Selected));
        if (s == null)
            return;
        plan = s;
        selected = 0;
        playing = false;
        Refresh();
        Say($"loaded {s.Name}: {s.Shots.Count} shots");
    }

    // The NPCs play the plan in the game: both (NPC vs NPC), or the CPU its own shots against you.
    void PlayInGame(bool npcVsNpc)
    {
        if (plan.Shots.Count == 0)
        {
            Say("add the shots first");
            return;
        }
        if (!npcVsNpc && plan.Server == "B")
        {
            Say("against you the CPU serves: make A the server for this");
            return;
        }
        StrategyRun.Active = plan;
        Hide();
        if (IsInstanceValid(match))
            match.PlayStrategy(npcVsNpc);
        else
        {
            StrategyRun.StartNpcMatch = npcVsNpc;
            GetTree().ChangeSceneToFile("res://Main.tscn");
        }
    }

    // ---------------------------------------------------------------- where everyone is

    // A shot's contact (where the ball is met) and where its hitter stands then (the table's x, z).
    public record Place(Vector2 Contact, Vector2 Body);

    // Each shot's place: the serve from the server's spot behind its end; any other shot met past
    // the bounce of the ball coming to it, the further back the more the method takes it late
    // (a push or flick at the top of the bounce, a drive as it drops, a chop well back), the body
    // behind and to the side of the ball (to its left for a forehand, as the player sees it).
    public static List<Place> Places(Strategy plan)
    {
        var places = new List<Place>();
        for (int i = 0; i < plan.Shots.Count; i++)
        {
            Strategy.Shot s = plan.Shots[i];
            string hitter = plan.HitterOf(i);
            int side = Strategy.SideOf(hitter);
            float left = hitter == "A" ? 1f : -1f; // the player's left, across the table
            Vector2 contact;
            if (i == 0)
                contact = new Vector2(plan.ServerX, side * (TableSpec.HalfLength + 0.12f));
            else
            {
                Strategy.Shot comes = plan.Shots[i - 1];
                float past = s.Method switch
                {
                    "fh_push" or "bh_push" or "fh_flick" or "bh_flick" => 0.25f,
                    "fh_block" or "bh_block" => 0.4f,
                    "fh_chop" or "bh_chop" => 1.7f,
                    "smash" => 0.55f,
                    _ => 0.7f,
                } + (comes.Pace == "fast" ? 0.2f : comes.Pace == "slow" ? -0.1f : 0f);
                float z = comes.LandZ + side * past;
                contact = new Vector2(comes.LandX * 1.15f, z);
            }
            bool forehand = Strategy.Forehand(s.Method);
            Vector2 body = contact + new Vector2(left * (forehand ? 0.38f : -0.22f), side * 0.3f);
            places.Add(new Place(contact, body));
        }
        return places;
    }

    // Where each player waits before its first shot: behind its end, near the middle.
    static Vector2 ReadyOf(string player) => new(player == "A" ? 0.1f : -0.1f, Strategy.SideOf(player) * (TableSpec.HalfLength + 0.55f));

    // ---------------------------------------------------------------- playing it on the map

    bool playing;
    float clock;

    static float FlightOf(Strategy.Shot s, bool serve) =>
        (s.Pace switch { "slow" => 1.0f, "fast" => 0.55f, _ => 0.75f }) + (serve ? 0.3f : 0f);

    float Length => plan.Shots.Select((s, i) => FlightOf(s, i == 0)).Sum() + 0.6f;

    void TogglePlay()
    {
        playing = !playing;
        if (playing && clock >= Length)
            clock = 0f;
        playButton.Text = playing ? "■ Stop" : "▶ Play on the map";
    }

    public override void _Process(double delta)
    {
        if (!Visible || !playing)
            return;
        clock += (float)delta;
        if (clock >= Length)
        {
            clock = Length;
            TogglePlay();
        }
        map.QueueRedraw();
    }

    // At time t of the rally: the ball (x, z, height 0..1) and each player's place, and the shot.
    (Vector3 Ball, Vector2 PlayerA, Vector2 PlayerB, int Shot) At(float t)
    {
        List<Place> places = Places(plan);
        float start = 0f;
        Vector3 ball = Vector3.Zero;
        int shot = plan.Shots.Count - 1;
        for (int i = 0; i < plan.Shots.Count; i++)
        {
            float d = FlightOf(plan.Shots[i], i == 0);
            if (t <= start + d || i == plan.Shots.Count - 1)
            {
                shot = i;
                float u = Mathf.Clamp((t - start) / d, 0f, 1f);
                Strategy.Shot s = plan.Shots[i];
                Vector2 from = places[i].Contact, land = new(s.LandX, s.LandZ);
                Vector2 to = i + 1 < places.Count ? places[i + 1].Contact : land + (land - from).Normalized() * 0.8f;
                // The serve bounces first on its own half.
                var legs = new List<Vector2> { from };
                if (i == 0)
                    legs.Add(new Vector2((from.X + land.X) / 2f, Strategy.SideOf(plan.HitterOf(0)) * 0.6f));
                legs.Add(land);
                legs.Add(to);
                float total = 0f;
                for (int k = 1; k < legs.Count; k++)
                    total += legs[k].DistanceTo(legs[k - 1]);
                float along = u * total;
                for (int k = 1; k < legs.Count; k++)
                {
                    float len = legs[k].DistanceTo(legs[k - 1]);
                    if (along <= len || k == legs.Count - 1)
                    {
                        float f = len > 1e-4f ? Mathf.Clamp(along / len, 0f, 1f) : 1f;
                        Vector2 p = legs[k - 1].Lerp(legs[k], f);
                        float high = (s.Pace == "slow" ? 0.6f : s.Pace == "fast" ? 0.3f : 0.45f) * (k == legs.Count - 1 ? 0.6f : 1f);
                        ball = new Vector3(p.X, p.Y, high * 4f * f * (1f - f));
                        break;
                    }
                    along -= len;
                }
                break;
            }
            start += d;
        }
        // The players: from one of their shots to the next (from where they wait before the first).
        Vector2 PlayerAt(string who)
        {
            var keys = new List<(float T, Vector2 P)> { (0f, plan.Server == who ? (places.Count > 0 ? places[0].Body : ReadyOf(who)) : ReadyOf(who)) };
            float time = 0f;
            for (int i = 0; i < places.Count; i++)
            {
                if (plan.HitterOf(i) == who)
                    keys.Add((time, places[i].Body));
                time += FlightOf(plan.Shots[i], i == 0);
            }
            keys.Add((time + 0.6f, ReadyOf(who)));
            for (int k = 1; k < keys.Count; k++)
                if (t <= keys[k].T)
                    return keys[k - 1].P.Lerp(keys[k].P, Mathf.Clamp((t - keys[k - 1].T) / Mathf.Max(keys[k].T - keys[k - 1].T, 1e-3f), 0f, 1f));
            return keys[^1].P;
        }
        return (ball, PlayerAt("A"), PlayerAt("B"), shot);
    }

    // ---------------------------------------------------------------- the map

    partial class MapView : Control
    {
        public StrategyMap Owner_;
        int dragging = -2; // a shot's landing being dragged, -1 the server, -2 none

        // The table's x, z to the map and back: z down the screen (A at the top), fitted to the view.
        float Scale => Mathf.Min(Size.X / 3.4f, Size.Y / 6.2f);
        Vector2 Centre => Size / 2f;
        Vector2 ToMap(Vector2 xz) => Centre + xz * Scale;
        Vector2 ToTable(Vector2 p) => (p - Centre) / Scale;

        public override void _Draw()
        {
            Strategy plan = Owner_.plan;
            DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.13f, 0.12f, 0.12f));
            // The table: its surface, its white lines and the net across the middle.
            float hw = TableSpec.HalfWidth, hl = TableSpec.HalfLength;
            var top = new Rect2(ToMap(new Vector2(-hw, -hl)), new Vector2(2f * hw, 2f * hl) * Scale);
            DrawRect(top, new Color(0.11f, 0.32f, 0.62f));
            DrawRect(top, Colors.White, false, 3f);
            DrawLine(ToMap(new Vector2(0f, -hl)), ToMap(new Vector2(0f, hl)), new Color(1f, 1f, 1f, 0.7f), 1.5f);
            DrawLine(ToMap(new Vector2(-TableSpec.NetHalfWidth, 0f)), ToMap(new Vector2(TableSpec.NetHalfWidth, 0f)), new Color(0.92f, 0.92f, 0.92f), 5f);
            foreach (float x in new[] { -TableSpec.NetHalfWidth, TableSpec.NetHalfWidth })
                DrawCircle(ToMap(new Vector2(x, 0f)), 5f, new Color(0.3f, 0.3f, 0.3f));
            var font = ThemeDB.FallbackFont;
            // Each end named at the left edge, clear of the players.
            DrawMultilineString(font, new Vector2(12f, ToMap(new Vector2(0f, -hl)).Y + 5f), "A: far end\n(the CPU / yellow NPC)", HorizontalAlignment.Left, -1, 14, -1, A);
            DrawMultilineString(font, new Vector2(12f, ToMap(new Vector2(0f, hl)).Y + 5f), "B: near end\n(you / the near NPC)", HorizontalAlignment.Left, -1, 14, -1, B);

            // Each shot: from where it is met to where it lands, numbered at its landing.
            List<Place> places = Places(plan);
            for (int i = 0; i < plan.Shots.Count; i++)
            {
                Strategy.Shot s = plan.Shots[i];
                Color c = plan.HitterOf(i) == "A" ? A : B;
                bool picked = i == Owner_.selected;
                Color line = picked ? c : new Color(c, 0.45f);
                Vector2 from = ToMap(places[i].Contact), land = ToMap(new Vector2(s.LandX, s.LandZ));
                if (i == 0)
                {
                    Vector2 bounce = ToMap(new Vector2((places[0].Contact.X + s.LandX) / 2f, Strategy.SideOf(plan.HitterOf(0)) * 0.6f));
                    DrawDashedLine(from, bounce, line, picked ? 3f : 2f, 8f);
                    DrawDashedLine(bounce, land, line, picked ? 3f : 2f, 8f);
                    DrawCircle(bounce, 4f, line);
                }
                else
                    DrawLine(from, land, line, picked ? 3.5f : 2f);
                Vector2 way = (land - from).Normalized();
                Vector2 side = new(-way.Y, way.X);
                DrawColoredPolygon(new[] { land, land - way * 14f + side * 7f, land - way * 14f - side * 7f }, line);
                DrawCircle(land, picked ? 13f : 10f, picked ? Colors.White : new Color(0.9f, 0.9f, 0.9f, 0.8f));
                DrawString(font, land + new Vector2(-5f, 5f), (i + 1).ToString(), HorizontalAlignment.Left, -1, 13, Colors.Black);
            }

            // The players: still at their places for the picked shot, or moving as it plays.
            if (Owner_.playing || Owner_.clock > 0f)
            {
                var (ball, pa, pb, _) = Owner_.At(Owner_.clock);
                Player(pa, "A", A);
                Player(pb, "B", B);
                DrawCircle(ToMap(new Vector2(ball.X, ball.Y)), 5f + 9f * ball.Z, Colors.White);
                DrawArc(ToMap(new Vector2(ball.X, ball.Y)), 5f + 9f * ball.Z, 0f, Mathf.Tau, 16, Colors.Black, 1.5f);
            }
            else if (plan.Shots.Count > 0)
            {
                int i = Owner_.selected;
                string hitter = plan.HitterOf(i);
                Player(places[i].Body, hitter, hitter == "A" ? A : B);
                // The other: where it met the shot before (or waits for the serve).
                string other = Strategy.Other(hitter);
                Vector2 otherAt = i > 0 ? places[i - 1].Body : ReadyOf(other);
                Player(otherAt, other, other == "A" ? A : B);
                DrawCircle(ToMap(places[i].Contact), 6f, Colors.White);
            }
            DrawString(font, new Vector2(12f, Size.Y - 14f), "Drag a numbered spot: where that shot lands.  Drag the server (shot 1) along its end line.", HorizontalAlignment.Left, -1, 13, new Color(1f, 1f, 1f, 0.7f));
        }

        void Player(Vector2 xz, string name, Color c)
        {
            Vector2 p = ToMap(xz);
            float r = 0.22f * Scale;
            DrawCircle(p, r, new Color(c, 0.85f));
            DrawArc(p, r, 0f, Mathf.Tau, 32, Colors.White, 2f);
            DrawString(ThemeDB.FallbackFont, p + new Vector2(-6f, 7f), name, HorizontalAlignment.Left, -1, 20, Colors.Black);
        }

        public override void _GuiInput(InputEvent e)
        {
            Strategy plan = Owner_.plan;
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } b)
            {
                if (b.Pressed)
                {
                    dragging = -2;
                    for (int i = 0; i < plan.Shots.Count; i++)
                        if (ToMap(new Vector2(plan.Shots[i].LandX, plan.Shots[i].LandZ)).DistanceTo(b.Position) < 16f)
                        {
                            dragging = i;
                            Owner_.selected = i;
                        }
                    if (dragging == -2 && plan.Shots.Count > 0 && ToMap(Places(plan)[0].Body).DistanceTo(b.Position) < 0.22f * Scale)
                        dragging = -1;
                    Owner_.Refresh();
                }
                else
                    dragging = -2;
                AcceptEvent();
            }
            else if (e is InputEventMouseMotion m && dragging > -2)
            {
                Vector2 at = ToTable(m.Position);
                if (dragging == -1)
                    plan.ServerX = Mathf.Clamp(at.X - (plan.Server == "A" ? 0.38f : -0.38f), -1.2f, 1.2f);
                else
                {
                    Strategy.Shot s = plan.Shots[dragging];
                    int towards = -Strategy.SideOf(plan.HitterOf(dragging));
                    s.LandX = Mathf.Clamp(at.X, -TableSpec.HalfWidth + 0.03f, TableSpec.HalfWidth - 0.03f);
                    s.LandZ = towards * Mathf.Clamp(towards * at.Y, 0.1f, TableSpec.HalfLength - 0.03f);
                }
                QueueRedraw();
                AcceptEvent();
            }
        }
    }
}
