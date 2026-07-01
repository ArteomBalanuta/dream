using Xunit;

public class ImGuiTextInputQueueTests
{
    [Fact]
    public void Enqueue_Ignores_Null_Character()
    {
        ImGuiTextInputQueue queue = new();

        queue.Enqueue(0);

        Assert.Empty(queue.Drain());
    }

    [Fact]
    public void Drain_Returns_Queued_Characters_And_Clears_Queue()
    {
        ImGuiTextInputQueue queue = new();
        queue.Enqueue('1');
        queue.Enqueue('.');
        queue.Enqueue('5');

        IReadOnlyList<uint> drained = queue.Drain();

        Assert.Equal([(uint)'1', (uint)'.', (uint)'5'], drained);
        Assert.Empty(queue.Drain());
    }
}
