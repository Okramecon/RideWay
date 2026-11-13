using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoHome.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class StopLocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Lat",
                table: "Stops",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Lng",
                table: "Stops",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "Stops",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PathJson",
                table: "Routes",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Lat",
                table: "Stops");

            migrationBuilder.DropColumn(
                name: "Lng",
                table: "Stops");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "Stops");

            migrationBuilder.DropColumn(
                name: "PathJson",
                table: "Routes");
        }
    }
}
