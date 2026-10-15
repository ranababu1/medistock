using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediStock.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseRowVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Purchases",
                type: "rowversion",
                rowVersion: true,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Purchases");
        }
    }
}
