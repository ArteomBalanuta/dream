using Xunit;

namespace Dream.Tests;

// RenderThreadAffinity is process-wide: while a test has it bound to its own thread, any other
// test that reaches a render-thread assertion on another thread (Render.Dispose,
// AnimatedModel's upload constructor, Shader, TextureDecoder) throws. Tests that bind it are put
// in this collection, which runs alone (after the parallel collections), so no other test can
// observe the binding. They take milliseconds.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RenderThreadAffinityCollection
{
    public const string Name = "RenderThreadAffinity";
}
