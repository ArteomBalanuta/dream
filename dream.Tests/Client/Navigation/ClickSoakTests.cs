using System.Text;
using Dream.Application;
using Dream.Client.Input;
using Dream.Client.Navigation;
using Dream.Client.Presentation;
using Dream.Client.World;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Core;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Load;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Client.Navigation;

// Clicks on the real generated terrain with the real controller, pathfinder, follower and Bullet
// player body. Each click starts where the last one ended, so failures carry over as a human would
// meet them. DREAM_LOAD_TEST=1 runs the long version; DREAM_CLICK_SOAK_REPORT=<file> keeps the rows.
public class ClickSoakTests(ITestOutputHelper output)
{
    private static readonly PlayerSimulationTuning Tuning = new(
        EyeHeightOffset: 0.35f,
        MouseSensitivity: 2.5f,
        MoveSpeed: 6f,
        NoclipSpeed: 150f,
        JumpVelocity: 5f);

    private enum Target
    {
        Ground,
        NextToWall,
        Gap,
        BlockTop,
        High,
        PastEdge,
        NearCube,
        NearPushable,
        NearRubble
    }

    private enum Outcome
    {
        Arrived,
        NearestSpot,
        NoPick,
        ClickIgnored,
        GaveUp,
        WrongStop,
        Timeout
    }

    private sealed class Host(ClientWorldSession session) : IClickMoveHost
    {
        private readonly MapNavWorld _nav = new(session.Map);

        public Vector3 Aim { get; set; }

        public INavWorld? World => _nav;

        public Vector3 BodyCentre => session.Player.GetWorldPosition();

        public Vector3 Eye => session.Player.Camera.Position;

        public bool NoclipActive => false;

        public PlayerInteractionMode Mode => PlayerInteractionMode.Building;

        public bool TryPick(Vector2 screenPosition, out ClickPick pick)
        {
            Vector3 direction = -IsometricView.FocusToCamera.Normalized();
            pick = WorldCursorPicker.Pick(_nav, Aim - (direction * 40f), direction, null);
            return pick.Kind != ClickPickKind.None;
        }

        public void SetFacing(Vector3 direction) => session.Player.SetFacing(direction);
    }

    private sealed class Probe(INavWorld world) : IClickPathing
    {
        private readonly NavPathing _inner = new();
        private readonly GridPathfinder _finder = new();
        private readonly List<NavCell> _cells = [];

        public bool HasPath => _inner.HasPath;

        public bool GaveUp => _inner.GaveUp;

        public int LastPathCells => _inner.LastPathCells;

        public int Plans { get; set; }

        public int Replans { get; set; }

        public bool SawGiveUp { get; set; }

        public bool PlanFailed { get; set; }

        public bool ReachesGoal { get; private set; }

        public NavCell PathEnd { get; private set; }

        public float PathMetres { get; private set; }

        public Vector3 LastDirection { get; private set; }

        public string FirstCells => string.Join(' ', _cells.Take(4).Select(c => $"{c.X},{c.Level},{c.Z}"));

        public bool Plan(INavWorld w, NavCell start, NavCell goal, bool viaStartCentre, out bool reachedGoal)
        {
            bool ok = _inner.Plan(w, start, goal, viaStartCentre, out reachedGoal);
            Plans++;
            Replans += viaStartCentre ? 1 : 0;
            PlanFailed |= !ok;
            if (ok && _finder.TryFindPath(world, start, goal, _cells, out bool reached))
            {
                ReachesGoal = reached;
                PathEnd = _cells[^1];
                float metres = 0f;
                for (int i = 1; i < _cells.Count; i++)
                {
                    metres += new Vector2(_cells[i].X - _cells[i - 1].X, _cells[i].Z - _cells[i - 1].Z).Length;
                }

                PathMetres = viaStartCentre ? PathMetres : metres;
            }

            return ok;
        }

        public void Clear() => _inner.Clear();

        public NavStep Step(Vector3 bodyCentre, INavWorld w)
        {
            NavStep step = _inner.Step(bodyCentre, w);
            SawGiveUp |= _inner.GaveUp;
            LastDirection = step.Direction == Vector3.Zero ? LastDirection : step.Direction;
            return step;
        }
    }

    private static PlayerCommandFrame Frame(ulong tick) =>
        new(new PlayerSessionId("soak"), tick, PlayerModeSwitchIntent.None, new PlayerMoveIntent(0f, 0f), PlayerLookIntent.None, false);

    private static float Horizontal(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length;

    private static List<Vector3> Candidates(Map map, MapNavWorld nav, Target kind)
    {
        List<Vector3> found = [];
        for (int x = 0; x < map.Width; x++)
        {
            for (int z = 0; z < map.Depth; z++)
            {
                for (int y = 1; y < Math.Min(map.Height, 12); y++)
                {
                    bool standable = GridPathfinder.IsStandable(nav, x, y, z);
                    bool topOfBlocked = nav.IsBlocked(x, y, z) && !nav.IsBlocked(x, y + 1, z);
                    int sides = (nav.IsBlocked(x - 1, y, z) ? 1 : 0) + (nav.IsBlocked(x + 1, y, z) ? 1 : 0)
                        + (nav.IsBlocked(x, y, z - 1) ? 1 : 0) + (nav.IsBlocked(x, y, z + 1) ? 1 : 0);
                    bool narrow = (nav.IsBlocked(x - 1, y, z) && nav.IsBlocked(x + 1, y, z))
                        || (nav.IsBlocked(x, y, z - 1) && nav.IsBlocked(x, y, z + 1));
                    bool wanted = kind switch
                    {
                        Target.Ground => standable,
                        Target.NextToWall => standable && sides >= 1,
                        Target.Gap => standable && narrow,
                        Target.BlockTop => topOfBlocked,
                        Target.High => topOfBlocked && y >= 3,
                        _ => false
                    };
                    if (wanted)
                    {
                        found.Add(new Vector3(x + 0.5f, kind is Target.BlockTop or Target.High ? y + 1f : y, z + 0.5f));
                    }
                }
            }
        }

        return found;
    }

    private Vector3 ChooseAim(Target kind, Map map, Random random, Dictionary<Target, List<Vector3>> pool, Vector3 start)
    {
        if (kind == Target.PastEdge)
        {
            return random.Next(4) switch
            {
                0 => new Vector3(-random.Next(1, 8), 1f, random.Next(map.Depth)),
                1 => new Vector3(map.Width + random.Next(1, 8), 1f, random.Next(map.Depth)),
                2 => new Vector3(random.Next(map.Width), 1f, -random.Next(1, 8)),
                _ => new Vector3(random.Next(map.Width), 1f, map.Depth + random.Next(1, 8))
            };
        }

        if (kind is not (Target.NearCube or Target.NearPushable or Target.NearRubble))
        {
            List<Vector3> list = pool[kind];
            return list[random.Next(list.Count)];
        }

        // A cube placed by the target, as a player building would leave one.
        List<Vector3> ground = pool[Target.Ground];
        Vector3 aim = ground[random.Next(ground.Count)];
        int cx = (int)MathF.Floor(aim.X) + random.Next(-2, 3);
        int cz = (int)MathF.Floor(aim.Z) + random.Next(-2, 3);
        if (Horizontal(new Vector3(cx + 0.5f, 0f, cz + 0.5f), start) <= 1.5f)
        {
            return aim;
        }

        if (kind == Target.NearRubble)
        {
            int cy = (int)aim.Y;
            map.RestoreFracturePieces(
                new GeometryCellKey(cx, cy, cz),
                GeometryMaterialId.Stone,
                [
                    new GeometryFracturePiece(new Vector3(cx + 0.25f, cy + 0.25f, cz + 0.25f), 0.5f),
                    new GeometryFracturePiece(new Vector3(cx + 0.75f, cy + 0.25f, cz + 0.75f), 0.5f),
                    new GeometryFracturePiece(new Vector3(cx + 0.75f, cy + 0.15f, cz + 0.25f), 0.3f)
                ]);
        }
        else
        {
            map.TryAddCube(new Vector3(cx, (int)aim.Y, cz), isStatic: kind == Target.NearCube);
        }

        return aim;
    }

    private Dictionary<Outcome, int> Run(int clicks, int seed)
    {
        using ClientWorldSession session = new(WorldDefaults.StartupFloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        Map map = session.Map;
        MapNavWorld nav = new(map);
        Host host = new(session);
        Probe probe = new(nav);
        ClickMoveController controller = new(host, probe);
        Random random = new(seed);
        Target[] kinds = Enum.GetValues<Target>();
        Dictionary<Target, List<Vector3>> pool = kinds.ToDictionary(k => k, k => Candidates(map, nav, k));
        Dictionary<Outcome, int> counts = Enum.GetValues<Outcome>().ToDictionary(o => o, _ => 0);
        Dictionary<(Target, Outcome), int> byKind = [];
        StringBuilder rows = new();
        ulong tick = 0;
        int longWalks = 0;
        WorldPointerState up = new(Vector2.Zero, false, false, false);
        WorldPointerState down = new(Vector2.Zero, true, false, false);

        for (int i = 0; i < 64; i++)
        {
            session.AdvancePredictionTick(++tick, SimulationClock.FixedDeltaSeconds, Frame(tick), Tuning);
        }

        for (int click = 0; click < clicks; click++)
        {
            Target kind = kinds[click % kinds.Length];
            Vector3 start = session.Player.GetWorldPosition();
            Vector3 aim = ChooseAim(kind, map, random, pool, start);
            host.Aim = aim;
            probe.Plans = 0;
            probe.Replans = 0;
            probe.SawGiveUp = false;
            probe.PlanFailed = false;
            controller.OnFrame(in down, true);
            controller.OnFrame(in up, true);
            NavCell goal = controller.GoalCell;
            bool picked = host.TryPick(Vector2.Zero, out _);
            bool started = probe.Plans > 0 && !probe.PlanFailed;
            float walked = 0f;
            int pinned = 0;
            int ticks = 0;
            int limit = 64 * (int)((probe.PathMetres * 0.5f) + 25f);
            Vector3 last = start;
            while (controller.Goal != ClickGoalKind.None && ticks < limit)
            {
                PlayerCommandFrame command = controller.SteerTick(controller.MarkFrame(Frame(++tick)));
                session.AdvancePredictionTick(tick, SimulationClock.FixedDeltaSeconds, command, Tuning);
                controller.OnFrame(in up, true);
                Vector3 now = session.Player.GetWorldPosition();
                float step = Horizontal(now, last);
                walked += step;
                pinned += command.MoveIntent.ForwardAxis != 0f && step < 0.02f ? 1 : 0;
                last = now;
                ticks++;
            }

            Vector3 end = session.Player.GetWorldPosition();
            bool idle = controller.Goal == ClickGoalKind.None;
            Outcome outcome = !idle ? Outcome.Timeout
                : !picked ? Outcome.NoPick
                : !started || probe.PlanFailed ? Outcome.ClickIgnored
                : probe.SawGiveUp ? Outcome.GaveUp
                : Horizontal(end, probe.PathEnd.Centre) >= 0.75f ? Outcome.WrongStop
                : probe.ReachesGoal ? Outcome.Arrived
                : Outcome.NearestSpot;
            counts[outcome]++;
            byKind[(kind, outcome)] = byKind.GetValueOrDefault((kind, outcome)) + 1;
            bool longWalk = walked > (probe.PathMetres * 2f) + 3f;
            longWalks += longWalk ? 1 : 0;
            rows.AppendLine($"{click}\t{kind}\t{outcome}\tstart {start.X:0.0},{start.Y:0.0},{start.Z:0.0}\taim {aim.X:0.0},{aim.Y:0.0},{aim.Z:0.0}\tgoal {goal.X},{goal.Level},{goal.Z}\tpathEnd {probe.PathEnd.X},{probe.PathEnd.Level},{probe.PathEnd.Z}\tend {end.X:0.00},{end.Y:0.00},{end.Z:0.00}\tpath {probe.PathMetres:0.0} walked {walked:0.0} ticks {ticks} pinned {pinned} replans {probe.Replans} dir {probe.LastDirection.X:0.00},{probe.LastDirection.Z:0.00} cells {probe.FirstCells}{(longWalk ? " LONG" : string.Empty)}");
            if (!idle)
            {
                controller.Cancel();
            }

            for (int i = 0; i < 16; i++)
            {
                session.AdvancePredictionTick(++tick, SimulationClock.FixedDeltaSeconds, controller.SteerTick(controller.MarkFrame(Frame(tick))), Tuning);
            }
        }

        WriteSummary(clicks, seed, kinds, counts, byKind, longWalks, rows);
        return counts;
    }

    private void WriteSummary(int clicks, int seed, Target[] kinds, Dictionary<Outcome, int> counts, Dictionary<(Target, Outcome), int> byKind, int longWalks, StringBuilder rows)
    {
        StringBuilder summary = new();
        summary.AppendLine($"clicks {clicks} seed {seed}: " + string.Join(", ", counts.Select(c => $"{c.Key}={c.Value}")) + $", LongWalks={longWalks}");
        foreach (Target kind in kinds)
        {
            summary.AppendLine($"  {kind}: " + string.Join(", ", byKind.Where(p => p.Key.Item1 == kind).Select(p => $"{p.Key.Item2}={p.Value}")));
        }

        output.WriteLine(summary.ToString());
        string? report = Environment.GetEnvironmentVariable("DREAM_CLICK_SOAK_REPORT");
        if (!string.IsNullOrWhiteSpace(report))
        {
            File.AppendAllText(report, summary + rows.ToString());
        }
    }

    private static void AssertClean(Dictionary<Outcome, int> counts)
    {
        Assert.Equal(0, counts[Outcome.Timeout]);
        // A jamb of a one-wide gap can still beat the body. It stops cleanly, and the next click works.
        Assert.InRange(counts[Outcome.GaveUp], 0, counts.Values.Sum() / 200);
        Assert.Equal(0, counts[Outcome.WrongStop]);
        Assert.Equal(0, counts[Outcome.ClickIgnored]);
    }

    [Fact]
    public void ShortSoak_OnTheGeneratedTerrain() => AssertClean(Run(70, 11));

    [LoadFact]
    public void LongSoak_OnTheGeneratedTerrain()
    {
        Dictionary<Outcome, int> total = Enum.GetValues<Outcome>().ToDictionary(o => o, _ => 0);
        foreach (int seed in new[] { 1, 2, 3 })
        {
            foreach ((Outcome outcome, int count) in Run(700, seed))
            {
                total[outcome] += count;
            }
        }

        AssertClean(total);
    }
}
