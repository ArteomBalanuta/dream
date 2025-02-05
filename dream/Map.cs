// Map.cs
using BulletSharp;
using BulletSharp.Math;
using OpenTK.Mathematics;
using System.Diagnostics;
using Vector3 = OpenTK.Mathematics.Vector3;

public class Map
{
    public int SideSize { get; } = 20;
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
        // Initialize ALL cubes to empty first, with centers at grid cells
        for (int x = 0; x < SideSize; x++)
            for (int y = 0; y < SideSize; y++)
                for (int z = 0; z < SideSize; z++)
                {
                    Cubes[x, y, z] = new Cube
                    {
                        Position = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), // Center position
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
     //   AddCube(new Vector3(3, 3, 2), true);
     //   AddCube(new Vector3(6, 6, 6), true);


        for (int x = 0; x < SideSize; x++)
        {
            for (int z = 0; z < SideSize; z++)
            {
                var cube = Cubes[x, 0, z];
                if (cube.Position != new Vector3(x + 0.5f, 0.5f, z + 0.5f))
                {
                    Debug.Fail($"Cube at [{x},0,{z}] misaligned! Position: {cube.Position}");
                }
            }
        }

    }


    public void AddCube(Vector3 position, bool isStatic = false)
    {
        // Snap to grid with epsilon to handle floating-point precision
        int x = (int)MathF.Floor(position.X + 1e-6f);
        int y = (int)MathF.Floor(position.Y + 1e-6f);
        int z = (int)MathF.Floor(position.Z + 1e-6f);


        if (!IsInBounds(new Vector3(x, y, z)) || !Cubes[x, y, z].IsEmpty)
            return;

        // Cube is already initialized at center in InitializeAllCubes
        var spaceCube = Cubes[x, y, z];
        var cube = new Cube
        {
            Position = spaceCube.Position,
            IsEmpty = false,
            IsSolid = true,
            //Animation = new AnimatedModel("D:\\workspace\\dream-project\\dream\\animations\\new.gltf"),
            ShaderName = "default"
        };

        Cubes[x, y, z] = cube;
        AddPhysicsBody(cube, isStatic);
    }

    private void AddPhysicsBody(Cube cube, bool isStatic)
    {
        var shape = new BoxShape(0.5f);
        shape.Margin = 0f; // Add this line to remove collision margin

        var mass = isStatic ? 0f : 1f;
        var position = cube.Position;

        var startTransform = Matrix.Translation(position.ToBullet());

        var motionState = new DefaultMotionState(startTransform);

        shape.CalculateLocalInertia(mass, out var inertia);
        var rbInfo = new RigidBodyConstructionInfo(mass, motionState, shape, inertia);

        var body = new RigidBody(rbInfo)
        {
            UserObject = (cube, position),
            Friction = 0.8f,
            Restitution = 0.2f,
            AngularFactor = BulletSharp.Math.Vector3.Zero

            /*
             * 
             * 
             * 
             * How to Fix It

You have two main options:
Option 1: Freeze Rotation in the Physics Simulation

If you want the cubes to never rotate, you can prevent rotation in Bullet by setting the angular factor to zero when creating the rigid body. For example, in your AddPhysicsBody method, add:

body.AngularFactor = BulletSharp.Math.Vector3.Zero;

This tells Bullet not to apply any rotation to that body. With no rotation, both the red and white cubes will stay aligned.
Option 2: Use the Full Physics Transform for Rendering

If you want to allow rotation in the physics simulation and have the rendered cube follow exactly, update your render code to use the full transform (including rotation) from the physics body. For example:

    Retrieve the full transform matrix from the physics body:

    body.MotionState.GetWorldTransform(out Matrix physicsTransform);
    Matrix4 modelMatrix = physicsTransform.ToOpenTK();

    Use that model matrix for rendering the white cube. (You might still need to incorporate a centering offset if your vertex data isn’t centered.)

This way, the white cube will be drawn with the same rotation as the physics body, keeping it aligned with the red collision box.
Which Option to Choose?

    Freeze Rotation (Option 1):
    Use this if you want your cubes to always be axis-aligned and avoid rotation artifacts. This is common for grid-based or block-based worlds where cubes should not tip or rotate.

    Full Transform (Option 2):
    Use this if you want the physics simulation to control the object’s orientation and have the rendering reflect that precisely.

Final Recommendation

If your intended behavior is that cubes remain unrotated, update your physics body with:

body.AngularFactor = BulletSharp.Math.Vector3.Zero;

Then you can safely keep your rendered model matrix as:

Matrix4 modelMatrix = Matrix4.CreateTranslation(cube.Position) * Matrix4.CreateScale(cube.Side);
This will ensure that, as the object moves, both the red and white cubes remain perfectly aligned.
Give that a try, and let me know if it resolves the disalignment during movement!

             * 
             * */
        };

        body.SetMassProps(mass, inertia); // Explicitly set mass

        _physics.World.AddRigidBody(body);
        _physics.MapCubeBodies.Add(body);

        // In Map.AddPhysicsBody (add debug logging)
        Console.WriteLine($"Adding cube at {cube.Position.X},{cube.Position.Y},{cube.Position.Z} with center {position}");
    }

    public bool IsInBounds(Vector3 pos) =>
        pos.X >= 0 && pos.X < SideSize &&
        pos.Y >= 0 && pos.Y < SideSize &&
        pos.Z >= 0 && pos.Z < SideSize;
}