using Godot;
using System.Collections.Generic;

// Geometry of an offensive shakehand blade with a flared (FL) handle, in paddle space: head
// centre at the origin, handle along -Y, faces towards ±Z, metres. Meshes are built once and
// shared by every paddle.
public static class BladeShape
{
    public const float BladeHalfThickness = 0.0029f;
    public const float RubberHalfThickness = 0.00175f;
    const float RubberCutY = -0.066f;   // rubber sheets stop here at the sides of the throat
    const float RubberCutSag = 0.006f;  // and dip this much lower in the middle
    const int CurveSteps = 6;

    // Half-width of the blade down its length (y, half width), traced from a photo of the
    // reference blade and scaled to its 150 mm width: a tall oval head (about 161 x 150 mm down
    // to the handle), widest at the origin, whose long shoulders sweep down past the top of the
    // handle into the throat, then the tang hidden inside the handle.
    static readonly Vector2[] Profile =
    {
        new(0.0909f, 0f), new(0.0893f, 0.0176f), new(0.0849f, 0.0303f), new(0.0815f, 0.0366f),
        new(0.0782f, 0.0419f), new(0.0716f, 0.0499f), new(0.065f, 0.0559f), new(0.0584f, 0.0606f),
        new(0.0518f, 0.0642f), new(0.0452f, 0.0669f), new(0.0386f, 0.0694f), new(0.032f, 0.0714f),
        new(0.0253f, 0.0727f), new(0.0187f, 0.0738f), new(0.0121f, 0.0745f), new(0.0022f, 0.0749f),
        new(-0.0077f, 0.0748f), new(-0.0143f, 0.0739f), new(-0.0242f, 0.0727f), new(-0.0309f, 0.0711f),
        new(-0.0375f, 0.0689f), new(-0.0441f, 0.0656f), new(-0.0507f, 0.062f), new(-0.0573f, 0.0568f),
        new(-0.0639f, 0.0499f), new(-0.0705f, 0.0397f), new(-0.0771f, 0.0273f), new(-0.0837f, 0.0193f),
        new(-0.0904f, 0.0154f), new(-0.097f, 0.0132f), new(-0.104f, 0.0119f), new(-0.165f, 0.0116f),
    };

    // Flared (FL) handle cross-sections (y, half width, half depth), traced like the head: about
    // 100 mm long, 25 mm wide at the waist and 35 mm at the butt. It ramps up from the blade
    // face just under the rubbers, stays slim down to the badge and flares out to the butt.
    static readonly Vector3[] HandleStations =
    {
        new(-0.07f, 0.0128f, 0.003f), new(-0.074f, 0.0128f, 0.0068f), new(-0.08f, 0.0127f, 0.0098f),
        new(-0.088f, 0.0125f, 0.011f), new(-0.1f, 0.0124f, 0.0112f), new(-0.117f, 0.0124f, 0.0112f),
        new(-0.127f, 0.0129f, 0.0113f), new(-0.137f, 0.014f, 0.0114f), new(-0.147f, 0.0151f, 0.0116f),
        new(-0.157f, 0.0162f, 0.0118f), new(-0.166f, 0.0173f, 0.0121f), new(-0.1705f, 0.0174f, 0.0122f),
        new(-0.172f, 0.0164f, 0.0114f),
    };

    static ArrayMesh blade;
    static ArrayMesh rubber;
    static ArrayMesh handle;

    public static ArrayMesh Blade => blade ??= Extrude(BladeOutline(), BladeHalfThickness);
    public static ArrayMesh Rubber => rubber ??= Extrude(RubberOutline(), RubberHalfThickness);
    public static ArrayMesh Handle => handle ??= Loft(HandleStations, 28, 3f);

    // The +X side of the outline from the top of the head down to the end of the tang, smoothed.
    static List<Vector2> RightSide()
    {
        var points = new List<Vector2>();
        for (int i = 0; i < Profile.Length - 1; i++)
        {
            Vector2 p0 = ToXY(Profile[Mathf.Max(i - 1, 0)]);
            Vector2 p1 = ToXY(Profile[i]);
            Vector2 p2 = ToXY(Profile[i + 1]);
            Vector2 p3 = ToXY(Profile[Mathf.Min(i + 2, Profile.Length - 1)]);
            for (int s = 0; s < CurveSteps; s++)
                points.Add(CatmullRom(p0, p1, p2, p3, (float)s / CurveSteps));
        }
        points.Add(ToXY(Profile[^1]));
        return points;
    }

    static Vector2 ToXY(Vector2 profile) => new(profile.Y, profile.X);

    static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float t2 = t * t;
        return 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
            + (3f * p1 - p0 - 3f * p2 + p3) * t2 * t);
    }

    // Right side down, across the end of the tang, left side back up.
    static Vector2[] BladeOutline()
    {
        List<Vector2> right = RightSide();
        var outline = new List<Vector2>(right);
        for (int i = right.Count - 1; i >= 1; i--)
            outline.Add(new Vector2(-right[i].X, right[i].Y));
        return outline.ToArray();
    }

    // The head outline down to the cut above the handle, closed by a shallow arc.
    static Vector2[] RubberOutline()
    {
        List<Vector2> right = RightSide();
        var side = new List<Vector2>();
        for (int i = 0; i < right.Count; i++)
        {
            if (right[i].Y > RubberCutY)
            {
                side.Add(right[i]);
                continue;
            }
            Vector2 above = right[i - 1];
            side.Add(above.Lerp(right[i], (above.Y - RubberCutY) / (above.Y - right[i].Y)));
            break;
        }

        var outline = new List<Vector2>(side);
        float halfCut = side[^1].X;
        const int arcSteps = 8;
        for (int k = 1; k < arcSteps; k++)
        {
            float x = halfCut * (1f - 2f * k / arcSteps);
            outline.Add(new Vector2(x, RubberCutY - RubberCutSag * (1f - x * x / (halfCut * halfCut))));
        }
        for (int i = side.Count - 1; i >= 1; i--)
            outline.Add(new Vector2(-side[i].X, side[i].Y));
        return outline.ToArray();
    }

    // A flat outline given thickness, with smooth normals around its edge.
    static ArrayMesh Extrude(Vector2[] outline, float halfThickness)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        int[] triangles = Geometry2D.TriangulatePolygon(outline);
        foreach (float face in new[] { -1f, 1f })
        {
            var normal = new Vector3(0f, 0f, face);
            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 Corner(int k) => new(outline[triangles[i + k]].X, outline[triangles[i + k]].Y, face * halfThickness);
                AddTriangle(st, (Corner(0), normal), (Corner(1), normal), (Corner(2), normal));
            }
        }

        int n = outline.Length;
        float orientation = SignedArea(outline) > 0f ? 1f : -1f;
        var normals = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            Vector2 before = outline[i] - outline[(i + n - 1) % n];
            Vector2 after = outline[(i + 1) % n] - outline[i];
            Vector2 normal = (Outward(before, orientation) + Outward(after, orientation)).Normalized();
            normals[i] = new Vector3(normal.X, normal.Y, 0f);
        }
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            AddQuad(st,
                (new Vector3(outline[i].X, outline[i].Y, -halfThickness), normals[i]),
                (new Vector3(outline[j].X, outline[j].Y, -halfThickness), normals[j]),
                (new Vector3(outline[j].X, outline[j].Y, halfThickness), normals[j]),
                (new Vector3(outline[i].X, outline[i].Y, halfThickness), normals[i]));
        }
        return st.Commit();
    }

    // Rounded-rectangle (superellipse) cross-sections along Y, capped at the last one.
    static ArrayMesh Loft(Vector3[] stations, int segments, float squareness)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        Vector3 Ring(Vector3 station, float angle) => new(
            Super(Mathf.Cos(angle), squareness) * station.Y,
            station.X,
            Super(Mathf.Sin(angle), squareness) * station.Z);

        Vector3 RingNormal(Vector3 station, float angle)
        {
            Vector3 tangent = Ring(station, angle + 0.001f) - Ring(station, angle - 0.001f);
            return new Vector3(tangent.Z, 0f, -tangent.X).Normalized();
        }

        for (int k = 0; k < stations.Length - 1; k++)
        {
            for (int s = 0; s < segments; s++)
            {
                float a0 = Mathf.Tau * s / segments;
                float a1 = Mathf.Tau * (s + 1) / segments;
                AddQuad(st,
                    (Ring(stations[k], a0), RingNormal(stations[k], a0)),
                    (Ring(stations[k], a1), RingNormal(stations[k], a1)),
                    (Ring(stations[k + 1], a1), RingNormal(stations[k + 1], a1)),
                    (Ring(stations[k + 1], a0), RingNormal(stations[k + 1], a0)));
            }
        }

        Vector3 butt = stations[^1];
        Vector3 centre = new(0f, butt.X, 0f);
        for (int s = 0; s < segments; s++)
        {
            AddTriangle(st,
                (centre, Vector3.Down),
                (Ring(butt, Mathf.Tau * s / segments), Vector3.Down),
                (Ring(butt, Mathf.Tau * (s + 1) / segments), Vector3.Down));
        }
        return st.Commit();
    }

    static void AddQuad(SurfaceTool st, (Vector3 P, Vector3 N) a, (Vector3 P, Vector3 N) b, (Vector3 P, Vector3 N) c, (Vector3 P, Vector3 N) d)
    {
        AddTriangle(st, a, b, c);
        AddTriangle(st, a, c, d);
    }

    // Godot treats clockwise triangles as front faces, and lights a back face as if its normal
    // were flipped, so wind every triangle clockwise as seen from the side its normal points to.
    static void AddTriangle(SurfaceTool st, (Vector3 P, Vector3 N) a, (Vector3 P, Vector3 N) b, (Vector3 P, Vector3 N) c)
    {
        Vector3 facing = (b.P - a.P).Cross(c.P - a.P);
        if (facing.Dot(a.N + b.N + c.N) > 0f)
            (b, c) = (c, b);
        foreach (var (p, n) in new[] { a, b, c })
        {
            st.SetNormal(n);
            st.AddVertex(p);
        }
    }

    static float Super(float value, float squareness) => Mathf.Sign(value) * Mathf.Pow(Mathf.Abs(value), 2f / squareness);

    static Vector2 Outward(Vector2 edge, float orientation) => new Vector2(edge.Y, -edge.X).Normalized() * orientation;

    static float SignedArea(Vector2[] polygon)
    {
        float area = 0f;
        for (int i = 0; i < polygon.Length; i++)
        {
            Vector2 a = polygon[i];
            Vector2 b = polygon[(i + 1) % polygon.Length];
            area += a.X * b.Y - b.X * a.Y;
        }
        return area * 0.5f;
    }
}
