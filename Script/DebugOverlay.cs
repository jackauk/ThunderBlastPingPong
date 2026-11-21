using Godot;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

// Debug tools, active only in debug builds (running from the editor or Visual Studio): a live
// panel with the ball's state, a log of game events written to Godot's output and log.txt,
// and review screenshots. Key moments are captured automatically; F12 saves a screenshot,
// F1 shows or hides the panel.
//
// Every run gets its own folder, Review/<date_time>/, with numbered PNGs and log.txt. When run
// from the editor it is inside the project (with a .gdignore so Godot does not import the
// images); in an exported debug build it is in the user data folder. Running with the user
// argument --no-review (after "--" on the command line) writes nothing.
public partial class DebugOverlay : CanvasLayer
{
    [Export] public Ball Ball;
    [Export] public bool AutoCapture = true;

    const int FontSize = 14;
    static readonly Vector2 PanelPosition = new(12f, 12f);
    static readonly Vector2 PanelSize = new(440f, 108f);

    static DebugOverlay instance;

    public static bool Enabled => instance != null && instance.enabled;

    bool enabled;
    Control panel;
    Label ballInfo;
    FileAccess logFile;
    string sessionFolder;
    int captureCount;

    public override void _EnterTree()
    {
        instance = this;
    }

    public override void _ExitTree()
    {
        if (instance == this)
            instance = null;
        logFile?.Close();
    }

    public override void _Ready()
    {
        enabled = OS.IsDebugBuild();
        Visible = enabled;
        if (!enabled)
            return;
        BuildPanel();
        if (!System.Array.Exists(OS.GetCmdlineUserArgs(), arg => arg == "--no-review"))
            OpenSession();
    }

    // Writes to Godot's output and, in debug builds, to the session's log.txt.
    public static void Log(string message)
    {
        GD.Print(message);
        if (Enabled)
            instance.Append(message);
    }

    // Saves a review screenshot of the next rendered frame, named after the moment.
    public static void Capture(string tag)
    {
        if (Enabled && instance.AutoCapture)
            instance.SaveScreenshot(tag);
    }

    // Spin relative to the ball's flight: topspin or backspin, and sidespin.
    public static string DescribeSpin(BallState s)
    {
        float top = s.Spin.X * Mathf.Sign(s.Velocity.Z);
        return $"spin {(top >= 0f ? "top" : "back")} {Mathf.Abs(top):F0}  side {s.Spin.Y:F0} rad/s";
    }

    public override void _Process(double delta)
    {
        if (!enabled || Ball == null)
            return;

        BallState s = Ball.State;
        string state = !Ball.Simulating ? "held" : Ball.HittableBy == 1 ? "yours to hit" : Ball.HittableBy == -1 ? "cpu to hit" : "in flight";
        float rpm = s.Spin.Length() * 60f / Mathf.Tau;
        ballInfo.Text =
            $"BALL  {state}\n" +
            $"pos  ({s.Position.X:F2}, {s.Position.Y:F2}, {s.Position.Z:F2})   {s.Position.Y - TableSpec.Height - Ball.Radius:F2} m above table\n" +
            $"vel  {s.Velocity.Length():F1} m/s  ({s.Velocity.X:F1}, {s.Velocity.Y:F1}, {s.Velocity.Z:F1})\n" +
            $"{DescribeSpin(s)}   ({rpm:F0} rpm)";
    }

    public override void _Input(InputEvent e)
    {
        if (!enabled || e is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        if (key.Keycode == Key.F1)
            panel.Visible = !panel.Visible;
        else if (key.Keycode == Key.F12)
            SaveScreenshot("manual");
    }

    void BuildPanel()
    {
        panel = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0.5f),
            Position = PanelPosition,
            Size = PanelSize,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        AddChild(panel);

        ballInfo = new Label
        {
            Position = new Vector2(8f, 6f),
            Size = PanelSize - new Vector2(16f, 12f),
            ClipText = true,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        ballInfo.AddThemeFontSizeOverride("font_size", FontSize);
        ballInfo.AddThemeColorOverride("font_color", new Color(0.6f, 1f, 0.6f));
        ballInfo.AddThemeColorOverride("font_outline_color", Colors.Black);
        ballInfo.AddThemeConstantOverride("outline_size", 3);
        panel.AddChild(ballInfo);
    }

    void OpenSession()
    {
        string root = ProjectSettings.GlobalizePath(OS.HasFeature("editor") ? "res://Review" : "user://Review");
        string stamp = Time.GetDatetimeStringFromSystem().Replace(":", "-").Replace("T", "_");
        sessionFolder = root.PathJoin(stamp);
        DirAccess.MakeDirRecursiveAbsolute(sessionFolder);

        string ignore = root.PathJoin(".gdignore");
        if (!FileAccess.FileExists(ignore))
            FileAccess.Open(ignore, FileAccess.ModeFlags.Write)?.Close();

        logFile = FileAccess.Open(sessionFolder.PathJoin("log.txt"), FileAccess.ModeFlags.Write);
        Log($"Review folder: {sessionFolder}");
    }

    void Append(string message)
    {
        logFile?.StoreLine($"[{Time.GetTicksMsec() / 1000.0:F2}s] {message}");
        logFile?.Flush();
    }

    async void SaveScreenshot(string tag)
    {
        // Nothing is rendered without a display (headless runs), and nowhere to save without a session.
        if (DisplayServer.GetName() == "headless" || sessionFolder == null)
            return;

        int number = ++captureCount;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image image = GetViewport().GetTexture().GetImage();
        if (image == null || image.IsEmpty())
            return;

        string file = $"{number:0000}_{Regex.Replace(tag.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_')}.png";
        string path = sessionFolder.PathJoin(file);
        // Encoding a PNG takes a few frames' worth of time; keep it off the game loop.
        _ = Task.Run(() => image.SavePng(path));
        Append($"image {file}");
    }
}
