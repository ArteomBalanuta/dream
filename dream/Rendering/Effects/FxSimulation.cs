using Dream.Rendering.Lighting;
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
    FxViewBasis Basis = default);

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
    private const float RadiusTolerance = 0.01f;
    private const float ClockWrap = 4096f;
    private const float FlameDepthBias = 0.55f;
    private const float FlashDepthBias = 0.4f;
    private const float TorchHaloHalfSize = 0.7f;
    private const float LanternHaloHalfSize = 0.55f;
    private const float LampHaloHalfSize = 1.25f;
    private const float HeldHaloLift = 0.12f;
    private const float FlameLift = 0.1f;
    private const float FlameHalfSize = 0.20f;
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
    private const float SparkStretchPerSpeed = 0.03f;
    private const float MaxSparkStretch = 6f;
    private static readonly Vector3 SparkCoolTint = new(1f, 0.55f, 0.25f);
    private static readonly Vector3 AshWarmTint = new(1f, 0.62f, 0.38f);
    private static readonly Vector3 AshColdTint = new(0.6f, 0.58f, 0.56f);
    private static readonly Vector3 HeatBirth = new(1f, 0.62f, 0.2f);
    private static readonly Vector3 HeatMid = new(0.95f, 0.33f, 0.07f);
    private static readonly Vector3 HeatEnd = new(0.42f, 0.07f, 0.015f);
    private static readonly Vector3 HeldHaloTint = new(0.375f, 0.27f, 0.157f);
    private static readonly Vector3 LampHaloTint = new(0.3f, 0.22f, 0.13f);

    private readonly FxParticlePool _pool = new(FxLimits.ParticleCapacity);
    private readonly FxInstance[] _halos = new FxInstance[FxLimits.HaloBudget];
    private FxRandom _random;
    private int _haloCount;
    private int _dustCount;
    private Vector3 _cameraPosition;
    private Vector3 _viewCentre;
    private Vector3 _playerPosition;
    private float _nightFactor;
    private float _clock;
    private FxViewBasis _basis;
    // The lights nearest the view centre, nearest first, which light the dust.
    private readonly PointLight[] _dustLights = new PointLight[DustLightCount];
    private readonly float[] _dustLightDistances = new float[DustLightCount];
    private int _dustLightCount;

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

    public void SpawnHit(Vector3 anchor, FloatingCombatTextKind kind)
    {
        anchor.Y -= AnchorDrop;
        switch (kind)
        {
            case FloatingCombatTextKind.Critical:
                EmitSparks(anchor, 16, new Vector3(1f, 0.88f, 0.58f), 3f, 8f, 1f);
                EmitBurstEmbers(anchor, 3, 0.8f, 2.4f, 0.85f);
                break;
            case FloatingCombatTextKind.Incoming:
                EmitSparks(anchor, 6, new Vector3(1f, 0.34f, 0.14f), 2f, 5f, 1f);
                EmitBurstEmbers(anchor, 2, 0.6f, 1.6f, 0.7f);
                break;
            default:
                EmitSparks(anchor, 8, new Vector3(1f, 0.72f, 0.36f), 2.5f, 6f, 1f);
                break;
        }
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

    public void Advance(in FxFrameInputs inputs, ReadOnlySpan<PointLight> lights)
    {
        float dt = inputs.DeltaSeconds;
        dt = dt > 0f ? MathF.Min(dt, MaxDeltaSeconds) : 0f;
        _cameraPosition = inputs.CameraPosition;
        _playerPosition = inputs.PlayerPosition;
        _viewCentre = ViewCentre(in inputs);
        _nightFactor = Math.Clamp(inputs.NightFactor, 0f, 1f);
        _basis = inputs.Basis;
        _clock += dt;
        if (_clock >= ClockWrap)
        {
            _clock -= ClockWrap;
        }

        StepParticles(dt);
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
            if (_pool[i].Kind == FxParticleKind.Ash)
            {
                WriteParticle(ref _pool[i], ref destination[written]);
                written++;
            }
        }

        for (int i = 0; i < _pool.Count && written - start < particleCount; i++)
        {
            if (_pool[i].Kind != FxParticleKind.Ash)
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
        float flameNight = 0.75f + 0.25f * _nightFactor;
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
            float phi = i * 2.4f;
            float flicker;
            float halfSize;
            Vector3 tint;
            Vector3 position;
            if (lamp)
            {
                float hash = light.Position.X * 12.9898f + light.Position.Z * 78.233f;
                float slowPhi = (hash - MathF.Floor(hash)) * MathHelper.TwoPi;
                flicker = 1f + 0.04f * MathF.Sin(1.7f * _clock + slowPhi) + 0.025f * MathF.Sin(2.9f * _clock + 1.3f * slowPhi);
                halfSize = LampHaloHalfSize;
                tint = LampHaloTint;
                position = light.Position;
            }
            else
            {
                flicker = torch
                    ? 1f + 0.07f * MathF.Sin(7.3f * _clock + phi) + 0.05f * MathF.Sin(13.1f * _clock + 1.7f * phi) + 0.03f * MathF.Sin(23.9f * _clock + 0.6f * phi)
                    : 1f;
                halfSize = (torch ? TorchHaloHalfSize : LanternHaloHalfSize) * (1f + 0.5f * (flicker - 1f));
                tint = HeldHaloTint;
                position = fxPosition + Vector3.UnitY * HeldHaloLift;
            }

            float gain = fade * flicker;
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
                float alpha = (1f - _nightFactor) * cull;
                float flame = cull * flicker * flameNight;
                // By day the flame is tinted saturated orange; only red is held at the alpha.
                float tintG = 0.62f + 0.38f * _nightFactor;
                float tintB = 0.25f + 0.75f * _nightFactor;
                _halos[_haloCount++] = new FxInstance
                {
                    Position = fxPosition + Vector3.UnitY * FlameLift,
                    HalfSize = FlameHalfSize,
                    Color = FxInstance.PackColor(MathF.Max(flame, alpha), flame * tintG, flame * tintB, alpha),
                    Rotation = 0f,
                    Frame = (float)FxFrame.Flame0 + (((int)(_clock * 12f + i)) & 3),
                    DepthBias = FlameDepthBias
                };
            }
        }
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

    // Lamp, torch and lantern lights all reach the collector with their catalog radius, so the
    // radius is the tag. Anything else (debug rings, future lights) gets no halo.
    private static bool TryClassify(float radius, out FxLightClass lightClass)
    {
        if (MathF.Abs(radius - LampLightIndex.LampRadius) < RadiusTolerance)
        {
            lightClass = FxLightClass.Lamp;
        }
        else if (MathF.Abs(radius - HeldLightCatalog.TorchRadius) < RadiusTolerance)
        {
            lightClass = FxLightClass.Torch;
        }
        else if (MathF.Abs(radius - HeldLightCatalog.LanternRadius) < RadiusTolerance)
        {
            lightClass = FxLightClass.Lantern;
        }
        else
        {
            lightClass = FxLightClass.None;
        }

        return lightClass != FxLightClass.None;
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
        switch (p.Kind)
        {
            case FxParticleKind.Spark:
                fade = MathF.Pow(1f - t, 0.7f);
                tint = Vector3.Lerp(Vector3.One, SparkCoolTint, t);
                // Lay the streak along the velocity as the camera sees it. A velocity straight at
                // (or away from) the lens, or no basis at all, projects to nothing and stays square.
                Streak(p.Velocity, in _basis, halfSize, out rotation, out stretch);
                break;
            case FxParticleKind.Ember:
                fade = MathF.Min(1f, t / 0.08f) * MathF.Pow(1f - t, 0.6f) * (0.85f + 0.15f * MathF.Sin(p.Age * 17f + p.Phase));
                tint = HeatRamp(t);
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
        }

        // Fade out near the lens: a speck at 10 cm would otherwise cover a patch of the screen.
        float nearStart = p.Kind == FxParticleKind.Dust ? 0.4f : 0.25f;
        float nearSpan = p.Kind == FxParticleKind.Dust ? 1.6f : 0.9f;
        fade *= Smooth((Vector3.Distance(p.Position, _cameraPosition) - nearStart) / nearSpan);
        fade = MathF.Max(fade, 0f);

        instance.Position = p.Position;
        instance.HalfSize = halfSize;
        instance.Color = FxInstance.PackColor(p.Color.X * tint.X * fade, p.Color.Y * tint.Y * fade, p.Color.Z * tint.Z * fade, p.Color.W * fade);
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

    private void EmitSpark(Vector3 anchor, Vector3 away, Vector3 rgb, float speedMin, float speedMax, float sizeScale)
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
            Color = Premultiply(rgb.X * heat, rgb.Y * heat, rgb.Z * heat, 0f)
        };
        _pool.TryAdd(in spark);
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

    private enum FxLightClass : byte
    {
        None,
        Lamp,
        Torch,
        Lantern
    }
}
