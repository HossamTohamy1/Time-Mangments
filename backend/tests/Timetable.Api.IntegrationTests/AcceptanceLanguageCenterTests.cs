using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Timetable.Api.IntegrationTests.Infrastructure;

namespace Timetable.Api.IntegrationTests;

/// <summary>
/// Acceptance proof that nothing is hard-coded: an institution type the product has never seen (a language training
/// centre with evening cohorts, conversation clubs and native-speaker trainers) is configured from the Blank template
/// purely through the public API, then generated, validated, exported in Arabic and imported into — with no code change.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AcceptanceLanguageCenterTests(TimetableApiFactory factory)
{
    private static async Task<Guid> Post(HttpClient c, string url, object body) => (await c.PostJson(url, body, 201)).Id();

    [Fact]
    public async Task Language_training_center_is_configured_generated_and_exported_without_code_changes()
    {
        // 1. Create the institution from the blank template (the creator becomes its administrator).
        var admin = await factory.LoginAsync();
        (await admin.PostAsJsonAsync("/api/v1/institutions", new { code = "LTC", nameAr = "مركز اللغات", nameEn = "Language Training Center", templateCode = "blank", defaultLanguage = "ar" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var c = await factory.LoginAsync(institutionCode: "LTC");

        // 2. Vocabulary of this institution: its own lookups and terminology.
        var studio = await Post(c, "/api/v1/lookups/room-types", new { code = "STUDIO", nameAr = "استوديو محادثة", nameEn = "Conversation studio", sortOrder = 1, isActive = true });
        var classroom = await Post(c, "/api/v1/lookups/room-types", new { code = "CLASS", nameAr = "فصل", nameEn = "Classroom", sortOrder = 2, isActive = true });
        var native = await Post(c, "/api/v1/lookups/instructor-types", new { code = "NATIVE", nameAr = "مدرب ناطق أصلي", nameEn = "Native-speaker trainer", sortOrder = 1, isActive = true });
        var tutor = await Post(c, "/api/v1/lookups/instructor-types", new { code = "TUTOR", nameAr = "مدرّس قواعد", nameEn = "Grammar tutor", sortOrder = 2, isActive = true });
        var cohortKind = await Post(c, "/api/v1/lookups/group-kinds", new { code = "COHORT", nameAr = "دفعة", nameEn = "Cohort", sortOrder = 1, isActive = true });
        var conv = await Post(c, "/api/v1/lookups/session-types", new
        {
            code = "CONV", nameAr = "نادي محادثة", nameEn = "Conversation club", color = "#0E7C66", sortOrder = 1, isActive = true, defaultDurationSlots = 1,
            defaultRoomTypeId = studio, requiresRoom = true, requiresInstructor = true, allowedInstructorTypeCodes = new[] { "NATIVE" },
        });
        var grammar = await Post(c, "/api/v1/lookups/session-types", new
        {
            code = "GRAMMAR", nameAr = "قواعد", nameEn = "Grammar workshop", color = "#7048E8", sortOrder = 2, isActive = true, defaultDurationSlots = 2,
            defaultRoomTypeId = classroom, requiresRoom = true, requiresInstructor = true,
        });
        (await c.PutAsJsonAsync("/api/v1/config/terminology", new[]
        {
            new { key = "term.group", ar = "دفعة", en = "Cohort" }, new { key = "term.groups", ar = "الدفعات", en = "Cohorts" },
            new { key = "term.instructor", ar = "مدرب", en = "Trainer" }, new { key = "term.course", ar = "برنامج", en = "Program" },
        })).IsSuccessStatusCode.ShouldBeTrue();

        // Features are switched on per institution: trainers are chosen from a pool by the engine.
        (await c.PutAsJsonAsync("/api/v1/config/features", new Dictionary<string, bool> { ["instructor-pools"] = true })).IsSuccessStatusCode.ShouldBeTrue();

        // 3. Evening time structure, Saturday–Wednesday, with a short break.
        await c.PutJson("/api/v1/config/time-structure", new
        {
            workingDays = new[] { 6, 0, 1, 2, 3 }, weekStartDay = 6, weekCycleLength = 1, weekCycleLabels = Array.Empty<string>(),
            periods = new[]
            {
                new { index = 0, nameAr = "الأولى", nameEn = "P1", start = "17:00", end = "18:00", isBreak = false },
                new { index = 1, nameAr = "الثانية", nameEn = "P2", start = "18:00", end = "19:00", isBreak = false },
                new { index = 2, nameAr = "استراحة", nameEn = "Tea break", start = "19:00", end = "19:15", isBreak = true },
                new { index = 3, nameAr = "الثالثة", nameEn = "P3", start = "19:15", end = "20:15", isBreak = false },
                new { index = 4, nameAr = "الرابعة", nameEn = "P4", start = "20:15", end = "21:15", isBreak = false },
            },
            shifts = Array.Empty<object>(), dayOverrides = Array.Empty<object>(),
        });

        // 4. A custom field and a no-code rule: conversation clubs only after the break (periods 4–5, 1-based).
        await Post(c, "/api/v1/custom-fields", new
        {
            entityType = "Room", key = "acoustic", nameAr = "معالجة صوتية", nameEn = "Acoustic treatment", dataType = "Boolean", required = false,
            searchable = true, showInTable = true, importable = true, sortOrder = 1, isActive = true,
        });
        await Post(c, "/api/v1/rules", new
        {
            code = "CONV_AFTER_BREAK", nameAr = "المحادثة بعد الاستراحة", nameEn = "Conversation after the break", severity = "Hard", weight = 1,
            definition = new { scope = new { sessionTypeCodes = new[] { "CONV" } }, condition = new { type = "slotRange", from = 4, to = 5 }, effect = "Limit" },
        });
        (await c.PutAsJsonAsync("/api/v1/config/constraints/GROUP_GAPS", new { severity = "Soft", weight = 8 })).IsSuccessStatusCode.ShouldBeTrue();

        // 5. Rooms come from a CSV import, including the custom field column.
        var csv = "code,nameAr,nameEn,roomType,capacity,cf.acoustic\n" +
                  "ST1,استوديو 1,Studio 1,STUDIO,14,yes\nST2,استوديو 2,Studio 2,STUDIO,14,no\nC1,فصل 1,Class 1,CLASS,20,no\nC2,فصل 2,Class 2,CLASS,20,no\n";
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "rooms.csv");
        var imported = await (await c.PostAsync("/api/v1/imports/rooms?dryRun=false", form)).Json();
        imported["applied"]!.GetValue<bool>().ShouldBeTrue();
        imported["created"]!.GetValue<int>().ShouldBe(4);

        // 6. People, programs, cohorts, term and sessions.
        var trainers = new List<Guid>();
        for (var i = 1; i <= 3; i++)
            trainers.Add(await Post(c, "/api/v1/instructors", new { code = $"N{i}", nameAr = $"مدرب {i}", nameEn = $"Trainer {i}", instructorTypeId = native }));
        var tutors = new List<Guid>();
        for (var i = 1; i <= 2; i++)
            tutors.Add(await Post(c, "/api/v1/instructors", new { code = $"G{i}", nameAr = $"مدرس {i}", nameEn = $"Tutor {i}", instructorTypeId = tutor }));
        var english = await Post(c, "/api/v1/courses", new { code = "ENG-B1", nameAr = "إنجليزي B1", nameEn = "English B1" });
        var french = await Post(c, "/api/v1/courses", new { code = "FR-A2", nameAr = "فرنسي A2", nameEn = "French A2" });
        var cohorts = new List<Guid>();
        for (var i = 1; i <= 3; i++)
            cohorts.Add(await Post(c, "/api/v1/groups", new { code = $"EVE-{i}", nameAr = $"دفعة مسائية {i}", nameEn = $"Evening {i}", groupKindId = cohortKind, studentCount = 12 }));
        var term = await Post(c, "/api/v1/terms", new { code = "FALL", nameAr = "خريف", nameEn = "Fall", startDate = "2026-09-05", endDate = "2026-12-30", isCurrent = true });
        for (var i = 0; i < cohorts.Count; i++)
        {
            await Post(c, "/api/v1/sessions", new
            {
                termId = term, courseId = i == 2 ? french : english, sessionTypeId = grammar, durationSlots = 2, sessionsPerWeek = 2, requiredRoomTypeId = classroom,
                instructorId = tutors[i % tutors.Count], groupIds = new[] { cohorts[i] }, weekMask = 0,
            });
            await Post(c, "/api/v1/sessions", new
            {
                termId = term, courseId = i == 2 ? french : english, sessionTypeId = conv, durationSlots = 1, sessionsPerWeek = 2, requiredRoomTypeId = studio,
                candidateInstructorIds = trainers, groupIds = new[] { cohorts[i] }, weekMask = 0,
            });
        }

        // 7. Effective configuration reflects all of it.
        var config = await c.GetJson("/api/v1/config/effective");
        config["terminology"]!["en"]!["term.group"]!.GetValue<string>().ShouldBe("Cohort");
        config["lookups"]!["session-types"]!.AsArray().Select(x => x!["code"]!.GetValue<string>()).ShouldBe(["CONV", "GRAMMAR"], ignoreOrder: true);
        config["time"]!["periods"]!.AsArray().Count.ShouldBe(5);

        // 8. Readiness, generation with CP-SAT, and the rule holds in the result.
        var readiness = await c.GetJson($"/api/v1/generation/readiness?termId={term}");
        readiness["issues"]!.AsArray().Where(x => x!["severity"]!.GetValue<string>() == "error").ShouldBeEmpty();
        var job = await c.PostJson("/api/v1/generation/jobs", new { termId = term, engine = "auto", timeLimitSeconds = 15, name = "Evening timetable" }, 201);
        JsonNode done;
        var until = DateTime.UtcNow.AddSeconds(120);
        do
        {
            await Task.Delay(300);
            done = await c.GetJson($"/api/v1/generation/jobs/{job.Id()}");
        } while (done["status"]!.GetValue<string>() is "Queued" or "Running" && DateTime.UtcNow < until);
        done["status"]!.GetValue<string>().ShouldBe("Succeeded");
        done["summary"]!["placed"]!.GetValue<int>().ShouldBe(12);
        var scheduleId = done["resultScheduleId"]!.GetValue<Guid>();

        var board = await c.GetJson($"/api/v1/schedules/{scheduleId}/board");
        var convSessions = board["sessions"]!.AsArray().Where(s => s!["sessionTypeId"]!.GetValue<Guid>() == conv).Select(s => s!["id"]!.GetValue<Guid>()).ToHashSet();
        var entries = board["entries"]!.AsArray();
        entries.Where(e => convSessions.Contains(e!["sessionId"]!.GetValue<Guid>())).ShouldAllBe(e => e!["startSlot"]!.GetValue<int>() >= 3,
            "conversation clubs must be after the break (rule CONV_AFTER_BREAK)");
        entries.ShouldAllBe(e => new[] { 6, 0, 1, 2, 3 }.Contains(e!["day"]!.GetValue<int>()));
        (await c.GetJson($"/api/v1/schedules/{scheduleId}/conflicts"))["hardCount"]!.GetValue<int>().ShouldBe(0);

        // 9. Manual edit honours the same rule: moving a conversation club before the break is refused with the rule's message.
        var convEntry = entries.First(e => convSessions.Contains(e!["sessionId"]!.GetValue<Guid>()))!;
        var refused = await c.PutAsJsonAsync($"/api/v1/schedules/{scheduleId}/entries/{convEntry.Id()}/move", new { day = convEntry["day"]!.GetValue<int>(), startSlot = 0 });
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.Json())["details"]!.AsArray().Select(d => d!["code"]!.GetValue<string>()).ShouldContain("RULE_SLOT_RANGE");

        // 10. Publish and export in Arabic with the institution's own vocabulary.
        await c.PostJson($"/api/v1/schedules/{scheduleId}/publish", new { });
        var pdf = await c.GetAsync($"/api/v1/schedules/{scheduleId}/export?format=pdf&view=group&lang=ar");
        pdf.StatusCode.ShouldBe(HttpStatusCode.OK);
        var csvExport = Encoding.UTF8.GetString(await c.GetByteArrayAsync($"/api/v1/schedules/{scheduleId}/export?format=csv&view=group&lang=en"));
        csvExport.ShouldContain("Program");
        csvExport.ShouldContain("Cohorts");
        csvExport.ShouldContain("Conversation club");
    }
}
