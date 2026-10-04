using System.Net;
using System.Net.Http.Json;
using Timetable.Api.IntegrationTests.Infrastructure;

namespace Timetable.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class AuthAndHostingTests(TimetableApiFactory factory)
{
    [Fact]
    public async Task Unknown_api_route_returns_json_404()
    {
        var res = await factory.CreateClient().GetAsync("/api/v1/does-not-exist");
        res.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        res.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Login_with_wrong_password_returns_localized_problem()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ar");
        var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "admin@demo.local", password = "nope-nope" });
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        body!["code"].ToString().ShouldBe("INVALID_CREDENTIALS");
        body["message"].ToString()!.ShouldContain("كلمة المرور");
    }

    [Fact]
    public async Task Admin_can_login_and_sees_both_demo_institutions_with_permissions()
    {
        var client = await factory.LoginAsync();
        var me = await client.GetFromJsonAsync<TimetableApiFactory.MeBody>("/api/v1/auth/me");
        me!.Institutions.Select(i => i.Code).ShouldBe(["SEC", "UNI"], ignoreOrder: true);
        me.Permissions.ShouldContain("timetable.edit");
        me.InstitutionId.ShouldNotBeNull();
    }

    [Fact]
    public async Task Refresh_cookie_rotates_and_issues_new_access_token()
    {
        var client = await factory.LoginAsync();
        var res = await client.PostAsync("/api/v1/auth/refresh", null);
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<TimetableApiFactory.TokenBody>();
        body!.AccessToken.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Requesting_foreign_institution_is_forbidden()
    {
        var client = await factory.LoginAsync("student@demo.local");
        client.DefaultRequestHeaders.Add("X-Institution-Id", Guid.NewGuid().ToString());
        var res = await client.GetAsync("/api/v1/auth/me");
        res.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Health_endpoint_reports_healthy()
    {
        var res = await factory.CreateClient().GetAsync("/health");
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
