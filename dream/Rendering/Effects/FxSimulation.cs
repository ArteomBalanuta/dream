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
    float NightFactor);

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
    public const float HaloDayStrength = 0.35f;
    public const float DepthBiasLamp = 0.9f;
    public const float DepthBiasHeld = 0.2f;

    private const int DustSpawnsPerFrame = 12;
    private const int MaxEmberTorches = 8;
    private const float TorchEmbersPerSecond = 3f;
    private const float RadiusTolerance = 0.01f;

    private readonly FxParticlePool _pool = new(FxLimits.ParticleCapacity);
    private readonly FxInstance[] _halos = new FxInstance[FxLimits.HaloBudget];
    private FxRandom _random;
    private int _haloCount;
    private int _dustCount;
    private Vector3 _cameraPosition;
    private Vector3 _viewCentre;
    private float _nightFactor;

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
        switch (kind)
        {
            case FloatingCombatTextKind.Critical:
                for (int i = 0; i < 12; i++)
                {
                    EmitSpark(anchor, new Vector3(1f, 0.86f, 0.5f), 3f, 8f, 1.3f);
                }

                for (int i = 0; i < 3; i++)
                {
                    EmitEmber(anchor, 0.8f, 2.4f, 0.6f);
                }

                break;
            case FloatingCombatTextKind.Incoming:
                for (int i = 0; i < 5; i++)
                {
                    EmitSpark(anchor, new Vector3(1f, 0.26f, 0.12f), 2f, 5f, 1f);
                }

                for (int i = 0; i < 2; i++)
                {
                    EmitEmber(anchor, 0.6f, 1.6f, 0.5f);
                }

                break;
            default:
                for (int i = 0; i < 7; i++)
                {
                    EmitSpark(anchor, new Vector3(1f, 0.68f, 0.3f), 2.5f, 6f, 1f);
                }

                break;
        }
    }

    public void SpawnDeath(Vector3 anchor)
    {
        for (int i = 0; i < 18; i++)
        {
            EmitEmber(anchor, 1f, 3.5f, 1f);
        }

        for (int i = 0; i < 14; i++)
        {
            EmitSpark(anchor, new Vector3(1f, 0.6f, 0.26f), 3f, 7f, 1.1f);
        }

        for (int i = 0; i < 7; i++)
        {
            EmitAsh(anchor);
        }
    }

    public void Advance(in FxFrameInputs inputs, ReadOnlySpan<PointLight> lights)
    {
        float dt = inputs.DeltaSeconds;
        dt = dt > 0f ? MathF.Min(dt, MaxDeltaSeconds) : 0f;
        _cameraPosition = inputs.CameraPosition;
        _viewCentre = ViewCentre(in inputs);
        _nightFactor = Math.Clamp(inputs.NightFactor, 0f, 1f);

        StepParticles(dt);
        TopUpDust();
        ScanLights(lights, dt);
    }

    // Particles first (ordered by pool slot, which swap-removal shuffles), then at most
    // HaloBudget halos. Never writes past the destination.
    public int WriteInstances(FxInstance[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        int written = 0;
        int particleCount = Math.Min(_pool.Count, destination.Length);
        for (int i = 0; i < particleCount; i++)
        {
            WriteParticle(ref _pool[i], ref destination[written]);
            written++;
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
                StartHalfSize = _random.Range(0.012f, 0.026f),
                Rotation = _random.Range(0f, MathHelper.TwoPi),
                AngularVelocity = _random.Range(-0.4f, 0.4f),
                Frame = (float)FxFrame.Dust0 + _random.Range(0, 4),
                Color = Premultiply(0.62f, 0.66f, 0.72f, 0.2f)
            };
            mote.EndHalfSize = mote.StartHalfSize;
            if (!_pool.TryAddQuietly(in mote))
            {
                break;
            }

            _dustCount++;
        }
    }

    // One pass over the frame's lights: build the halo list and let held torches shed embers.
    private void ScanLights(ReadOnlySpan<PointLight> lights, float dt)
    {
        _haloCount = 0;
        float strength = HaloDayStrength + (HaloNightStrength - HaloDayStrength) * _nightFactor;
        float maxViewSquared = HaloMaxViewDistance * HaloMaxViewDistance;
        float minCameraSquared = HaloMinCameraDistance * HaloMinCameraDistance;
        int emberTorches = 0;
        for (int i = 0; i < lights.Length && _haloCount < FxLimits.HaloBudget; i++)
        {
            ref readonly PointLight light = ref lights[i];
            if (!TryClassify(light.Radius, out FxLightClass lightClass))
            {
                continue;
            }

            if ((light.Position - _viewCentre).LengthSquared > maxViewSquared)
            {
                continue;
            }

            if (lightClass == FxLightClass.Torch && emberTorches < MaxEmberTorches)
            {
                emberTorches++;
                if (_random.NextFloat() < TorchEmbersPerSecond * dt)
                {
                    EmitTorchEmber(light.Position);
                }
            }

            if ((light.Position - _cameraPosition).LengthSquared < minCameraSquared)
            {
                continue;
            }

            bool lamp = lightClass == FxLightClass.Lamp;
            float distance = MathF.Sqrt((light.Position - _viewCentre).LengthSquared);
            float gain = (lamp ? 0.42f : 0.5f) * strength * Smooth((HaloMaxViewDistance - distance) / HaloFadeDistance);
            _halos[_haloCount++] = new FxInstance
            {
                Position = light.Position,
                HalfSize = lightClass switch { FxLightClass.Lamp => 1.7f, FxLightClass.Torch => 1.1f, _ => 0.95f },
                Color = FxInstance.PackColor(light.Color.X * gain, light.Color.Y * gain, light.Color.Z * gain, 0f),
                Rotation = 0f,
                Frame = (float)(lamp ? FxFrame.Halo : FxFrame.HaloCore),
                DepthBias = lamp ? DepthBiasLamp : DepthBiasHeld
            };
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
        float fade = 1f;
        switch (p.Kind)
        {
            case FxParticleKind.Spark:
                fade = 1f - t;
                break;
            case FxParticleKind.Ember:
                fade = MathF.Min(1f, t / 0.12f) * (1f - t) * (0.8f + 0.2f * MathF.Sin(p.Age * 24f + p.Phase));
                break;
            case FxParticleKind.Ash:
                fade = MathF.Min(1f, t / 0.15f) * (1f - t);
                break;
            case FxParticleKind.Dust:
                Vector3 relative = (p.Position - _viewCentre) / DustHalfExtents;
                float edge = 1f - MathF.Max(MathF.Abs(relative.X), MathF.Max(MathF.Abs(relative.Y), MathF.Abs(relative.Z)));
                fade = Smooth(edge * 4f) * (1f - 0.5f * _nightFactor);
                break;
        }

        // Fade out near the lens: a speck at 10 cm would otherwise cover a patch of the screen.
        float nearStart = p.Kind == FxParticleKind.Dust ? 0.4f : 0.25f;
        float nearSpan = p.Kind == FxParticleKind.Dust ? 1.6f : 0.9f;
        fade *= Smooth((Vector3.Distance(p.Position, _cameraPosition) - nearStart) / nearSpan);
        fade = MathF.Max(fade, 0f);

        instance.Position = p.Position;
        instance.HalfSize = p.StartHalfSize + (p.EndHalfSize - p.StartHalfSize) * t;
        instance.Color = FxInstance.PackColor(p.Color.X * fade, p.Color.Y * fade, p.Color.Z * fade, p.Color.W * fade);
        instance.Rotation = p.Rotation;
        instance.Frame = p.Frame;
        instance.DepthBias = 0f;
    }

    private void EmitSpark(Vector3 anchor, Vector3 rgb, float speedMin, float speedMax, float sizeScale)
    {
        Vector3 direction = _random.UnitVector();
        direction.Y += 0.5f;
        direction = Vector3.Normalize(direction);
        float heat = _random.Range(0.75f, 1f);
        FxParticle spark = new()
        {
            Kind = FxParticleKind.Spark,
            Position = anchor,
            Velocity = direction * _random.Range(speedMin, speedMax),
            Life = _random.Range(0.28f, 0.55f),
            StartHalfSize = _random.Range(0.035f, 0.06f) * sizeScale,
            EndHalfSize = 0.01f,
            Rotation = _random.Range(0f, MathHelper.TwoPi),
            AngularVelocity = _random.Range(-6f, 6f),
            Frame = (float)FxFrame.Spark0 + _random.Range(0, 4),
            Gravity = -9f,
            Drag = 1.6f,
            Color = Premultiply(rgb.X * heat, rgb.Y * heat, rgb.Z * heat, 0f)
        };
        _pool.TryAdd(in spark);
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
            Life = _random.Range(0.9f, 2.0f),
            StartHalfSize = _random.Range(0.04f, 0.07f),
            EndHalfSize = 0.015f,
            Rotation = _random.Range(0f, MathHelper.TwoPi),
            AngularVelocity = _random.Range(-2f, 2f),
            Frame = (float)FxFrame.Ember0 + _random.Range(0, 4),
            Gravity = -0.9f,
            Drag = 1.2f,
            Phase = _random.Range(0f, MathHelper.TwoPi),
            Color = Premultiply(0.9f * brightness, 0.24f * brightness, 0.07f * brightness, 0f)
        };
        _pool.TryAdd(in ember);
    }

    // A slow, dull-red ember rising off a held torch.
    private void EmitTorchEmber(Vector3 torch)
    {
        FxParticle ember = new()
        {
            Kind = FxParticleKind.Ember,
            Position = torch + new Vector3(_random.Range(-0.08f, 0.08f), _random.Range(0.05f, 0.2f), _random.Range(-0.08f, 0.08f)),
            Velocity = new Vector3(_random.Range(-0.25f, 0.25f), _random.Range(0.5f, 1.2f), _random.Range(-0.25f, 0.25f)),
            Life = _random.Range(1.2f, 2.4f),
            StartHalfSize = _random.Range(0.025f, 0.045f),
            EndHalfSize = 0.01f,
            Rotation = _random.Range(0f, MathHelper.TwoPi),
            AngularVelocity = _random.Range(-1.5f, 1.5f),
            Frame = (float)FxFrame.Ember0 + _random.Range(0, 4),
            Gravity = 0.2f,
            Drag = 0.6f,
            Phase = _random.Range(0f, MathHelper.TwoPi),
            Color = Premultiply(0.85f, 0.22f, 0.06f, 0f)
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
            Velocity = new Vector3(direction.X * 0.7f, _random.Range(0.3f, 1.1f), direction.Z * 0.7f),
            Life = _random.Range(1.2f, 2.2f),
            StartHalfSize = _random.Range(0.1f, 0.16f),
            EndHalfSize = _random.Range(0.28f, 0.42f),
            Rotation = _random.Range(0f, MathHelper.TwoPi),
            AngularVelocity = _random.Range(-0.8f, 0.8f),
            Frame = (float)FxFrame.Ash0 + _random.Range(0, 4),
            Gravity = 0.1f,
            Drag = 1.4f,
            Color = Premultiply(0.5f, 0.47f, 0.44f, 0.55f)
        };
        _pool.TryAdd(in ash);
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
