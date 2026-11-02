using OpenTK.Mathematics;

namespace Dream.Rendering.Effects;

// Contact blobs under the actors: a soft dark quad laid on the ground a few centimetres above it.
// The scene fills it each frame (Clear, then Add per actor) and Write turns it into ground-aligned
// instances that go ahead of the particles, so a later additive flame is not dimmed by the blob.
// Fixed storage, no allocation.
public sealed class FxShadowList
{
    public const float Lift = 0.03f;
    public const float PlayerRadius = 0.5f;
    public const float EnemyRadiusScale = 1.9f;
    // Blob strength: stronger by day, when it has to read on bright ground, and 0.7 at night.
    public const float DayAlpha = 0.95f;
    public const float NightAlpha = 0.7f;
    public const float MaxAirHeight = 2.5f;
    public const float MaxViewDistance = 30f;
    public const float MinCameraDistance = 1.6f;

    private readonly Vector3[] _feet = new Vector3[FxLimits.ShadowBudget];
    private readonly float[] _groundY = new float[FxLimits.ShadowBudget];
    private readonly float[] _radius = new float[FxLimits.ShadowBudget];
    private int _count;
    private Vector3 _viewCentre;
    private bool _culling;

    public int Count => _count;

    // Empties the list without a distance cull.
    public void Clear()
    {
        _count = 0;
        _culling = false;
    }

    // Empties the list and culls by distance to the view centre in Add, so blobs the view would
    // drop anyway cannot take a budget slot from a near one.
    public void Begin(Vector3 viewCentre)
    {
        _count = 0;
        _viewCentre = viewCentre;
        _culling = true;
    }

    public void Add(Vector3 feet, float groundY, float radius)
    {
        if (_count >= FxLimits.ShadowBudget || !float.IsFinite(feet.X) || !float.IsFinite(feet.Y) || !float.IsFinite(feet.Z)
            || !float.IsFinite(groundY) || !(radius > 0f) || !float.IsFinite(radius))
        {
            return;
        }

        if (_culling && (new Vector3(feet.X, groundY, feet.Z) - _viewCentre).LengthSquared > MaxViewDistance * MaxViewDistance)
        {
            return;
        }

        _feet[_count] = feet;
        _groundY[_count] = groundY;
        _radius[_count] = radius;
        _count++;
    }

    // Writes one instance per blob from the start of the destination and returns how many; never
    // writes past the destination.
    public int Write(FxInstance[] destination, Vector3 camera, Vector3 viewCentre, float night)
    {
        ArgumentNullException.ThrowIfNull(destination);
        int written = 0;
        float alpha = DayAlpha + (NightAlpha - DayAlpha) * Math.Clamp(night, 0f, 1f);
        for (int i = 0; i < _count && written < destination.Length; i++)
        {
            Vector3 feet = _feet[i];
            float groundY = _groundY[i];
            Vector3 onGround = new(feet.X, groundY, feet.Z);
            if ((onGround - viewCentre).LengthSquared > MaxViewDistance * MaxViewDistance
                || (onGround - camera).LengthSquared < MinCameraDistance * MinCameraDistance)
            {
                continue;
            }

            float k = Math.Clamp(1f - (feet.Y - groundY) / MaxAirHeight, 0f, 1f);
            destination[written++] = new FxInstance
            {
                Position = new Vector3(feet.X, groundY + Lift, feet.Z),
                HalfSize = _radius[i] * (1f + 0.3f * (1f - k)),
                Color = FxInstance.PackColor(0f, 0f, 0f, alpha * (0.35f + 0.65f * k)),
                Rotation = 0f,
                Frame = (float)FxFrame.Blob,
                DepthBias = 0f,
                Stretch = FxInstance.GroundAligned
            };
        }

        return written;
    }
}
