using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Dream.Rendering.Core;
using Dream.Rendering.Models;
using Dream.Rendering.World;
using Dream.Simulation.Players;

namespace Dream.Rendering.Actors;

// Draws the items a player holds, one box at a time, on the hand of the arm that holds them. The
// boxes are unit-cube draws of one shared mesh (WeaponMeshCatalog gives each box its place, size
// and material), so there is nothing to upload per item and a new item costs no GL object. The
// owner (the local or the remote player renderer) calls Draw right after the model, with the same
// shader and model matrix; the frame state (view, projection, tint) is set here again, so it can
// also run after a pass that changed it.
public sealed class HeldWeaponRenderer : IDisposable
{
    // Where the fist is in the arm's own frame (the arm is a 0.4 wide, 1.0 long box hanging from
    // the shoulder, 0.2 either side of the node), and where a forearm shield sits.
    public const float FistOffsetX = 0.2f;
    public const float FistOffsetY = -0.9f;
    public const float ShieldOffsetX = 0.31f;
    public const float ShieldOffsetY = -0.55f;

    private StaticMeshBuffer? _cube;
    private bool _disposed;

    public static Matrix4 CreateGrip(WeaponGrip grip, bool offHand)
    {
        // The left arm hangs at +X, the right arm at -X.
        float side = offHand ? 1f : -1f;
        if (grip == WeaponGrip.Shield)
        {
            // The outer face (+Z) turns outward.
            return Matrix4.CreateRotationY(MathHelper.DegreesToRadians(90f * side)) *
                Matrix4.CreateTranslation(side * ShieldOffsetX, ShieldOffsetY, 0f);
        }

        // +Y (the weapon's length) turns to the arm's forward axis, so a hanging arm carries it
        // pointing ahead and a raised arm carries it pointing up.
        return Matrix4.CreateRotationX(MathHelper.DegreesToRadians(90f)) *
            Matrix4.CreateTranslation(side * FistOffsetX, FistOffsetY, 0f);
    }

    public void Draw(
        Shader shader,
        Camera camera,
        Matrix4 projection,
        AnimatedModelInstance instance,
        PlayerPoseLayers layers,
        in PlayerEquipmentVisuals equipment,
        Matrix4 modelMatrix)
    {
        if (_disposed || equipment.IsEmpty)
        {
            return;
        }

        bool prepared = false;
        if (equipment.MainHand is { } main)
        {
            DrawHeld(shader, camera, projection, instance, layers, main, offHand: false, modelMatrix, ref prepared);
        }

        if (equipment.OffHand is { } off)
        {
            DrawHeld(shader, camera, projection, instance, layers, off, offHand: true, modelMatrix, ref prepared);
        }

        if (prepared)
        {
            StaticMeshBuffer.EndBatchedDraws();
        }
    }

    private void DrawHeld(
        Shader shader,
        Camera camera,
        Matrix4 projection,
        AnimatedModelInstance instance,
        PlayerPoseLayers layers,
        string visualId,
        bool offHand,
        Matrix4 modelMatrix,
        ref bool prepared)
    {
        if (!layers.TryGetArmMatrix(instance, offHand, out Matrix4 arm))
        {
            return;
        }

        WeaponModel model = WeaponMeshCatalog.Resolve(visualId);
        Matrix4 attach = CreateGrip(model.Grip, offHand) * arm * modelMatrix;

        if (!prepared)
        {
            PrepareState(shader, camera, projection);
            prepared = true;
        }

        _cube ??= new StaticMeshBuffer(TerrainFallbackCubeMesh.Create());
        WeaponPart[] parts = model.PartArray;
        WeaponMaterial lastMaterial = (WeaponMaterial)(-1);
        for (int i = 0; i < parts.Length; i++)
        {
            WeaponPart part = parts[i];
            if (part.Material != lastMaterial)
            {
                lastMaterial = part.Material;
                shader.SetVector4("material.baseColorFactor", WeaponMeshCatalog.BaseColor(part.Material));
                shader.SetEmissiveFactor(WeaponMeshCatalog.Emissive(part.Material));
            }

            shader.SetModelMatrix(part.ToMatrix() * attach);
            _cube.DrawBatched();
        }
    }

    private static void PrepareState(Shader shader, Camera camera, Matrix4 projection)
    {
        GL.Enable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        shader.Use();
        shader.SetMatrix4("view", camera.GetViewMatrix());
        shader.SetMatrix4("projection", projection);
        shader.SetBool("useSkinning", false);
        shader.SetBool("useInstancing", false);
        shader.SetBool("material.hasBaseColorTexture", false);
        shader.SetHasEmissiveTexture(false);
        shader.SetFloat("material.metallicFactor", 0f);
        shader.SetFloat("material.roughnessFactor", 1f);
    }

    public void Dispose()
    {
        _disposed = true;
        _cube?.Dispose();
        _cube = null;
    }
}
