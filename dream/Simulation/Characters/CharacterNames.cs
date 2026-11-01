using System.Globalization;

namespace Dream.Simulation.Characters;

public static class CharacterNames
{
    public const int MaxNewNameLength = 32;
    public const int MaxCharactersPerAccount = 5;

    // ASCII case fold only, the same as SQLite's NOCASE.
    public static IEqualityComparer<string> Comparer { get; } = new AsciiNoCaseComparer();

    public static string Normalize(string name) => name.Trim();

    // 1..32 UTF-16 code units; no Control, Format, LineSeparator, ParagraphSeparator or PrivateUse
    // category; no unpaired surrogate. Applied only when creating or renaming a character.
    public static bool IsValidNewName(string trimmedName)
    {
        if (trimmedName is null || trimmedName.Length < 1 || trimmedName.Length > MaxNewNameLength)
        {
            return false;
        }

        for (int i = 0; i < trimmedName.Length; i++)
        {
            char c = trimmedName[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= trimmedName.Length
                    || !char.IsLowSurrogate(trimmedName[i + 1])
                    || IsRejectedCategory(CharUnicodeInfo.GetUnicodeCategory(trimmedName, i)))
                {
                    return false;
                }

                i++;
                continue;
            }

            if (char.IsLowSurrogate(c) || IsRejectedCategory(CharUnicodeInfo.GetUnicodeCategory(c)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsRejectedCategory(UnicodeCategory category) =>
        category is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.PrivateUse;

    private sealed class AsciiNoCaseComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null || x.Length != y.Length)
            {
                return false;
            }

            for (int i = 0; i < x.Length; i++)
            {
                if (Fold(x[i]) != Fold(y[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(string obj)
        {
            HashCode hash = new();
            foreach (char c in obj)
            {
                hash.Add(Fold(c));
            }

            return hash.ToHashCode();
        }

        private static char Fold(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
    }
}
