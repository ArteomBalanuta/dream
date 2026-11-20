using Dream.Client.Hover;
using Dream.Client.Hover.Providers;
using Dream.Client.Hover.Sources;
using Dream.Client.Navigation;
using Dream.Client.Presentation;
using Dream.Rendering.Core;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Enemies;
using OpenTK.Mathematics;

namespace Dream.Client.Scenes;

// The world hover: what the pointer rests on (enemy, NPC, ground item, remote player), as one answer per
// update that the cursor, the cursor aim, the right click and the zoom anchor all read, plus the info panel the
// classic HUD draws for it. Built lazily; rebuilt when the session's enemy registry is replaced.
public partial class PlayScene
{
    private WorldHover? _worldHover;
    private EnemyRegistry? _hoverEnemies;
    private EnemyHoverSource? _enemyHoverSource;
    private RemotePlayerHoverSource? _remotePlayerHoverSource;
    private HoverInfoComposer? _hoverComposer;
    private NpcHoverInfoProvider? _npcHoverInfo;
    private GroundItemHoverInfoProvider? _groundHoverInfo;
    private RemotePlayerHoverInfoProvider? _remotePlayerHoverInfo;
    private ClickMoveHost? _hoverHost;
    private bool _hoverLeftWasDown;
    private bool _hoverRightWasDown;

    // This update's hover answer (default before the first, and while loading).
    private HoverFrame _hover;

    private void EnsureWorldHover()
    {
        EnemyRegistry enemies = _runtime.Enemies;
        if (_worldHover is not null && ReferenceEquals(enemies, _hoverEnemies))
        {
            return;
        }

        _hoverEnemies = enemies;
        _enemyHoverSource = new EnemyHoverSource(enemies, _scenePresenter);
        _remotePlayerHoverSource = new RemotePlayerHoverSource();
        _worldHover = new WorldHover(
        [
            _enemyHoverSource,
            _remotePlayerHoverSource,
            new NpcHoverSource(_npcs),
            new GroundItemHoverSource(_groundItems)
        ]);
        _npcHoverInfo = new NpcHoverInfoProvider(_npcs);
        _groundHoverInfo = new GroundItemHoverInfoProvider(_groundItems);
        _remotePlayerHoverInfo = new RemotePlayerHoverInfoProvider();
        _hoverComposer = new HoverInfoComposer(
        [
            new EnemyHoverInfoProvider(enemies),
            _remotePlayerHoverInfo,
            _npcHoverInfo,
            _groundHoverInfo
        ]);
        _hover = default;
    }

    // Runs once per update, before the cursor aim and click-to-move read the answer. The pick itself is
    // throttled inside WorldHover (a still pointer over a still camera re-picks about ten times a second).
    private void UpdateWorldHover(in WorldPointerState pointer, bool allowGameplayInput, bool pointerFocused, float delta)
    {
        EnsureWorldHover();
        _enemyHoverSource!.RenderTick = RemotePlayerRenderTick;

        // A press this frame must see a fresh pick, so what the click hits is what the cursor showed.
        bool forcePick = (pointer.LeftDown && !_hoverLeftWasDown) || (pointer.RightDown && !_hoverRightWasDown);
        _hoverLeftWasDown = pointer.LeftDown;
        _hoverRightWasDown = pointer.RightDown;

        bool gated = !allowGameplayInput
            || !pointerFocused
            || _render.CameraDragging
            || InventoryUiState.Current.ModalOpen
            || InventoryUiState.Current.DragActive;
        // Deliberate: with an item menu open or an item dragged the cursor shows no hover, so a right press in
        // the world is a ground pick (it never attacks an enemy the cursor did not show).
        HoverTargetId label = ToHoverTarget(_render.ClassicHoveredLabel);
        Dream.Simulation.Players.Camera? camera = _render.LastWorldCamera;
        _hoverHost ??= new ClickMoveHost(this);
        INavWorld? world = _hoverHost.World;
        HoverUpdate input = new(
            delta,
            gated,
            pointer.OverHud,
            label,
            world,
            camera is not null,
            camera?.GetViewMatrix() ?? Matrix4.Identity,
            _render.ProjectionMatrix,
            pointer.Position,
            _render.ViewportSize,
            forcePick);
        _hover = _worldHover!.Update(in input);
        PublishHover();
    }

    // Loading or a closed scene: drop the hover at once.
    private void ClearWorldHover()
    {
        _worldHover?.Clear();
        _hover = default;
        PublishHover();
    }

    private void PublishHover()
    {
        _render.HoverCursorKind = _hover.Current.Kind;
        _render.PointerOverEnemy = _hover.Current.Kind == HoverKind.Enemy;
    }

    // Before the HUD pass: fills the info panel for the debounced panel target from live state.
    private void ComposeHoverPanel(IReadOnlyList<RemotePlayerPresentationState> remotePlayers)
    {
        if (_hoverComposer is null)
        {
            _render.HoverPanel = default;
            _render.HoverInfo = null;
            return;
        }

        _remotePlayerHoverSource!.Players = remotePlayers;
        _remotePlayerHoverInfo!.Players = remotePlayers;
        Vector3 player = _runtime.Player.GetWorldPosition();
        _npcHoverInfo!.PlayerPosition = player;
        _groundHoverInfo!.PlayerPosition = player;
        _groundHoverInfo.LocalActorId = LocalActorId;
        _groundHoverInfo.ServerTick = EstimatedServerTick;

        HoverTargetId target = _hover.PanelTarget;
        if (target.IsNone)
        {
            _hoverComposer.Compose(HoverTargetId.None);
            _render.HoverPanel = default;
            _render.HoverInfo = null;
            return;
        }

        // Gone with nothing kept to show (it vanished before the panel ever described it): no panel, rather
        // than an empty frame for the defeated hold.
        if (!_hoverComposer.Compose(target) && _hoverComposer.Info.Target.IsNone)
        {
            _render.HoverPanel = default;
            _render.HoverInfo = null;
            return;
        }

        _render.HoverPanel = new HoverPanelState(_hover.PanelVisible, _hover.PanelAlpha, target.Kind);
        _render.HoverInfo = _hoverComposer.Info;
    }

    // The enemy the hover shows, if it is still the registry's live actor.
    private bool TryGetHoveredEnemy(out EnemyActor enemy)
    {
        enemy = null!;
        if (_hover.Current.Kind != HoverKind.Enemy || _hover.Current.Id is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        if (!_runtime.Enemies.TryGetEnemyByActorId((int)_hover.Current.Id, out EnemyActor? actor) || actor is null || !actor.IsActive)
        {
            return false;
        }

        enemy = actor;
        return true;
    }

    private static HoverTargetId ToHoverTarget(HudLabelClick click) => click.Kind switch
    {
        HudLabelClickKind.PickUpGround => new HoverTargetId(HoverKind.GroundItem, click.Id),
        HudLabelClickKind.InteractNpc => new HoverTargetId(HoverKind.Npc, click.Id),
        _ => HoverTargetId.None
    };
}
