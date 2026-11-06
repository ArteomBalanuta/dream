using Dream.Client.Replication;
using Dream.Rendering.Actors;
using Dream.Rendering.Hud;
using Dream.Rendering.Shadows;
using Dream.Rendering.Ui;
using Dream.Simulation.Players;
using OpenTK.Mathematics;

namespace Dream.Rendering.Core;

// Ground items and NPCs (M3): the placeholder boxes in the world pass and their clickable labels in the
// classic HUD. The scene calls DrawWorldEntities once a frame; the HUD pass then reads what it left.
public partial class Render
{
    private readonly List<WorldBox> _worldEntityBoxes = new();
    private NpcModelRenderer? _npcModelRenderer;
    private readonly List<WorldEntityLabel> _worldEntityLabels = new();
    private ClientGroundItemCache? _entityGround;
    private ClientNpcCache? _entityNpcs;
    private Vector3 _entityPlayerPosition;
    private int _entityLocalActorId;
    private ulong _entityServerTick;

    // The vendor window's state, drawn by the classic HUD; set with DrawWorldEntities.
    public VendorHudState? Vendor { get; private set; }

    public int LastWorldEntityBoxCount => _worldEntityBoxes.Count;

    // The villagers' models, created on the first frame that draws them (a field initializer
    // cannot see `this`, and Render's constructor does not need one more renderer).
    public NpcModelRenderer Npcs => _npcModelRenderer ??= new NpcModelRenderer(this);

    // Draws the flat boxes of the ground items, the villagers' models, and remembers what the HUD
    // labels need. deltaTime is this frame's dt: each villager's clip advances by it exactly once.
    public void DrawWorldEntities(
        Camera camera,
        ClientGroundItemCache ground,
        ClientNpcCache npcs,
        VendorHudState vendor,
        Vector3 playerPosition,
        int localActorId,
        ulong serverTick,
        float deltaTime)
    {
        _entityGround = ground;
        _entityNpcs = npcs;
        Vendor = vendor;
        _entityPlayerPosition = playerPosition;
        _entityLocalActorId = localActorId;
        _entityServerTick = serverTick;

        Npcs.Draw(camera, npcs, deltaTime);

        _worldEntityBoxes.Clear();
        WorldEntityRenderer.CollectBoxes(ground, npcs, camera.Position, _worldEntityBoxes);
        if (_worldEntityBoxes.Count == 0)
        {
            return;
        }

        Shader shader = _shaders["default"];
        Matrix4 view = camera.GetViewMatrix();
        for (int n = 0; n < _worldEntityBoxes.Count; n++)
        {
            WorldBox box = _worldEntityBoxes[n];
            shader.Use();
            shader.SetVector3("color", box.Color);
            DrawCubeInternal(shader, Matrix4.CreateScale(box.Size) * Matrix4.CreateTranslation(box.Center), view);
        }
    }

    // The ground items' boxes as shadow casters: the boxes DrawWorldEntities collected last frame, drawn depth-only with the
    // fallback cube mesh (one call per box, where the visible pass's debug cube costs six). Returns the boxes drawn.
    internal int DrawShadowGroundItems(in ShadowCasterView view)
    {
        Shader? shader = null;
        int drawn = 0;
        try
        {
            for (int n = 0; n < _worldEntityBoxes.Count; n++)
            {
                WorldBox box = _worldEntityBoxes[n];
                Vector3 half = box.Size * 0.5f;
                if (!view.Accepts(box.Center - half, box.Center + half))
                {
                    continue;
                }

                shader ??= BeginShadowDepthProgram(in view.View, in view.Projection);
                shader.SetModelMatrix(Matrix4.CreateScale(box.Size) * Matrix4.CreateTranslation(box.Center));
                _fallbackCubeBuffer.DrawBatched();
                drawn++;
            }
        }
        finally
        {
            if (shader is not null)
            {
                StaticMeshBuffer.EndBatchedDraws();
            }
        }

        return drawn;
    }

    // Projects the ground-item and NPC labels into the classic HUD's label list.
    private void AppendEconomyLabels(Camera camera)
    {
        if (_entityGround is null || _entityNpcs is null)
        {
            return;
        }

        List<WorldEntityLabel> labels = _worldEntityLabels;
        labels.Clear();
        WorldEntityRenderer.CollectLabels(
            _entityGround, _entityNpcs, _entityPlayerPosition, _entityLocalActorId, _entityServerTick, labels);
        for (int i = 0; i < labels.Count; i++)
        {
            WorldEntityLabel label = labels[i];
            if (TryProjectWorldToScreen(camera, label.WorldAnchor, out System.Numerics.Vector2 screen))
            {
                _classicLabels.Add(new HudWorldLabel(
                    new Vector2(screen.X, screen.Y), label.Name, label.Sub, label.Click, label.Tier, label.Dim));
            }
        }
    }

    // Adds what the classic frame picked in a ground label or the vendor window.
    private HudInteractionRequest MergeEconomyRequest(HudInteractionRequest request)
    {
        HudEconomyRequest picked = _classicSurface?.TakeEconomyRequests() ?? default;
        return picked.IsEmpty
            ? request
            : request with
            {
                GroundPickupId = picked.GroundPickupId,
                InteractActorId = picked.InteractActorId,
                Vendor = picked.Vendor,
                CloseVendor = picked.CloseVendor
            };
    }
}
