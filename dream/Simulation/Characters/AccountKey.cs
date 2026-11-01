using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;

namespace Dream.Simulation.Characters;

// An account: the SHA-256 of its token. Never holds the token. default(AccountKey) has no hash.
public readonly record struct AccountKey
{
    public const int TokenBytes = 32;
    public const int TokenChars = 43;

    private readonly string? _hashHex;

    private AccountKey(string hashHex)
    {
        _hashHex = hashHex;
    }

    // 64 lowercase hex characters (empty for default).
    public string HashHex => _hashHex ?? string.Empty;

    public byte[] ToHashBytes() => _hashHex is null ? [] : Convert.FromHexString(_hashHex);

    public static AccountKey FromHash(ReadOnlySpan<byte> sha256)
    {
        if (sha256.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException("A token hash is 32 bytes.", nameof(sha256));
        }

        return new AccountKey(Convert.ToHexStringLower(sha256));
    }

    // Strict: exactly 43 base64url characters that decode to 32 bytes and re-encode to the same text.
    // Null, blank or malformed gives false.
    public static bool TryFromToken(string? token, out AccountKey key)
    {
        key = default;
        if (token is null || token.Length != TokenChars)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[TokenBytes + 3];
        if (Base64Url.DecodeFromChars(token, bytes, out int consumed, out int written) != OperationStatus.Done
            || consumed != TokenChars
            || written != TokenBytes)
        {
            return false;
        }

        Span<char> reencoded = stackalloc char[TokenChars + 4];
        if (!Base64Url.TryEncodeToChars(bytes[..TokenBytes], reencoded, out int chars)
            || chars != TokenChars
            || !reencoded[..chars].SequenceEqual(token))
        {
            return false;
        }

        key = FromHash(SHA256.HashData(bytes[..TokenBytes]));
        return true;
    }

    public override string ToString() => "account";
}
