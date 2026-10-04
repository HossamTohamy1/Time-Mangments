using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Timetable.Api.IntegrationTests.Infrastructure;

namespace Timetable.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class ScheduleEditingTests(TimetableApiFactory factory)
{
    private static JsonNode Entry(JsonNode mutation) => mutation["upserted"]!.AsArray().Single()!;

    private static async Task<JsonNode> Assign(HttpClient c, Guid schedule, Guid session, int day, int slot, Guid? room = null) =>
        await c.PostJson($"/api/v1/schedules/{schedule}/entries", new { sessionId = session, day, startSlot = slot, roomId = room });

    [Fact]
    public async Task Assign_rejects_hard_conflicts_with_reasons_and_allows_free_cells()
    {
        var client = await factory.LoginAsync(institutionCode: "SEC");
        var schedule = await factory.NewDraft("SEC", "edit-assign");
        var (a, b, room) = await factory.TwoSessionsOfOneGroup("SEC");

        var first = Entry(await Assign(client, schedule, a, 0, 0, room));
        first["day"]!.GetValue<int>().ShouldBe(0);
        first["roomId"]!.GetValue<Guid>().ShouldBe(room);

        var clash = await client.PostAsJsonAsync($"/api/v1/schedules/{schedule}/entries", new { sessionId = b, day = 0, startSlot = 0 });
        clash.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await clash.Json();
        problem["code"]!.GetValue<string>().ShouldBe("MOVE_CONFLICT");
        problem["details"]!.AsArray().Select(v => v!["code"]!.GetValue<string>()).ShouldContain("GROUP_DOUBLE_BOOKED");

        // Same session elsewhere: allowed, room picked automatically.
        var second = Entry(await Assign(client, schedule, b, 0, 1));
        second["roomId"].ShouldNotBeNull();

        var board = await client.GetJson($"/api/v1/schedules/{schedule}/board");
        board["entries"]!.AsArray().Count.ShouldBe(2);
        board["canUndo"]!.GetValue<bool>().ShouldBeTrue();
        board["sessions"]!.AsArray().Count.ShouldBeGreaterThan(10);
    }

    [Fact]
    public async Task Move_checks_row_version_and_pins_block_moves()
    {
        var client = await factory.LoginAsync(institutionCode: "SEC");
        var schedule = await factory.NewDraft("SEC", "edit-move");
        var (a, _, room) = await factory.TwoSessionsOfOneGroup("SEC");
        var entry = Entry(await Assign(client, schedule, a, 1, 0, room));
        var id = entry.Id();
        var version = entry["rowVersion"]!.GetValue<string>();

        var moved = Entry(await client.PutJson($"/api/v1/schedules/{schedule}/entries/{id}/move", new { day = 1, startSlot = 1, rowVersion = version }));
        moved["startSlot"]!.GetValue<int>().ShouldBe(1);
        moved["rowVersion"]!.GetValue<string>().ShouldNotBe(version);

        var stale = await client.PutAsJsonAsync($"/api/v1/schedules/{schedule}/entries/{id}/move", new { day = 1, startSlot = 2, rowVersion = version });
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await stale.Json())["code"]!.GetValue<string>().ShouldBe("CONCURRENCY_CONFLICT");

        await client.PutJson($"/api/v1/schedules/{schedule}/entries/{id}/pin", new { pinned = true });
        var pinned = await client.PutAsJsonAsync($"/api/v1/schedules/{schedule}/entries/{id}/move", new { day = 1, startSlot = 2 });
        pinned.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await pinned.Json())["code"]!.GetValue<string>().ShouldBe("ENTRY_PINNED");
    }

    [Fact]
    public async Task Undo_and_redo_restore_previous_states()
    {
        var client = await factory.LoginAsync(institutionCode: "SEC");
        var schedule = await factory.NewDraft("SEC", "edit-undo");
        var (a, _, room) = await factory.TwoSessionsOfOneGroup("SEC");
        var id = Entry(await Assign(client, schedule, a, 2, 0, room)).Id();
        await client.PutJson($"/api/v1/schedules/{schedule}/entries/{id}/move", new { day = 2, startSlot = 2 });

        var undo1 = await client.PostJson($"/api/v1/schedules/{schedule}/undo", new { });
        Entry(undo1)["startSlot"]!.GetValue<int>().ShouldBe(0);
        var undo2 = await client.PostJson($"/api/v1/schedules/{schedule}/undo", new { });
        undo2["removed"]!.AsArray().Single()!.GetValue<Guid>().ShouldBe(id);
        undo2["canUndo"]!.GetValue<bool>().ShouldBeFalse();
        undo2["canRedo"]!.GetValue<bool>().ShouldBeTrue();

        var none = await client.PostAsJsonAsync($"/api/v1/schedules/{schedule}/undo", new { });
        none.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var redo = await client.PostJson($"/api/v1/schedules/{schedule}/redo", new { });
        Entry(redo).Id().ShouldBe(id);
        (await client.GetJson($"/api/v1/schedules/{schedule}/board"))["entries"]!.AsArray().Count.ShouldBe(1);

        var history = (await client.GetJson($"/api/v1/schedules/{schedule}/changes")).AsArray();
        history.Select(h => h!["kind"]!.GetValue<string>()).ShouldBe(["move", "assign"]);
    }

    [Fact]
    public async Task Swap_exchanges_time_slots()
    {
        var client = await factory.LoginAsync(institutionCode: "SEC");
        var schedule = await factory.NewDraft("SEC", "edit-swap");
        var (a, b, room) = await factory.TwoSessionsOfOneGroup("SEC");
        var ea = Entry(await Assign(client, schedule, a, 3, 0, room)).Id();
        var eb = Entry(await Assign(client, schedule, b, 3, 1, room)).Id();
        var swapped = await client.PostJson($"/api/v1/schedules/{schedule}/entries/swap", new { firstEntryId = ea, secondEntryId = eb });
        var list = swapped["upserted"]!.AsArray();
        list.Single(e => e!.Id() == ea)!["startSlot"]!.GetValue<int>().ShouldBe(1);
        list.Single(e => e!.Id() == eb)!["startSlot"]!.GetValue<int>().ShouldBe(0);
    }

    [Fact]
    public async Task Auto_place_never_creates_hard_conflicts_and_publish_archives_previous_version()
    {
        var client = await factory.LoginAsync(institutionCode: "SEC");
        var schedule = await factory.NewDraft("SEC", "edit-auto");
        var placed = await client.PostJson($"/api/v1/schedules/{schedule}/entries/auto-place", new { });
        placed["upserted"]!.AsArray().Count.ShouldBeGreaterThan(10);

        var report = await client.PostJson($"/api/v1/schedules/{schedule}/validate", new { });
        report["hardCount"]!.GetValue<int>().ShouldBe(0);

        var published = await client.PostJson($"/api/v1/schedules/{schedule}/publish", new { });
        published["status"]!.GetValue<string>().ShouldBe("Published");

        // Published schedules are read-only; a clone is the editable next version.
        var locked = await client.PostAsJsonAsync($"/api/v1/schedules/{schedule}/entries/auto-place", new { });
        locked.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var clone = await client.PostJson($"/api/v1/schedules/{schedule}/clone", new { name = "next" }, 201);
        clone["entryCount"]!.GetValue<int>().ShouldBe(placed["upserted"]!.AsArray().Count);
        await client.PostJson($"/api/v1/schedules/{clone.Id()}/publish", new { });

        var list = (await client.GetJson("/api/v1/schedules")).AsArray();
        list.Single(s => s!.Id() == schedule)!["status"]!.GetValue<string>().ShouldBe("Archived");
        list.Single(s => s!.Id() == clone.Id())!["status"]!.GetValue<string>().ShouldBe("Published");
    }
}
