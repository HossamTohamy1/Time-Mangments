using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Timetable.Api.Hosting;

namespace Timetable.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the real API. Uses a dedicated SQL Server test database when TIMETABLE_TEST_SQLSERVER is set (created and
/// dropped per run); otherwise an in-memory SQLite database as a local fallback (see docs/decisions.md).
/// </summary>
public class TimetableApiFactory : WebApplicationFactory<Program>
{
    private static readonly object Gate = new();
    private readonly SqliteConnection? _sqlite;
    private readonly string? _sqlServer;

    public TimetableApiFactory() : this(seedDemo: true) { }

    protected TimetableApiFactory(bool seedDemo)
    {
        SeedDemo = seedDemo;
        var server = Environment.GetEnvironmentVariable("TIMETABLE_TEST_SQLSERVER");
        if (!string.IsNullOrWhiteSpace(server))
        {
            var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(server) { InitialCatalog = $"TimetableTest_{Guid.NewGuid():N}" };
            _sqlServer = b.ConnectionString;
        }
        else
        {
            _sqlite = new SqliteConnection("DataSource=:memory:");
            _sqlite.Open();
        }
    }

    public bool SeedDemo { get; }
    public bool UsesSqlServer => _sqlServer is not null;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:ApplyMigrationsOnStartup"] = "true",
            ["Database:SeedDemoData"] = SeedDemo ? "true" : "false",
            ["Jwt:SigningKey"] = "integration-tests-signing-key-0123456789abcdefghijkl",
            ["RateLimit:AuthPerMinute"] = "1000",
            ["Solver:DefaultTimeLimitSeconds"] = "20",
            ["Serilog:MinimumLevel:Default"] = "Warning",
        }));
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        lock (Gate)
        {
            HostSetup.DbOverride = _sqlServer is not null
                ? o => o.UseSqlServer(_sqlServer)
                : o => o.UseSqlite(_sqlite!);
            try { return base.CreateHost(builder); }
            finally { HostSetup.DbOverride = null; }
        }
    }

    public async Task<HttpClient> LoginAsync(string email = "admin@demo.local", string password = "Demo#12345", string? institutionCode = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, BaseAddress = new Uri("https://localhost") });
        var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<TokenBody>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.AccessToken);
        if (institutionCode is not null)
        {
            var me = await client.GetFromJsonAsync<MeBody>("/api/v1/auth/me");
            var inst = me!.Institutions.First(i => i.Code == institutionCode);
            client.DefaultRequestHeaders.Add("X-Institution-Id", inst.InstitutionId.ToString());
        }
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        _sqlite?.Dispose();
        if (_sqlServer is not null)
        {
            try
            {
                var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_sqlServer);
                var db = b.InitialCatalog;
                b.InitialCatalog = "master";
                using var c = new Microsoft.Data.SqlClient.SqlConnection(b.ConnectionString);
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END";
                cmd.ExecuteNonQuery();
            }
            catch (Exception) { /* best effort cleanup */ }
        }
    }

    public sealed record TokenBody(string AccessToken, DateTimeOffset ExpiresAt);
    public sealed record MeBody(object Profile, List<Membership> Institutions, Guid? InstitutionId, List<string> Permissions);
    public sealed record Membership(Guid InstitutionId, string Code, string? NameAr, string? NameEn, List<string> RoleCodes);
}

/// <summary>Empty database (templates only) for the dynamic-proof acceptance test.</summary>
public sealed class EmptyApiFactory() : TimetableApiFactory(seedDemo: false);

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<TimetableApiFactory>
{
    public const string Name = "api";
}
