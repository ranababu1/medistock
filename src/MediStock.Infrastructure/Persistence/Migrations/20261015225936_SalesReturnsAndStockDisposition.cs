using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediStock.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesReturnsAndStockDisposition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CancellationReason",
                table: "Sales",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancelledAtUtc",
                table: "Sales",
                type: "datetimeoffset(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelledBy",
                table: "Sales",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Sales",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ReturnedAmount",
                table: "SaleLines",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ReturnedCost",
                table: "SaleLines",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ReturnedQuantity",
                table: "SaleLines",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ReturnedTax",
                table: "SaleLines",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "SaleLines",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ReturnedQuantity",
                table: "SaleLineBatchAllocations",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "SaleLineBatchAllocations",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SalesReturns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReturnNumber = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ReturnedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    ReturnedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    SaleId = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RefundMethod = table.Column<int>(type: "int", nullable: false),
                    RefundTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesReturns", x => x.Id);
                    table.CheckConstraint("CK_SalesReturns_Reason_NotBlank", "LEN(LTRIM(RTRIM([Reason]))) > 0");
                    table.CheckConstraint("CK_SalesReturns_RefundMethod_Valid", "[RefundMethod] BETWEEN 1 AND 4");
                    table.CheckConstraint("CK_SalesReturns_RefundTotal_NonNegative", "[RefundTotal] >= 0");
                    table.ForeignKey(
                        name: "FK_SalesReturns_Sales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "Sales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesReturnLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SalesReturnId = table.Column<int>(type: "int", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    SaleLineId = table.Column<int>(type: "int", nullable: false),
                    MedicineBatchId = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    RefundAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CostAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Disposition = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesReturnLines", x => x.Id);
                    table.CheckConstraint("CK_SalesReturnLines_Disposition_Valid", "[Disposition] BETWEEN 1 AND 3");
                    table.CheckConstraint("CK_SalesReturnLines_LineNumber_Positive", "[LineNumber] > 0");
                    table.CheckConstraint("CK_SalesReturnLines_Quantity_Positive", "[Quantity] > 0");
                    table.CheckConstraint("CK_SalesReturnLines_Refund_Components_NonNegative", "[TaxAmount] >= 0 AND [CostAmount] >= 0");
                    table.CheckConstraint("CK_SalesReturnLines_RefundAmount_NonNegative", "[RefundAmount] >= 0");
                    table.ForeignKey(
                        name: "FK_SalesReturnLines_MedicineBatches_MedicineBatchId",
                        column: x => x.MedicineBatchId,
                        principalTable: "MedicineBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesReturnLines_SaleLines_SaleLineId",
                        column: x => x.SaleLineId,
                        principalTable: "SaleLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesReturnLines_SalesReturns_SalesReturnId",
                        column: x => x.SalesReturnId,
                        principalTable: "SalesReturns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Sales_Cancellation_Consistent",
                table: "Sales",
                sql: "([Status] <> 3 AND [CancelledAtUtc] IS NULL AND [CancelledBy] IS NULL AND [CancellationReason] IS NULL)\r\nOR ([Status] = 3 AND [CancelledAtUtc] IS NOT NULL AND [CancelledBy] IS NOT NULL AND [CancellationReason] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SaleLines_Returned_Within_Sold",
                table: "SaleLines",
                sql: "[ReturnedQuantity] >= 0 AND [ReturnedQuantity] <= [Quantity]");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SaleLines_ReturnedAmount_Within_LineTotal",
                table: "SaleLines",
                sql: "[ReturnedAmount] >= 0 AND [ReturnedAmount] <= [LineTotal]");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SaleLines_ReturnedCost_Within_LineCost",
                table: "SaleLines",
                sql: "[ReturnedCost] >= 0 AND [ReturnedCost] <= ROUND([Quantity] * [UnitCost], 2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SaleLines_ReturnedTax_Within_Tax",
                table: "SaleLines",
                sql: "[ReturnedTax] >= 0 AND [ReturnedTax] <= [TaxAmount]");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SaleLineBatchAllocations_Returned_Within_Allocated",
                table: "SaleLineBatchAllocations",
                sql: "[ReturnedQuantity] >= 0 AND [ReturnedQuantity] <= [Quantity]");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturnLines_MedicineBatchId",
                table: "SalesReturnLines",
                column: "MedicineBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturnLines_SaleLineId",
                table: "SalesReturnLines",
                column: "SaleLineId");

            migrationBuilder.CreateIndex(
                name: "UX_SalesReturnLines_Return_LineNumber",
                table: "SalesReturnLines",
                columns: new[] { "SalesReturnId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturns_BusinessDate",
                table: "SalesReturns",
                column: "BusinessDate");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturns_SaleId",
                table: "SalesReturns",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "UX_SalesReturns_ReturnNumber",
                table: "SalesReturns",
                column: "ReturnNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SalesReturnLines");

            migrationBuilder.DropTable(
                name: "SalesReturns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Sales_Cancellation_Consistent",
                table: "Sales");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SaleLines_Returned_Within_Sold",
                table: "SaleLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SaleLines_ReturnedAmount_Within_LineTotal",
                table: "SaleLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SaleLines_ReturnedCost_Within_LineCost",
                table: "SaleLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SaleLines_ReturnedTax_Within_Tax",
                table: "SaleLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SaleLineBatchAllocations_Returned_Within_Allocated",
                table: "SaleLineBatchAllocations");

            migrationBuilder.DropColumn(
                name: "CancellationReason",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "CancelledAtUtc",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "CancelledBy",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "ReturnedAmount",
                table: "SaleLines");

            migrationBuilder.DropColumn(
                name: "ReturnedCost",
                table: "SaleLines");

            migrationBuilder.DropColumn(
                name: "ReturnedQuantity",
                table: "SaleLines");

            migrationBuilder.DropColumn(
                name: "ReturnedTax",
                table: "SaleLines");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "SaleLines");

            migrationBuilder.DropColumn(
                name: "ReturnedQuantity",
                table: "SaleLineBatchAllocations");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "SaleLineBatchAllocations");
        }
    }
}
