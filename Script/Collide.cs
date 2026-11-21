using Godot;

// Hard limits for bodies and rackets: nothing may pass through the table or through a body.
// Each check returns how far, and which way, a point (with a radius) has to move to be clear.
public static class Collide
{
    const float TableThickness = 0.03f;

    // The table top: a point over it must stay above its surface (below the top slab is the space
    // under the table, but a racket or hand over the table can only have got there through it).
    public static Vector3 OutOfTable(Vector3 p, float radius)
    {
        bool over = Mathf.Abs(p.X) < TableSpec.HalfWidth + radius && Mathf.Abs(p.Z) < TableSpec.HalfLength + radius;
        float top = TableSpec.Height + radius;
        if (!over || p.Y >= top || p.Y < TableSpec.Height - TableThickness - 0.5f)
            return Vector3.Zero;
        return Vector3.Up * (top - p.Y);
    }

    // A body part as a capsule: the segment a..b thickened by r.
    public static Vector3 OutOfCapsule(Vector3 p, float radius, Vector3 a, Vector3 b, float r)
    {
        Vector3 ab = b - a;
        float t = ab.LengthSquared() > 1e-8f ? Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0f, 1f) : 0f;
        Vector3 away = p - (a + ab * t);
        float depth = r + radius - away.Length();
        if (depth <= 0f)
            return Vector3.Zero;
        Vector3 dir = away.LengthSquared() > 1e-8f ? away.Normalized() : Vector3.Up;
        return dir * depth;
    }

    public record struct Capsule(Vector3 A, Vector3 B, float Radius);

    // The push a point needs to clear the table and all the capsules: the deepest one.
    public static Vector3 OutOfAll(Vector3 p, float radius, Capsule[] body, bool table = true)
    {
        Vector3 worst = table ? OutOfTable(p, radius) : Vector3.Zero;
        foreach (Capsule c in body)
        {
            Vector3 push = OutOfCapsule(p, radius, c.A, c.B, c.Radius);
            if (push.LengthSquared() > worst.LengthSquared())
                worst = push;
        }
        return worst;
    }
}
