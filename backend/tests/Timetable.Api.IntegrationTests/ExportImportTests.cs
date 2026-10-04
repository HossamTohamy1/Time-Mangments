using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Timetable.Api.IntegrationTests.Infrastructure;

namespace Timetable.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class ExportImportTests(TimetableApiFactory factory)
{
    private async Task<(HttpClient Client, Guid Schedule)> PlacedSchedule(string code)
    {
        var client = await factory.LoginAsync(institutionCode: code);
        var schedule = await factory.NewDraft(code, $"export-{Guid.NewGuid():N}");
        await client.PostJson($"/api/v1/schedules/{schedule}/entries/auto-place", new { });
        return (client, schedule);
    }

    [Fact]
    public async Task Pdf_export_embeds_the_arabic_font_and_renders_one_page_per_resource()
    {
        var (client, schedule) = await PlacedSchedule("SEC");
        var res = await client.GetAsync($"/api/v1/schedules/{schedule}/export?format=pdf&view=group&lang=ar");
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Encoding.ASCII.GetString(bytes, 0, 5).ShouldBe("%PDF-");
        Encoding.Latin1.GetString(bytes).ShouldContain("IBMPlexSansArabic");
    }

    [Fact]
    public async Task Excel_export_has_a_sheet_per_resource_right_to_left_in_arabic_and_an_entries_sheet()
    {
        var (client, schedule) = await PlacedSchedule("SEC");
        var res = await client.GetAsync($"/api/v1/schedules/{schedule}/export?format=xlsx&view=instructor&lang=ar");
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var wb = new XLWorkbook(new MemoryStream(await res.Content.ReadAsByteArrayAsync()));
        wb.Worksheets.Count.ShouldBeGreaterThan(2);
        wb.Worksheets.First().RightToLeft.ShouldBeTrue();
        var entries = wb.Worksheets.Last();
        entries.Cell(1, 1).GetString().ShouldBe("اليوم");
        entries.RowsUsed().Count().ShouldBeGreaterThan(5);
    }

    [Fact]
    public async Task Csv_export_is_utf8_with_bom_and_english_headers()
    {
        var (client, schedule) = await PlacedSchedule("UNI");
        var bytes = await client.GetByteArrayAsync($"/api/v1/schedules/{schedule}/export?format=csv&view=room&lang=en");
        bytes.Take(3).ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF });
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        text.Split("\r\n")[0].ShouldStartWith("Day,Start,End,Code");
    }

    private static MultipartFormDataContent Csv(string content, string name = "rooms.csv")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", name);
        return form;
    }

    [Fact]
    public async Task Import_dry_run_reports_row_errors_and_writes_nothing()
    {
        var client = await factory.LoginAsync(institutionCode: "SEC");
        var roomType = await factory.WithDb(db => db.RoomTypes.Where(t => db.Institutions.Any(i => i.Id == t.InstitutionId && i.Code == "SEC")).Select(t => t.Code).FirstAsync());
        var csv = "code,nameEn,roomType,capacity\n" +
                  $"IMP-1,Import one,{roomType},30\n" +
                  "IMP-2,Import two,NOPE,30\n" +
                  $"IMP-1,Again,{roomType},30\n" +
                  $"IMP-3,Three,{roomType},many\n";
        var res = await client.PostAsync("/api/v1/imports/rooms?dryRun=false", Csv(csv));
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        var report = await res.Json();
        report["applied"]!.GetValue<bool>().ShouldBeFalse();
        report["failed"]!.GetValue<int>().ShouldBe(3);
        var rows = report["rows"]!.AsArray();
        rows[1]!["issues"]!.AsArray().Select(i => i!["code"]!.GetValue<string>()).ShouldContain("REFERENCE_NOT_FOUND");
        rows[2]!["issues"]!.AsArray().Select(i => i!["code"]!.GetValue<string>()).ShouldContain("IMPORT_DUPLICATE_IN_FILE");
        rows[3]!["issues"]!.AsArray().Select(i => i!["code"]!.GetValue<string>()).ShouldContain("IMPORT_NOT_A_NUMBER");
        (await factory.WithDb(db => db.Rooms.AnyAsync(r => r.Code == "IMP-1"))).ShouldBeFalse("a failed import is all-or-nothing");
    }

    [Fact]
    public async Task Import_creates_then_updates_by_code_keeping_absent_columns()
    {
        var client = await factory.LoginAsync(institutionCode: "SEC");
        var roomType = await factory.WithDb(db => db.RoomTypes.Where(t => db.Institutions.Any(i => i.Id == t.InstitutionId && i.Code == "SEC")).Select(t => t.Code).FirstAsync());

        var preview = await (await client.PostAsync("/api/v1/imports/rooms?dryRun=true", Csv($"code,nameAr,roomType,capacity\nUPS-1,قاعة الاستيراد,{roomType},25\n"))).Json();
        preview["created"]!.GetValue<int>().ShouldBe(1);
        preview["applied"]!.GetValue<bool>().ShouldBeFalse();
        (await factory.WithDb(db => db.Rooms.AnyAsync(r => r.Code == "UPS-1"))).ShouldBeFalse("dry run");

        var applied = await (await client.PostAsync("/api/v1/imports/rooms?dryRun=false", Csv($"code,nameAr,roomType,capacity\nUPS-1,قاعة الاستيراد,{roomType},25\n"))).Json();
        applied["applied"]!.GetValue<bool>().ShouldBeTrue();

        var update = await (await client.PostAsync("/api/v1/imports/rooms?dryRun=false", Csv("code,capacity\nUPS-1,55\n"))).Json();
        update["updated"]!.GetValue<int>().ShouldBe(1);
        var room = await factory.WithDb(db => db.Rooms.FirstAsync(r => r.Code == "UPS-1"));
        room.Capacity.ShouldBe(55);
        room.NameAr.ShouldBe("قاعة الاستيراد");

        var insertOnly = await (await client.PostAsync("/api/v1/imports/rooms?dryRun=false&mode=insert", Csv("code,capacity\nUPS-1,99\n"))).Json();
        insertOnly["skipped"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task Import_messages_follow_the_client_language_header()
    {
        var client = await factory.LoginAsync(institutionCode: "SEC");
        client.DefaultRequestHeaders.Add("X-Client-Language", "ar");
        var report = await (await client.PostAsync("/api/v1/imports/rooms?dryRun=true", Csv("code,nameEn,roomType,capacity\nAR-1,x,NOPE,ten\n"))).Json();
        var messages = report["rows"]![0]!["issues"]!.AsArray().Select(i => i!["message"]!.GetValue<string>()).ToList();
        messages.ShouldContain(m => m.Contains("ليست رقماً"));
    }

    [Fact]
    public async Task Templates_and_kinds_are_available()
    {
        var client = await factory.LoginAsync(institutionCode: "UNI");
        var kinds = (await client.GetJson("/api/v1/imports")).AsArray().Select(k => k!["kind"]!.GetValue<string>()).ToList();
        kinds.ShouldBe(["buildings", "rooms", "instructors", "courses", "groups"], ignoreOrder: true);
        var res = await client.GetAsync("/api/v1/imports/groups/template?format=xlsx");
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var wb = new XLWorkbook(new MemoryStream(await res.Content.ReadAsByteArrayAsync()));
        wb.Worksheets.First().Cell(1, 1).GetString().ShouldBe("code");
        var unknown = await client.PostAsync("/api/v1/imports/groups?dryRun=true", Csv("code,colour\nX,red\n", "groups.csv"));
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await unknown.Json())["code"]!.GetValue<string>().ShouldBe("IMPORT_UNKNOWN_COLUMNS");
    }
}
