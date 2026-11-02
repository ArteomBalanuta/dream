using OpenTK.Mathematics;
using Dream.Rendering.Lighting;

namespace Dream.Rendering.Effects;

public enum FxFlashKind : byte
{
    Hit,
    Critical,
    Incoming,
    Death
}

// The brief point-light pop of a hit or a kill. Plain CPU state, fixed storage, no allocation.
// AppendTo adds the live flashes to the frame's collector after the held lights and lamps, and
// never takes a slot from one of them: it adds at most the slots LightList.MaxLights leaves free
// (strongest first), so a flash can never push a collected lamp out of the selection. The radii
// are none of the lamp, torch or lantern radii (6, 8, 10), so a flash is never drawn as a halo.
public sealed class FxFlashList
{
    public const int Capacity = 4;
    public const float MergeDistance = 1.5f;
    public const float MergeMinAge = 0.1f;
    public const float NightFloor = 0.3f;

    private readonly Vector3[] _positions = new Vector3[Capacity];
    private readonly FxFlashKind[] _kinds = new FxFlashKind[Capacity];
    private readonly float[] _ages = new float[Capacity];
    private readonly bool[] _live = new bool[Capacity];

    public int Live
    {
        get
        {
            int live = 0;
            for (int i = 0; i < Capacity; i++)
            {
                if (_live[i])
                {
                    live++;
                }
            }

            return live;
        }
    }

    public static float Radius(FxFlashKind kind) => kind switch
    {
        FxFlashKind.Critical => 4.5f,
        FxFlashKind.Incoming => 3f,
        FxFlashKind.Death => 5.5f,
        _ => 3.5f
    };

    public static float Life(FxFlashKind kind) => kind switch
    {
        FxFlashKind.Critical => 0.15f,
        FxFlashKind.Incoming => 0.12f,
        FxFlashKind.Death => 0.4f,
        _ => 0.11f
    };

    public static float Peak(FxFlashKind kind) => kind switch
    {
        FxFlashKind.Critical => 1.3f,
        FxFlashKind.Incoming => 0.7f,
        FxFlashKind.Death => 2.2f,
        _ => 0.9f
    };

    public static Vector3 BaseColor(FxFlashKind kind) => kind switch
    {
        FxFlashKind.Incoming => new Vector3(1f, 0.28f, 0.12f),
        FxFlashKind.Death => new Vector3(1f, 0.52f, 0.22f),
        _ => new Vector3(1f, 0.62f, 0.3f)
    };

    public void Trigger(Vector3 position, FxFlashKind kind)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
        {
            return;
        }

        float peak = Peak(kind);
        for (int i = 0; i < Capacity; i++)
        {
            if (!_live[i] || (_positions[i] - position).LengthSquared > MergeDistance * MergeDistance)
            {
                continue;
            }

            // A nearby live flash absorbs the trigger: only a kill, or a hit at least as strong
            // as what is left of it and old enough to have been seen, restarts it.
            if (kind == FxFlashKind.Death || (peak >= Intensity(i) && _ages[i] >= MergeMinAge))
            {
                Start(i, position, kind);
            }

            return;
        }

        int slot = -1;
        float weakest = float.MaxValue;
        for (int i = 0; i < Capacity; i++)
        {
            float strength = _live[i] ? Intensity(i) : -1f;
            if (strength < weakest)
            {
                weakest = strength;
                slot = i;
            }
        }

        if (slot >= 0 && (weakest < 0f || peak >= weakest))
        {
            Start(slot, position, kind);
        }
    }

    public void Advance(float deltaSeconds)
    {
        float dt = deltaSeconds > 0f ? MathF.Min(deltaSeconds, FxSimulation.MaxDeltaSeconds) : 0f;
        for (int i = 0; i < Capacity; i++)
        {
            if (!_live[i])
            {
                continue;
            }

            _ages[i] += dt;
            if (_ages[i] >= Life(_kinds[i]))
            {
                _live[i] = false;
            }
        }
    }

    // Adds the live flashes, strongest first, up to the slots the collector's selection still has
    // free. Returns how many it added.
    public int AppendTo(LightCollector lights, float night)
    {
        ArgumentNullException.ThrowIfNull(lights);
        int free = LightList.MaxLights - lights.Count;
        float nightScale = NightFloor + (1f - NightFloor) * Math.Clamp(night, 0f, 1f);
        int added = 0;
        int used = 0;
        while (added < free)
        {
            int best = -1;
            float bestStrength = 0f;
            for (int i = 0; i < Capacity; i++)
            {
                if ((used & (1 << i)) != 0 || !_live[i])
                {
                    continue;
                }

                float strength = Intensity(i);
                if (strength > bestStrength)
                {
                    bestStrength = strength;
                    best = i;
                }
            }

            if (best < 0)
            {
                break;
            }

            used |= 1 << best;
            FxFlashKind kind = _kinds[best];
            Vector3 color = BaseColor(kind) * (bestStrength * nightScale);
            PointLight light = new(_positions[best], Radius(kind), color);
            lights.Add(in light);
            added++;
        }

        return added;
    }

    // peak * (1 - age / life)^2, before the night scale.
    private float Intensity(int index)
    {
        FxFlashKind kind = _kinds[index];
        float remaining = 1f - _ages[index] / Life(kind);
        return remaining > 0f ? Peak(kind) * remaining * remaining : 0f;
    }

    private void Start(int index, Vector3 position, FxFlashKind kind)
    {
        _positions[index] = position;
        _kinds[index] = kind;
        _ages[index] = 0f;
        _live[index] = true;
    }
}
