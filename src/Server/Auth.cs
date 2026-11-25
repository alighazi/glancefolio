using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;

namespace PortfolioWeb;

public sealed record Account(string Id, string Email, string PasswordHash, DateTime CreatedUtc);
public sealed record PublicAccount(string Id, string Email, DateTime CreatedUtc);
public sealed record AccountCreation(PublicAccount User, bool IsFirstAccount);

/// <summary>Small file-backed account directory. Passwords are PBKDF2 hashes; plaintext is never persisted.</summary>
public sealed class UserStore
{
    private const int Iterations = 310_000;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UserStore(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    public async Task<AccountCreation?> CreateAsync(string email, string password, CancellationToken ct)
    {
        var normalized = NormalizeEmail(email);
        await _gate.WaitAsync(ct);
        try
        {
            var accounts = await ReadAsync(ct);
            if (accounts.Any(a => string.Equals(a.Email, normalized, StringComparison.OrdinalIgnoreCase))) return null;
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
            var account = new Account(Guid.NewGuid().ToString("N"), normalized,
                $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}", DateTime.UtcNow);
            accounts.Add(account);
            await WriteAsync(accounts, ct);
            return new AccountCreation(Public(account), accounts.Count == 1);
        }
        finally { _gate.Release(); }
    }

    public async Task<PublicAccount?> VerifyAsync(string email, string password, CancellationToken ct)
    {
        var normalized = NormalizeEmail(email);
        await _gate.WaitAsync(ct);
        try
        {
            var account = (await ReadAsync(ct)).FirstOrDefault(a => string.Equals(a.Email, normalized, StringComparison.OrdinalIgnoreCase));
            if (account is null)
            {
                // Keep unknown-email attempts close to the same cost as a bad password.
                _ = Rfc2898DeriveBytes.Pbkdf2(password, new byte[16], Iterations, HashAlgorithmName.SHA256, 32);
                return null;
            }
            if (!VerifyPassword(password, account.PasswordHash)) return null;
            return Public(account);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<Account>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return [];
        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<List<Account>>(stream, Json.Options, ct) ?? [];
        }
        catch (JsonException) { throw new InvalidOperationException("The account store is unreadable; repair or restore " + _path); }
    }

    private async Task WriteAsync(List<Account> accounts, CancellationToken ct)
    {
        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(accounts, Json.Options), ct);
        File.Move(temp, _path, true);
    }

    private static bool VerifyPassword(string password, string encoded)
    {
        var fields = encoded.Split('$');
        if (fields.Length != 4 || fields[0] != "pbkdf2-sha256" ||
            !int.TryParse(fields[1], out var iterations) || iterations < 100_000 || iterations > 2_000_000) return false;
        try
        {
            var salt = Convert.FromBase64String(fields[2]);
            var expected = Convert.FromBase64String(fields[3]);
            if (salt.Length < 16 || expected.Length is < 16 or > 64) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
    private static PublicAccount Public(Account account) => new(account.Id, account.Email, account.CreatedUtc);
}

public sealed record AuthRequest(string? Email, string? Password);

public static class AuthHelpers
{
    public const string UserIdClaim = "glancefolio_user_id";
    public static string? UserId(ClaimsPrincipal principal) => principal.FindFirstValue(UserIdClaim);

    public static bool IsSameOrigin(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue("Origin", out var origin)) return true;
        var forwardedScheme = context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault()?.Split(',')[0].Trim();
        var requestScheme = string.IsNullOrWhiteSpace(forwardedScheme) ? context.Request.Scheme : forwardedScheme;
        var forwardedHost = context.Request.Headers["X-Forwarded-Host"].FirstOrDefault()?.Split(',')[0].Trim();
        var requestHost = string.IsNullOrWhiteSpace(forwardedHost) ? context.Request.Host.Value : forwardedHost;
        return Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri) &&
               Uri.TryCreate($"{requestScheme}://{requestHost}", UriKind.Absolute, out var expected) &&
               string.Equals(uri.Host, expected.Host, StringComparison.OrdinalIgnoreCase) &&
               uri.Port == expected.Port &&
               string.Equals(uri.Scheme, requestScheme, StringComparison.OrdinalIgnoreCase);
    }
}
