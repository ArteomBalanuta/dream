namespace Dream.Client.Identity;

// The player's hidden identity: one random token per server, kept on this computer. The token is a
// secret; callers pass it only to the join request and never log, print or format it.
public interface IClientIdentityStore
{
    // Returns the stored token for the server key, creating and saving one on first use. Throws
    // IOException (readable message, no token) when the store cannot be written.
    string GetOrCreateToken(string serverKey);
}
