using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Server.Persistence;

// A starved seat's command frames keep re-requesting the mode it already has, every tick. Each
// request used to queue a profile save (64 a second); only a change saves now.
public sealed class InteractionModeProfileSaveTests
{
    private const string PlayerName = "Mode Saver";

    [Fact]
    public void RequestingTheCurrentMode_SavesNothing_AndAChangeSavesOnce()
    {
        CountingProfiles profiles = new();
        using WorldSessionRuntime runtime = new(
            12,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: profiles,
            persistenceWrites: PersistenceWriteQueue.Inline());
        PlayerSessionId session = new("mode-saver");
        runtime.AttachRemoteSession(session, PlayerName);
        runtime.RestorePersistedPlayerState(session, PlayerName);
        // The join's restore still saves the profile, unchanged mode or not.
        Assert.True(profiles.Saves >= 1);
        PlayerInteractionMode current = runtime.InteractionMode;
        int savesAfterJoin = profiles.Saves;

        for (int i = 0; i < 64; i++)
        {
            runtime.SetAuthoritativeInteractionMode(current);
        }

        Assert.Equal(savesAfterJoin, profiles.Saves);

        PlayerInteractionMode other = current == PlayerInteractionMode.Building
            ? PlayerInteractionMode.Combat
            : PlayerInteractionMode.Building;
        runtime.SetAuthoritativeInteractionMode(other);
        runtime.SetAuthoritativeInteractionMode(other);

        Assert.Equal(other, runtime.InteractionMode);
        Assert.Equal(savesAfterJoin + 1, profiles.Saves);
        Assert.Equal(other, profiles.TryGet(PlayerName)!.Value.InteractionMode);
    }

    private sealed class CountingProfiles : IPlayerProfileRepository
    {
        private readonly Dictionary<string, PersistedPlayerProfile> _rows = new(StringComparer.OrdinalIgnoreCase);

        public int Saves { get; private set; }

        public PersistedPlayerProfile GetOrCreate(string playerName)
        {
            if (!_rows.TryGetValue(playerName, out PersistedPlayerProfile profile))
            {
                profile = PersistedPlayerProfile.CreateDefault(playerName, DateTime.UtcNow);
                _rows[playerName] = profile;
            }

            return profile;
        }

        public PersistedPlayerProfile? TryGet(string playerName) =>
            _rows.TryGetValue(playerName, out PersistedPlayerProfile profile) ? profile : null;

        public void Save(PersistedPlayerProfile profile)
        {
            Saves++;
            _rows[profile.PlayerName] = profile;
        }
    }
}
