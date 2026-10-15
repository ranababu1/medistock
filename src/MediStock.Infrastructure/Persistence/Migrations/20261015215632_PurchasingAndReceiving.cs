using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediStock.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PurchasingAndReceiving : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Purchases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SupplierId = table.Column<int>(type: "int", nullable: false),
                    InvoiceNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    InvoiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ReceivedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ReceivedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: true),
                    ClosedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    StatusReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Purchases", x => x.Id);
                    table.CheckConstraint("CK_Purchases_Lifecycle_Consistent", "([Status] = 1 AND [ReceivedOn] IS NULL AND [ReceivedAtUtc] IS NULL AND [ReceivedBy] IS NULL\r\n            AND [ClosedAtUtc] IS NULL AND [ClosedBy] IS NULL AND [StatusReason] IS NULL)\r\nOR ([Status] = 2 AND [ReceivedOn] IS NOT NULL AND [ReceivedAtUtc] IS NOT NULL AND [ReceivedBy] IS NOT NULL\r\n            AND [ClosedAtUtc] IS NULL AND [ClosedBy] IS NULL AND [StatusReason] IS NULL)\r\nOR ([Status] = 4 AND [ReceivedOn] IS NULL AND [ReceivedAtUtc] IS NULL AND [ReceivedBy] IS NULL\r\n            AND [ClosedAtUtc] IS NOT NULL AND [ClosedBy] IS NOT NULL AND [StatusReason] IS NOT NULL)\r\nOR ([Status] = 3 AND [ReceivedOn] IS NOT NULL AND [ReceivedAtUtc] IS NOT NULL AND [ReceivedBy] IS NOT NULL\r\n            AND [ClosedAtUtc] IS NOT NULL AND [ClosedBy] IS NOT NULL AND [StatusReason] IS NOT NULL)");
                    table.CheckConstraint("CK_Purchases_Status_Valid", "[Status] BETWEEN 1 AND 4");
                    table.ForeignKey(
                        name: "FK_Purchases_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PurchaseId = table.Column<int>(type: "int", nullable: false),
                    MedicineId = table.Column<int>(type: "int", nullable: false),
                    BatchNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    QuantityReceived = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitCost = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    SellingPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    MedicineBatchId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseLines", x => x.Id);
                    table.CheckConstraint("CK_PurchaseLines_Costs_NonNegative", "[UnitCost] >= 0 AND [SellingPrice] >= 0");
                    table.CheckConstraint("CK_PurchaseLines_Quantity_Positive", "[Quantity] > 0");
                    table.CheckConstraint("CK_PurchaseLines_QuantityReceived_Within", "[QuantityReceived] >= 0 AND [QuantityReceived] <= [Quantity]");
                    table.ForeignKey(
                        name: "FK_PurchaseLines_MedicineBatches_MedicineBatchId",
                        column: x => x.MedicineBatchId,
                        principalTable: "MedicineBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseLines_Medicines_MedicineId",
                        column: x => x.MedicineId,
                        principalTable: "Medicines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseLines_Purchases_PurchaseId",
                        column: x => x.PurchaseId,
                        principalTable: "Purchases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseLines_MedicineBatchId",
                table: "PurchaseLines",
                column: "MedicineBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseLines_MedicineId",
                table: "PurchaseLines",
                column: "MedicineId");

            migrationBuilder.CreateIndex(
                name: "UX_PurchaseLines_Purchase_Medicine_Batch",
                table: "PurchaseLines",
                columns: new[] { "PurchaseId", "MedicineId", "BatchNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_InvoiceDate",
                table: "Purchases",
                column: "InvoiceDate");

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_Status",
                table: "Purchases",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_SupplierId_InvoiceNumber",
                table: "Purchases",
                columns: new[] { "SupplierId", "InvoiceNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PurchaseLines");

            migrationBuilder.DropTable(
                name: "Purchases");
        }
    }
}
