using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Timetable.Api.IntegrationTests.Infrastructure;

public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static async Task<JsonNode> Json(this HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        return JsonNode.Parse(string.IsNullOrEmpty(text) ? "{}" : text)!;
    }

    public static async Task<JsonNode> GetJson(this HttpClient c, string url)
    {
        var res = await c.GetAsync(url);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"GET {url} → {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return await res.Json();
    }

    public static async Task<JsonNode> PostJson(this HttpClient c, string url, object body, int expected = 200)
    {
        var res = await c.PostAsJsonAsync(url, body, Options);
        if ((int)res.StatusCode != expected) throw new InvalidOperationException($"POST {url} → {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return await res.Json();
    }

    public static async Task<JsonNode> PutJson(this HttpClient c, string url, object body, int expected = 200)
    {
        var res = await c.PutAsJsonAsync(url, body, Options);
        if ((int)res.StatusCode != expected) throw new InvalidOperationException($"PUT {url} → {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return await res.Json();
    }

    public static Guid Id(this JsonNode n) => Guid.Parse(n["id"]!.GetValue<string>());

    public static async Task<Guid> LookupId(this HttpClient c, string kind, string code)
    {
        var page = await c.GetJson($"/api/v1/lookups/{kind}");
        return page["items"]!.AsArray().First(i => i!["code"]!.GetValue<string>() == code)!.Id();
    }
}
