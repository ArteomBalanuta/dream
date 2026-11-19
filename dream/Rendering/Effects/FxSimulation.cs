using Dream.Rendering.Actors;
using Dream.Rendering.Lighting;
using Dream.Rendering.Models;
using Dream.Rendering.World;
using Dream.Simulation.Combat;
using OpenTK.Mathematics;

namespace Dream.Rendering.Effects;

// What the simulation needs from the frame. CameraFront is the unit view direction; NightFactor
// is SkyState.StarVisibility (0 at noon, 1 at night).
public readonly record struct FxFrameInputs(
    float DeltaSeconds,
    Vector3 CameraPosition,
    Vector3 CameraFront,
    Vector3 PlayerPosition,
    float NightFactor,
    FxViewBasis Basis = default,
    double ClockSeconds = double.NaN);

// The effects pass's state: a particle pool (hit sparks, death bursts, torch embers, ambient dust)
// and a small list of light halos rebuilt from the frame's point lights. Pure CPU and client only;
// jitter comes from a private FxRandom, never from the simulation. Advance and WriteInstances run
// on the render thread once per frame and allocate nothing.
public sealed class FxSimulation
{
    public const float MaxDeltaSeconds = 0.1f;
    // The nearest the view centre gets to the camera along the view direction: first person
    // looks about this far ahead, an isometric camera looks at the player ~18 m away.
    public const float MinViewDistance = 6f;
    public const int DustTarget = 96;
    public static readonly Vector3 DustHalfExtents = new(7f, 3.5f, 7f);
    // Halos closer to the camera than this are skipped (the torch in the player's own hand).
    public const float HaloMinCameraDistance = 1.5f;
    public const float HaloMaxViewDistance = 30f;
    // Halos fade out over this last stretch before the cull, so they do not pop.
    public const float HaloFadeDistance = 5f;
    public const float HaloNightStrength = 1f;
    public const float HaloDayStrength = 0.12f;

    // The halo has no flicker of its own: the light colour the FX collector is handed already carries
    // FireFlicker's intensity, so the halo's packed byte holds one flicker factor (peak 1 + A), which
    // is what HeldLightCatalog's clipping ceilings assume.
    public const float DepthBiasLamp = 0.9f;
    // Enough that the carrier's body does not cut the glow with a hard edge.
    public const float DepthBiasHeld = 0.4f;
    // Held fire burns at shoulder height: the light itself sits HeldLightAnchor.BodyUp above the
    // feet (and does not move), the halo, flame and embers sit this far below it.
    public const float HeldFxDrop = 1f;
    // Hit and death effects start this far below the anchor (the text anchor floats above the target).
    public const float AnchorDrop = 0.2f;
    public const float DustLightGain = 0.9f;
    public const int DustLightCount = 6;

    private const int DustSpawnsPerFrame = 12;
    private const int MaxEmberTorches = 8;
    private const int MaxEmberLamps = 8;
    private const float TorchEmbersPerSecond = 5f;
    private const float LampEmbersPerSecond = 0.5f;
    private const double ClockWrap = 4096.0;
    private const float FlameDepthBias = 0.55f;
    private const float FlashDepthBias = 0.4f;
    private const float TorchHaloHalfSize = 0.7f;
    private const float LanternHaloHalfSize = 0.55f;
    private const float LampHaloHalfSize = 1.25f;
    private const float HeldHaloLift = 0.12f;
    private const float FlameLift = 0.1f;
    private const float FlameHalfSize = 0.20f;
    // The dimmest flame brightness (flicker 1 - A); unchanged from the old min(flicker, 1) floor of 0.75.
    internal const float FlameDimBrightness = 0.75f;
    // The flame's size follows the flicker: half-size * (1 + FlameSizeFollow * (flicker - 1)). Area goes
    // as size squared, so 0.5 makes the sprite's AREA track the flicker intensity to first order
    // (1 + 2*0.5*(f-1) = f) and equals the torch halo's own follow, so flame and halo swell together.
    private const float FlameSizeFollow = 0.5f;
    // The flame's colour is cull * flameNight * min(flicker, cap) * tint, packed in a byte. Night is the
    // brightest case (nightFactor 1, cull 1), so these constants give the largest per-channel gain the
    // flame can have; the brightness cap that still fits a byte is the reciprocal (MaxUnclippedFlameBrightness
    // in HeldLightCatalog). Above the cap the flame grows instead of brightening.
    private const float FlameNightDay = 0.75f;
    private const float FlameNightSpan = 0.25f;
    private const float FlameTintGDay = 0.62f;
    private const float FlameTintGSpan = 0.38f;
    private const float FlameTintBDay = 0.25f;
    private const float FlameTintBSpan = 0.75f;
    internal static readonly float FlameMaxChannelGain =
        (FlameNightDay + FlameNightSpan) * MathF.Max(1f, MathF.Max(FlameTintGDay + FlameTintGSpan, FlameTintBDay + FlameTintBSpan));
    public const float SparkStartHalfSize = 0.10f;
    public const float SparkEndHalfSize = 0.04f;
    // Embers read tiny at isometric range, so every ember row is this much larger than the plan.
    private const float EmberSizeGain = 1.3f;
    private const float BurstEmberMinHalfSize = 0.028f * EmberSizeGain;
    private const float BurstEmberMaxHalfSize = 0.05f * EmberSizeGain;
    private const float BurstEmberEndHalfSize = 0.01f * EmberSizeGain;
    private const float PlumeEmberMinHalfSize = 0.02f * EmberSizeGain;
    private const float PlumeEmberMaxHalfSize = 0.034f * EmberSizeGain;
    private const float PlumeEmberEndHalfSize = 0.008f * EmberSizeGain;
    // Skill casts. Every cast adds at most MaxCastParticles, and leaves CastPoolReserve slots free so
    // a cast on a crowded pool cannot starve the ambient dust (96) and torch embers.
    public const int MaxCastParticles = 19;
    public const int CastPoolReserve = 128;
    // The largest hit (a critical); it keeps the same reserve, so hit spam cannot take the ambient's slots.
    public const int MaxHitParticles = 23;
    // Hit marks in the atlas, all additive: impact flash, crit star, slash blade along +X, ragged splat.
    private const float HitFlash = 27f;
    private const float HitStar = 28f;
    private const float HitSlash = 29f;
    private const float HitSplat = 30f;
    private const int ArcSparks = 8;
    // The blade trail: this many billboard segments laid end to end along FxBladeTrailGeometry's path over the
    // active window. One quad, the trail and the sparks together are MaxCastParticles.
    internal const int BladeTrailSegments = 10;
    // A segment is born when the blade passes its start and is gone this long after: twice the swing, so the tail
    // end has faded while the tip is still being laid.
    internal const float TrailLife = 2f * MeleeArcSwing.ActiveSeconds;
    // Half-sizes in authored metres, like FxBladeTrailGeometry; WorldMetresPerAuthoredMetre makes them world sizes.
    private const float TrailStartHalfSize = 0.26f;
    private const float TrailEndHalfSize = 0.10f;
    // Each segment's quad is stretched to this many times the chord it spans, so neighbours overlap.
    private const float TrailOverlap = 1.5f;
    private const float TrailDepthBias = 0.5f;
    private const float TrailBrightness = 0.97f;
    private static readonly Lazy<float> WorldPerAuthored =
        new(static () => HeldItemScale.ModelUnitsPerMetre * PlayerModelAssetDefinition.VisualScale);

    // FxBladeTrailGeometry (and the clip test that measures it) speaks authored metres: model units over
    // HeldItemScale.ModelUnitsPerMetre. The figure is drawn at PlayerModelAssetDefinition.VisualScale world metres
    // per model unit (PlayerRenderState.Create), so one authored metre is this many world metres.
    internal static float WorldMetresPerAuthoredMetre => WorldPerAuthored.Value;
    private static readonly Vector3 TrailTint = new(0.94f, 0.97f, 1f);
    private const int BoltDarts = 12;
    private const int BoltSparks = 6;
    private const int MendEmbers = 14;
    // The caster's capsule centre to the soles of the feet, and to chest height.
    private const float CasterFeetDrop = 0.9f;
    private const float CasterChestLift = 0.2f;
    private const float GroundQuadLift = 0.05f;
    // The crescent is emitted as the swing turns active, rises over the active window and lingers after it.
    private const float ArcLinger = 0.25f;
    private const float ArcLife = MeleeArcSwing.ActiveSeconds + ArcLinger;
    // Sweep sparks ride the painted edge, a little inside it, from the caster's left tip to the right.
    private const float ArcSparkRadiusFraction = 0.9f;
    private const float ArcSparkHeight = 0.25f;
    // A caster that moves further than this between two looks has teleported; the sweep stops following it.
    private const float MaxFollowJump = 3f;
    public const int MaxPendingArcSweeps = 8;

    private const float RingLife = 0.6f;
    private const float RingStartHalfSize = 0.3f;
    private const float RingEndHalfSize = 0.9f;
    private const float BoltMinSpeed = 24f;
    private const float BoltMaxSpeed = 36f;
    private const float BoltMaxLife = 0.8f;
    private const float BoltHalfSize = 0.13f;
    private const float DefaultArcRange = 2f;
    private const float DefaultBoltRange = 12f;
    // Peak channel of a cast sprite; with the tints below no packed channel reaches 255.
    private const float CastBrightness = 0.9f;
    // The most a ground quad occludes by day (the flame's trick: none at night, so it is pure light).
    private const float GroundCoverage = 0.7f;
    private static readonly Vector3 ArcTint = new(0.70f, 0.74f, 0.85f);
    private static readonly Vector3 BoltTint = new(0.52f, 0.40f, 0.90f);
    private static readonly Vector3 MendTint = new(0.46f, 0.68f, 0.44f);
    private const float SparkStretchPerSpeed = 0.03f;
    private const float MaxSparkStretch = 6f;
    private static readonly Vector3 SparkCoolTint = new(1f, 0.55f, 0.25f);
    private static readonly Vector3 AshWarmTint = new(1f, 0.62f, 0.38f);
    private static readonly Vector3 AshColdTint = new(0.6f, 0.58f, 0.56f);
    private static readonly Vector3 HeatBirth = new(1f, 0.62f, 0.2f);
    private static readonly Vector3 HeatMid = new(0.95f, 0.33f, 0.07f);
    private static readonly Vector3 HeatEnd = new(0.42f, 0.07f, 0.015f);
    // Internal, not private: HeldLightCatalog derives the largest torch and brazier intensities whose
    // halos do not clip from these tints and the one flicker peak, so the arithmetic lives in one
    // place. The lamp tint's red was 0.30 and is 0.26: a brazier (3.0) at 0.30 reached 0.959 of the
    // byte before any flicker headroom; at 0.26 its peak with a 25 % flicker is 0.975.
    internal static readonly Vector3 HeldHaloTint = new(0.375f, 0.27f, 0.157f);
    internal static readonly Vector3 LampHaloTint = new(0.26f, 0.22f, 0.13f);

    private readonly FxParticlePool _pool = new(FxLimits.ParticleCapacity);
    private readonly FxInstance[] _halos = new FxInstance[FxLimits.HaloBudget];
    private FxRandom _random;
    private int _haloCount;
    private int _dustCount;
    private Vector3 _cameraPosition;
    private Vector3 _viewCentre;
    private Vector3 _playerPosition;
    private float _nightFactor;
    private double _clock;
    private FxViewBasis _basis;
    // The lights nearest the view centre, nearest first, which light the dust.
    private readonly PointLight[] _dustLights = new PointLight[DustLightCount];
    private readonly float[] _dustLightDistances = new float[DustLightCount];
    private int _dustLightCount;
    // Cleave sweeps waiting for, or inside, their active window. Fixed size; a cast on a full array is dropped.
    private readonly ArcSweep[] _sweeps = new ArcSweep[MaxPendingArcSweeps];
    private int _sweepCount;

    private struct ArcSweep
    {
        public Vector3 Origin;
        public Vector3 Aim;
        public int CasterActorId;
        public Vector3 LastCasterPosition;
        public bool HasCasterPosition;
        public float AnchorLift;
        // The soles of the caster's feet in world Y, once the locator has answered (a located pose is the body
        // centre, FxShadowRules.PlayerPoseToFeet above them, whichever side published the cast).
        public float LocatedFeetY;
        public bool HasLocatedFeet;
        public bool FollowLost;
        public bool Aimed;
        public float Range;
        public float Elapsed;
        public bool QuadEmitted;
        public int SparksEmitted;
        public int TrailEmitted;
        public int Remaining;
    }

    // Where an actor is now, by id; false when unknown or gone. Set once at scene setup.
    public delegate bool ActorLocator(int actorId, out Vector3 position);

    // Lets a timed effect follow its caster: the server sweeps a Cleave from where the caster stands on each active
    // tick, so the effect is anchored to the caster's current position, not the one it had at the cast. Null (or an
    // unknown actor) keeps the position the cast was published with.
    public ActorLocator? CasterLocator { get; set; }

    public FxSimulation(uint seed = FxRandom.DefaultSeed)
    {
        _random = new FxRandom(seed);
    }

    public int ActiveParticles => _pool.Count;

    public int ActiveHalos => _haloCount;

    public long DroppedSpawns => _pool.DroppedSpawns;

    // The point the ambient dust surrounds. Along the view direction, at least MinViewDistance out
    // and as far as the player when the player is farther: in first person that is a few metres
    // ahead, and with a distant (isometric) camera it is the player's own surroundings rather than
    // the empty air by the lens.
    public static Vector3 ViewCentre(in FxFrameInputs inputs)
    {
        float along = Vector3.Dot(inputs.PlayerPosition - inputs.CameraPosition, inputs.CameraFront);
        return inputs.CameraPosition + inputs.CameraFront * MathF.Max(MinViewDistance, along);
    }

    // A hit is a short burst of marks, each a single frame that lives a fraction of a second, plus the
    // familiar sparks. Regular: flash, slash and 8 sparks (10 particles) in amber. Critical: a long star,
    // a flash, two crossed slashes, 16 sparks and 3 embers (23) in white gold, about twice the size.
    // Incoming: a ragged red splat, a dim flash, 6 sparks and 2 embers (10) in red.
    // At most MaxHitParticles, never into the reserve the ambient dust lives on; marks are placed first.
    public void SpawnHit(Vector3 anchor, FloatingCombatTextKind kind)
    {
        anchor.Y -= AnchorDrop;
        int budget = Math.Max(Math.Min(MaxHitParticles, _pool.Capacity - _pool.Count - CastPoolReserve), 0);
        int granted = budget;
        int planned;
        switch (kind)
        {
            case FloatingCombatTextKind.Critical:
            {
                planned = 4 + 16 + 3;
                float angle = _random.Range(-0.5f, 0.5f);
                Vector3 gold = new Vector3(1f, 0.88f, 0.58f) * CastBrightness;
                EmitHitMark(anchor, HitStar, 0f, 0.25f, 0.6f, 0.2f, gold, ref budget);
                EmitHitMark(anchor, HitFlash, 0f, 0.2f, 0.4f, 0.14f, gold, ref budget);
                EmitHitMark(anchor, HitSlash, angle + 0.75f, 0.35f, 0.65f, 0.16f, gold, ref budget);
                EmitHitMark(anchor, HitSlash, angle - 0.75f, 0.35f, 0.65f, 0.16f, gold, ref budget);
                EmitHitSparks(anchor, 16, new Vector3(1f, 0.88f, 0.58f), 3f, 8f, ref budget);
                EmitHitEmbers(anchor, 3, 0.8f, 2.4f, 0.85f, ref budget);
                break;
            }
            case FloatingCombatTextKind.Incoming:
            {
                planned = 2 + 6 + 2;
                Vector3 blood = new Vector3(1f, 0.16f, 0.08f) * CastBrightness;
                EmitHitMark(anchor, HitSplat, _random.Range(0f, 6.2832f), 0.2f, 0.42f, 0.22f, blood, ref budget);
                EmitHitMark(anchor, HitFlash, 0f, 0.12f, 0.24f, 0.12f, blood * 0.7f, ref budget);
                EmitHitSparks(anchor, 6, new Vector3(1f, 0.34f, 0.14f), 2f, 5f, ref budget);
                EmitHitEmbers(anchor, 2, 0.6f, 1.6f, 0.7f, ref budget);
                break;
            }
            default:
            {
                planned = 2 + 8;
                Vector3 amber = new Vector3(1f, 0.72f, 0.36f) * CastBrightness;
                EmitHitMark(anchor, HitFlash, 0f, 0.16f, 0.3f, 0.12f, amber, ref budget);
                EmitHitMark(anchor, HitSlash, _random.Range(0f, 3.1416f), 0.22f, 0.42f, 0.14f, amber, ref budget);
                EmitHitSparks(anchor, 8, new Vector3(1f, 0.72f, 0.36f), 2.5f, 6f, ref budget);
                break;
            }
        }

        _pool.CountDropped(planned - (granted - budget));
    }

    // One camera-facing mark, additive and bright at once, squaring away over its short life.
    private void EmitHitMark(Vector3 anchor, float frame, float rotation, float startHalfSize, float endHalfSize, float life, Vector3 rgb, ref int budget)
    {
        if (budget <= 0)
        {
            return;
        }

        budget--;
        FxParticle mark = new()
        {
            Kind = FxParticleKind.Flash,
            Position = anchor,
            Life = life,
            StartHalfSize = startHalfSize,
            EndHalfSize = endHalfSize,
            Rotation = rotation,
            Frame = frame,
            Color = new Vector4(rgb, 0f)
        };
        _pool.TryAdd(in mark);
    }

    private void EmitHitSparks(Vector3 anchor, int count, Vector3 rgb, float speedMin, float speedMax, ref int budget)
    {
        count = Math.Min(count, budget);
        budget -= count;
        EmitSparks(anchor, count, rgb, speedMin, speedMax, 1f);
    }

    private void EmitHitEmbers(Vector3 anchor, int count, float speedMin, float speedMax, float brightness, ref int budget)
    {
        count = Math.Min(count, budget);
        budget -= count;
        EmitBurstEmbers(anchor, count, speedMin, speedMax, brightness);
    }

    public void SpawnDeath(Vector3 anchor)
    {
        anchor.Y -= AnchorDrop;
        EmitFlash(anchor);
        EmitSparks(anchor, 26, new Vector3(1f, 0.66f, 0.3f), 3f, 8f, 1.2f);
        EmitBurstEmbers(anchor, 20, 1f, 3.5f, 1f);
        for (int i = 0; i < 14; i++)
        {
            EmitAsh(anchor);
        }
    }

    public void SpawnSkillCast(SkillFxKind kind, Vector3 origin, Vector3 direction, float range, int casterActorId = 0)
    {
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z))
        {
            return;
        }

        // A horizontal unit vector, or zero for "no direction".
        float flat = direction.X * direction.X + direction.Z * direction.Z;
        bool aimed = float.IsFinite(flat) && flat > 1e-4f;
        Vector3 aim = aimed ? new Vector3(direction.X, 0f, direction.Z) / MathF.Sqrt(flat) : Vector3.Zero;
        range = float.IsFinite(range) ? MathF.Max(range, 0f) : 0f;

        // At most MaxCastParticles, and never into the reserve the ambient particles live on.
        int budget = Math.Max(Math.Min(MaxCastParticles, _pool.Capacity - _pool.Count - CastPoolReserve), 0);
        int granted = budget;
        int planned;
        switch (kind)
        {
            case SkillFxKind.Arc:
                // Nothing is drawn now: the swing is queued and Advance emits it as the swing turns active.
                planned = (aimed ? 1 + BladeTrailSegments : 0) + ArcSparks;
                if (_sweepCount < _sweeps.Length)
                {
                    ArcSweep queued = new()
                    {
                        Origin = origin,
                        CasterActorId = casterActorId,
                        Aim = aim,
                        Aimed = aimed,
                        Range = range > 0f ? range : DefaultArcRange,
                        Remaining = MaxCastParticles
                    };
                    _sweeps[_sweepCount++] = queued;
                    return;
                }

                _pool.CountDropped(planned);
                return;
            case SkillFxKind.Bolt:
                planned = BoltDarts + BoltSparks;
                EmitBolt(origin, aim, range > 0f ? range : DefaultBoltRange, ref budget);
                break;
            case SkillFxKind.Mend:
                planned = 1 + MendEmbers;
                EmitMend(origin, ref budget);
                break;
            default:
                return;
        }

        _pool.CountDropped(planned - (granted - budget));
    }

    public void Advance(in FxFrameInputs inputs, ReadOnlySpan<PointLight> lights)
    {
        float dt = inputs.DeltaSeconds;
        dt = dt > 0f ? MathF.Min(dt, MaxDeltaSeconds) : 0f;
        _cameraPosition = inputs.CameraPosition;
        _playerPosition = inputs.PlayerPosition;
        _viewCentre = ViewCentre(in inputs);
        _nightFactor = Math.Clamp(inputs.NightFactor, 0f, 1f);
        _basis = inputs.Basis;
        // The presentation clock when the frame supplies one, so halo, light, flame and shadow are in
        // phase; otherwise (tests, tools) the simulation counts its own.
        if (double.IsFinite(inputs.ClockSeconds))
        {
            _clock = inputs.ClockSeconds;
        }
        else
        {
            _clock += dt;
            if (_clock >= ClockWrap)
            {
                _clock -= ClockWrap;
            }
        }

        StepParticles(dt);
        AdvanceArcSweeps(dt);
        TopUpDust();
        ScanLights(lights, dt);
    }

    // Particles first (ash, then the rest, each ordered by pool slot, which swap-removal shuffles), then at most
    // HaloBudget halos, written from index `start`. Returns the end index and never writes past
    // the destination.
    public int WriteInstances(FxInstance[] destination, int start = 0)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, destination.Length);
        int written = start;
        int room = destination.Length - start;
        int particleCount = Math.Min(_pool.Count, room);
        // Ash first, so the sparks, embers and flash of the same death draw over it; the pool's
        // swap-removal shuffles slots, and a single pass would let particles pop across the ash.
        for (int i = 0; i < _pool.Count && written - start < particleCount; i++)
        {
            if (IsUnderlay(_pool[i].Kind))
            {
                WriteParticle(ref _pool[i], ref destination[written]);
                written++;
            }
        }

        for (int i = 0; i < _pool.Count && written - start < particleCount; i++)
        {
            if (!IsUnderlay(_pool[i].Kind))
            {
                WriteParticle(ref _pool[i], ref destination[written]);
                written++;
            }
        }

        int haloCount = Math.Min(_haloCount, destination.Length - written);
        for (int i = 0; i < haloCount; i++)
        {
            destination[written++] = _halos[i];
        }

        return written;
    }

    // Ash and the flat ground quads of a skill cast draw first, under the sparks and embers.
    private static bool IsUnderlay(FxParticleKind kind)
    {
        return kind == FxParticleKind.Ash || kind == FxParticleKind.Arc || kind == FxParticleKind.Ring;
    }

    private void StepParticles(float dt)
    {
        Vector3 half = DustHalfExtents;
        Vector3 full = half * 2f;
        int i = 0;
        while (i < _pool.Count)
        {
            ref FxParticle p = ref _pool[i];
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                if (p.Kind == FxParticleKind.Dust)
                {
                    _dustCount--;
                }

                _pool.RemoveAt(i);
                continue;
            }

            if (p.Sway != 0f)
            {
                // Plume curl: a slow sideways wander so the column does not rise ruler-straight.
                p.Velocity.X += MathF.Sin(p.Age * 3f + p.Phase) * 0.6f * p.Sway * dt;
                p.Velocity.Z += MathF.Cos(p.Age * 2.3f + p.Phase) * 0.6f * p.Sway * dt;
            }

            p.Velocity.Y += p.Gravity * dt;
            p.Velocity /= 1f + p.Drag * dt;
            p.Position += p.Velocity * dt;
            p.Rotation += p.AngularVelocity * dt;
            if (p.Kind == FxParticleKind.Dust)
            {
                // Torus wrap around the view centre: density stays even while the camera moves,
                // and a mote leaving one side re-enters on the other (faded at the edge).
                Vector3 relative = p.Position - _viewCentre + half;
                relative.X -= full.X * MathF.Floor(relative.X / full.X);
                relative.Y -= full.Y * MathF.Floor(relative.Y / full.Y);
                relative.Z -= full.Z * MathF.Floor(relative.Z / full.Z);
                p.Position = _viewCentre - half + relative;
            }

            i++;
        }
    }

    private void TopUpDust()
    {
        int spawns = Math.Min(DustTarget - _dustCount, DustSpawnsPerFrame);
        for (int i = 0; i < spawns; i++)
        {
            Vector3 offset = new(
                _random.Range(-DustHalfExtents.X, DustHalfExtents.X),
                _random.Range(-DustHalfExtents.Y, DustHalfExtents.Y),
                _random.Range(-DustHalfExtents.Z, DustHalfExtents.Z));
            FxParticle mote = new()
            {
                Kind = FxParticleKind.Dust,
                Position = _viewCentre + offset,
                Velocity = new Vector3(_random.Range(-0.12f, 0.12f), _random.Range(-0.05f, 0.08f), _random.Range(-0.12f, 0.12f)),
                Life = float.PositiveInfinity,
                StartHalfSize = _random.Range(0.028f, 0.05f),
                Rotation = _random.Range(0f, MathHelper.TwoPi),
                AngularVelocity = _random.Range(-0.4f, 0.4f),
                Frame = (float)FxFrame.Dust0 + _random.Range(0, 4),
                Phase = _random.Range(0f, MathHelper.TwoPi),
                // Pure light: the lighting at the mote is worked out when it is written.
                Color = new Vector4(1f, 1f, 1f, 0f)
            };
            mote.EndHalfSize = mote.StartHalfSize;
            if (!_pool.TryAddQuietly(in mote))
            {
                break;
            }

            _dustCount++;
        }
    }

    // One pass over the frame's lights: keep the few nearest the view for the dust, build the
    // halo list (and the torch flames) and let held torches and lamps shed embers.
    private void ScanLights(ReadOnlySpan<PointLight> lights, float dt)
    {
        _haloCount = 0;
        _dustLightCount = 0;
        float strength = HaloDayStrength + (HaloNightStrength - HaloDayStrength) * _nightFactor;
        float maxViewSquared = HaloMaxViewDistance * HaloMaxViewDistance;
        float minCameraSquared = HaloMinCameraDistance * HaloMinCameraDistance;
        int emberTorches = 0;
        int emberLamps = 0;
        for (int i = 0; i < lights.Length; i++)
        {
            ref readonly PointLight light = ref lights[i];
            float viewSquared = (light.Position - _viewCentre).LengthSquared;
            InsertDustLight(in light, viewSquared);
            if (!TryClassify(light.Radius, out FxLightClass lightClass))
            {
                continue;
            }

            if (viewSquared > maxViewSquared)
            {
                continue;
            }

            bool lamp = lightClass == FxLightClass.Lamp;
            bool torch = lightClass == FxLightClass.Torch;
            bool firstPerson = !lamp && (light.Position - _cameraPosition).LengthSquared < minCameraSquared;
            Vector3 fxPosition = lamp || firstPerson ? light.Position : light.Position - Vector3.UnitY * HeldFxDrop;
            bool emberTorch = torch && emberTorches < MaxEmberTorches;
            if (emberTorch)
            {
                emberTorches++;
                if (_random.NextFloat() < TorchEmbersPerSecond * dt)
                {
                    EmitPlumeEmber(fxPosition + Vector3.UnitY * 0.2f, 1f);
                }
            }
            else if (lamp && emberLamps < MaxEmberLamps)
            {
                emberLamps++;
                if (_random.NextFloat() < LampEmbersPerSecond * _nightFactor * dt)
                {
                    EmitPlumeEmber(light.Position + Vector3.UnitY * 0.55f, 0.8f);
                }
            }

            if (firstPerson || _haloCount >= FxLimits.HaloBudget)
            {
                continue;
            }

            float distance = MathF.Sqrt(viewSquared);
            float cull = Smooth((HaloMaxViewDistance - distance) / HaloFadeDistance);
            float fade = strength * cull;
            // The one flicker, the same value the light itself carries. The light BRINGS its phase:
            // PointLight.FlickerSeed is FireFlicker.SeedFor(the fire's base position) for a placed fire
            // and the actor id for a held one, so this reads the very phase the producer sampled at,
            // whatever the flame's wander has since done to light.Position. Only a light whose producer
            // left the seed at 0 falls back to the older derivations - the colour read-back for a held
            // torch, the hash of the light's own position for a placed fire.
            float flicker = FireIntensity(in light, lightClass, _clock);
            float halfSize;
            Vector3 tint;
            Vector3 position;
            if (lamp)
            {
                halfSize = LampHaloHalfSize * (1f + 0.35f * (flicker - 1f));
                tint = LampHaloTint;
                position = light.Position;
            }
            else
            {
                halfSize = (torch ? TorchHaloHalfSize : LanternHaloHalfSize) * (1f + 0.5f * (flicker - 1f));
                tint = HeldHaloTint;
                position = fxPosition + Vector3.UnitY * HeldHaloLift;
            }

            // light.Color already carries the flicker; multiplying it again would put two flicker
            // factors in the packed byte and break the ceilings in HeldLightCatalog.
            float gain = fade;
            _halos[_haloCount++] = new FxInstance
            {
                Position = position,
                HalfSize = halfSize,
                Color = FxInstance.PackColor(light.Color.X * tint.X * gain, light.Color.Y * tint.Y * gain, light.Color.Z * tint.Z * gain, 0f),
                Rotation = 0f,
                Frame = (float)(lamp ? FxFrame.Halo : FxFrame.HaloCore),
                DepthBias = lamp ? DepthBiasLamp : DepthBiasHeld
            };

            if (emberTorch && _haloCount < FxLimits.HaloBudget)
            {
                // The flame ignores the halo strength (0.12 at noon): additive light cannot show on a
                // bright sky, so by day it gets coverage alpha instead and stays opaque enough to read.
                // RGB never drops below the alpha, so the occluding flame is not dark.
                _halos[_haloCount++] = new FxInstance
                {
                    Position = fxPosition + Vector3.UnitY * FlameLift,
                    HalfSize = FlameHalfSizeAt(flicker),
                    Color = FlameColor(cull, flicker, _nightFactor),
                    Rotation = 0f,
                    Frame = (float)FxFrame.Flame0 + (((int)(_clock * 12f + i)) & 3),
                    DepthBias = FlameDepthBias
                };
            }
        }
    }

    // The flame sprite's half-size at a flicker: it swells above 1 and shrinks below it.
    internal static float FlameHalfSizeAt(float flicker) => FlameHalfSize * (1f + FlameSizeFollow * (flicker - 1f));

    // The flicker as flame brightness: a LINEAR map of [1 - A, 1 + A] onto [FlameDimBrightness,
    // FlameBrightnessCeiling], so every flicker value is a distinct brightness (no plateau) and the top
    // stays FlameSaturationMargin under the byte. The ceiling is derived (HeldLightCatalog), and the
    // test reads this unclamped value because the byte clamp would hide an overshoot.
    internal static float FlameBrightnessAt(float flicker)
    {
        float a = FireFlicker.TorchAmplitude;
        float t = Math.Clamp((flicker - (1f - a)) / (2f * a), 0f, 1f);
        return FlameDimBrightness + ((HeldLightCatalog.FlameBrightnessCeiling - FlameDimBrightness) * t);
    }

    // The flame's packed colour. Brightness is the flicker CAPPED at what a byte holds, so the upper half
    // of the cycle cannot pin the channels (it shows as size instead, FlameHalfSizeAt). By day the flame
    // is tinted saturated orange; only red is held at the coverage alpha, so it is never dark.
    internal static uint FlameColor(float cull, float flicker, float nightFactor)
    {
        float alpha = (1f - nightFactor) * cull;
        float flameNight = FlameNightDay + FlameNightSpan * nightFactor;
        float flame = cull * FlameBrightnessAt(flicker) * flameNight;
        float tintG = FlameTintGDay + FlameTintGSpan * nightFactor;
        float tintB = FlameTintBDay + FlameTintBSpan * nightFactor;
        return FxInstance.PackColor(MathF.Max(flame, alpha), flame * tintG, flame * tintB, alpha);
    }

    // Insertion into the short, sorted list of the lights nearest the view centre.
    private void InsertDustLight(in PointLight light, float distanceSquared)
    {
        int slot = _dustLightCount;
        while (slot > 0 && _dustLightDistances[slot - 1] > distanceSquared)
        {
            slot--;
        }

        if (slot >= DustLightCount)
        {
            return;
        }

        int last = Math.Min(_dustLightCount, DustLightCount - 1);
        for (int j = last; j > slot; j--)
        {
            _dustLights[j] = _dustLights[j - 1];
            _dustLightDistances[j] = _dustLightDistances[j - 1];
        }

        _dustLights[slot] = light;
        _dustLightDistances[slot] = distanceSquared;
        if (_dustLightCount < DustLightCount)
        {
            _dustLightCount++;
        }
    }

    // Lamp, torch and lantern lights reach the collector with their catalog radius times a FireFlicker
    // radius scale (at most +-8.75 %), so the radius is the tag to within that: FireFlicker's bands
    // (the nearest class radius within its tolerance). The lantern burns steady, so its band is tight:
    // a death flash (radius 5.5) must not read as a lantern. Anything else gets no halo.
    internal static bool TryClassify(float radius, out FxLightClass lightClass)
    {
        lightClass = FxLightClass.None;
        if (FireFlicker.TryClassifyRadius(radius, out FireFlickerKind kind))
        {
            lightClass = kind switch
            {
                FireFlickerKind.HeldTorch => FxLightClass.Torch,
                FireFlickerKind.HeldLantern => FxLightClass.Lantern,
                _ => FxLightClass.Lamp
            };
        }

        return lightClass != FxLightClass.None;
    }

    // The fire's current flicker intensity for a classified light, in phase with the light itself.
    internal static float FireIntensity(in PointLight light, FxLightClass lightClass, double clock)
    {
        return lightClass == FxLightClass.None ? 1f : FireFlicker.IntensityOf(in light, clock);
    }

    private void WriteParticle(ref FxParticle p, ref FxInstance instance)
    {
        float t = p.Life > 0f && float.IsFinite(p.Life) ? p.Age / p.Life : 0f;
        float halfSize = p.StartHalfSize + (p.EndHalfSize - p.StartHalfSize) * t;
        float fade = 1f;
        Vector3 tint = Vector3.One;
        float rotation = p.Rotation;
        float stretch = 0f;
        float depthBias = 0f;
        float coverage = 0f;
        switch (p.Kind)
        {
            case FxParticleKind.Spark:
                fade = MathF.Pow(1f - t, 0.7f);
                tint = p.Plain ? Vector3.One : Vector3.Lerp(Vector3.One, SparkCoolTint, t);
                // Lay the streak along the velocity as the camera sees it. A velocity straight at
                // (or away from) the lens, or no basis at all, projects to nothing and stays square.
                Streak(p.Velocity, in _basis, halfSize, out rotation, out stretch);
                break;
            case FxParticleKind.Ember:
                fade = MathF.Min(1f, t / 0.08f) * MathF.Pow(1f - t, 0.6f) * (0.85f + 0.15f * MathF.Sin(p.Age * 17f + p.Phase));
                tint = p.Plain ? Vector3.One : HeatRamp(t);
                break;
            case FxParticleKind.Ash:
                fade = MathF.Min(1f, t / 0.15f) * (1f - t);
                tint = Vector3.Lerp(AshWarmTint, AshColdTint, Math.Clamp(t / 0.35f, 0f, 1f));
                break;
            case FxParticleKind.Dust:
                Vector3 relative = (p.Position - _viewCentre) / DustHalfExtents;
                float edge = 1f - MathF.Max(MathF.Abs(relative.X), MathF.Max(MathF.Abs(relative.Y), MathF.Abs(relative.Z)));
                fade = Smooth(edge * 4f) * (0.75f + 0.25f * MathF.Sin(p.Age * 1.3f + p.Phase));
                tint = DustLight(p.Position);
                break;
            case FxParticleKind.Flash:
                fade = (1f - t) * (1f - t);
                depthBias = FlashDepthBias;
                break;
            case FxParticleKind.Arc:
                // Fully in by the end of the active window, then a linger that fades out.
                fade = MathF.Min(1f, p.Age / MeleeArcSwing.ActiveSeconds) * MathF.Min(1f, (p.Life - p.Age) / ArcLinger);
                stretch = FxInstance.GroundAligned;
                depthBias = DepthBiasHeld;
                coverage = (1f - _nightFactor) * GroundCoverage;
                break;
            case FxParticleKind.Ring:
                fade = MathF.Min(1f, t / 0.1f) * (1f - t);
                stretch = FxInstance.GroundAligned;
                depthBias = DepthBiasHeld;
                coverage = (1f - _nightFactor) * GroundCoverage;
                break;
            case FxParticleKind.Trail:
                // Fades from the tail end quickly; the width shrinks through the halfSize lerp. The quad lies along
                // the segment as the camera sees it and is stretched so its bright plateau spans the chord.
                fade = MathF.Pow(MathF.Max(1f - t, 0f), 1.5f);
                float ax = Vector3.Dot(p.Axis, _basis.Right);
                float ay = Vector3.Dot(p.Axis, _basis.Up);
                float chord = MathF.Sqrt(ax * ax + ay * ay);
                rotation = chord > 1e-6f ? MathF.Atan2(ay, ax) : 0f;
                stretch = chord > 1e-6f
                    ? MathF.Max(1f, chord * TrailOverlap / (MathF.Max(halfSize, 1e-4f) * FxBladeTrailGeometry.CellPlateauFraction))
                    : 1f;
                depthBias = TrailDepthBias;
                break;
            case FxParticleKind.Bolt:
                // A dart is a lance head: bright at once, dimming along its flight. It lies along its
                // velocity like a spark, but keeps its own violet.
                fade = MathF.Min(1f, t / 0.05f) * MathF.Sqrt(MathF.Max(1f - t, 0f));
                Streak(p.Velocity, in _basis, halfSize, out rotation, out stretch);
                break;
        }

        // Fade out near the lens: a speck at 10 cm would otherwise cover a patch of the screen.
        float nearStart = p.Kind == FxParticleKind.Dust ? 0.4f : 0.25f;
        float nearSpan = p.Kind == FxParticleKind.Dust ? 1.6f : 0.9f;
        fade *= Smooth((Vector3.Distance(p.Position, _cameraPosition) - nearStart) / nearSpan);
        fade = MathF.Max(fade, 0f);

        instance.Position = p.Position;
        instance.HalfSize = halfSize;
        // A coverage quad occludes by day and is pure light at night; its RGB never drops below half
        // the alpha, so the occluder is not black. Every other kind has coverage 0 and is unchanged.
        float alpha = (p.Color.W + coverage) * fade;
        float floor = alpha * 0.5f;
        instance.Color = FxInstance.PackColor(
            MathF.Max(p.Color.X * tint.X * fade, floor),
            MathF.Max(p.Color.Y * tint.Y * fade, floor),
            MathF.Max(p.Color.Z * tint.Z * fade, floor),
            alpha);
        instance.Rotation = rotation;
        instance.Frame = p.Frame;
        instance.DepthBias = depthBias;
        instance.Stretch = stretch;
    }

    // The streak of a spark moving at `velocity`: rotated along its projection onto the screen and
    // stretched in proportion to the screen-space speed. A velocity straight at (or away from) the
    // lens, or no basis at all, projects to nothing and gives a square quad (stretch 1).
    internal static void Streak(Vector3 velocity, in FxViewBasis basis, float halfSize, out float rotation, out float stretch)
    {
        float vx = Vector3.Dot(velocity, basis.Right);
        float vy = Vector3.Dot(velocity, basis.Up);
        rotation = MathF.Atan2(vy, vx);
        stretch = Math.Clamp(MathF.Sqrt(vx * vx + vy * vy) * SparkStretchPerSpeed / MathF.Max(halfSize, 1e-4f), 1f, MaxSparkStretch);
    }

    // What the nearest lights (and, by day, the sky) put on a mote: the same squared falloff the
    // point-light shader uses. With no light and no daylight this is exactly zero.
    private Vector3 DustLight(Vector3 position)
    {
        float day = 1f - _nightFactor;
        float r = 0.14f * day;
        float g = 0.15f * day;
        float b = 0.16f * day;
        for (int i = 0; i < _dustLightCount; i++)
        {
            ref readonly PointLight light = ref _dustLights[i];
            float distanceSquared = (position - light.Position).LengthSquared;
            float radiusSquared = light.Radius * light.Radius;
            if (!(distanceSquared < radiusSquared))
            {
                continue;
            }

            float f = 1f - distanceSquared / radiusSquared;
            f = f * f * DustLightGain;
            r += light.Color.X * f;
            g += light.Color.Y * f;
            b += light.Color.Z * f;
        }

        // Divide by the brightest channel when above 1, so the hue survives a strong light.
        float scale = 1f / MathF.Max(MathF.Max(r, g), MathF.Max(b, 1f));
        return new Vector3(r * scale, g * scale, b * scale);
    }

    // White-hot at birth, orange mid-life, dull red at the end; piecewise linear in life.
    private static Vector3 HeatRamp(float t)
    {
        return t < 0.35f
            ? Vector3.Lerp(HeatBirth, HeatMid, t / 0.35f)
            : Vector3.Lerp(HeatMid, HeatEnd, (t - 0.35f) / 0.65f);
    }

    private void EmitSparks(Vector3 anchor, int count, Vector3 rgb, float speedMin, float speedMax, float sizeScale)
    {
        // Throw sparks away from the player: they read against the scene rather than into the lens.
        Vector3 away = new(anchor.X - _playerPosition.X, 0f, anchor.Z - _playerPosition.Z);
        float lengthSquared = away.LengthSquared;
        away = lengthSquared >= 0.01f ? away / MathF.Sqrt(lengthSquared) : Vector3.Zero;
        for (int i = 0; i < count; i++)
        {
            EmitSpark(anchor, away, rgb, speedMin, speedMax, sizeScale);
        }
    }

    private void EmitSpark(Vector3 anchor, Vector3 away, Vector3 rgb, float speedMin, float speedMax, float sizeScale, bool plain = false)
    {
        Vector3 direction = _random.UnitVector() + away + Vector3.UnitY * 0.35f;
        direction = direction.LengthSquared > 1e-6f ? Vector3.Normalize(direction) : Vector3.UnitY;
        float heat = _random.Range(0.75f, 1f);
        FxParticle spark = new()
        {
            Kind = FxParticleKind.Spark,
            Position = anchor,
            Velocity = direction * _random.Range(speedMin, speedMax),
            Life = _random.Range(0.3f, 0.6f),
            StartHalfSize = SparkStartHalfSize * sizeScale,
            EndHalfSize = SparkEndHalfSize,
            Frame = (float)FxFrame.Spark0 + _random.Range(0, 4),
            Gravity = -7f,
            Drag = 0.9f,
            Plain = plain,
            Color = Premultiply(rgb.X * heat, rgb.Y * heat, rgb.Z * heat, 0f)
        };
        _pool.TryAdd(in spark);
    }

    // Cleave: nothing at the cast. As the swing turns active (MeleeArcSwing.WindupSeconds after the cast) a
    // flat crescent is laid on the ground ahead of the caster, sized by FxArcGeometry so its painted outer edge
    // lands on the skill's range. Over the active window a white blade trail is laid along FxBladeTrailGeometry's
    // path and steel sparks are thrown along that edge, from the caster's left tip to the right tip, matching the
    // sweep of the blade.
    private void AdvanceArcSweeps(float dt)
    {
        for (int i = 0; i < _sweepCount;)
        {
            ref ArcSweep sweep = ref _sweeps[i];
            sweep.Elapsed += dt;
            FollowCaster(ref sweep);
            if (sweep.Elapsed >= MeleeArcSwing.WindupSeconds)
            {
                EmitArcSweep(ref sweep);
            }

            if (sweep.QuadEmitted && sweep.SparksEmitted >= ArcSparks && (sweep.TrailEmitted >= BladeTrailSegments || !sweep.Aimed))
            {
                _sweeps[i] = _sweeps[--_sweepCount];
                _sweeps[_sweepCount] = default;
                continue;
            }

            i++;
        }
    }

    // Anchors the sweep's origin to its caster's located position, absolute: a remote caster's pose lags the cast
    // origin (the server position at the cast tick) by the interpolation delay, so following by displacement would
    // keep that gap and draw the crescent ahead of the body. The cast origin may sit at a different height than the
    // located position (a remote cast is published from the eye, a local one from the body), so the vertical gap
    // seen on the first sighting is kept as a fixed lift; horizontally the anchor is the located position. A caster
    // the locator does not know leaves the origin where it was. A jump of more than MaxFollowJump in one look
    // (a teleport or respawn) ends the following and keeps the last anchor. The crescent itself is laid once, at
    // activation, and stays.
    private void FollowCaster(ref ArcSweep sweep)
    {
        if (sweep.FollowLost || sweep.CasterActorId == 0 || CasterLocator is not { } locate
            || !locate(sweep.CasterActorId, out Vector3 now))
        {
            return;
        }

        if (!sweep.HasCasterPosition)
        {
            sweep.AnchorLift = sweep.Origin.Y - now.Y;
            sweep.HasCasterPosition = true;
        }
        else if ((now - sweep.LastCasterPosition).LengthSquared > MaxFollowJump * MaxFollowJump)
        {
            sweep.FollowLost = true;
            return;
        }

        sweep.LastCasterPosition = now;
        sweep.LocatedFeetY = now.Y - FxShadowRules.PlayerPoseToFeet;
        sweep.HasLocatedFeet = true;
        sweep.Origin = new Vector3(now.X, now.Y + sweep.AnchorLift, now.Z);
    }

    // The ground under the caster. A local cast is published from the body centre and a remote one from the eye, so
    // the cast origin cannot say where the feet are; the located body centre can. With no locator answer the origin
    // is taken as the capsule-centre convention of the other casts (CasterFeetDrop below it).
    private static float SweepFeetY(in ArcSweep sweep) =>
        sweep.HasLocatedFeet ? sweep.LocatedFeetY : sweep.Origin.Y - CasterFeetDrop;

    private int SweepRoom(ref ArcSweep sweep)
    {
        return Math.Max(Math.Min(sweep.Remaining, _pool.Capacity - _pool.Count - CastPoolReserve), 0);
    }

    private void EmitArcSweep(ref ArcSweep sweep)
    {
        Vector3 origin = sweep.Origin;
        Vector3 aim = sweep.Aim;
        if (!sweep.QuadEmitted)
        {
            sweep.QuadEmitted = true;
            int planned = sweep.Aimed ? 1 : 0;
            int room = SweepRoom(ref sweep);
            int granted = room;
            if (sweep.Aimed && room > 0)
            {
                room--;
                Vector3 feet = new(origin.X, SweepFeetY(in sweep) + GroundQuadLift, origin.Z);
                float half = FxArcGeometry.HalfSizeForRange(sweep.Range);
                EmitGroundQuad(FxParticleKind.Arc, FxFrame.Arc, feet, MathF.Atan2(aim.Z, aim.X), half, half, ArcLife, ArcTint);
            }

            sweep.Remaining -= granted - room;
            _pool.CountDropped(planned - (granted - room));
        }

        float progress = Math.Clamp((sweep.Elapsed - MeleeArcSwing.WindupSeconds) / MeleeArcSwing.ActiveSeconds, 0f, 1f);
        if (sweep.Aimed)
        {
            EmitBladeTrail(ref sweep, progress);
        }

        int due = progress >= 1f ? ArcSparks : (int)(progress * ArcSparks);
        if (due <= sweep.SparksEmitted)
        {
            return;
        }

        int wanted = due - sweep.SparksEmitted;
        int emit = Math.Min(wanted, SweepRoom(ref sweep));
        Vector3 right = new(-aim.Z, 0f, aim.X);
        Vector3 steel = ArcTint * CastBrightness;
        for (int n = 0; n < emit; n++)
        {
            EmitSweepSpark(ref sweep, sweep.SparksEmitted + n, aim, right, steel);
        }

        sweep.Remaining -= emit;
        _pool.CountDropped(wanted - emit);
        sweep.SparksEmitted = due;
    }

    // The blade tip at `progress` through the active window, in world space (FxBladeTrailGeometry in the caster's frame).
    // The geometry is authored metres; radius and height are converted to world metres here.
    private static Vector3 TrailPoint(Vector3 origin, float feetY, Vector3 aim, Vector3 right, float progress)
    {
        float scale = WorldMetresPerAuthoredMetre;
        float theta = FxBladeTrailGeometry.TipAngleRadians(progress);
        Vector3 direction = aim * MathF.Cos(theta) + right * MathF.Sin(theta);
        return new Vector3(origin.X, feetY + (FxBladeTrailGeometry.TipHeightMetres(progress) * scale), origin.Z)
            + (direction * (FxBladeTrailGeometry.TipRadiusMetres * scale));
    }

    // Lays the segments the blade has passed since the last look. Segment i spans progress i/N .. (i+1)/N; it is
    // aged by how long ago the blade passed its start, so a late frame lays it already part-faded.
    private void EmitBladeTrail(ref ArcSweep sweep, float progress)
    {
        const int n = BladeTrailSegments;
        int due = progress >= 1f ? n : (int)(progress * n) + 1;
        if (due <= sweep.TrailEmitted)
        {
            return;
        }

        int wanted = due - sweep.TrailEmitted;
        int emit = Math.Min(wanted, SweepRoom(ref sweep));
        Vector3 aim = sweep.Aim;
        Vector3 right = new(-aim.Z, 0f, aim.X);
        Vector3 colour = TrailTint * TrailBrightness;
        float feetY = SweepFeetY(in sweep);
        float scale = WorldMetresPerAuthoredMetre;
        for (int k = 0; k < emit; k++)
        {
            int i = sweep.TrailEmitted + k;
            Vector3 from = TrailPoint(sweep.Origin, feetY, aim, right, (float)i / n);
            Vector3 to = TrailPoint(sweep.Origin, feetY, aim, right, (float)(i + 1) / n);
            float born = MeleeArcSwing.WindupSeconds + ((float)i / n * MeleeArcSwing.ActiveSeconds);
            FxParticle segment = new()
            {
                Kind = FxParticleKind.Trail,
                Position = TrailPoint(sweep.Origin, feetY, aim, right, (i + 0.5f) / n),
                Axis = (to - from) * 0.5f,
                Age = Math.Clamp(sweep.Elapsed - born, 0f, 0.9f * TrailLife),
                Life = TrailLife,
                StartHalfSize = TrailStartHalfSize * scale,
                EndHalfSize = TrailEndHalfSize * scale,
                Frame = (float)FxFrame.Trail,
                Color = new Vector4(colour, 0f)
            };
            _pool.TryAdd(in segment);
        }

        sweep.Remaining -= emit;
        _pool.CountDropped(wanted - emit);
        sweep.TrailEmitted = due;
    }

    // One spark on the painted edge at the sweep's angle for `index`: -arc/2 (the caster's left) up to +arc/2
    // (the right). direction(theta) = cos(theta) * aim + sin(theta) * right.
    private void EmitSweepSpark(ref ArcSweep sweep, int index, Vector3 aim, Vector3 right, Vector3 steel)
    {
        Vector3 origin = sweep.Origin;
        if (!sweep.Aimed)
        {
            EmitSpark(new Vector3(origin.X, origin.Y + CasterChestLift, origin.Z), Vector3.Zero, steel, 3f, 7f, 1f, plain: true);
            return;
        }

        Vector3 feet = new(origin.X, SweepFeetY(in sweep) + ArcSparkHeight, origin.Z);
        float theta = MathHelper.DegreesToRadians(FxArcGeometry.PaintedArcDegrees) * ((index + 0.5f) / ArcSparks - 0.5f);
        Vector3 direction = aim * MathF.Cos(theta) + right * MathF.Sin(theta);
        float heat = _random.Range(0.75f, 1f);
        FxParticle spark = new()
        {
            Kind = FxParticleKind.Spark,
            Position = feet + direction * (sweep.Range * ArcSparkRadiusFraction),
            Velocity = direction * _random.Range(1f, 2.5f) + Vector3.UnitY * _random.Range(0.6f, 1.6f),
            Life = _random.Range(0.3f, 0.5f),
            StartHalfSize = SparkStartHalfSize,
            EndHalfSize = SparkEndHalfSize,
            Frame = (float)FxFrame.Spark0 + _random.Range(0, 4),
            Gravity = -7f,
            Drag = 0.9f,
            Plain = true,
            Color = Premultiply(steel.X * heat, steel.Y * heat, steel.Z * heat, 0f)
        };
        _pool.TryAdd(in spark);
    }

    // Bolt: twelve single-frame darts leave at chest height with a spread of speeds, so they string
    // out into a lance in flight; each is stretched along its screen velocity (about 0.3 m thick and
    // up to 1.5 m long, roughly 12 px by 70 px at isometric scale). A few sparks burst at the muzzle.
    private void EmitBolt(Vector3 origin, Vector3 aim, float range, ref int budget)
    {
        Vector3 muzzle = new(origin.X, origin.Y + CasterChestLift, origin.Z);
        Vector3 violet = BoltTint * CastBrightness;
        int darts = Math.Min(BoltDarts, budget);
        budget -= darts;
        for (int i = 0; i < darts; i++)
        {
            float speed = _random.Range(BoltMinSpeed, BoltMaxSpeed);
            Vector3 heading = aim;
            Vector3 position = muzzle;
            if (aim == Vector3.Zero)
            {
                // No aim: the lance becomes a radial burst.
                heading = _random.UnitVector();
            }
            else
            {
                position += new Vector3(_random.Range(-0.08f, 0.08f), _random.Range(-0.08f, 0.08f), _random.Range(-0.08f, 0.08f));
            }

            FxParticle dart = new()
            {
                Kind = FxParticleKind.Bolt,
                Position = position,
                Velocity = heading * speed,
                Life = MathF.Min(range / speed, BoltMaxLife),
                StartHalfSize = BoltHalfSize,
                EndHalfSize = BoltHalfSize * 0.6f,
                Frame = (float)FxFrame.Bolt0 + _random.Range(0, 2),
                Color = new Vector4(violet, 0f)
            };
            _pool.TryAdd(in dart);
        }

        int sparks = Math.Min(BoltSparks, budget);
        budget -= sparks;
        for (int i = 0; i < sparks; i++)
        {
            EmitSpark(muzzle + aim * 0.5f, aim, violet, 2f, 5f, 1f, plain: true);
        }
    }

    // Mend: a ring spreading from 0.3 m to 0.9 m at the feet over 0.6 s, with bone-green motes rising
    // past head height (0.07 to 0.11 m half-size, so 7 to 11 px across at isometric scale).
    private void EmitMend(Vector3 origin, ref int budget)
    {
        Vector3 feet = new(origin.X, origin.Y - CasterFeetDrop + GroundQuadLift, origin.Z);
        if (budget > 0)
        {
            budget--;
            EmitGroundQuad(FxParticleKind.Ring, FxFrame.Ring, feet, 0f, RingStartHalfSize, RingEndHalfSize, RingLife, MendTint);
        }

        Vector3 green = MendTint * CastBrightness;
        int embers = Math.Min(MendEmbers, budget);
        budget -= embers;
        for (int i = 0; i < embers; i++)
        {
            float angle = _random.Range(0f, MathHelper.TwoPi);
            float radius = _random.Range(0.1f, 0.6f);
            FxParticle mote = new()
            {
                Kind = FxParticleKind.Ember,
                Position = feet + new Vector3(MathF.Cos(angle) * radius, 0.1f, MathF.Sin(angle) * radius),
                Velocity = new Vector3(0f, _random.Range(1.8f, 2.8f), 0f),
                Life = _random.Range(0.9f, 1.6f),
                StartHalfSize = _random.Range(0.07f, 0.11f),
                EndHalfSize = 0.02f,
                Rotation = _random.Range(0f, MathHelper.TwoPi),
                AngularVelocity = _random.Range(-1.5f, 1.5f),
                Frame = (float)FxFrame.Ember0 + _random.Range(0, 4),
                Gravity = 0.35f,
                Drag = 0.6f,
                Phase = _random.Range(0f, MathHelper.TwoPi),
                Sway = 1f,
                Plain = true,
                Color = new Vector4(green, 0f)
            };
            _pool.TryAdd(in mote);
        }
    }

    private void EmitGroundQuad(FxParticleKind kind, FxFrame frame, Vector3 position, float rotation, float startHalfSize, float endHalfSize, float life, Vector3 tint)
    {
        FxParticle quad = new()
        {
            Kind = kind,
            Position = position,
            Life = life,
            StartHalfSize = startHalfSize,
            EndHalfSize = endHalfSize,
            Rotation = rotation,
            Frame = (float)frame,
            Color = new Vector4(tint * CastBrightness, 0f)
        };
        _pool.TryAdd(in quad);
    }

    private void EmitBurstEmbers(Vector3 anchor, int count, float speedMin, float speedMax, float brightness)
    {
        for (int i = 0; i < count; i++)
        {
            EmitEmber(anchor, speedMin, speedMax, brightness);
        }
    }

    private void EmitEmber(Vector3 anchor, float speedMin, float speedMax, float brightness)
    {
        Vector3 direction = _random.UnitVector();
        direction.Y = MathF.Abs(direction.Y) * 0.6f + 0.4f;
        direction = Vector3.Normalize(direction);
        FxParticle ember = new()
        {
            Kind = FxParticleKind.Ember,
            Position = anchor,
            Velocity = direction * _random.Range(speedMin, speedMax),
            Life = _random.Range(0.8f, 1.6f),
            StartHalfSize = _random.Range(BurstEmberMinHalfSize, BurstEmberMaxHalfSize),
            EndHalfSize = BurstEmberEndHalfSize,
            Rotation = _random.Range(0f, MathHelper.TwoPi),
            AngularVelocity = _random.Range(-2f, 2f),
            Frame = (float)FxFrame.Ember0 + _random.Range(0, 4),
            Gravity = -0.3f,
            Drag = 1.6f,
            Phase = _random.Range(0f, MathHelper.TwoPi),
            Color = new Vector4(brightness, brightness, brightness, 0f)
        };
        _pool.TryAdd(in ember);
    }

    // A slow ember rising off a held torch or a street lamp, curling as it goes.
    private void EmitPlumeEmber(Vector3 origin, float sizeScale)
    {
        FxParticle ember = new()
        {
            Kind = FxParticleKind.Ember,
            Position = origin + new Vector3(_random.Range(-0.04f, 0.04f), _random.Range(-0.04f, 0.04f), _random.Range(-0.04f, 0.04f)),
            Velocity = new Vector3(_random.Range(-0.1f, 0.1f), _random.Range(0.7f, 1.3f), _random.Range(-0.1f, 0.1f)),
            Life = _random.Range(0.9f, 1.6f),
            StartHalfSize = _random.Range(PlumeEmberMinHalfSize, PlumeEmberMaxHalfSize) * sizeScale,
            EndHalfSize = PlumeEmberEndHalfSize * sizeScale,
            Rotation = _random.Range(0f, MathHelper.TwoPi),
            AngularVelocity = _random.Range(-1.5f, 1.5f),
            Frame = (float)FxFrame.Ember0 + _random.Range(0, 4),
            Gravity = 0.35f,
            Drag = 0.8f,
            Phase = _random.Range(0f, MathHelper.TwoPi),
            Sway = 1f,
            Color = new Vector4(1f, 1f, 1f, 0f)
        };
        _pool.TryAddQuietly(in ember);
    }

    private void EmitAsh(Vector3 anchor)
    {
        Vector3 direction = _random.UnitVector();
        FxParticle ash = new()
        {
            Kind = FxParticleKind.Ash,
            Position = anchor,
            Velocity = new Vector3(direction.X * 0.7f, _random.Range(0.5f, 1.4f), direction.Z * 0.7f),
            Life = _random.Range(1.4f, 2.4f),
            StartHalfSize = 0.14f,
            EndHalfSize = 0.5f,
            Rotation = _random.Range(0f, MathHelper.TwoPi),
            AngularVelocity = _random.Range(-0.8f, 0.8f),
            Frame = (float)FxFrame.Ash0 + _random.Range(0, 4),
            Gravity = 0.2f,
            Drag = 1.4f,
            Color = Premultiply(1f, 1f, 1f, 0.75f)
        };
        _pool.TryAdd(in ash);
    }

    // The brief glow of a kill, lighting the air around it.
    private void EmitFlash(Vector3 anchor)
    {
        FxParticle flash = new()
        {
            Kind = FxParticleKind.Flash,
            Position = anchor,
            Life = 0.32f,
            StartHalfSize = _random.Range(0.8f, 1.5f),
            Frame = (float)FxFrame.Halo,
            Color = new Vector4(0.75f, 0.4f, 0.16f, 0f)
        };
        flash.EndHalfSize = flash.StartHalfSize;
        _pool.TryAdd(in flash);
    }

    // Colour and coverage to the premultiplied form the blend state expects: with coverage the
    // colour is the tint weighted by it, and with none it is already the emitted light.
    private static Vector4 Premultiply(float r, float g, float b, float a)
    {
        return a > 0f ? new Vector4(r * a, g * a, b * a, a) : new Vector4(r, g, b, 0f);
    }

    private static float Smooth(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    internal enum FxLightClass : byte
    {
        None,
        Lamp,
        Torch,
        Lantern
    }
}
