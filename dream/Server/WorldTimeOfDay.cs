using System.Globalization;
using Dream.Simulation.World;

namespace Dream.Server;

// DREAM_TIME_OF_DAY: pure parsing and tick arithmetic. Hours are in [0, 24).
//
// Named times (picked from the SkyModelKeyframes table so each looks like its name):
//   dawn     06:00  the sunrise keyframe (sun on the east horizon, orange horizon); WorldDayPhase.Dawn
//   morning  08:00  just after the 07:30 morning keyframe; the world's new-game start time
//   noon     12:00  the high near-white sun
//   dusk     18:00  the sunset keyframe (sun on the west horizon, saturated orange); WorldDayPhase.Dusk
//   evening  18:00  alias of dusk
//   midnight 00:00  the night keyframe, moon highest
//   night    22:00  inside the flat night keyframes (20:00-04:00): full darkness and stars
public static class WorldTimeOfDay
{
    public const double Dawn = 6d;
    public const double Morning = 8d;
    public const double Noon = 12d;
    public const double Dusk = 18d;
    public const double Midnight = 0d;
    public const double Night = 22d;

    // Accepts "HH:MM" (0-23 : 0-59), a decimal hour in [0, 24), or a name; case-insensitive, trimmed.
    public static bool TryParse(string? value, out double hours)
    {
        hours = 0d;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string text = value.Trim();
        switch (text.ToLowerInvariant())
        {
            case "dawn": hours = Dawn; return true;
            case "morning": hours = Morning; return true;
            case "noon": hours = Noon; return true;
            case "dusk":
            case "evening": hours = Dusk; return true;
            case "midnight": hours = Midnight; return true;
            case "night": hours = Night; return true;
        }

        int colon = text.IndexOf(':');
        if (colon >= 0)
        {
            string hourText = text[..colon];
            string minuteText = text[(colon + 1)..];
            if (!IsDigits(hourText, 2) || !IsDigits(minuteText, 2) ||
                !int.TryParse(hourText, NumberStyles.None, CultureInfo.InvariantCulture, out int hour) ||
                !int.TryParse(minuteText, NumberStyles.None, CultureInfo.InvariantCulture, out int minute) ||
                hour > 23 || minute > 59)
            {
                return false;
            }

            hours = hour + minute / 60d;
            return true;
        }

        if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double decimalHours) ||
            !double.IsFinite(decimalHours) || decimalHours < 0d || decimalHours >= 24d)
        {
            return false;
        }

        hours = decimalHours;
        return true;
    }

    // "HH:MM" of hours, rounded to the nearest minute (24:00 wraps to 00:00).
    public static string Format(double hours)
    {
        int totalMinutes = (int)Math.Round(hours * 60d) % (24 * 60);
        return $"{totalMinutes / 60:00}:{totalMinutes % 60:00}";
    }

    // Ticks to add to worldTicks so the clock's time of day is hours: 0 when it already is, else
    // in [1, TicksPerDay), wrapping to the next day. Same formula as WorldClock.TimeOfDay01.
    public static long ForwardTicks(WorldClockSettings settings, long worldTicks, double hours)
    {
        long perDay = settings.TicksPerDay;
        long target = (long)Math.Round(hours / 24d * perDay) % perDay;
        long current = (worldTicks + settings.StartOffsetTicks) % perDay;
        return ((target - current) % perDay + perDay) % perDay;
    }

    private static bool IsDigits(string text, int maxLength)
    {
        if (text.Length == 0 || text.Length > maxLength)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        return true;
    }
}
