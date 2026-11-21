using Godot;

// The forearm of a racket that has no body (the player's): built every frame from the racket
// out, as the Grip Lab does: the wrist where the hand holds the handle, the forearm the way the
// grip sets it (Paddle.ForearmDirection), ending at a rounded elbow. So the hand runs into a
// wrist and forearm instead of ending in the air. Only the forearm shows (ForearmOnly): the
// player's view is from behind where the body would be, so a shoulder and upper arm would hang
// in the middle of the picture from nothing. Added after the paddle in the tree, so it is posed
// after the paddle has moved this frame.
public partial class HeldArm : Arm
{
    public Paddle Paddle;

    public override void _Ready()
    {
        ForearmOnly = true;
        base._Ready();
    }

    public override void _Process(double delta)
    {
        if (Paddle == null || !IsVisibleInTree())
            return;
        Vector3 wrist = Paddle.WristPoint;
        Vector3 elbow = wrist + Paddle.ForearmDirection * ForearmSize;
        Vector3 shoulder = elbow + Paddle.UpperArmDirection * UpperArmSize;
        Solve(shoulder, wrist, elbow - shoulder);
    }
}
