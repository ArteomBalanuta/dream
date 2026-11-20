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

    // An enemy 1.5 m away - the Risen Skeleton's stand-off - on a rise, in the WORST orientation: the
    // camera's ground direction lies across the line of attack, so all of the parallax becomes lateral
    // miss. Straight along the line of attack none of it would.
    private static Vector3 RaisedEnemy(float rise)
    {
        Vector3 ground = ViewDirection();
        Vector2 cameraGround = new Vector2(ground.X, ground.Z).Normalized();
        Vector2 across = new(cameraGround.Y, -cameraGround.X);
        return new Vector3(Body.X + (across.X * 1.5f), Body.Y + rise, Body.Z + (across.Y * 1.5f));
    }

    private static WorldPointerState Pointer() => new(new Vector2(640f, 360f), true, false, false);

    private static void Frame(CursorAimController aim, Vector3? aimPoint = null) =>
        aim.OnFrame(Pointer(), true, false, true, aimPoint);

    [Fact]
    public void GivenTheEnemysAnchor_TheFacingIsExact_AtAnyHeight()
    {
        foreach (float rise in new[] { 0f, 0.5f, 1.4f, 3f })
        {
            Vector3 enemy = RaisedEnemy(rise);
            CursorAimController aim = new(new SpriteHost(enemy));

            Frame(aim, enemy);

            Assert.True(aim.AimedAtTarget);
            Assert.True(aim.TryGetFacing(out Vector3 facing));
            Assert.Equal(0f, LateralMiss(facing, enemy), 4);
        }
    }

    // The bug, in the terms that decide a hit: with no aim point the facing comes from the plane cut,
    // and at a 1.4 m rise the enemy sits further off the swing than the swing's own radius - so every
    // normal attack misses, silently, while a wide-arc skill still connects.
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
        // rise / tan(pitch) along the camera's ground direction, foreshortened by the 1.5 m range. The
        // band is deliberately narrow: it is the camera's geometry, so a pitch change must break this
        // test rather than quietly shrink the bug it describes - one degree either way leaves it. It
        // pins the PITCH only: RaisedEnemy places the enemy relative to the camera's own ground
        // direction, so the figure is the same at every yaw and a yaw change cannot break this. Note how
        // thin the real margin is - 1.4 m of rise clears the 0.9 m radius by under 3%, which is why the
        // bug read as "sometimes it just does nothing" rather than as a clean dead zone.
        Assert.InRange(miss, 0.92f, 0.93f);

        // Same geometry, aim point given: the miss goes away. This is the whole fix.
        Frame(aim, enemy);
        Assert.True(aim.TryGetFacing(out Vector3 fixedFacing));
        Assert.Equal(0f, LateralMiss(fixedFacing, enemy), 4);
    }

    // On the flat the plane cut was always right, and it must stay the fallback: a cursor on open ground
    // has no enemy to aim at, and that is how the character is steered.
    [Fact]
    public void OnTheFlat_ThePlaneCutIsStillUsed_AndIsStillExact()
    {
        Vector3 enemy = RaisedEnemy(0f);
        CursorAimController aim = new(new SpriteHost(enemy));

        Frame(aim);

        Assert.False(aim.AimedAtTarget);
        Assert.True(aim.OwnsFacing);
        Assert.True(aim.TryGetFacing(out Vector3 facing));
        Assert.Equal(0f, LateralMiss(facing, enemy), 4);
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
        Vector3 almostOnTop = new(Body.X + 0.05f, Body.Y + 1.4f, Body.Z + 0.05f);
        CursorAimController aim = new(new SpriteHost(almostOnTop));

        Frame(aim, almostOnTop);

        Assert.False(aim.AimedAtTarget);
        Assert.True(aim.OwnsFacing);
        // The cut is 1.4 / tan(pitch) out along the camera's ground direction, so there is a facing.
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
