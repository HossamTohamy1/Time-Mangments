using Timetable.Api.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.AddTimetableHost();

var app = builder.Build();
await app.UseTimetableHostAsync();
await app.RunAsync();

/// <summary>Exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;
