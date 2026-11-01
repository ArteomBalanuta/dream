namespace Dream.Simulation.Characters;

public readonly record struct CharacterRecordRef(CharacterKey Key, string DisplayName, AccountKey? Owner);

// What the join prefetch read. Existing: the row whose display_name matches NOCASE. KeyReserved: no
// display match, but a row has this exact player_name. OwnedByAccount: characters the token owns.
public readonly record struct CharacterLookup(CharacterRecordRef? Existing, bool KeyReserved, int OwnedByAccount);

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
    bool IsNewAccount = false)
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
                    ? new(CharacterAccessOutcome.Owned, existing.Key, existing.DisplayName, account, null)
                    : Reject(trimmedName, JoinRejectionReasons.CharacterOwned);
            }

            return lookup.OwnedByAccount >= CharacterNames.MaxCharactersPerAccount
                ? Reject(trimmedName, JoinRejectionReasons.CharacterLimit)
                : new(CharacterAccessOutcome.ClaimUnowned, existing.Key, existing.DisplayName, account, null, lookup.OwnedByAccount == 0);
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
            : new(CharacterAccessOutcome.CreateNew, new CharacterKey(trimmedName), trimmedName, account, null, lookup.OwnedByAccount == 0);
    }

    private static CharacterAccessDecision Reject(string trimmedName, string reason) =>
        new(CharacterAccessOutcome.Rejected, default, trimmedName, null, reason);
}
