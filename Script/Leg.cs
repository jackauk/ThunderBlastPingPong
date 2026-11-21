using Godot;

// A jointed leg: hip, thigh, knee, shin, ankle and a shoe, posed every frame by two-bone IK
// from the hip to the ankle, with the knee bending forward over the toes. The foot stands flat
// on the ground (or lifted while it steps) and points where its owner says.
public partial class Leg : Node3D
{
    [Export] public Material Trousers;
    [Export] public Material Shoe;
    [Export] public float ThighLength = 0.43f;
    [Export] public float ShinLength = 0.41f;

    public const float AnkleHeight = 0.075f;

    // Where the joints are now (world space), for keeping things out of the leg.
    public Vector3 HipPoint { get; private set; }
    public Vector3 KneePoint { get; private set; }
    public Vector3 AnklePoint { get; private set; }
    public Vector3 Toes { get; private set; } = Vector3.Back; // the way the foot points (horizontal)

    // Shoe in foot space (+Z towards the toes, origin under the ankle on the ground): the ankle
    // sits over the back third of the foot.
    static readonly Vector3 ShoeCentre = new(0f, 0.04f, 0.055f);
    static readonly Vector3 ShoeSize = new(0.1f, 0.075f, 0.27f);
    const float SoleThickness = 0.018f;

    MeshInstance3D hip;
    MeshInstance3D thigh;
    MeshInstance3D knee;
    MeshInstance3D shin;
    MeshInstance3D ankle;
    Node3D foot;

    public override void _Ready()
    {
        TopLevel = true;
        hip = Limb.Joint(this, 0.075f, Trousers);
        thigh = Limb.Bone(this, 0.075f, 0.058f, Trousers);
        knee = Limb.Joint(this, 0.057f, Trousers);
        shin = Limb.Bone(this, 0.055f, 0.042f, Trousers);
        ankle = Limb.Joint(this, 0.038f, Shoe);

        foot = new Node3D();
        AddChild(foot);
        Limb.Ellipsoid(foot, ShoeCentre, ShoeSize, Shoe);
        var sole = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(ShoeSize.X * 0.92f, SoleThickness, ShoeSize.Z * 0.95f), Material = Shoe },
            Position = new Vector3(0f, SoleThickness * 0.5f, ShoeCentre.Z)
        };
        foot.AddChild(sole);
    }

    // hipPos: the hip joint; footPos: the ground point under the ankle (higher while stepping);
    // toes: horizontal direction the foot points. The knee bends towards the toes, or towards
    // kneeToward when given (a knee turned out or in).
    public void Solve(Vector3 hipPos, Vector3 footPos, Vector3 toes, Vector3 kneeToward = default)
    {
        toes = new Vector3(toes.X, 0f, toes.Z).Normalized();
        // The leg keeps its length: out of reach, the foot comes up short of its place (lifted).
        Vector3 anklePos = Limb.Reach(hipPos, footPos + Vector3.Up * AnkleHeight, ThighLength, ShinLength);
        footPos = anklePos - Vector3.Up * AnkleHeight;
        Vector3 kneePos = Limb.TwoBone(hipPos, anklePos, ThighLength, ShinLength, kneeToward == default ? toes : kneeToward);

        hip.GlobalPosition = hipPos;
        HipPoint = hipPos;
        KneePoint = kneePos;
        AnklePoint = anklePos;
        Limb.PlaceBone(thigh, hipPos, kneePos);
        knee.GlobalPosition = kneePos;
        Limb.PlaceBone(shin, kneePos, anklePos);
        ankle.GlobalPosition = anklePos;
        foot.GlobalTransform = new Transform3D(new Basis(Vector3.Up.Cross(toes), Vector3.Up, toes), footPos);
        Toes = toes;
    }
}
