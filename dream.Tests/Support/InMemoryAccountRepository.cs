using Dream.Simulation.Characters;
using Dream.Simulation.Persistence;

namespace Dream.Tests.Support;

// An IAccountRepository over dictionaries, following the SQLite behaviour: display names are unique
// ignoring ASCII case, the player key is exact.
internal sealed class InMemoryAccountRepository : IAccountRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string DisplayName, AccountKey? Owner)> _characters = new(StringComparer.Ordinal);
    private readonly Dictionary<AccountKey, DateTime> _accounts = [];

    public int AccountCount
    {
        get
        {
            lock (_gate)
            {
                return _accounts.Count;
            }
        }
    }

    public int RecordJoinCalls { get; private set; }

    public void AddCharacter(string key, string displayName, AccountKey? owner = null)
    {
        lock (_gate)
        {
            _characters[key] = (displayName, owner);
            if (owner is { } account)
            {
                _accounts.TryAdd(account, DateTime.UtcNow);
            }
        }
    }

    public AccountKey? OwnerOf(string key)
    {
        lock (_gate)
        {
            return _characters.TryGetValue(key, out var row) ? row.Owner : null;
        }
    }

    public CharacterLookup Lookup(string trimmedName, AccountKey? account)
    {
        lock (_gate)
        {
            CharacterRecordRef? existing = null;
            foreach ((string key, (string displayName, AccountKey? owner)) in _characters)
            {
                if (CharacterNames.Comparer.Equals(displayName, trimmedName))
                {
                    existing = new CharacterRecordRef(new CharacterKey(key), displayName, owner);
                    break;
                }
            }

            int owned = account is { } a ? _characters.Values.Count(row => row.Owner is { } o && o.Equals(a)) : 0;
            return new CharacterLookup(existing, existing is null && _characters.ContainsKey(trimmedName), owned);
        }
    }

    public bool RecordJoin(AccountKey account, CharacterKey character, bool claim, DateTime utcNow)
    {
        lock (_gate)
        {
            RecordJoinCalls++;
            _accounts[account] = utcNow;
            if (!claim)
            {
                return true;
            }

            if (_characters.TryGetValue(character.Value, out var row) && row.Owner is null)
            {
                _characters[character.Value] = (row.DisplayName, account);
                return true;
            }

            return false;
        }
    }
}
