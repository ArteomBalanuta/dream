using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Input;
using Dream.Client.Presentation;
using Dream.Rendering.Core;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;

namespace Dream.Tests.Client.Input;

// Why the cursor aim takes an aim point instead of always cutting the cursor ray with the player's
// ground plane. The camera here is the real one (IsometricView's own angles and distance, not copies),
// and the lateral miss is measured exactly as AuthoritativeCombatResolver's flat branch measures it,
// against EnemyRegistry.DefaultHitRadiusMetres - so these tests state the bug in the terms that decide
// a hit rather than in degrees of yaw. The resolver also bounds the height difference separately
// (MaxMeleeHeightDifferenceMetres), which is not modelled here: these tests are about the facing.
public sealed class CursorAimParallaxTests
{
    private static readonly Vector3 Body = new(10f, 1.5f, 10f);

    // The isometric camera looks from target + offset back at the target, so the view direction is the
    // offset negated.
    private static Vector3 ViewDirection()
    {
        float pitch = MathHelper.DegreesToRadians(IsometricView.PitchDegrees);
        float yaw = MathHelper.DegreesToRadians(IsometricView.YawDegrees);
        Vector3 offset = new(
            MathF.Cos(pitch) * MathF.Cos(yaw),
            MathF.Sin(pitch),
            MathF.Cos(pitch) * MathF.Sin(yaw));
        return -offset.Normalized();
    }

    // A host whose cursor ray is the ray that really passes through the given world point: that is the
    // ray the cursor casts when the player points at an enemy's body.
    private sealed class SpriteHost : ICursorAimHost
    {
        public SpriteHost(Vector3 pointedAt)
        {
            PointedAt = pointedAt;
        }

        public Vector3 PointedAt { get; }

        public Vector3 BodyCentre => Body;

        public PlayerInteractionMode Mode { get; set; } = PlayerInteractionMode.Combat;

        public bool TryBuildRay(Vector2 screenPosition, out Vector3 origin, out Vector3 direction)
        {
            direction = ViewDirection();
            // Anywhere up the same ray is the same ray; the camera's own distance is where it sits.
            origin = PointedAt - (direction * IsometricView.Distance);
            return true;
        }
    }

    // The resolver's own measurement: a flat swing from the body along the facing, and how far the
    // enemy's anchor sits off that line on the ground plane.
    private static float LateralMiss(Vector3 facing, Vector3 anchor)
    {
        float dx = anchor.X - Body.X;
        float dz = anchor.Z - Body.Z;
        float projected = (dx * facing.X) + (dz * facing.Z);
        float lx = dx - (facing.X * projected);
        float lz = dz - (facing.Z * projected);
        return MathF.Sqrt((lx * lx) + (lz * lz));
    }

    // An enemy 1.5 m away - the Risen Skeleton's stand-off - with its combat anchor the given height
    // above the player's body plane, in the WORST orientation: the camera's ground direction lies across
    // the line of attack, so all of the parallax becomes lateral miss. Along it, none would.
    //
    // anchorAboveBody is NOT a ground rise. An enemy on level ground already has its anchor
    // WolfDefinition.CombatAnchorHeight up, so FlatGroundAnchor is the zero case and anything more is
    // that plus the rise.
    private static Vector3 RaisedEnemy(float anchorAboveBody)
    {
        Vector3 ground = ViewDirection();
        Vector2 cameraGround = new Vector2(ground.X, ground.Z).Normalized();
        Vector2 across = new(cameraGround.Y, -cameraGround.X);
        return new Vector3(
            Body.X + (across.X * 1.5f),
            Body.Y + anchorAboveBody,
            Body.Z + (across.Y * 1.5f));
    }

    // What an enemy standing on the same floor as the player really gives: its feet at the player's own
    // height, its anchor 0.75 m up. Derived, because this is the number the whole argument turns on.
    private const float FlatGroundAnchor = WolfDefinition.CombatAnchorHeight;

    private static WorldPointerState Pointer() => new(new Vector2(640f, 360f), true, false, false);

    private static void Frame(CursorAimController aim, Vector3? aimPoint = null) =>
        aim.OnFrame(Pointer(), true, false, true, aimPoint);

    [Fact]
    public void GivenTheEnemysAnchor_TheFacingIsExact_AtAnyHeight()
    {
        foreach (float anchorAboveBody in new[] { 0f, FlatGroundAnchor, 1.4f, 3f })
        {
            Vector3 enemy = RaisedEnemy(anchorAboveBody);
            CursorAimController aim = new(new SpriteHost(enemy));

            Frame(aim, enemy);

            Assert.True(aim.AimedAtTarget);
            Assert.True(aim.TryGetFacing(out Vector3 facing));
            Assert.Equal(0f, LateralMiss(facing, enemy), 4);
        }
    }

    // The bug, in the terms that decide a hit: with no aim point the facing comes from the plane cut,
    // and with the anchor 1.4 m above the player's plane - a one-block step, 0.75 m of anchor plus about
    // 0.65 m of ground - the enemy sits further off the swing than the swing's own radius. Every normal
    // attack misses, silently, while a wide-arc skill still connects.
    [Fact]
    public void WithNoAimPoint_ARaisedEnemyIsMissedByMoreThanTheSwingRadius()
    {
        Vector3 enemy = RaisedEnemy(1.4f);
        CursorAimController aim = new(new SpriteHost(enemy));

        Frame(aim);

        Assert.False(aim.AimedAtTarget);
        Assert.True(aim.TryGetFacing(out Vector3 facing));
        float miss = LateralMiss(facing, enemy);
        Assert.True(
            miss > EnemyRegistry.DefaultHitRadiusMetres,
            $"expected the plane cut to miss by more than {EnemyRegistry.DefaultHitRadiusMetres} m, got {miss:0.000} m");
        // h / tan(pitch) along the camera's ground direction, foreshortened by the 1.5 m range. The band
        // is deliberately narrow: it is the camera's geometry, so a pitch change must break this test
        // rather than quietly shrink the bug it describes - one degree either way leaves it. It pins the
        // PITCH only: RaisedEnemy places the enemy relative to the camera's own ground direction, so the
        // figure is the same at every yaw and a yaw change cannot break this.
        Assert.InRange(miss, 0.92f, 0.93f);

        // Same geometry, aim point given: the miss goes away. This is the whole fix.
        Frame(aim, enemy);
        Assert.True(aim.TryGetFacing(out Vector3 fixedFacing));
        Assert.Equal(0f, LateralMiss(fixedFacing, enemy), 4);
    }

    // The plane cut is exact for a point at the player's own height, and it must stay the fallback: a
    // cursor on open ground has no enemy to aim at, and that is how the character is steered.
    [Fact]
    public void ForAPointAtThePlayersOwnHeight_ThePlaneCutIsUsed_AndIsExact()
    {
        Vector3 level = RaisedEnemy(0f);
        CursorAimController aim = new(new SpriteHost(level));

        Frame(aim);

        Assert.False(aim.AimedAtTarget);
        Assert.True(aim.OwnsFacing);
        Assert.True(aim.TryGetFacing(out Vector3 facing));
        Assert.Equal(0f, LateralMiss(facing, level), 4);
    }

    // And why this is not only a slope fix. An enemy on the SAME floor still has its anchor 0.75 m up,
    // so the plane cut misses it by 0.58 m - 64% of the 0.9 m the swing allows - leaving almost no margin
    // for the enemy stepping sideways or the player walking. With the anchor, nothing is spent.
    [Fact]
    public void EvenOnLevelGround_ThePlaneCutSpendsMostOfTheSwingsTolerance()
    {
        Vector3 enemy = RaisedEnemy(FlatGroundAnchor);
        CursorAimController aim = new(new SpriteHost(enemy));

        Frame(aim);
        Assert.True(aim.TryGetFacing(out Vector3 cut));
        float spent = LateralMiss(cut, enemy);
        Assert.InRange(spent, 0.57f, 0.59f);
        Assert.True(
            spent > EnemyRegistry.DefaultHitRadiusMetres * 0.6f,
            $"expected the level-ground cut to spend over 60% of the tolerance, spent {spent:0.000} m");

        Frame(aim, enemy);
        Assert.True(aim.TryGetFacing(out Vector3 aimed));
        Assert.Equal(0f, LateralMiss(aimed, enemy), 4);
    }

    // Building is left exactly as it was: there the left button places a block instead of swinging, so
    // bending the facing onto an enemy would change placement for no gain.
    [Fact]
    public void InBuildingMode_TheAimPointIsIgnored_AndThePlaneCutStands()
    {
        Vector3 enemy = RaisedEnemy(1.4f);
        SpriteHost host = new(enemy) { Mode = PlayerInteractionMode.Building };
        CursorAimController aim = new(host);

        Frame(aim, enemy);

        Assert.False(aim.AimedAtTarget);
        Assert.True(aim.TryGetFacing(out Vector3 facing));
        Assert.True(LateralMiss(facing, enemy) > EnemyRegistry.DefaultHitRadiusMetres);

        // The same host in Combat does take it, so the gate is the mode and nothing else.
        host.Mode = PlayerInteractionMode.Combat;
        Frame(aim, enemy);
        Assert.True(aim.AimedAtTarget);
    }

    // An enemy almost on top of the player: its anchor is inside MinAimMetres, where a facing would be
    // noise. Taking it would leave OwnsFacing true with no facing to give, which freezes the character's
    // turn - so the aim point is declined and the plane cut, which always lands further out, is used.
    [Fact]
    public void AnAnchorInsideTheMinimumAimDistance_FallsThroughToThePlaneCut()
    {
        Vector3 almostOnTop = new(Body.X + 0.05f, Body.Y + FlatGroundAnchor, Body.Z + 0.05f);
        CursorAimController aim = new(new SpriteHost(almostOnTop));

        Frame(aim, almostOnTop);

        Assert.False(aim.AimedAtTarget);
        Assert.True(aim.OwnsFacing);
        // The cut is 0.75 / tan(pitch) out along the camera's ground direction, so there is a facing.
        Assert.True(aim.TryGetFacing(out Vector3 facing));
        Assert.Equal(1f, facing.Length, 4);
    }

    // Suppression and a dead player come first: an aim point must not sneak a facing past either gate.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void WhenTheAimIsGatedOff_AnAimPointChangesNothing(bool allowGameplayInput, bool suppressed)
    {
        Vector3 enemy = RaisedEnemy(1.4f);
        CursorAimController aim = new(new SpriteHost(enemy));

        aim.OnFrame(Pointer(), allowGameplayInput, suppressed, true, enemy);

        Assert.False(aim.OwnsFacing);
        Assert.False(aim.AimedAtTarget);
        Assert.False(aim.TryGetFacing(out _));
    }
}
