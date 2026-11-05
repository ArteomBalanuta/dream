using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Dream.Rendering.Core;
using Dream.Rendering.Models;
using Dream.Rendering.World;
using Dream.Simulation.Players;

namespace Dream.Rendering.Actors;

// Draws the armour a player wears, one box at a time, on the node of the body part it covers. Like
// HeldWeaponRenderer, the boxes are unit-cube draws of one shared mesh (ArmourMeshCatalog gives each
// box its place, size and material), so there is nothing to upload per item. Every piece rides its
// node's OVERRIDDEN world matrix, so a flinch carries the cuirass and cloak, a held arm the gauntlet
// and an air tuck the boots. Armour draws at scale 1.0: it must match the baked body exactly. Arm,
// leg and foot pieces are one model drawn twice, mirrored in X. The owner (the local or the remote
// player renderer) calls Draw right after the model, with the same shader and model matrix.
public sealed class WornArmourRenderer : IDisposable
{
    // The arm hangs 0.2 either side of its node (see HeldWeaponRenderer.FistOffsetX).
    public const float ArmOffsetX = 0.2f;

    private StaticMeshBuffer? _cube;
    private bool _disposed;

    public void Draw(
        Shader shader,
        Camera camera,
        Matrix4 projection,
        AnimatedModelInstance instance,
        PlayerPoseLayers layers,
        in PlayerEquipmentVisuals equipment,
        Matrix4 modelMatrix)
    {
        if (_disposed || !equipment.HasWorn)
        {
            return;
        }

        bool prepared = false;
        DrawSingle(shader, camera, projection, instance, layers, equipment.Head, PlayerBodyNode.Head, modelMatrix, ref prepared);
        DrawSingle(shader, camera, projection, instance, layers, equipment.Body, PlayerBodyNode.Torso, modelMatrix, ref prepared);
        DrawSingle(shader, camera, projection, instance, layers, equipment.Back, PlayerBodyNode.Torso, modelMatrix, ref prepared);
        DrawSingle(shader, camera, projection, instance, layers, equipment.Companion, PlayerBodyNode.Torso, modelMatrix, ref prepared);
        DrawPair(shader, camera, projection, instance, layers, equipment.Hands, PlayerBodyNode.LeftArm, PlayerBodyNode.RightArm, ArmOffsetX, modelMatrix, ref prepared);
        DrawPair(shader, camera, projection, instance, layers, equipment.Legs, PlayerBodyNode.LeftLeg, PlayerBodyNode.RightLeg, 0f, modelMatrix, ref prepared);
        DrawPair(shader, camera, projection, instance, layers, equipment.Feet, PlayerBodyNode.LeftLeg, PlayerBodyNode.RightLeg, 0f, modelMatrix, ref prepared);

        if (prepared)
        {
            StaticMeshBuffer.EndBatchedDraws();
        }
    }

    private void DrawSingle(
        Shader shader,
        Camera camera,
        Matrix4 projection,
        AnimatedModelInstance instance,
        PlayerPoseLayers layers,
        string? visualId,
        PlayerBodyNode node,
        Matrix4 modelMatrix,
        ref bool prepared)
    {
        if (ArmourMeshCatalog.Resolve(visualId) is not { } model ||
            !layers.TryGetNodeMatrix(instance, node, out Matrix4 nodeMatrix))
        {
            return;
        }

        DrawModel(shader, camera, projection, model, nodeMatrix * modelMatrix, ref prepared);
    }

    private void DrawPair(
        Shader shader,
        Camera camera,
        Matrix4 projection,
        AnimatedModelInstance instance,
        PlayerPoseLayers layers,
        string? visualId,
        PlayerBodyNode left,
        PlayerBodyNode right,
        float offsetX,
        Matrix4 modelMatrix,
        ref bool prepared)
    {
        if (ArmourMeshCatalog.Resolve(visualId) is not { } model)
        {
            return;
        }

        // The left side hangs at +X; the right one is the model mirrored.
        if (layers.TryGetNodeMatrix(instance, left, out Matrix4 leftMatrix))
        {
            DrawModel(shader, camera, projection, model, CreateSideAttach(+1f, offsetX) * leftMatrix * modelMatrix, ref prepared);
        }

        if (layers.TryGetNodeMatrix(instance, right, out Matrix4 rightMatrix))
        {
            DrawModel(shader, camera, projection, model, CreateSideAttach(-1f, offsetX) * rightMatrix * modelMatrix, ref prepared);
        }
    }

    // Mirrors the model in X for the right side, then moves it out to the limb's own axis.
    public static Matrix4 CreateSideAttach(float side, float offsetX) =>
        Matrix4.CreateScale(side, 1f, 1f) * Matrix4.CreateTranslation(side * offsetX, 0f, 0f);

    // The eight corners of every box of every worn piece, as Draw would place them (node override,
    // side attach, model matrix), appended to corners. The capture's actorbox unions these with the
    // authored bounds, because worn pieces stand outside the authored model. Nothing is appended for an
    // empty slot or an id the catalog does not know, exactly as Draw draws nothing for it. No GL.
    public static void AppendCorners(
        AnimatedModelInstance instance,
        PlayerPoseLayers layers,
        in PlayerEquipmentVisuals equipment,
        Matrix4 modelMatrix,
        List<Vector3> corners)
    {
        if (!equipment.HasWorn)
        {
            return;
        }

        AppendSingle(instance, layers, equipment.Head, PlayerBodyNode.Head, modelMatrix, corners);
        AppendSingle(instance, layers, equipment.Body, PlayerBodyNode.Torso, modelMatrix, corners);
        AppendSingle(instance, layers, equipment.Back, PlayerBodyNode.Torso, modelMatrix, corners);
        AppendSingle(instance, layers, equipment.Companion, PlayerBodyNode.Torso, modelMatrix, corners);
        AppendPair(instance, layers, equipment.Hands, PlayerBodyNode.LeftArm, PlayerBodyNode.RightArm, ArmOffsetX, modelMatrix, corners);
        AppendPair(instance, layers, equipment.Legs, PlayerBodyNode.LeftLeg, PlayerBodyNode.RightLeg, 0f, modelMatrix, corners);
        AppendPair(instance, layers, equipment.Feet, PlayerBodyNode.LeftLeg, PlayerBodyNode.RightLeg, 0f, modelMatrix, corners);
    }

    // The corners of one model's boxes under attach (the matrix DrawModel is given).
    public static void AppendModelCorners(ArmourModel model, Matrix4 attach, List<Vector3> corners)
    {
        ArmourPart[] parts = model.PartArray;
        for (int i = 0; i < parts.Length; i++)
        {
            AppendBoxCorners(parts[i].ToMatrix() * attach, corners);
        }
    }

    // The unit cube the batch draws is centred on the origin, so its corners are (+-0.5)^3.
    public static void AppendBoxCorners(Matrix4 boxToWorld, List<Vector3> corners)
    {
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 local = new(
                (corner & 1) == 0 ? -0.5f : 0.5f,
                (corner & 2) == 0 ? -0.5f : 0.5f,
                (corner & 4) == 0 ? -0.5f : 0.5f);
            corners.Add(Vector3.TransformPosition(local, boxToWorld));
        }
    }

    private static void AppendSingle(
        AnimatedModelInstance instance, PlayerPoseLayers layers, string? visualId, PlayerBodyNode node,
        Matrix4 modelMatrix, List<Vector3> corners)
    {
        if (ArmourMeshCatalog.Resolve(visualId) is { } model &&
            layers.TryGetNodeMatrix(instance, node, out Matrix4 nodeMatrix))
        {
            AppendModelCorners(model, nodeMatrix * modelMatrix, corners);
        }
    }

    private static void AppendPair(
        AnimatedModelInstance instance, PlayerPoseLayers layers, string? visualId,
        PlayerBodyNode left, PlayerBodyNode right, float offsetX, Matrix4 modelMatrix, List<Vector3> corners)
    {
        if (ArmourMeshCatalog.Resolve(visualId) is not { } model)
        {
            return;
        }

        if (layers.TryGetNodeMatrix(instance, left, out Matrix4 leftMatrix))
        {
            AppendModelCorners(model, CreateSideAttach(+1f, offsetX) * leftMatrix * modelMatrix, corners);
        }

        if (layers.TryGetNodeMatrix(instance, right, out Matrix4 rightMatrix))
        {
            AppendModelCorners(model, CreateSideAttach(-1f, offsetX) * rightMatrix * modelMatrix, corners);
        }
    }

    private void DrawModel(
        Shader shader,
        Camera camera,
        Matrix4 projection,
        ArmourModel model,
        Matrix4 attach,
        ref bool prepared)
    {
        if (!prepared)
        {
            PrepareState(shader, camera, projection);
            prepared = true;
        }

        _cube ??= new StaticMeshBuffer(TerrainFallbackCubeMesh.Create());
        ArmourPart[] parts = model.PartArray;
        ArmourMaterial lastMaterial = (ArmourMaterial)(-1);
        for (int i = 0; i < parts.Length; i++)
        {
            ArmourPart part = parts[i];
            if (part.Material != lastMaterial)
            {
                lastMaterial = part.Material;
                shader.SetVector4("material.baseColorFactor", ArmourMeshCatalog.BaseColor(part.Material));
                shader.SetEmissiveFactor(ArmourMeshCatalog.Emissive(part.Material));
            }

            shader.SetModelMatrix(part.ToMatrix() * attach);
            _cube.DrawBatched();
        }
    }

    // The same frame state as HeldWeaponRenderer, duplicated on purpose so the proven held path stays
    // untouched.
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
