using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using WorkPlanStudio.Contracts;

namespace WorkPlanStudio.Api.Tests;

/// <summary>
/// The application under test: the real composition root, a real SQLite file,
/// and configuration supplied the way a deployment would supply it.
/// <para>
/// The environment is "Testing" rather than "Development" on purpose. It is the
/// path a deployment takes — the development signing key is refused there, the
/// documentation UI is not mapped — so the tests exercise the configuration that
/// ships rather than the one that only ever runs on a laptop.
/// </para>
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    /// <summary>A signing key that is long enough and is not the development sample.</summary>
    public const string SigningKey = "test-signing-key-with-more-than-thirty-two-bytes-of-material";

    /// <summary>Password of every seeded test account. Satisfies Identity's default policy.</summary>
    public const string Password = "Test-Password-2026";

    private readonly string _databasePath;
    private readonly Dictionary<string, string?> _settings;

    /// <summary>Creates a factory with its own database file.</summary>
    /// <param name="databaseName">Short name that goes into the file name, so a failing run can be traced to its class.</param>
    /// <param name="overrides">Configuration keys to change or add for this test class.</param>
    public ApiFactory(string databaseName, IDictionary<string, string?>? overrides = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "workplan-api-tests");
        Directory.CreateDirectory(directory);
        _databasePath = Path.Combine(directory, $"{databaseName}-{Guid.NewGuid():N}.db");

        _settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:Default"] = $"Data Source={_databasePath}",
            ["Jwt:SigningKey"] = SigningKey,
            ["Jwt:AccessTokenMinutes"] = "10",
            ["Jwt:RefreshTokenDays"] = "14",
            ["Auth:AllowRegistration"] = "true",
            ["Auth:MaxFailedAttempts"] = "3",
            ["Auth:LockoutMinutes"] = "15",

            // The rate limiter partitions by client address, and the in-process
            // test server has none — every test in the run would share one
            // bucket. Raised here so only the test that is about rate limiting
            // sees it; that one lowers it again.
            ["Auth:AuthRequestsPerWindow"] = "900",
            ["Auth:AuthWindowSeconds"] = "60",

            ["Seed:SampleData"] = "true",
            ["Seed:Users:0:UserName"] = "planner",
            ["Seed:Users:0:Password"] = Password,
            ["Seed:Users:0:Role"] = WorkspaceRoles.Planner,
            ["Seed:Users:1:UserName"] = "supervisor",
            ["Seed:Users:1:Password"] = Password,
            ["Seed:Users:1:Role"] = WorkspaceRoles.Supervisor,
            ["Seed:Users:2:UserName"] = "guest",
            ["Seed:Users:2:Password"] = Password,
            ["Seed:Users:2:Role"] = WorkspaceRoles.Guest
        };

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
                _settings[key] = value;
        }
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(_settings));
    }

    /// <summary>A client signed in as the named seeded account, with the bearer header already set.</summary>
    /// <param name="userName">"planner", "supervisor" or "guest".</param>
    public async Task<HttpClient> SignedInAsync(string userName)
    {
        var client = CreateClient();
        var tokens = await LoginAsync(client, userName, Password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    /// <summary>Posts credentials and returns the issued pair. Throws when the sign-in was refused.</summary>
    /// <param name="client">The client to use.</param>
    /// <param name="userName">The account name.</param>
    /// <param name="password">The password.</param>
    public static async Task<AuthTokens> LoginAsync(HttpClient client, string userName, string password)
    {
        ArgumentNullException.ThrowIfNull(client);

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(userName, password));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AuthTokens>()
               ?? throw new InvalidOperationException("The login response was empty.");
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;

        // Microsoft.Data.Sqlite pools connections, and a pooled handle keeps the
        // file locked on Windows long after the host is gone.
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _databasePath, _databasePath + "-shm", _databasePath + "-wal" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A leftover file in the temp directory is not worth failing a run over.
            }
        }
    }
}
