using Timetable.Api.IntegrationTests.Infrastructure;

namespace Timetable.Api.IntegrationTests;

/// <summary>Exports the OpenAPI document consumed by `npm run generate:api` (contract between backend and frontend).</summary>
[Collection(ApiCollection.Name)]
public sealed class OpenApiContractTests(TimetableApiFactory factory)
{
    [Fact]
    public async Task Exports_openapi_document()
    {
        var client = factory.CreateClient();
        var json = await client.GetStringAsync("/api/swagger/v1/swagger.json");
        json.ShouldContain("/api/v1/rooms");
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "openapi"));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "v1.json"), json);
    }
}
