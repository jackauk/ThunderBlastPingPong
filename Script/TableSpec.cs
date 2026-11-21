using Godot;

// Official table tennis dimensions in metres. The table is centred on the origin with its
// length along Z: the player defends the +Z half, the opponent defends the -Z half.
public static class TableSpec
{
    public const float Height = 0.76f;
    public const float HalfLength = 1.37f;
    public const float HalfWidth = 0.7625f;
    public const float NetHeight = 0.1525f;
    public const float NetHalfWidth = 0.915f;
    public const float NetTop = Height + NetHeight;

    // +1 for the player's half, -1 for the opponent's half.
    public static int SideOf(float z) => z >= 0f ? 1 : -1;
}
