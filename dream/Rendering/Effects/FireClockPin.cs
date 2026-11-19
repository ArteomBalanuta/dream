namespace Dream.Rendering.Effects;

// DREAM_FIRE_CLOCK: pins the clock the fire flicker reads (torch, brazier and lamp lights, their shadows, the halo
// brightness and the flame frame), so a capture is reproducible. Read once. Unset, blank or non-finite means the live
// clock. It does not pin DREAM_FX_CLOCK's consumers (clouds, sway, mist), and particles and embers stay live.
internal static class FireClockPin
{
    public const string EnvironmentVariable = "DREAM_FIRE_CLOCK";

    public static readonly double? Seconds = Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static double? Parse(string? text) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)
        && double.IsFinite(v) ? v : null;

    public static double Resolve(double liveSeconds) => Resolve(liveSeconds, Seconds);

    public static double Resolve(double liveSeconds, double? pinnedSeconds) => pinnedSeconds ?? liveSeconds;
}
