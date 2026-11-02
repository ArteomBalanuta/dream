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

    [Fact]
    public async Task ConnectWithThePickerOpen_ConnectsAsTheTypedNewName_WithoutFetchingAgain()
    {
        Connector connector = new();
        Source source = new([new CharacterListEntry("Alice", 4)]);
        NetworkSessionCoordinator coordinator = new(new Server(), connector, characterList: source);

        coordinator.BeginConnectWithPicker(new ClientConnectionOptions("10.0.0.2", "5057", "Typed"));
        await coordinator.WaitForPendingConnectionAsync();
        Assert.NotNull(coordinator.PickerCharacters);

        coordinator.BeginConnectWithPicker(new ClientConnectionOptions("10.0.0.2", "5057", "  Brand New "));
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(1, source.Fetches);
        Assert.Equal(["Brand New"], connector.Names);
    }

    [Fact]
    public async Task CancelClosesThePicker_AndTheNextConnectFetchesAgain()
    {
        Connector connector = new();
        Source source = new([new CharacterListEntry("Alice", 4)]);
        NetworkSessionCoordinator coordinator = new(new Server(), connector, characterList: source);
        ClientConnectionOptions options = new("10.0.0.2", "5057", "Typed");

        coordinator.BeginConnectWithPicker(options);
        await coordinator.WaitForPendingConnectionAsync();
        coordinator.CancelPicker();
        Assert.Null(coordinator.PickerCharacters);
        coordinator.BeginConnectWithPicker(options);
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(2, source.Fetches);
        Assert.NotNull(coordinator.PickerCharacters);
        Assert.Empty(connector.Names);
    }

    [Fact]
    public async Task ServerFullRightAfterAnEmptyProbe_IsRetriedOnce()
    {
        NetworkSessionCoordinator.ProbeSlotRetryDelay = TimeSpan.FromMilliseconds(1);
        Connector connector = new() { FailFirstWith = ServerConnectionOptions.ServerFullRejectionReason };
        NetworkSessionCoordinator coordinator = new(new Server(), connector, characterList: new Source([]));

        coordinator.BeginConnectWithPicker(new ClientConnectionOptions("10.0.0.2", "5057", "Typed"));
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(["Typed", "Typed"], connector.Names);
    }

    [Fact]
    public async Task ServerFullWithoutAProbe_IsNotRetried()
    {
        Connector connector = new() { FailFirstWith = ServerConnectionOptions.ServerFullRejectionReason };
        NetworkSessionCoordinator coordinator = new(new Server(), connector);

        coordinator.BeginConnectWithPicker(new ClientConnectionOptions("10.0.0.2", "5057", "Typed"));
        await coordinator.WaitForPendingConnectionAsync();

        Assert.Equal(["Typed"], connector.Names);
    }

    private sealed class Source(IReadOnlyList<CharacterListEntry> characters) : ICharacterListSource
    {
        public int Fetches { get; private set; }

        public Task<IReadOnlyList<CharacterListEntry>> FetchAsync(RemoteClientOptions options, CancellationToken cancellationToken = default)
        {
            Fetches++;
            return Task.FromResult(characters);
        }
    }

    private sealed class Connector : IRemoteSessionConnector
    {
        public List<string> Names { get; } = [];

        public string? FailFirstWith { get; set; }

        public ValueTask<RemoteClientSession> ConnectAsync(RemoteClientOptions options, CancellationToken cancellationToken = default)
        {
            Names.Add(options.DisplayName);
            return ValueTask.FromException<RemoteClientSession>(new InvalidOperationException(
                FailFirstWith != null && Names.Count == 1 ? FailFirstWith : "no server in this test"));
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
