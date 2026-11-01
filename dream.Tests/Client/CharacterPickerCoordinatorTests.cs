using Xunit;
using Dream.Client.Networking;
using Dream.Client.Startup;
using Dream.Multiplayer.Protocol;
using Dream.Server;

namespace Dream.Tests.Client;

// M1-C: the connection screen asks for the account's characters before it joins. None: it connects with the
// typed name as before. Some: the picker opens and nothing connects until a character (or a new name) is chosen.
public sealed class CharacterPickerCoordinatorTests
{
    [Fact]
    public async Task NoCharacters_ConnectsWithTheTypedName_AndOpensNoPicker()
    {
        Connector connector = new();
        NetworkSessionCoordinator coordinator = new(new Server(), connector, characterList: new Source([]));

        coordinator.BeginConnectWithPicker(new ClientConnectionOptions("10.0.0.2", "5057", "Typed"));
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Null(coordinator.PickerCharacters);
        Assert.Equal(["Typed"], connector.Names);
    }

    [Fact]
    public async Task Characters_OpenThePicker_AndConnectOnlyWhenOneIsChosen()
    {
        Connector connector = new();
        NetworkSessionCoordinator coordinator = new(
            new Server(),
            connector,
            characterList: new Source([new CharacterListEntry("Alice", 4), new CharacterListEntry("Bob", 2)]));
        ClientConnectionOptions options = new("10.0.0.2", "5057", "Typed");

        coordinator.BeginConnectWithPicker(options);
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(2, coordinator.PickerCharacters!.Count);
        Assert.Empty(connector.Names);
        Assert.False(coordinator.IsBusy);

        coordinator.ChooseCharacter(options, "Bob");
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Null(coordinator.PickerCharacters);
        Assert.Equal(["Bob"], connector.Names);
    }

    [Fact]
    public async Task WithoutASource_ConnectIsTheOldConnect()
    {
        Connector connector = new();
        NetworkSessionCoordinator coordinator = new(new Server(), connector);

        coordinator.BeginConnectWithPicker(new ClientConnectionOptions("10.0.0.2", "5057", "Typed"));
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(["Typed"], connector.Names);
    }

    private sealed class Source(IReadOnlyList<CharacterListEntry> characters) : ICharacterListSource
    {
        public Task<IReadOnlyList<CharacterListEntry>> FetchAsync(RemoteClientOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(characters);
    }

    private sealed class Connector : IRemoteSessionConnector
    {
        public List<string> Names { get; } = [];

        public ValueTask<RemoteClientSession> ConnectAsync(RemoteClientOptions options, CancellationToken cancellationToken = default)
        {
            Names.Add(options.DisplayName);
            return ValueTask.FromException<RemoteClientSession>(new InvalidOperationException("no server in this test"));
        }
    }

    private sealed class Server : ILocalServerHandle
    {
        public bool IsReady => true;

        public int BoundPort => 6123;

        public string? ErrorMessage => null;

        public DedicatedServerPerformanceSnapshot LatestPerformance => DedicatedServerPerformanceSnapshot.Empty;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
