using Xunit;

namespace Dream.Tests;

// Tests that assert wall-clock bounds or drive real tick loops over loopback TCP. They flaked only
// under full parallel runs, so they run alone: DisableParallelization stops this collection from
// executing concurrently with any other collection.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingSensitiveCollection
{
    public const string Name = "TimingSensitive";
}
