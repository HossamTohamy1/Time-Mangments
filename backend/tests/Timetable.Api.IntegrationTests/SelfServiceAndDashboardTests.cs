using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Timetable.Api.IntegrationTests.Infrastructure;

namespace Timetable.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class SelfServiceAndDashboardTests(TimetableApiFactory factory)
{
    /// <summary>Publishes an auto-placed UNI timetable (once) so self-service has something to show.</summary>
    private async Task<Guid> PublishedUni()
    {
        var existing = await factory.WithDb(db => db.Schedules.Where(s => s.Status == Domain.Common.ScheduleStatus.Published
            && db.Institutions.Any(i => i.Id == s.InstitutionId && i.Code == "UNI")).Select(s => (Guid?)s.Id).FirstOrDefaultAsync());
        if (existing is { } id) return id;
        var admin = await factory.LoginAsync(institutionCode: "UNI");
        var schedule = await factory.NewDraft("UNI", "self-service");
        await admin.PostJson($"/api/v1/schedules/{schedule}/entries/auto-place", new { });
        await admin.PostJson($"/api/v1/schedules/{schedule}/publish", new { });
        return schedule;
    }

    [Fact]
    public async Task Instructor_sees_own_week_and_is_notified_of_publication()
    {
        var schedule = await PublishedUni();
        var client = await factory.LoginAsync("dr.ahmed@demo.local");
        var termStart = await factory.WithDb(db => db.Schedules.Where(s => s.Id == schedule).Join(db.AcademicTerms, s => s.TermId, t => t.Id, (s, t) => t.StartDate).FirstAsync());
        var week = await client.GetJson($"/api/v1/me/timetable?date={termStart.AddDays(14):yyyy-MM-dd}");
        week["subjectKind"]!.GetValue<string>().ShouldBe("instructor");
        var items = week["days"]!.AsArray().SelectMany(d => d!["items"]!.AsArray()).ToList();
        items.Count.ShouldBeGreaterThan(0);
        items.ShouldAllBe(i => i!["status"]!.GetValue<string>() == "normal");

        var notes = await client.GetJson("/api/v1/me/notifications");
        notes["items"]!.AsArray().Select(n => n!["type"]!.GetValue<string>()).ShouldContain("NOTIFY_SCHEDULE_PUBLISHED");
        (await client.PostAsJsonAsync("/api/v1/me/notifications/read", Array.Empty<Guid>())).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetJson("/api/v1/me/notifications"))["unread"]!.GetValue<int>().ShouldBe(0);

        var pdf = await client.GetAsync("/api/v1/me/timetable/export?format=pdf&lang=ar");
        pdf.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Student_sees_cohort_lectures_and_own_section()
    {
        var schedule = await PublishedUni();
        var client = await factory.LoginAsync("student@demo.local");
        var termStart = await factory.WithDb(db => db.Schedules.Where(s => s.Id == schedule).Join(db.AcademicTerms, s => s.TermId, t => t.Id, (s, t) => t.StartDate).FirstAsync());
        var week = await client.GetJson($"/api/v1/me/timetable?date={termStart.AddDays(14):yyyy-MM-dd}");
        week["subjectKind"]!.GetValue<string>().ShouldBe("group");
        var items = week["days"]!.AsArray().SelectMany(d => d!["items"]!.AsArray()).ToList();
        items.Count.ShouldBeGreaterThan(0);
        // Students cannot read the full schedule.
        (await client.GetAsync($"/api/v1/schedules/{schedule}/board")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Dashboard_reports_coverage_load_and_utilization()
    {
        await PublishedUni();
        var client = await factory.LoginAsync(institutionCode: "UNI");
        var d = await client.GetJson("/api/v1/dashboard");
        d["schedule"]!["status"]!.GetValue<string>().ShouldBe("Published");
        var k = d["kpis"]!;
        k["occurrences"]!.GetValue<int>().ShouldBeGreaterThan(0);
        k["coveragePercent"]!.GetValue<decimal>().ShouldBeGreaterThan(50m);
        k["hardConflicts"]!.GetValue<int>().ShouldBe(0);
        d["instructorLoad"]!.AsArray().Count.ShouldBeGreaterThan(0);
        d["roomTypes"]!.AsArray().Count.ShouldBeGreaterThan(0);
        d["perDay"]!.AsArray().Sum(x => x!["count"]!.GetValue<int>()).ShouldBe(k["placed"]!.GetValue<int>());
    }
}
