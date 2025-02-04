// Map.cs
using BulletSharp;
using BulletSharp.Math;
using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;

public class Map
{
    public int SideSize { get; } = 25;
    public Cube[,,] Cubes { get; }
    private readonly PhysicsIntegrator _physics;

    public Map(PhysicsIntegrator physics)
    {
        _physics = physics;
        Cubes = new Cube[SideSize, SideSize, SideSize];
        // Initialize all cubes first
        InitializeAllCubes();

        // Then create the floor and special cubes
        Initialize();
    }

    private void InitializeAllCubes()
    {
        // Initialize ALL cubes to empty first
        for (int x = 0; x < SideSize; x++)
            for (int y = 0; y < SideSize; y++)
                for (int z = 0; z < SideSize; z++)
                {
                    Cubes[x, y, z] = new Cube
                    {
                        Position = new Vector3(x, y, z),
                        IsEmpty = true,
                        IsSolid = false
                    };
                }
    }

    private void Initialize()
    {
        // Create floor (y = 0)
        for (int x = 0; x < SideSize; x++)
            for (int z = 0; z < SideSize; z++)
            {
                AddCube(new Vector3(x, 0, z), true);
            }

        // Add special cubes
        AddCube(new Vector3(3, 3, 2), true);
        AddCube(new Vector3(6, 6, 6), true);
    }


    public void AddCube(Vector3 position, bool isStatic = false)
    {
        var (x, y, z) = ((int)position.X, (int)position.Y, (int)position.Z);

        if (!IsInBounds(position) || !Cubes[x, y, z].IsEmpty)
            return;

        Vector3 centerPos = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
        var cube = new Cube
        {
            Position = centerPos,
            IsEmpty = false,
            IsSolid = true,
            // Animation = new AnimatedModel("D:\\workspace\\dream-project\\dream\\animations\\new.gltf"),
            ShaderName = "default"
        };

        Cubes[x, y, z] = cube;
        AddPhysicsBody(cube, centerPos, isStatic);
    }

    private void AddPhysicsBody(Cube cube, Vector3 position, bool isStatic)
    {
        var shape = new BoxShape(0.5f);
        var mass = isStatic ? 0f : 1f;

        // Use the provided position as the center (it's already x+0.5, y+0.5, z+0.5)
        var centerPosition = position;

        var startTransform = Matrix.Translation(centerPosition.ToBullet());
        var motionState = new DefaultMotionState(startTransform);

        shape.CalculateLocalInertia(mass, out var inertia);
        var rbInfo = new RigidBodyConstructionInfo(mass, motionState, shape, inertia);

        var body = new RigidBody(rbInfo)
        {
            UserObject = (cube, position),
            Friction = 0.8f,
            Restitution = 0.2f
        };

        body.SetMassProps(mass, inertia); // Explicitly set mass

        _physics.World.AddRigidBody(body);
        _physics.MapCubeBodies.Add(body);

        // In Map.AddPhysicsBody (add debug logging)
        Console.WriteLine($"Adding cube at {cube.Position.X},{cube.Position.Y},{cube.Position.Z} with center {centerPosition}");
    }

    public bool IsInBounds(Vector3 pos) =>
        pos.X >= 0 && pos.X < SideSize &&
        pos.Y >= 0 && pos.Y < SideSize &&
        pos.Z >= 0 && pos.Z < SideSize;
}