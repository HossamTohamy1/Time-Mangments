using System.Net;
using System.Net.Http.Json;
using Timetable.Api.IntegrationTests.Infrastructure;

namespace Timetable.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class CrudAndSettingsTests(TimetableApiFactory factory)
{
    [Fact]
    public async Task Effective_config_returns_lookups_terminology_and_supports_etag()
    {
        var client = await factory.LoginAsync(institutionCode: "UNI");
        var res = await client.GetAsync("/api/v1/config/effective");
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        var etag = res.Headers.ETag!.Tag;
        var body = await res.Json();
        body["lookups"]!["session-types"]!.AsArray().Select(x => x!["code"]!.GetValue<string>()).ShouldContain("LECTURE");
        body["terminology"]!["en"]!["term.group"]!.GetValue<string>().ShouldBe("Section");
        body["features"]!["week-cycles"]!.GetValue<bool>().ShouldBeTrue();
        body["time"]!["periods"]!.AsArray().Count.ShouldBe(7);

        var again = new HttpRequestMessage(HttpMethod.Get, "/api/v1/config/effective");
        again.Headers.IfNoneMatch.ParseAdd(etag);
        (await client.SendAsync(again)).StatusCode.ShouldBe(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task Room_crud_validates_and_blocks_deleting_home_room_in_use()
    {
        var client = await factory.LoginAsync(institutionCode: "SEC");
        var classroom = await client.LookupId("room-types", "CLASSROOM");

        var bad = await client.PostAsJsonAsync("/api/v1/rooms", new { code = "", capacity = -1, roomTypeId = classroom });
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await bad.Json();
        problem["code"]!.GetValue<string>().ShouldBe("VALIDATION_FAILED");
        problem["errors"]!["capacity"].ShouldNotBeNull();

        var created = await client.PostJson("/api/v1/rooms", new { code = "R-TEST", nameEn = "Test room", nameAr = "قاعة اختبار", roomTypeId = classroom, capacity = 30, tags = new[] { "new" } }, 201);
        var id = created.Id();
        var updated = await client.PutJson($"/api/v1/rooms/{id}", new { code = "R-TEST", nameEn = "Test room 2", roomTypeId = classroom, capacity = 35 });
        updated["capacity"]!.GetValue<int>().ShouldBe(35);
        (await client.DeleteAsync($"/api/v1/rooms/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // A home room used by a class cannot be deleted; the response lists usages.
        var rooms = await client.GetJson("/api/v1/rooms?search=CR-10A");
        var home = rooms["items"]!.AsArray()[0]!.Id();
        var blocked = await client.DeleteAsync($"/api/v1/rooms/{home}");
        blocked.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var blockedBody = await blocked.Json();
        blockedBody["code"]!.GetValue<string>().ShouldBe("IN_USE");
        blockedBody["details"]!.AsArray().Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Deleting_in_use_system_lookup_is_blocked_and_merge_repoints_references()
    {
        var client = await factory.LoginAsync(institutionCode: "SEC");
        var gym = await client.LookupId("room-types", "GYM");
        var del = await client.DeleteAsync($"/api/v1/lookups/room-types/{gym}");
        del.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await del.Json())["code"]!.GetValue<string>().ShouldBe("SYSTEM_LOOKUP_IN_USE");

        var newType = (await client.PostJson("/api/v1/lookups/room-types", new { code = "SPORTS_HALL", nameEn = "Sports hall", nameAr = "صالة رياضية", sortOrder = 9, isActive = true }, 201)).Id();
        var merge = await client.PostAsJsonAsync($"/api/v1/lookups/room-types/{gym}/merge", new { targetId = newType });
        merge.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var rooms = await client.GetJson($"/api/v1/rooms?roomTypeId={newType}");
        rooms["total"]!.GetValue<int>().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Feature_flag_off_hides_feature_and_rejects_api_calls()
    {
        var client = await factory.LoginAsync(institutionCode: "UNI");
        (await client.PutAsJsonAsync("/api/v1/config/features", new Dictionary<string, bool> { ["curriculum"] = false })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var cfg = await client.GetJson("/api/v1/config/effective");
        cfg["features"]!["curriculum"]!.GetValue<bool>().ShouldBeFalse();
        var res = await client.GetAsync("/api/v1/curriculum-rules");
        res.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await res.Json())["code"]!.GetValue<string>().ShouldBe("FEATURE_DISABLED");
        (await client.PutAsJsonAsync("/api/v1/config/features", new Dictionary<string, bool> { ["curriculum"] = true })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync("/api/v1/curriculum-rules")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Curriculum_generation_is_idempotent()
    {
        var client = await factory.LoginAsync(institutionCode: "SEC");
        var terms = await client.GetJson("/api/v1/terms");
        var termId = terms["items"]!.AsArray()[0]!.Id();
        var preview = await client.PostJson("/api/v1/curriculum/generate-sessions?preview=true", new { termId });
        var items = preview["items"]!.AsArray();
        items.Count.ShouldBeGreaterThan(0);
        items.Where(i => i!["action"]!.GetValue<string>() != "unchanged").Select(i => i!.ToJsonString()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Constraint_settings_validate_parameters_and_protect_core()
    {
        var client = await factory.LoginAsync(institutionCode: "SEC");
        var core = await client.PutAsJsonAsync("/api/v1/config/constraints/ROOM_CONFLICT", new { severity = "Off", weight = 1 });
        core.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await core.Json())["code"]!.GetValue<string>().ShouldBe("CONSTRAINT_IS_CORE");

        var badParam = await client.PutAsJsonAsync("/api/v1/config/constraints/MAX_COURSE_SESSIONS_PER_DAY", new { severity = "Hard", weight = 5, parameters = new { max = "two" } });
        badParam.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var ok = await client.PutAsJsonAsync("/api/v1/config/constraints/MAX_COURSE_SESSIONS_PER_DAY", new { severity = "Hard", weight = 5, parameters = new { max = 2 } });
        ok.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Instructor_availability_round_trip_and_self_service()
    {
        var admin = await factory.LoginAsync(institutionCode: "UNI");
        var list = await admin.GetJson("/api/v1/instructors?search=AHASSAN");
        var id = list["items"]!.AsArray()[0]!.Id();
        await admin.PutJson($"/api/v1/instructors/{id}/availability", new[] { new { dayOfWeek = 1, slotIndex = 0, state = "Unavailable" } });
        var got = await admin.GetJson($"/api/v1/instructors/{id}/availability");
        got["cells"]!.AsArray().Count.ShouldBe(1);

        var instructor = await factory.LoginAsync("dr.ahmed@demo.local");
        var mine = await instructor.GetJson("/api/v1/me/availability");
        mine["cells"]!.AsArray().Count.ShouldBe(1);
        await instructor.PutJson("/api/v1/me/availability", new[] { new { dayOfWeek = 0, slotIndex = 0, state = "Preferred" } });
        // An instructor cannot edit someone else's availability.
        var other = (await admin.GetJson("/api/v1/instructors?search=SNABIL"))["items"]!.AsArray()[0]!.Id();
        (await instructor.PutAsJsonAsync($"/api/v1/instructors/{other}/availability", Array.Empty<object>())).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Config_export_import_round_trip_with_preview()
    {
        var client = await factory.LoginAsync(institutionCode: "UNI");
        var export = await client.GetAsync("/api/v1/config/export");
        export.StatusCode.ShouldBe(HttpStatusCode.OK);
        var bundle = await export.Json();
        bundle["sessionTypes"]!.AsArray().Count.ShouldBe(3);
        var preview = await client.PostJson("/api/v1/config/import", new { bundle, dryRun = true });
        preview.AsArray().Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Terminology_override_is_saved_and_published()
    {
        var client = await factory.LoginAsync(institutionCode: "SEC");
        var terms = await client.GetJson("/api/v1/config/terminology");
        var list = terms.AsArray().Select(t => new { key = t!["key"]!.GetValue<string>(), ar = t["ar"]?.GetValue<string>(), en = t["en"]?.GetValue<string>() }).ToList();
        list.Add(new { key = "term.room", ar = (string?)"فصل دراسي", en = (string?)"Classroom" });
        (await client.PutAsJsonAsync("/api/v1/config/terminology", list)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var cfg = await client.GetJson("/api/v1/config/effective");
        cfg["terminology"]!["ar"]!["term.room"]!.GetValue<string>().ShouldBe("فصل دراسي");
    }
}
