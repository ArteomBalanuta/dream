using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Input;
using Dream.Client.Navigation;
using Dream.Rendering.Core;
using Dream.Simulation.Players;

namespace Dream.Tests.Client.Input;

// The cursor's sword must show exactly when a right click at the same spot would start an attack: the host's
// enemy answer (what the scene serves from the world hover) and the click controller agree in every mode.
public class EnemyHoverClickAgreementTests
{
    private sealed class Nav : INavWorld
    {
        public int Width => 64;

        public int Height => 16;

        public int Depth => 64;

        public bool IsBlocked(int x, int y, int z) => y == 0;

        public bool IsSupport(int x, int y, int z) => y == 0;
    }

    private sealed class Target : IClickTarget
    {
        public bool IsAlive { get; set; } = true;

        public Vector3 Anchor { get; set; } = new(9.5f, 1.5f, 9.5f);
    }

    private sealed class Host : IClickMoveHost
    {
        public INavWorld? World => new Nav();

        public Vector3 BodyCentre => new(5.5f, 1.5f, 5.5f);

        public Vector3 Eye => new(5.5f, 2f, 5.5f);

        public bool NoclipActive => false;

        public PlayerInteractionMode Mode { get; set; }

        public Vector3 Facing => Vector3.UnitX;

        public bool AimOwnsFacing => false;

        public ClickPick? NextPick { get; set; }

        public bool TryPick(Vector2 screenPosition, out ClickPick pick)
        {
            pick = NextPick ?? default;
            return NextPick.HasValue;
        }

        public void SetFacing(Vector3 direction) { }
    }

    private sealed class Pathing : IClickPathing
    {
        public bool HasPath { get; private set; }

        public int LastPathCells => 1;

        public bool GaveUp => false;

        public bool Plan(INavWorld world, NavCell start, NavCell goal, bool viaStartCentre, out bool reachedGoal)
        {
            HasPath = true;
            reachedGoal = true;
            return true;
        }

        public void Clear() => HasPath = false;

        public NavStep Step(Vector3 bodyCentre, INavWorld world) => new(Vector3.UnitX, false, false, false);
    }

    private static readonly Vector2 Spot = new(400f, 300f);

    public static IEnumerable<object[]> Cases()
    {
        foreach (PlayerInteractionMode mode in new[] { PlayerInteractionMode.Combat, PlayerInteractionMode.Building })
        {
            yield return [mode, "live enemy"];
            yield return [mode, "dead enemy"];
            yield return [mode, "ground"];
            yield return [mode, "nothing"];
        }
    }

    private static ClickPick? PickFor(string what) => what switch
    {
        "live enemy" => new ClickPick(ClickPickKind.Enemy, default, new Vector3(9.5f, 1.5f, 9.5f), new Target()),
        "dead enemy" => new ClickPick(ClickPickKind.Enemy, default, new Vector3(9.5f, 1.5f, 9.5f), new Target { IsAlive = false }),
        "ground" => new ClickPick(ClickPickKind.Ground, new NavCell(8, 1, 8), new Vector3(8.5f, 1f, 8.5f), null),
        _ => null
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void HoverAnswer_MatchesWhetherARightClickAttacks(PlayerInteractionMode mode, string what)
    {
        Host host = new() { Mode = mode, NextPick = PickFor(what) };
        bool hover = ((IClickMoveHost)host).TryGetEnemyAnchorAt(Spot, out _);

        ClickMoveController click = new(host, new Pathing());
        click.OnFrame(new WorldPointerState(Spot, false, false, false), true);
        click.OnFrame(new WorldPointerState(Spot, false, true, false), true);
        bool attacks = click.Goal == ClickGoalKind.Attack;

        Assert.Equal(what == "live enemy", hover);
        Assert.Equal(attacks, hover);
    }
}
