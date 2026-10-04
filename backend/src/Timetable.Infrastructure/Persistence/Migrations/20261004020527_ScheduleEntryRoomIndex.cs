using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Timetable.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleEntryRoomIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ScheduleEntries_ScheduleId_DayOfWeek_StartSlot_RoomId_WeekMask",
                table: "ScheduleEntries");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleEntries_ScheduleId_DayOfWeek_StartSlot_RoomId_WeekMask",
                table: "ScheduleEntries",
                columns: new[] { "ScheduleId", "DayOfWeek", "StartSlot", "RoomId", "WeekMask" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ScheduleEntries_ScheduleId_DayOfWeek_StartSlot_RoomId_WeekMask",
                table: "ScheduleEntries");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleEntries_ScheduleId_DayOfWeek_StartSlot_RoomId_WeekMask",
                table: "ScheduleEntries",
                columns: new[] { "ScheduleId", "DayOfWeek", "StartSlot", "RoomId", "WeekMask" },
                unique: true,
                filter: "RoomId IS NOT NULL");
        }
    }
}
