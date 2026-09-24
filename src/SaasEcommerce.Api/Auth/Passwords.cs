namespace SaasEcommerce.Api.Auth;

public static class Passwords
{
    // Verified against when the account doesn't exist, so a login takes the same time
    // whether or not the email is registered.
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString());

    public static string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password);

    public static bool Verify(string password, string? hash)
    {
        var matches = BCrypt.Net.BCrypt.Verify(password, hash ?? DummyHash);
        return hash is not null && matches;
    }
}
