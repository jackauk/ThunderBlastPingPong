using Godot;

// Moves the camera between the player's eye view, the cinematic shots of an NPC's serve and,
// in NPC vs NPC mode, a broadcast view from the side of the table.
// Blends run on real time so they keep their pace while the game is in slow motion.
public partial class CameraRig : Camera3D
{
    public enum Shot
    {
        Player,      // the player's eyes, swaying a little with the blade
        ServeFocus,  // close on the server while it gets ready to serve
        FollowBall,  // tracks the served ball from the same spot
        Broadcast    // from the side, both players in view, drifting a little with the ball
    }

    [Export] public Blade Blade;
    [Export] public Ball Ball;
    [Export] public Opponent Server;
    [Export] public Vector3 FocusPosition = new(0.9f, 1.35f, 0.5f); // for a server at the far end
    [Export] public float FocusFov = 35f;
    [Export] public Vector3 BroadcastPosition = new(3.2f, 2.2f, 0f);
    [Export] public float BroadcastFov = 52f;

    public Shot Mode = Shot.Player;

    Transform3D playerPose;
    float playerFov;

    public override void _Ready()
    {
        playerPose = Transform;
        playerFov = Fov;
    }

    public override void _Process(double delta)
    {
        float realDt = (float)(delta / Engine.TimeScale);
        // Over the receiver's half, turned half round for a server at the near end.
        Vector3 focus = Server.Side < 0 ? FocusPosition : FocusPosition * new Vector3(-1f, 1f, -1f);
        Transform3D target;
        float fov;
        float sharpness;
        switch (Mode)
        {
            case Shot.ServeFocus:
                target = new Transform3D(Basis.Identity, focus)
                    .LookingAt(Server.ServePoint + new Vector3(0f, 0.1f, 0f), Vector3.Up);
                fov = FocusFov;
                sharpness = 3f;
                break;
            case Shot.FollowBall:
                target = new Transform3D(Basis.Identity, focus).LookingAt(Ball.GlobalPosition, Vector3.Up);
                fov = FocusFov + 10f;
                sharpness = 12f;
                break;
            case Shot.Broadcast:
                float drift = Mathf.Clamp(Ball.GlobalPosition.Z, -2f, 2f) * 0.2f;
                target = new Transform3D(Basis.Identity, BroadcastPosition + new Vector3(0f, 0f, drift))
                    .LookingAt(new Vector3(0f, 0.85f, drift), Vector3.Up);
                fov = BroadcastFov;
                sharpness = 4f;
                break;
            default:
                target = playerPose;
                target.Origin.X += Blade.Position.X * 0.15f;
                fov = playerFov;
                sharpness = 10f;
                break;
        }

        float weight = 1f - Mathf.Exp(-sharpness * realDt);
        Transform = Transform.InterpolateWith(target, weight);
        Fov = Mathf.Lerp(Fov, fov, weight);
    }
}
