using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoHome.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeatLayoutAndPhoto : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SeatNumber",
                table: "OneOffBookings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeatNumber",
                table: "Memberships",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CabinPhoto",
                table: "Cars",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Layout",
                table: "Cars",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SeatNumber",
                table: "OneOffBookings");

            migrationBuilder.DropColumn(
                name: "SeatNumber",
                table: "Memberships");

            migrationBuilder.DropColumn(
                name: "CabinPhoto",
                table: "Cars");

            migrationBuilder.DropColumn(
                name: "Layout",
                table: "Cars");
        }
    }
}
