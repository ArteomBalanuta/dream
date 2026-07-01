using Xunit;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Simulation.Runtime;


public class SceneEventHubTests
{
    private readonly record struct TestSceneEvent(int Value);

    [Fact]
    public void Publish_Delivers_Event_To_Typed_Subscriber()
    {
        SceneEventHub hub = new();
        int observed = 0;

        hub.Subscribe<TestSceneEvent>(evt => observed = evt.Value);

        hub.Publish(new TestSceneEvent(7));

        Assert.Equal(7, observed);
    }

    [Fact]
    public void Dispose_Subscription_Stops_Future_Delivery()
    {
        SceneEventHub hub = new();
        int observed = 0;

        IDisposable subscription = hub.Subscribe<TestSceneEvent>(evt => observed += evt.Value);
        subscription.Dispose();

        hub.Publish(new TestSceneEvent(5));

        Assert.Equal(0, observed);
    }

    [Fact]
    public void Publish_Delivers_To_Multiple_Subscribers_In_Registration_Order()
    {
        SceneEventHub hub = new();
        List<int> order = new();

        hub.Subscribe<TestSceneEvent>(_ => order.Add(1));
        hub.Subscribe<TestSceneEvent>(_ => order.Add(2));

        hub.Publish(new TestSceneEvent(9));

        Assert.Equal([1, 2], order);
    }

    [Fact]
    public void Disposing_One_Subscription_Does_Not_Remove_Other_Handlers()
    {
        SceneEventHub hub = new();
        int kept = 0;

        IDisposable removed = hub.Subscribe<TestSceneEvent>(_ => { });
        hub.Subscribe<TestSceneEvent>(evt => kept = evt.Value);

        removed.Dispose();
        hub.Publish(new TestSceneEvent(11));

        Assert.Equal(11, kept);
    }
}
