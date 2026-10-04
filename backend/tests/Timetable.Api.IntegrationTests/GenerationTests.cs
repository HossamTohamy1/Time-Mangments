using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Timetable.Api.IntegrationTests.Infrastructure;

namespace Timetable.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class GenerationTests(TimetableApiFactory factory)
{
    private Task<Guid> TermId(string code) => factory.WithDb(db =>
        db.AcademicTerms.Where(t => db.Institutions.Any(i => i.Id == t.InstitutionId && i.Code == code)).OrderByDescending(t => t.IsCurrent).Select(t => t.Id).FirstAsync());

    private static async Task<JsonNode> WaitForJob(HttpClient client, Guid jobId, int seconds = 120)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            var job = await client.GetJson($"/api/v1/generation/jobs/{jobId}");
            if (job["status"]!.GetValue<string>() is not ("Queued" or "Running")) return job;
            await Task.Delay(300);
        }
        throw new TimeoutException("generation did not finish");
    }

    [Fact]
    public async Task Readiness_reports_counts_and_engine_availability()
    {
        var client = await factory.LoginAsync(institutionCode: "SEC");
        var r = await client.GetJson($"/api/v1/generation/readiness?termId={await TermId("SEC")}");
        r["sessions"]!.GetValue<int>().ShouldBeGreaterThan(10);
        r["hardConstraints"]!.GetValue<int>().ShouldBeGreaterThan(3);
        r["cpSatAvailable"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Theory]
    [InlineData("SEC", "cpsat")]
    [InlineData("UNI", "heuristic")]
    public async Task Generation_produces_a_timetable_without_hard_conflicts(string code, string engine)
    {
        var client = await factory.LoginAsync(institutionCode: code);
        var job = await client.PostJson("/api/v1/generation/jobs", new { termId = await TermId(code), engine, timeLimitSeconds = 20, name = $"gen-{engine}" }, 201);
        var busy = await client.PostAsJsonAsync("/api/v1/generation/jobs", new { termId = await TermId(code), engine, timeLimitSeconds = 20 });
        if (busy.StatusCode == HttpStatusCode.Conflict) (await busy.Json())["code"]!.GetValue<string>().ShouldBe("GENERATION_ALREADY_RUNNING");
        else await WaitForJob(client, (await busy.Json()).Id());

        var done = await WaitForJob(client, job.Id());
        done["status"]!.GetValue<string>().ShouldBeOneOf("Succeeded", "Infeasible");
        var summary = done["summary"]!;
        summary["hardViolations"]!.GetValue<int>().ShouldBe(0);
        summary["placed"]!.GetValue<int>().ShouldBeGreaterThan(summary["total"]!.GetValue<int>() / 2);
        summary["engineUsed"]!.GetValue<string>().ShouldStartWith(engine);

        var scheduleId = done["resultScheduleId"]!.GetValue<Guid>();
        var report = await client.GetJson($"/api/v1/schedules/{scheduleId}/conflicts");
        report["hardCount"]!.GetValue<int>().ShouldBe(0);
        var board = await client.GetJson($"/api/v1/schedules/{scheduleId}/board");
        board["editable"]!.GetValue<bool>().ShouldBeTrue("the result is unlocked once the job finished");
        board["entries"]!.AsArray().Count.ShouldBe(summary["placed"]!.GetValue<int>());
    }

    [Fact]
    public async Task Complete_mode_keeps_existing_entries_and_compare_reports_differences()
    {
        var client = await factory.LoginAsync(institutionCode: "SEC");
        var draft = await factory.NewDraft("SEC", "partial-base");
        var (a, _, room) = await factory.TwoSessionsOfOneGroup("SEC");
        var kept = await factory.Place(draft, a, 4, 2, room);

        var job = await client.PostJson("/api/v1/generation/jobs", new { termId = await TermId("SEC"), baseScheduleId = draft, mode = "complete", engine = "heuristic", timeLimitSeconds = 10 }, 201);
        var done = await WaitForJob(client, job.Id());
        var result = done["resultScheduleId"]!.GetValue<Guid>();
        var entries = (await client.GetJson($"/api/v1/schedules/{result}/board"))["entries"]!.AsArray();
        entries.ShouldContain(e => e!["sessionId"]!.GetValue<Guid>() == a && e["day"]!.GetValue<int>() == 4 && e["startSlot"]!.GetValue<int>() == 2);

        var cmp = await client.GetJson($"/api/v1/schedules/compare?a={draft}&b={result}");
        cmp["unchanged"]!.GetValue<int>().ShouldBe(1);
        cmp["added"]!.GetValue<int>().ShouldBe(entries.Count - 1);
        cmp["b"]!["hardCount"]!.GetValue<int>().ShouldBe(0);
        _ = kept;
    }

    [Fact]
    public async Task Substitution_lists_affected_sessions_ranks_substitutes_and_records_exceptions()
    {
        var client = await factory.LoginAsync(institutionCode: "SEC");
        var schedule = await factory.NewDraft("SEC", "subst-base");
        await client.PostJson($"/api/v1/schedules/{schedule}/entries/auto-place", new { });
        await client.PostJson($"/api/v1/schedules/{schedule}/publish", new { });

        var (instructor, term) = await factory.WithDb(async db =>
        {
            var i = await db.ScheduleEntries.Where(e => e.ScheduleId == schedule && e.InstructorId != null)
                .GroupBy(e => e.InstructorId).OrderByDescending(g => g.Count()).Select(g => g.Key!.Value).FirstAsync();
            var t = await db.Schedules.Where(s => s.Id == schedule).Join(db.AcademicTerms, s => s.TermId, x => x.Id, (s, x) => x).FirstAsync();
            return (i, t);
        });
        var from = term.StartDate.AddDays(7);
        var sub = await client.PostJson("/api/v1/substitutions", new { absentInstructorId = instructor, fromDate = from, toDate = from.AddDays(6), reason = "Conference" }, 201);
        var items = sub["items"]!.AsArray();
        items.Count.ShouldBeGreaterThan(0);
        items.ShouldAllBe(i => i!["status"]!.GetValue<string>() == "open");

        var first = items[0]!;
        var candidates = (await client.GetJson($"/api/v1/substitutions/{sub.Id()}/candidates?entryId={first["entryId"]}&date={first["date"]}")).AsArray();
        candidates.Count.ShouldBeGreaterThan(0);
        candidates.ShouldNotContain(c => c!["instructorId"]!.GetValue<Guid>() == instructor);

        var assigned = await client.PostJson($"/api/v1/substitutions/{sub.Id()}/assign",
            new { entryId = first["entryId"]!.GetValue<Guid>(), date = first["date"]!.GetValue<string>(), instructorId = candidates[0]!["instructorId"]!.GetValue<Guid>() });
        assigned["covered"]!.GetValue<int>().ShouldBe(1);

        if (items.Count > 1)
        {
            var second = items[1]!;
            var cancelled = await client.PostJson($"/api/v1/substitutions/{sub.Id()}/cancel-session",
                new { entryId = second["entryId"]!.GetValue<Guid>(), date = second["date"]!.GetValue<string>(), reason = "No cover" });
            cancelled["cancelled"]!.GetValue<int>().ShouldBe(1);
        }

        (await factory.WithDb(db => db.ScheduleExceptions.CountAsync(e => e.SubstitutionId == sub.Id()))).ShouldBe(Math.Min(2, items.Count));
        var closed = await client.PostJson($"/api/v1/substitutions/{sub.Id()}/close?cancel=true", new { });
        closed["status"]!.GetValue<string>().ShouldBe("Cancelled");
        (await factory.WithDb(db => db.ScheduleExceptions.CountAsync(e => e.SubstitutionId == sub.Id()))).ShouldBe(0);
    }
}
