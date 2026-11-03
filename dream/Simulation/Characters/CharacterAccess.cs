namespace Dream.Simulation.Characters;

public readonly record struct CharacterRecordRef(CharacterKey Key, string DisplayName, AccountKey? Owner);

// What the join prefetch read. Existing: the row whose display_name matches NOCASE. KeyReserved: no
// display match, but a row has this exact player_name. OwnedByAccount: characters the token owns.
// M8. What the account behind a character stands at: Role 0 player or 1 admin, a ban and a mute with their
// end (UTC; null for none), and the ban reason.
public readonly record struct AccountStanding(int Role, DateTime? BannedUntilUtc, string? BanReason, DateTime? MutedUntilUtc);

// Standing is the owning account's role and sanctions (M8); HeldElsewhere is true while another world holds
// the character's presence lease.
public readonly record struct CharacterLookup(
    CharacterRecordRef? Existing,
    bool KeyReserved,
    int OwnedByAccount,
    AccountStanding Standing = default,
    bool HeldElsewhere = false);

// M8. The text a banned join is refused with: the standing sentence, the end of a limited ban and the reason.
public static class BanMessage
{
    // Bans at or past this are shown as permanent.
    public static readonly DateTime PermanentThresholdUtc = new(9000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static bool IsActive(in AccountStanding standing, DateTime nowUtc) =>
        standing.BannedUntilUtc is { } until && until > nowUtc;

    public static string Format(in AccountStanding standing)
    {
        string text = JoinRejectionReasons.Banned;
        if (standing.BannedUntilUtc is { } until && until < PermanentThresholdUtc)
        {
            text += $" The ban ends {until.ToUniversalTime():yyyy-MM-dd HH:mm} UTC.";
        }

        if (!string.IsNullOrWhiteSpace(standing.BanReason))
        {
            text += $" Reason: {standing.BanReason}";
        }

        return text;
    }
}

public enum CharacterAccessOutcome : byte
{
    Unchecked,
    Owned,
    ClaimUnowned,
    CreateNew,
    Rejected
}

public readonly record struct JoinHolderIdentity(CharacterKey Character, AccountKey Account);

public readonly record struct CharacterAccessDecision(
    CharacterAccessOutcome Outcome,
    CharacterKey Key,
    string DisplayName,
    AccountKey? Account,
    string? RejectionReason,
    bool IsNewAccount = false,
    AccountStanding Standing = default)
{
    public bool IsRejected => Outcome == CharacterAccessOutcome.Rejected;

    // Null for Unchecked and Rejected.
    public JoinHolderIdentity? Holder =>
        Outcome is CharacterAccessOutcome.Owned or CharacterAccessOutcome.ClaimUnowned or CharacterAccessOutcome.CreateNew
        && Account is { } account
            ? new JoinHolderIdentity(Key, account)
            : null;

    public static CharacterAccessDecision Unchecked(string trimmedName) =>
        new(CharacterAccessOutcome.Unchecked, default, trimmedName, null, null);
}

public static class CharacterAccessRules
{
    // Pure. The name rule is never applied to an existing character, so legacy names stay valid.
    // The name check comes before the limit check so a player fixes one thing at a time.
    public static CharacterAccessDecision Decide(string trimmedName, AccountKey account, in CharacterLookup lookup)
    {
        if (lookup.Existing is { } existing)
        {
            if (existing.Owner is { } owner)
            {
                return owner.Equals(account)
                    ? new(CharacterAccessOutcome.Owned, existing.Key, existing.DisplayName, account, null, Standing: lookup.Standing)
                    : Reject(trimmedName, JoinRejectionReasons.CharacterOwned);
            }

            return lookup.OwnedByAccount >= CharacterNames.MaxCharactersPerAccount
                ? Reject(trimmedName, JoinRejectionReasons.CharacterLimit)
                : new(CharacterAccessOutcome.ClaimUnowned, existing.Key, existing.DisplayName, account, null, lookup.OwnedByAccount == 0, lookup.Standing);
        }

        if (lookup.KeyReserved)
        {
            return Reject(trimmedName, JoinRejectionReasons.NameTaken);
        }

        if (!CharacterNames.IsValidNewName(trimmedName))
        {
            return Reject(trimmedName, JoinRejectionReasons.NameInvalid);
        }

        return lookup.OwnedByAccount >= CharacterNames.MaxCharactersPerAccount
            ? Reject(trimmedName, JoinRejectionReasons.CharacterLimit)
            : new(CharacterAccessOutcome.CreateNew, new CharacterKey(trimmedName), trimmedName, account, null, lookup.OwnedByAccount == 0, lookup.Standing);
    }

    private static CharacterAccessDecision Reject(string trimmedName, string reason) =>
        new(CharacterAccessOutcome.Rejected, default, trimmedName, null, reason);
}
