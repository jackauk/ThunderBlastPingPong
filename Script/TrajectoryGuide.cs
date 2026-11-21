using Godot;
using System.Collections.Generic;

// Dotted line along a predicted flight, showing the direction of the NPC's serve from its
// contact point, with bigger dots where it bounces. Dots disappear as the ball passes them.
public partial class TrajectoryGuide : MultiMeshInstance3D
{
    [Export] public Color DotColor = new(1f, 0.6f, 0.15f, 0.85f);
    [Export] public float DotSpacing = 0.04f;
    [Export] public float BounceDotScale = 2.5f;

    const float MaxFlightTime = 3f;

    readonly List<float> dotTimes = new(); // flight time of each dot, latest first
    float age;

    public override void _Ready()
    {
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = DotColor
        };
        Multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = new SphereMesh { Radius = 0.006f, Height = 0.012f, RadialSegments = 8, Rings = 4, Material = material }
        };
        CastShadow = ShadowCastingSetting.Off;
        Visible = false;
    }

    // Plots the flight from start until its given number of table bounces (or net / floor).
    public void Plot(BallState start, int bounces)
    {
        var dots = new List<(Vector3 Position, float Time, bool Bounce)> { (start.Position, 0f, false) };
        BallState s = start;
        float dt = Ball.SubStepTime;
        Vector3 last = s.Position;
        int seen = 0;
        for (float t = dt; t < MaxFlightTime && seen < bounces; t += dt)
        {
            BallEvent evt = Ball.Step(ref s, dt, false);
            if (evt == BallEvent.Table)
            {
                seen++;
                dots.Add((s.Position, t, true));
                last = s.Position;
            }
            else if (evt != BallEvent.None)
            {
                break;
            }
            else if (s.Position.DistanceTo(last) >= DotSpacing)
            {
                dots.Add((s.Position, t, false));
                last = s.Position;
            }
        }

        // Latest dots first, so VisibleInstanceCount can drop the ones the ball has passed.
        Multimesh.InstanceCount = dots.Count;
        dotTimes.Clear();
        for (int i = 0; i < dots.Count; i++)
        {
            var dot = dots[dots.Count - 1 - i];
            float scale = dot.Bounce ? BounceDotScale : 1f;
            Multimesh.SetInstanceTransform(i, new Transform3D(Basis.FromScale(new Vector3(scale, scale, scale)), dot.Position));
            dotTimes.Add(dot.Time);
        }
        Multimesh.VisibleInstanceCount = dots.Count;
        age = 0f;
        Visible = true;
    }

    public void Clear() => Visible = false;

    public override void _PhysicsProcess(double delta)
    {
        if (!Visible)
            return;

        age += (float)delta;
        int ahead = 0;
        while (ahead < dotTimes.Count && dotTimes[ahead] > age)
            ahead++;
        Multimesh.VisibleInstanceCount = ahead;
        Visible = ahead > 0;
    }
}
