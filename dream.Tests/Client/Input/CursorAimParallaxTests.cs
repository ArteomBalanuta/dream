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
    // above the player's body plane, at +X.
    //
    // +X is not arbitrary and it is not the worst case: it is where DevSpawn puts the first enemy
    // ("the first one stands at +X so a single spawn is predictable"), so these figures are directly
    // comparable with the capture that measured the real thing. There IS no single worst bearing - the
    // lateral miss peaks across the camera's ground direction while the displacement is small (0.21 m at
    // the level-ground height) and swings round to +X once it is large (1.05 m at a one-block step),
    // because a displacement comparable to the range turns the facing much further. Assuming one fixed
    // worst orientation is a mistake this file made once.
    //
    // anchorAboveBody is NOT a ground rise; see LevelGroundAnchor.
    private static Vector3 RaisedEnemy(float anchorAboveBody)
    {
        return new Vector3(Body.X + 1.5f, Body.Y + anchorAboveBody, Body.Z);
    }

    // How high an enemy's combat anchor really stands above the player's body plane on level ground.
    //
    // This is MEASURED, from the aimcheck line of artifacts/captures/aim-flat, and it is deliberately
    // not derived from WolfDefinition.CombatAnchorHeight (0.75 m): that constant is measured from the
    // ENEMY's own position, and an enemy's body centre sits about half a metre below the player's, so
    // using it here overstates the error by 3.6x. Deriving it from the wrong origin is the mistake this
    // file exists to stop repeating. The same capture with a block built under the enemy gives the step
    // figure below.
    private const float LevelGroundAnchor = 0.25f;

    // The same enemy standing on one built block, from artifacts/captures/aim-step.
    private const float OneBlockStepAnchor = LevelGroundAnchor + 1f;

    private static WorldPointerState Pointer() => new(new Vector2(640f, 360f), true, false, false);

    private static void Frame(CursorAimController aim, Vector3? aimPoint = null) =>
        aim.OnFrame(Pointer(), true, false, true, aimPoint);

    [Fact]
    public void GivenTheEnemysAnchor_TheFacingIsExact_AtAnyHeight()
    {
        foreach (float anchorAboveBody in new[] { 0f, LevelGroundAnchor, OneBlockStepAnchor, 3f })
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
    // and with the anchor 1.4 m above the player's plane - a little over the one-block step the engine
    // measured at 1.25 m - the enemy sits further off the swing than the swing's own radius. Every
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
        // h / tan(pitch) along the camera's ground direction, turned into a miss by the 1.5 m range. The
        // band is deliberately narrow: it is the camera's geometry, so a change to either angle must
        // break this test rather than quietly shrink the bug it describes.
        Assert.InRange(miss, 1.16f, 1.18f);

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

    // Where the line actually falls, in the two cases the engine was made to measure: level ground is
    // safe under the old plane cut, and one block of rise is not. This is the test that says what the fix
    // is FOR - without it the file would only prove that a 1.4 m contrivance misses.
    [Fact]
    public void LevelGroundStillHitsUnderThePlaneCut_AndAOneBlockStepDoesNot()
    {
        Vector3 level = RaisedEnemy(LevelGroundAnchor);
        CursorAimController levelAim = new(new SpriteHost(level));
        Frame(levelAim);
        Assert.True(levelAim.TryGetFacing(out Vector3 levelCut));
        float levelMiss = LateralMiss(levelCut, level);
        Assert.True(
            levelMiss < EnemyRegistry.DefaultHitRadiusMetres,
            $"level ground must still hit under the old aim, missed by {levelMiss:0.000} m");

        Vector3 stepped = RaisedEnemy(OneBlockStepAnchor);
        CursorAimController stepAim = new(new SpriteHost(stepped));
        Frame(stepAim);
        Assert.True(stepAim.TryGetFacing(out Vector3 stepCut));
        float stepMiss = LateralMiss(stepCut, stepped);
        Assert.True(
            stepMiss > EnemyRegistry.DefaultHitRadiusMetres,
            $"one block of rise must miss under the old aim, missed by only {stepMiss:0.000} m");
        // The engine measured 1.022 m for this case and this geometry gives 1.049 m: the difference is
        // that the resolver swings from the eye, 0.35 m above the plane the cut is taken on. Pinned so
        // the pair cannot drift apart silently - if this moves, re-run artifacts/captures/aim-step.
        Assert.InRange(stepMiss, 1.03f, 1.06f);

        // And the aim point removes the miss in both, which is the whole fix.
        Frame(levelAim, level);
        Assert.True(levelAim.TryGetFacing(out Vector3 levelAimed));
        Assert.Equal(0f, LateralMiss(levelAimed, level), 4);
        Frame(stepAim, stepped);
        Assert.True(stepAim.TryGetFacing(out Vector3 stepAimed));
        Assert.Equal(0f, LateralMiss(stepAimed, stepped), 4);
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
        Vector3 almostOnTop = new(Body.X + 0.05f, Body.Y + OneBlockStepAnchor, Body.Z + 0.05f);
        CursorAimController aim = new(new SpriteHost(almostOnTop));

        Frame(aim, almostOnTop);

        Assert.False(aim.AimedAtTarget);
        Assert.True(aim.OwnsFacing);
        // The cut is 1.25 / tan(pitch) out along the camera's ground direction, so there is a facing.
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
