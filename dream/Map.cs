// Map.cs
using BulletSharp;
using BulletSharp.Math;
using OpenTK.Mathematics;
using System.Diagnostics;
using Vector3 = OpenTK.Mathematics.Vector3;

public class Map
{
    public int Width { get; }
    public int Height { get; }
    public int Depth { get; }
    public Cube?[,,] Cubes { get; }
    public IReadOnlyList<Cube> SolidCubes => _solidCubes;
    private readonly PhysicsIntegrator? _physics;
    private readonly List<Cube> _solidCubes = new();
    private readonly bool _loadVisualAssets;

    public Map(
        PhysicsIntegrator? physics = null,
        bool loadVisualAssets = true,
        int width = 25,
        int height = 8,
        int depth = 25)
    {
        _physics = physics;
        _loadVisualAssets = loadVisualAssets;
        Width = width;
        Height = height;
        Depth = depth;
        Cubes = new Cube[Width, Height, Depth];
        Initialize();
    }

    private void Initialize()
    {
        for (int x = 0; x < Width; x++)
            for (int z = 0; z < Depth; z++)
            {
                AddCube(new Vector3(x, 0, z), isStatic: true, withPhysics: false);
            }

        if (_physics != null)
        {
            AddFloorPhysicsBody();
        }

        for (int x = 0; x < Width; x++)
        {
            for (int z = 0; z < Depth; z++)
            {
                var cube = Cubes[x, 0, z];
                if (cube == null || cube.Position != new Vector3(x + 0.5f, 0.5f, z + 0.5f))
                {
                    Debug.Fail($"Cube at [{x},0,{z}] misaligned! Position: {cube?.Position}");
                }
            }
        }
    }


    public void AddCube(Vector3 position, bool isStatic = false, bool withPhysics = true)
    {
        int x = (int)MathF.Floor(position.X + 1e-6f);
        int y = (int)MathF.Floor(position.Y + 1e-6f);
        int z = (int)MathF.Floor(position.Z + 1e-6f);

        if (!IsInBounds(new Vector3(x, y, z)) || Cubes[x, y, z] != null)
            return;

        var cube = new Cube
        {
            Position = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f),
            IsEmpty = false,
            IsSolid = true,
            Animation = _loadVisualAssets ? ModelAssets.GrassCube : null,
            ShaderName = "cube"
        };

        Cubes[x, y, z] = cube;
        _solidCubes.Add(cube);

        if (withPhysics && _physics != null)
        {
            AddPhysicsBody(cube, isStatic);
        }
    }

    public void CopyPlacedCubesTo(Map target)
    {
        foreach (Cube cube in _solidCubes)
        {
            int x = (int)MathF.Floor(cube.Position.X);
            int y = (int)MathF.Floor(cube.Position.Y);
            int z = (int)MathF.Floor(cube.Position.Z);

            if (y == 0)
            {
                continue;
            }

            Vector3 cell = new Vector3(x, y, z);
            if (!target.IsInBounds(cell))
            {
                continue;
            }

            target.AddCube(cell, withPhysics: target._physics != null);
        }
    }

    private void AddFloorPhysicsBody()
    {
        var shape = new BoxShape(Width / 2f, 0.5f, Depth / 2f);
        shape.Margin = 0f;

        var floorCenter = new BulletSharp.Math.Vector3(Width / 2f, 0.5f, Depth / 2f);
        var startTransform = Matrix.Translation(floorCenter);
        var motionState = new DefaultMotionState(startTransform);
        var rbInfo = new RigidBodyConstructionInfo(0f, motionState, shape, BulletSharp.Math.Vector3.Zero);

        var body = new RigidBody(rbInfo)
        {
            Friction = 0.8f,
            Restitution = 0.2f,
            AngularFactor = BulletSharp.Math.Vector3.Zero
        };

        _physics.World.AddRigidBody(body);
        _physics.MapCubeBodies.Add(body);
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
        if (!isStatic)
        {
            _physics.DynamicMapCubeBodies.Add(body);
        }

    }

    public bool IsInBounds(Vector3 pos) =>
        pos.X >= 0 && pos.X < Width &&
        pos.Y >= 0 && pos.Y < Height &&
        pos.Z >= 0 && pos.Z < Depth;
}
