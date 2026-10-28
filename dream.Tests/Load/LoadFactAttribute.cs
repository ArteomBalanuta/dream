using Xunit;

namespace Dream.Tests.Load;

// A load test: skipped unless DREAM_LOAD_TEST=1, so the default suite stays fast. Load tests run
// a real server on loopback for tens of seconds to minutes and write artifacts/load/<scenario>.json.
public sealed class LoadFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "DREAM_LOAD_TEST";

    public LoadFactAttribute()
    {
        if (!IsEnabled)
        {
            Skip = $"Load test; set {EnvironmentVariable}=1 to run it.";
        }
    }

    public static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "1", StringComparison.Ordinal);
}
