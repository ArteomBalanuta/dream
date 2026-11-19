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

    // Held items are scaled per item by HeldItemScale.Resolve (about the grip, or a shield's centre)
    // so each comes out at its documented real-world length: 1 m is 1.408 model units, and the
    // authored figure is 2.68 units = 1.904 m.

    // One GL buffer per sculpted shape, built on first use (EquipmentMeshCache). It replaces the
    // single shared cube: a part names a shape and is drawn with that shape's mesh instead.
    private readonly EquipmentMeshCache _meshes = new();
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
        try
        {
            if (equipment.MainHand is { } main)
            {
                DrawHeld(shader, camera, projection, instance, layers, main, offHand: false, modelMatrix, ref prepared);
            }

            if (equipment.OffHand is { } off)
            {
                DrawHeld(shader, camera, projection, instance, layers, off, offHand: true, modelMatrix, ref prepared);
            }
        }
        finally
        {
            // Whenever the pass was prepared - including a hand that bailed out half way - the
            // batch is closed and uEquipSurface goes back to zero, which is what makes the shader's
            // material block a no-op for every other actor, enemy, NPC and prop draw.
            if (prepared)
            {
                StaticMeshBuffer.EndBatchedDraws();
                shader.SetEquipmentSurface(EquipmentSurface.None.ToUniform());
            }
        }
    }

    // The eight corners of every box of the held items, as Draw would place them. Like
    // WornArmourRenderer.AppendCorners, for the capture's actorbox: a blade raised overhead is on screen
    // and outside the authored bounds. No GL.
    public static void AppendCorners(
        AnimatedModelInstance instance,
        PlayerPoseLayers layers,
        in PlayerEquipmentVisuals equipment,
        Matrix4 modelMatrix,
        List<Vector3> corners)
    {
        if (equipment.MainHand is { } main)
        {
            AppendHeld(instance, layers, main, offHand: false, modelMatrix, corners);
        }

        if (equipment.OffHand is { } off)
        {
            AppendHeld(instance, layers, off, offHand: true, modelMatrix, corners);
        }
    }

    private static void AppendHeld(
        AnimatedModelInstance instance, PlayerPoseLayers layers, string visualId, bool offHand,
        Matrix4 modelMatrix, List<Vector3> corners)
    {
        if (!layers.TryGetArmMatrix(instance, offHand, out Matrix4 arm))
        {
            return;
        }

        WeaponModel model = WeaponMeshCatalog.Resolve(visualId);
        float scale = HeldItemScale.Resolve(visualId);
        Matrix4 attach = Matrix4.CreateScale(scale) * CreateGrip(model.Grip, offHand) * arm * modelMatrix;
        WeaponPart[] parts = model.PartArray;
        for (int i = 0; i < parts.Length; i++)
        {
            WornArmourRenderer.AppendBoxCorners(parts[i].ToMatrix() * attach, corners);
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
        float scale = HeldItemScale.Resolve(visualId);
        Matrix4 attach = Matrix4.CreateScale(scale) * CreateGrip(model.Grip, offHand) * arm * modelMatrix;

        if (!prepared)
        {
            PrepareState(shader, camera, projection);
            prepared = true;
        }

        // The tint is this ITEM's, so it is resolved once per item and the material guard below is
        // per item too (lastMaterial is local to this call): a second item with the same material
        // re-sets the colour with its own tint instead of inheriting the first one's.
        EquipmentTint tint = EquipmentTintCatalog.Resolve(visualId);
        WeaponPart[] parts = model.PartArray;
        WeaponMaterial lastMaterial = (WeaponMaterial)(-1);
        for (int i = 0; i < parts.Length; i++)
        {
            WeaponPart part = parts[i];
            if (part.Material != lastMaterial)
            {
                lastMaterial = part.Material;
                EquipmentSurface surface = EquipmentSurface.For(part.Material);
                shader.SetVector4(
                    "material.baseColorFactor",
                    tint.Apply(WeaponMeshCatalog.BaseColor(part.Material), surface.AccentMix));
                shader.SetEmissiveFactor(WeaponMeshCatalog.Emissive(part.Material));
                shader.SetEquipmentSurface(surface.ToUniform());
            }

            shader.SetModelMatrix(part.ToMatrix() * attach);
            // The VAO changes with the shape; DrawBatched rebinds, so one batch still spans them all.
            _meshes.Get(part.Shape).DrawBatched();
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
        // Equipment never inherits an enemy's tint: EnemyRenderer sets uActorTintOffset per enemy
        // and the program is shared, so it is zeroed here as part of the pass's own frame state.
        shader.SetVector3("uActorTintOffset", Vector3.Zero);
        // The real eye, for the specular half vector (uCameraPosition is the fog origin, not the eye).
        shader.SetEquipmentEye(camera.Position);
    }

    public void Dispose()
    {
        _disposed = true;
        _meshes.Dispose();
    }
}
