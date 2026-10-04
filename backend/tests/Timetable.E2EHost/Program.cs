using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Timetable.Api.Hosting;

// Runs the real Timetable.Api (same Program, middleware, SPA hosting) against a disposable database:
// a dedicated SQL Server database when TIMETABLE_TEST_SQLSERVER is set, otherwise in-memory SQLite.
// Usage (from backend/src/Timetable.Api so wwwroot is found): dotnet run --project ../../tests/Timetable.E2EHost -- --urls http://localhost:5099
var server = Environment.GetEnvironmentVariable("TIMETABLE_TEST_SQLSERVER");
string? sqliteFile = null;
if (!string.IsNullOrWhiteSpace(server))
{
    var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(server) { InitialCatalog = $"TimetableE2E_{Guid.NewGuid():N}" };
    var cs = b.ConnectionString;
    HostSetup.DbOverride = o => o.UseSqlServer(cs);
}
else
{
    // A temporary file (not a shared in-memory connection) so concurrent requests each get their own connection.
    sqliteFile = Path.Combine(Path.GetTempPath(), $"timetable-e2e-{Guid.NewGuid():N}.db");
    var cs = new SqliteConnectionStringBuilder { DataSource = sqliteFile, DefaultTimeout = 30, Pooling = false }.ToString();
    HostSetup.DbOverride = o => o.UseSqlite(cs);
}

Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Testing");
Environment.SetEnvironmentVariable("Database__ApplyMigrationsOnStartup", "true");
Environment.SetEnvironmentVariable("Database__SeedDemoData", "true");
Environment.SetEnvironmentVariable("Jwt__SigningKey", "e2e-only-signing-key-0123456789abcdefghijklmnop");
Environment.SetEnvironmentVariable("RateLimit__AuthPerMinute", "10000");

var entry = typeof(HostSetup).Assembly.EntryPoint!;
var result = entry.Invoke(null, [args]);
if (result is Task t) await t;
if (sqliteFile is not null) try { File.Delete(sqliteFile); } catch (IOException) { }
