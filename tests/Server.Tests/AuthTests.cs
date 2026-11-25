using PortfolioWeb;

namespace Server.Tests;

public sealed class AuthTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glancefolio-auth-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "users.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Registration_persists_a_salted_hash_and_authenticates_case_insensitively()
    {
        var users = new UserStore(FilePath);
        var created = await users.CreateAsync("Person@Example.com", "correct horse battery staple", default);
        Assert.NotNull(created);
        Assert.Equal("person@example.com", created.User.Email);
        Assert.True(created.IsFirstAccount);
        Assert.DoesNotContain("correct horse battery staple", await File.ReadAllTextAsync(FilePath));

        var signedIn = await users.VerifyAsync("PERSON@example.com", "correct horse battery staple", default);
        Assert.Equal(created.User.Id, signedIn?.Id);
        Assert.Null(await users.VerifyAsync("person@example.com", "incorrect password", default));
    }

    [Fact]
    public async Task Duplicate_email_is_rejected_without_creating_a_second_account()
    {
        var users = new UserStore(FilePath);
        Assert.NotNull(await users.CreateAsync("person@example.com", "a-long-enough-password", default));
        Assert.Null(await users.CreateAsync("PERSON@example.com", "another-long-password", default));
        Assert.Single(System.Text.Json.JsonSerializer.Deserialize<List<Account>>(await File.ReadAllTextAsync(FilePath), Json.Options)!);
    }
}
