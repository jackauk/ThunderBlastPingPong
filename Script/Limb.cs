using Godot;

// Helpers for building body parts out of primitive meshes.
public static class Limb
{
    // A basis whose Y axis points along dir, for meshes modelled along Y (capsules, cylinders).
    public static Basis AlongY(Vector3 dir)
    {
        Vector3 y = dir.Normalized();
        Vector3 helper = Mathf.Abs(y.Dot(Vector3.Up)) > 0.99f ? Vector3.Back : Vector3.Up;
        Vector3 x = helper.Cross(y).Normalized();
        return new Basis(x, y, x.Cross(y));
    }

    // A rounded segment from a to b (ends extend by the radius, like a finger joint). smooth
    // builds it from more faces, for parts seen close up (the racket hand).
    public static MeshInstance3D Segment(Node3D parent, Vector3 a, Vector3 b, float radius, Material material, bool smooth = false)
    {
        var mesh = new CapsuleMesh
        {
            Radius = radius,
            Height = a.DistanceTo(b) + 2f * radius,
            RadialSegments = smooth ? 20 : 12,
            Rings = smooth ? 8 : 3,
            Material = material
        };
        var node = new MeshInstance3D { Mesh = mesh, Transform = new Transform3D(AlongY(b - a), (a + b) * 0.5f) };
        parent.AddChild(node);
        return node;
    }

    // An ellipsoid with the given full size along each axis.
    public static MeshInstance3D Ellipsoid(Node3D parent, Vector3 centre, Vector3 size, Material material, bool smooth = false)
    {
        var mesh = new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = smooth ? 32 : 16, Rings = smooth ? 16 : 8, Material = material };
        var node = new MeshInstance3D { Mesh = mesh, Transform = new Transform3D(Basis.FromScale(size), centre) };
        parent.AddChild(node);
        return node;
    }

    // A tapered cylinder of unit length along Y, stretched between joints by PlaceBone.
    public static MeshInstance3D Bone(Node3D parent, float startRadius, float endRadius, Material material)
    {
        var mesh = new CylinderMesh
        {
            BottomRadius = startRadius,
            TopRadius = endRadius,
            Height = 1f,
            RadialSegments = 16,
            Rings = 1,
            Material = material
        };
        var node = new MeshInstance3D { Mesh = mesh };
        parent.AddChild(node);
        return node;
    }

    public static void PlaceBone(MeshInstance3D bone, Vector3 from, Vector3 to)
    {
        Basis basis = AlongY(to - from);
        basis.Y *= from.DistanceTo(to);
        bone.GlobalTransform = new Transform3D(basis, (from + to) * 0.5f);
    }

    // The bones never change length: past full reach the end stops short of its target (and as
    // close in as the bones fold), on the line from the root towards it. Where the end of two
    // bones of lengths a and b from root actually gets to, aiming for target.
    public static Vector3 Reach(Vector3 root, Vector3 target, float a, float b)
    {
        Vector3 toTarget = target - root;
        float distance = toTarget.Length();
        if (distance < 1e-6f)
            return target;
        float reached = Mathf.Clamp(distance, Mathf.Abs(a - b) + 0.001f, 0.999f * (a + b));
        return Mathf.IsEqualApprox(reached, distance) ? target : root + toTarget * (reached / distance);
    }

    // Two-bone IK: where the middle joint (elbow, knee) goes for bones of lengths a and b from
    // root towards target, bending towards pole. The bones keep their lengths: a target out of
    // reach is met as far as they go (see Reach).
    public static Vector3 TwoBone(Vector3 root, Vector3 target, float a, float b, Vector3 pole)
    {
        Vector3 toTarget = target - root;
        float distance = toTarget.Length();
        if (distance < 0.001f)
            return root;
        Vector3 dir = toTarget / distance;
        distance = Mathf.Clamp(distance, Mathf.Abs(a - b) + 0.001f, 0.999f * (a + b));

        // Law of cosines: how far along the root-target line the joint sits, and how far off it.
        float along = (a * a - b * b + distance * distance) / (2f * distance);
        float off = Mathf.Sqrt(Mathf.Max(a * a - along * along, 0f));
        Vector3 bend = pole - dir * pole.Dot(dir);
        bend = bend.LengthSquared() > 1e-6f ? bend.Normalized() : AlongY(dir).X;
        return root + dir * along + bend * off;
    }

    // A sphere joint.
    public static MeshInstance3D Joint(Node3D parent, float radius, Material material)
    {
        var mesh = new SphereMesh { Radius = radius, Height = 2f * radius, RadialSegments = 16, Rings = 8, Material = material };
        var node = new MeshInstance3D { Mesh = mesh };
        parent.AddChild(node);
        return node;
    }
}
