using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Timetable.Api.IntegrationTests.Infrastructure;

namespace Timetable.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class ValidatorAndImpactTests(TimetableApiFactory factory)
{
    [Theory]
    [InlineData("SEC")]
    [InlineData("UNI")]
    public async Task Conflicts_report_detects_group_double_booking_for_each_template(string code)
    {
        var client = await factory.LoginAsync(institutionCode: code);
        var schedule = await factory.NewDraft(code, $"conflicts-{code}");
        var (a, b, room) = await factory.TwoSessionsOfOneGroup(code);
        await factory.Place(schedule, a, 0, 0, room);
        await factory.Place(schedule, b, 0, 0);
        var report = await client.GetJson($"/api/v1/schedules/{schedule}/conflicts");
        report["hardCount"]!.GetValue<int>().ShouldBeGreaterThan(0);
        var codes = report["violations"]!.AsArray().Select(v => v!["code"]!.GetValue<string>()).ToList();
        codes.ShouldContain("GROUP_DOUBLE_BOOKED");
        report["unplacedOccurrences"]!.GetValue<int>().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Violation_messages_are_localized_in_arabic()
    {
        var client = await factory.LoginAsync(institutionCode: "SEC");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ar");
        await client.PutAsJsonAsync("/api/v1/auth/profile", new { preferredLanguage = "ar" });
        var schedule = await factory.NewDraft("SEC", "ar-messages");
        var (a, b, room) = await factory.TwoSessionsOfOneGroup("SEC");
        await factory.Place(schedule, a, 1, 0, room);
        await factory.Place(schedule, b, 1, 0);
        var report = await client.GetJson($"/api/v1/schedules/{schedule}/conflicts");
        var msg = report["violations"]!.AsArray().First(v => v!["code"]!.GetValue<string>() == "GROUP_DOUBLE_BOOKED")!["message"]!.GetValue<string>();
        msg.ShouldContain("في هذا الوقت");
        await client.PutAsJsonAsync("/api/v1/auth/profile", new { preferredLanguage = "en" });
    }

    [Fact]
    public async Task Valid_slots_mark_occupied_cells_invalid_and_free_cells_valid()
    {
        var client = await factory.LoginAsync(institutionCode: "SEC");
        var schedule = await factory.NewDraft("SEC", "valid-slots");
        var (a, b, room) = await factory.TwoSessionsOfOneGroup("SEC");
        await factory.Place(schedule, a, 2, 0, room);
        var options = (await client.GetJson($"/api/v1/sessions/{b}/valid-slots?scheduleId={schedule}")).AsArray();
        options.Count.ShouldBeGreaterThan(20);
        var taken = options.Single(o => o!["day"]!.GetValue<int>() == 2 && o["startSlot"]!.GetValue<int>() == 0)!;
        taken["status"]!.GetValue<string>().ShouldBe("invalid");
        taken["reasons"]!.AsArray().Select(r => r!["code"]!.GetValue<string>()).ShouldContain("GROUP_DOUBLE_BOOKED");
        options.ShouldContain(o => o!["status"]!.GetValue<string>() != "invalid");
        options.ShouldNotContain(o => o!["startSlot"]!.GetValue<int>() == 3, "slot 3 is the break in the school template");
    }

    [Fact]
    public async Task Validate_move_returns_status_without_saving()
    {
        var client = await factory.LoginAsync(institutionCode: "SEC");
        var schedule = await factory.NewDraft("SEC", "validate-move");
        var (a, b, room) = await factory.TwoSessionsOfOneGroup("SEC");
        await factory.Place(schedule, a, 3, 1, room);
        var res = await client.PostJson($"/api/v1/schedules/{schedule}/entries/validate-move", new { sessionId = b, day = 3, startSlot = 1, roomId = room });
        res["status"]!.GetValue<string>().ShouldBe("invalid");
        (await factory.WithDb(db => db.ScheduleEntries.CountAsync(e => e.ScheduleId == schedule))).ShouldBe(1);
    }

    [Fact]
    public async Task Rule_preview_lists_matching_sessions_and_dry_runs_on_a_schedule()
    {
        var client = await factory.LoginAsync(institutionCode: "SEC");
        var schedule = await factory.NewDraft("SEC", "rule-preview");
        var (a, b, room) = await factory.TwoSessionsOfOneGroup("SEC");
        await factory.Place(schedule, a, 0, 0, room);
        await factory.Place(schedule, b, 0, 1, room);
        var preview = await client.PostJson("/api/v1/rules/preview", new
        {
            definition = new { scope = new { }, condition = new { type = "maxPerDay", max = 1, per = "group" }, effect = "limit" },
            scheduleId = schedule,
        });
        preview["affectedSessions"]!.GetValue<int>().ShouldBeGreaterThan(0);
        preview["violations"]!.GetValue<int>().ShouldBeGreaterThan(0, "two CLASS sessions of the same class on day 0");
    }

    [Fact]
    public async Task Changing_a_constraint_parameter_triggers_impact_analysis_and_revalidation()
    {
        var client = await factory.LoginAsync(institutionCode: "SEC");
        var schedule = await factory.NewDraft("SEC", "impact");
        var (a, _, room) = await factory.TwoSessionsOfOneGroup("SEC");
        await factory.Place(schedule, a, 4, 0, room, occurrence: 0);
        await factory.Place(schedule, a, 4, 1, room, occurrence: 1);
        await client.GetJson($"/api/v1/schedules/{schedule}/conflicts"); // baseline

        var impact = await client.PostJson("/api/v1/config/constraints/MAX_COURSE_SESSIONS_PER_DAY/impact", new { severity = "Hard", weight = 8, parameters = new { max = 1 } });
        var mine = impact["schedules"]!.AsArray().Single(s => s!["scheduleId"]!.GetValue<string>() == schedule.ToString())!;
        mine["hardAfter"]!.GetValue<int>().ShouldBeGreaterThan(mine["hardBefore"]!.GetValue<int>());
        mine["newlyInvalidEntryIds"]!.AsArray().Count.ShouldBeGreaterThan(0);

        (await client.PutAsJsonAsync("/api/v1/config/constraints/MAX_COURSE_SESSIONS_PER_DAY", new { severity = "Hard", weight = 8, parameters = new { max = 1 } }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        // The background re-validation stores the new count on the schedule.
        var hard = 0;
        for (var i = 0; i < 40 && hard == 0; i++)
        {
            await Task.Delay(250);
            hard = await factory.WithDb(db => db.Schedules.Where(s => s.Id == schedule).Select(s => s.HardViolations ?? 0).FirstAsync());
        }
        hard.ShouldBeGreaterThan(0);
        (await client.PutAsJsonAsync("/api/v1/config/constraints/MAX_COURSE_SESSIONS_PER_DAY", new { severity = "Hard", weight = 8, parameters = new { max = 2 } }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Time_structure_impact_reports_entries_that_lose_their_slot()
    {
        var client = await factory.LoginAsync(institutionCode: "SEC");
        var schedule = await factory.NewDraft("SEC", "time-impact");
        var (a, _, room) = await factory.TwoSessionsOfOneGroup("SEC");
        await factory.Place(schedule, a, 1, 7, room);
        var cfg = await client.GetJson("/api/v1/config/effective");
        var time = cfg["time"]!.AsObject();
        // Proposal: disable period 7 on Monday (day 1).
        time["dayOverrides"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject { ["dayOfWeek"] = 1, ["slotIndex"] = 7, ["disabled"] = true });
        var impact = await client.PostJson("/api/v1/config/time-structure/impact", time);
        var mine = impact["schedules"]!.AsArray().Single(s => s!["scheduleId"]!.GetValue<string>() == schedule.ToString())!;
        mine["newViolations"]!.AsArray().Select(v => v!["code"]!.GetValue<string>()).ShouldContain("SLOT_DISABLED");
    }
}
