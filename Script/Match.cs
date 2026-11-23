using Godot;

// Rules and flow of a game to 11: judging every bounce, scoring and the HUD. When an NPC serves
// the camera closes in on it, the serve plays in slow motion from the contact point with its
// flight path drawn ahead of the ball, and once the ball crosses the net the game returns to
// normal speed and the playing view.
//
// Against the player the NPC serves every point. In NPC vs NPC mode (N key, or the
// --npc-vs-npc user argument) a second NPC takes the player's end: the serve changes every two
// points (every point from 10:10), the camera watches from the side, and a finished game is
// followed by the next one. M there cycles a slow motion to watch the strokes (SlowModes).
public partial class Match : Node3D
{
    enum Phase
    {
        Serving,   // the server holds the ball
        Toss,      // ball in the air before the serve
        Rally,
        PointOver,
        GameOver
    }

    const int PointsToWin = 11;
    const float ServeDelay = 1.5f;        // time for the camera to settle on the server
    const float PointPause = 1.8f;
    const float NextGamePause = 4f;       // NPC vs NPC: before the next game starts
    const float RallyTimeout = 6f;        // a ball nobody can play any more
    const double ServeTimeScale = 0.25;
    const double TimeScaleRecovery = 3.0; // per real second
    const double TimeScaleDrop = 6.0;     // per real second, slowing down

    // NPC vs NPC slow motion (M cycles): the whole rally at one speed, or only round each stroke
    // (from when the ball crosses the net to the receiver until just after the hit), the rest at
    // normal speed. With it on, each stroke is captioned: who, attack / defence / control, what.
    static readonly (string Name, double Scale, bool AroundStrokes)[] SlowModes =
    {
        ("off", 1.0, false),
        ("x0.5", 0.5, false),
        ("x0.25", 0.25, false),
        ("x0.1", 0.1, false),
        ("x0.2 round each stroke", 0.2, true),
    };
    const float AfterStroke = 0.3f;       // game seconds the stroke slow motion lasts past the hit
    int slowMode;

    Ball ball;
    Blade blade;
    Opponent opponent;      // far end
    Opponent nearOpponent;  // near end, playing instead of the player in NPC vs NPC mode
    CameraRig cameraRig;
    TrajectoryGuide guide;
    Label scoreLabel;
    Label messageLabel;
    Label hintLabel;
    Label tiredLabel;
    string playerHint;

    Phase phase;
    bool npcMatch;
    int games;        // games started, for alternating who serves first
    Opponent server;
    int playerScore;  // the near end's points: the player's or the near NPC's
    int opponentScore;
    int lastHitter;
    int bounces;      // table bounces since the last strike
    bool isServe;
    bool touchedNet;
    bool serveReplay; // slow motion running
    float timer;

    public override void _Ready()
    {
        ball = GetNode<Ball>("Ball");
        blade = GetNode<Blade>("Blade");
        opponent = GetNode<Opponent>("Opponent");
        nearOpponent = GetNode<Opponent>("OpponentNear");
        cameraRig = GetNode<CameraRig>("Camera");
        guide = GetNode<TrajectoryGuide>("TrajectoryGuide");
        scoreLabel = GetNode<Label>("HUD/Score");
        messageLabel = GetNode<Label>("HUD/Message");
        hintLabel = GetNode<Label>("HUD/Hint");
        playerHint = hintLabel.Text;
        // How tired the NPCs are, small under the score.
        tiredLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        tiredLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
        tiredLabel.OffsetTop = 64f;
        tiredLabel.AddThemeFontSizeOverride("font_size", 16);
        tiredLabel.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.8f));
        tiredLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        tiredLabel.AddThemeConstantOverride("outline_size", 4);
        GetNode("HUD").AddChild(tiredLabel);

        ball.Contact += OnContact;
        ball.Struck += OnStruck;

        Input.MouseMode = Input.MouseModeEnum.Hidden;
        // NPC vs NPC: asked on the command line, or to play a strategy from the Pose Lab's map.
        SetMode(System.Array.Exists(OS.GetCmdlineUserArgs(), arg => arg == "--npc-vs-npc") || StrategyRun.StartNpcMatch);
        StrategyRun.StartNpcMatch = false;
        NewGame();

        // F2: freeze the match and pose the NPC (Pose Lab).
        var poseLab = new PoseLab { Name = "PoseLab", Targets = new[] { opponent, nearOpponent } };
        AddChild(poseLab);
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--pose-test="))
                poseLab.InGameTest(arg["--pose-test=".Length..]);
            // Every saved motion and template checked for joints past their range, wrong faces,
            // lost balance (res://Review/pose_check.log); =fix also puts the saved motions right.
            if (arg == "--pose-check" || arg == "--pose-check=fix")
                poseLab.CheckAll(arg.EndsWith("=fix"));
        }
    }

    public override void _ExitTree()
    {
        Engine.TimeScale = 1.0;
    }

    public override void _Process(double delta)
    {
        // Ease to the speed wanted (normal, or the slow motion's) after the slow-motion serve,
        // measured in real time.
        if (serveReplay)
            return;
        double target = WantedTimeScale(), real = delta / Engine.TimeScale;
        Engine.TimeScale = Engine.TimeScale < target
            ? Mathf.Min(target, Engine.TimeScale + real * TimeScaleRecovery)
            : Mathf.Max(target, Engine.TimeScale - real * TimeScaleDrop);
    }


    double WantedTimeScale()
    {
        if (!npcMatch || slowMode == 0)
            return 1.0;
        var mode = SlowModes[slowMode];
        if (!mode.AroundStrokes)
            return mode.Scale;
        // Round a stroke: the ball over the receiver's half (on its way to be hit), or just hit.
        bool coming = phase == Phase.Rally && ball.State.Position.Z * lastHitter < 0f;
        bool justHit = phase == Phase.Rally && timer < AfterStroke;
        return coming || justHit ? mode.Scale : 1.0;
    }

    void CycleSlowMotion()
    {
        slowMode = (slowMode + 1) % SlowModes.Length;
        DebugOverlay.Log($"slow motion: {SlowModes[slowMode].Name}");
        UpdateHint();
        messageLabel.Text = $"Slow motion: {SlowModes[slowMode].Name}";
    }

    // Who played what, in slow motion: e.g. "Blue: DEFENCE - backhand block".
    void Caption(int side, string wing)
    {
        if (!npcMatch || slowMode == 0)
            return;
        Opponent hitter = side == -1 ? opponent : nearOpponent;
        string role = hitter.LastRole switch
        {
            Opponent.StrokeRole.Attack => "ATTACK",
            Opponent.StrokeRole.Defence => "DEFENCE",
            Opponent.StrokeRole.Control => "CONTROL",
            _ => "SERVE",
        };
        messageLabel.Text = $"{Capital(Who(side))}: {role} - {wing}{hitter.LastStroke}";
        DebugOverlay.Log($"  caption: {messageLabel.Text}");
    }

    string Who(int side) => side == 0 ? "-" : npcMatch
        ? (side == 1 ? nearOpponent.Tag : opponent.Tag)
        : (side == 1 ? "you" : "cpu");

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        switch (phase)
        {
            case Phase.Serving:
                ball.Hold(server.ServePoint);
                if ((timer -= dt) <= 0f)
                {
                    phase = Phase.Toss;
                    server.BeginServe(ball);
                }
                break;
            case Phase.Rally:
                if (serveReplay && ball.State.Position.Z * server.Side < 0f)
                    EndServeReplay();
                if ((timer += dt) > RallyTimeout)
                    Fault(Culprit(), "Dead ball");
                break;
            case Phase.PointOver:
                if ((timer -= dt) <= 0f)
                    StartServe();
                break;
            case Phase.GameOver:
                if (npcMatch && (timer -= dt) <= 0f)
                    NewGame();
                break;
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && phase == Phase.GameOver)
        {
            NewGame();
        }
        else if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.N })
        {
            SetMode(!npcMatch);
            NewGame();
        }
        else if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.M } && npcMatch)
        {
            CycleSlowMotion();
        }
        else if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F3 })
        {
            RacketLocker.Open(GetTree()); // the racket collection
        }
        else if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F4 })
        {
            StrategyMap.Open(GetTree(), this); // rallies planned from above
        }
        else if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Hidden
                ? Input.MouseModeEnum.Visible
                : Input.MouseModeEnum.Hidden;
        }
    }

    // A strategy from the map played from a new game: by both NPCs, or by the CPU against you.
    public void PlayStrategy(bool npcVsNpc)
    {
        SetMode(npcVsNpc);
        NewGame();
        DebugOverlay.Log($"=== strategy {StrategyRun.Active?.Name}: {StrategyRun.Active?.Shots.Count} shots ===");
    }

    // Puts the player's blade or the near NPC at the near end.
    void SetMode(bool npcVsNpc)
    {
        npcMatch = npcVsNpc;
        blade.Visible = !npcMatch;
        blade.ProcessMode = npcMatch ? ProcessModeEnum.Disabled : ProcessModeEnum.Inherit;
        nearOpponent.Visible = npcMatch;
        nearOpponent.ProcessMode = npcMatch ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        opponent.Tag = npcMatch ? "yellow" : "cpu";

        ball.Hitters.Clear();
        ball.Hitters.Add(npcMatch ? nearOpponent : blade);
        ball.Hitters.Add(opponent);

        if (!npcMatch)
            slowMode = 0; // slow motion is for watching the NPCs
        UpdateHint();
        DebugOverlay.Log(npcMatch ? "=== NPC vs NPC ===" : "=== you vs cpu ===");
        // A new match: everyone fresh.
        opponent.Freshen();
        nearOpponent.Freshen();
    }

    void UpdateHint()
    {
        string plan = StrategyRun.Active != null ? $"Strategy: {StrategyRun.Active.Name}  |  " : "";
        hintLabel.Text = plan + (npcMatch
            ? $"NPC vs NPC  |  M: slow motion ({SlowModes[slowMode].Name})  |  N: play against the CPU yourself  |  F2: freeze and pose  |  F3: rackets  |  Ball glow: orange = topspin, blue = backspin"
            : playerHint + "  |  N: NPC vs NPC  |  F2: freeze and pose  |  F3: rackets") + "  |  F4: strategy map";
    }

    void NewGame()
    {
        playerScore = 0;
        opponentScore = 0;
        games++;
        // The break between games (the first of a match starts fresh anyway).
        opponent.Rest(Fatigue.GameBreak);
        nearOpponent.Rest(Fatigue.GameBreak);
        StartServe();
    }

    // Against the player the NPC always serves. NPC vs NPC: two serves each in turn, one each
    // from 10:10, and the first server alternates from game to game.
    Opponent NextServer()
    {
        // A strategy being played: its server serves every point (A the far end).
        if (StrategyRun.Active is { } plan)
            return plan.Server == "B" && npcMatch ? nearOpponent : opponent;
        if (!npcMatch)
            return opponent;
        int played = playerScore + opponentScore;
        int turn = played < 2 * (PointsToWin - 1) ? played / 2 : played - (PointsToWin - 1);
        return (turn + games) % 2 == 0 ? nearOpponent : opponent;
    }

    void StartServe()
    {
        if (serveReplay)
            EndServeReplay();
        phase = Phase.Serving;
        timer = ServeDelay;
        server = NextServer();
        StrategyRun.Struck = 0; // a strategy starts again with each serve
        UpdateHint();
        opponent.HasServe = server == opponent;
        nearOpponent.HasServe = server == nearOpponent;
        opponent.Reset();
        nearOpponent.Reset();
        if (playerScore + opponentScore > 0) // the break between points
        {
            opponent.Rest(Fatigue.PointBreak);
            if (npcMatch)
                nearOpponent.Rest(Fatigue.PointBreak);
        }
        ball.Hold(server.ServePoint);
        guide.Clear();
        cameraRig.Server = server;
        cameraRig.Mode = CameraRig.Shot.ServeFocus;
        messageLabel.Text = npcMatch ? $"{Capital(Who(server.Side))} serves" : "Opponent serves";
        UpdateScore();
        DebugOverlay.Log($"--- point {playerScore + opponentScore + 1}, {playerScore}:{opponentScore}: {Who(server.Side)} serves ---");
    }

    void OnStruck(int side)
    {
        isServe = phase == Phase.Toss;
        phase = Phase.Rally;
        lastHitter = side;
        StrategyRun.Struck++; // the next shot of the strategy is the next one
        bounces = 0;
        touchedNet = false;
        timer = 0f;

        Paddle paddle = side == -1 ? opponent.Paddle : npcMatch ? nearOpponent.Paddle : blade.Paddle;
        string stroke = isServe ? "serve" : paddle.Forehand ? "forehand" : "backhand";
        BallState s = ball.State;
        DebugOverlay.Log($"{Who(side)} {stroke} from ({s.Position.X:F2}, {s.Position.Y:F2}, {s.Position.Z:F2})  " +
            $"{s.Velocity.Length():F1} m/s  {DebugOverlay.DescribeSpin(s)}");
        DebugOverlay.Capture($"{Who(side)}_{stroke}");

        if (isServe)
            StartServeReplay();
        Caption(side, isServe ? "" : stroke + " ");
    }

    void StartServeReplay()
    {
        serveReplay = true;
        Engine.TimeScale = Mathf.Min(ServeTimeScale, WantedTimeScale()); // slower still if the slow motion is slower
        cameraRig.Mode = CameraRig.Shot.FollowBall;
        guide.Plot(ball.State, 2);
        messageLabel.Text = "";
        DebugOverlay.Log($"slow motion x{ServeTimeScale:F2}");
    }

    void EndServeReplay()
    {
        serveReplay = false;
        cameraRig.Mode = npcMatch ? CameraRig.Shot.Broadcast : CameraRig.Shot.Player;
        DebugOverlay.Log("ball over the net: back to normal speed");
    }

    void OnContact(BallEvent evt, Vector3 at)
    {
        if (phase != Phase.Rally)
            return;

        int receiver = -lastHitter;
        switch (evt)
        {
            case BallEvent.Net:
                touchedNet = true;
                DebugOverlay.Log($"  net at x {at.X:F2}");
                break;

            case BallEvent.Table:
                int side = TableSpec.SideOf(at.Z);
                int legalBounces = isServe ? 2 : 1;
                bounces++;
                DebugOverlay.Log($"  bounce {bounces} on {Who(side)}'s side at ({at.X:F2}, {at.Z:F2})");
                if (bounces > legalBounces)
                    Fault(receiver, "Missed");
                else if (bounces < legalBounces && side != lastHitter)
                    Fault(lastHitter, "Serve must bounce on the server's side first");
                else if (bounces == legalBounces && side != receiver)
                    Fault(lastHitter, touchedNet ? "Net" : "Wrong side");
                else if (bounces == legalBounces)
                    ball.HittableBy = receiver;
                break;

            case BallEvent.Floor:
                int loser = Culprit();
                Fault(loser, loser == receiver ? "Missed" : touchedNet ? "Net" : "Out");
                break;
        }
    }

    // Who loses if the ball dies now: the receiver once it has legally reached them.
    int Culprit() => ball.HittableBy == -lastHitter ? -lastHitter : lastHitter;

    static string Capital(string name) => char.ToUpper(name[0]) + name[1..];

    void Fault(int loser, string reason)
    {
        if (serveReplay)
            EndServeReplay();
        ball.HittableBy = 0;
        ball.Dead = true; // the players stop playing it
        if (loser == 1)
            opponentScore++;
        else
            playerScore++;
        UpdateScore();

        bool gameOver = Mathf.Max(playerScore, opponentScore) >= PointsToWin
            && Mathf.Abs(playerScore - opponentScore) >= 2;
        DebugOverlay.Log($"POINT to {Who(-loser)}: {reason}   {playerScore}:{opponentScore}");
        if (gameOver)
        {
            int winner = playerScore > opponentScore ? 1 : -1;
            phase = Phase.GameOver;
            timer = NextGamePause;
            messageLabel.Text = npcMatch
                ? $"{Capital(Who(winner))} wins {Mathf.Max(playerScore, opponentScore)}:{Mathf.Min(playerScore, opponentScore)}!\nNext game soon"
                : (winner == 1 ? "You win!" : "You lose!") + "\nClick to play again";
            DebugOverlay.Log($"GAME OVER: {Who(winner)} wins {playerScore}:{opponentScore}");
            DebugOverlay.Capture("game_over");
            return;
        }

        phase = Phase.PointOver;
        timer = PointPause;
        messageLabel.Text = npcMatch
            ? $"{reason}\nPoint to {Who(-loser)}"
            : $"{reason}\nPoint to {(loser == 1 ? "opponent" : "you")}";
        DebugOverlay.Capture($"point {Who(-loser)} {reason}");
    }

    void UpdateScore()
    {
        scoreLabel.Text = npcMatch
            ? $"{Who(1).ToUpper()}  {playerScore} : {opponentScore}  {Who(-1).ToUpper()}"
            : $"YOU  {playerScore} : {opponentScore}  CPU";
        static string Tired(Opponent npc) => $"tired {npc.Fatigue.Level * 100f:F0}%";
        tiredLabel.Text = npcMatch
            ? $"{Tired(nearOpponent)}          {Tired(opponent)}"
            : $"CPU {Tired(opponent)}";
    }
}
