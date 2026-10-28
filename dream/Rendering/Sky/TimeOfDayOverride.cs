namespace Dream.Rendering.Sky;

// A debug override for the time of day the sky and lighting use, on this client only. It is set
// from the Settings window and never sent to the server: the replicated world clock keeps running
// and the other clients keep seeing it.
public sealed class TimeOfDayOverride
{
    public const float HoursPerDay = 24f;

    // Noon, so turning the override on without moving the slider shows today's reference look.
    private float _timeOfDay01 = 0.5f;

    public bool Enabled { get; set; }

    // Wrapped into [0, 1). A non-finite value is ignored.
    public float TimeOfDay01
    {
        get => _timeOfDay01;
        set
        {
            if (float.IsFinite(value))
            {
                _timeOfDay01 = Wrap01(value);
            }
        }
    }

    // The slider's view of TimeOfDay01, in hours [0, 24).
    public float Hours
    {
        get => _timeOfDay01 * HoursPerDay;
        set => TimeOfDay01 = value / HoursPerDay;
    }

    // The time of day to render: the override when it is enabled, otherwise the replicated time.
    public float Resolve(float replicated) => Enabled ? _timeOfDay01 : replicated;

    private static float Wrap01(float value)
    {
        float wrapped = value - MathF.Floor(value);
        // value - floor(value) rounds to 1 for tiny negative inputs.
        return wrapped >= 1f ? 0f : wrapped;
    }
}
