using Dream.Multiplayer.Protocol;
using Dream.Simulation.Inventory;

namespace Dream.Rendering.Hud;

// The loot lines the HUD shows after an inventory push (wave I): the newest four, each held for four
// seconds. The notices are the server's word and hostile: at most InventoryPushMessage.MaxNotices are
// read, a name is cut to MaxNameLength, a missing name reads "an item" and an unknown rarity shows no
// suffix. Lines are built once per notice; drawing and updating allocate nothing. The HUD must draw
// them with TextUnformatted, so a '%' or '{' in an item name is shown as typed.
public sealed class LootFeedbackSystem
{
    public const int MaxLines = 4;
    public const float HoldSeconds = 4f;
    public const int MaxNameLength = 40;

    private readonly string?[] _lines = new string?[MaxLines];
    private readonly float[] _remainingSeconds = new float[MaxLines];
    private int _count;

    // Lines currently shown, oldest first.
    public int Count => _count;

    public string GetLine(int index)
    {
        if ((uint)index >= (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return _lines[index]!;
    }

    public void AddNotices(IReadOnlyList<LootNotice>? notices)
    {
        if (notices is null)
        {
            return;
        }

        int limit = Math.Min(notices.Count, InventoryPushMessage.MaxNotices);
        for (int i = 0; i < limit; i++)
        {
            AddLine(Format(notices[i]));
        }
    }

    public void Update(float deltaSeconds)
    {
        if (_count == 0)
        {
            return;
        }

        float step = float.IsFinite(deltaSeconds) ? Math.Max(0f, deltaSeconds) : 0f;
        int kept = 0;
        for (int i = 0; i < _count; i++)
        {
            float remaining = _remainingSeconds[i] - step;
            if (remaining <= 0f)
            {
                continue;
            }

            _lines[kept] = _lines[i];
            _remainingSeconds[kept] = remaining;
            kept++;
        }

        for (int i = kept; i < _count; i++)
        {
            _lines[i] = null;
        }

        _count = kept;
    }

    public void Clear()
    {
        Array.Clear(_lines);
        _count = 0;
    }

    public static string Format(LootNotice notice)
    {
        string name = string.IsNullOrWhiteSpace(notice.ItemDisplayName)
            ? "an item"
            : notice.ItemDisplayName.Length > MaxNameLength
                ? notice.ItemDisplayName[..MaxNameLength]
                : notice.ItemDisplayName;

        if (notice.Lost)
        {
            return string.Concat("Backpack full: ", name, " was lost");
        }

        string? rarity = notice.Rarity switch
        {
            ItemRarity.Common => "Common",
            ItemRarity.Uncommon => "Uncommon",
            ItemRarity.Rare => "Rare",
            _ => null,
        };

        return rarity is null
            ? string.Concat("Looted: ", name)
            : string.Concat("Looted: ", name, " (", rarity, ")");
    }

    private void AddLine(string line)
    {
        if (_count == MaxLines)
        {
            for (int i = 1; i < MaxLines; i++)
            {
                _lines[i - 1] = _lines[i];
                _remainingSeconds[i - 1] = _remainingSeconds[i];
            }

            _count--;
        }

        _lines[_count] = line;
        _remainingSeconds[_count] = HoldSeconds;
        _count++;
    }
}
