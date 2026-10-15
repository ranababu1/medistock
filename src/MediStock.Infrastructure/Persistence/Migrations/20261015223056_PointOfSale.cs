using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediStock.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PointOfSale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "TaxRatePercent",
                table: "Medicines",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "MedicineBatches",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InvoiceSequences",
                columns: table => new
                {
                    SequenceKey = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    NextNumber = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceSequences", x => x.SequenceKey);
                    table.CheckConstraint("CK_InvoiceSequences_NextNumber_NonNegative", "[NextNumber] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "Sales",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InvoiceNumber = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SoldAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false),
                    SoldBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    CustomerName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CustomerPhone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    SubTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DiscountTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxableTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CostTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sales", x => x.Id);
                    table.CheckConstraint("CK_Sales_Customer_Phone_With_Name", "[CustomerPhone] IS NULL OR [CustomerName] IS NOT NULL");
                    table.CheckConstraint("CK_Sales_Status_Valid", "[Status] BETWEEN 1 AND 3");
                    table.CheckConstraint("CK_Sales_Totals_Reconcile", "[SubTotal] >= 0 AND [DiscountTotal] >= 0 AND [TaxableTotal] >= 0 AND [TaxTotal] >= 0\r\nAND [CostTotal] >= 0 AND [Total] >= 0\r\nAND [SubTotal] = [TaxableTotal] + [DiscountTotal]\r\nAND [Total] = [TaxableTotal] + [TaxTotal]\r\nAND [DiscountTotal] <= [SubTotal]");
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SaleId = table.Column<int>(type: "int", nullable: false),
                    Method = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PaidAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.CheckConstraint("CK_Payments_Amount_Positive", "[Amount] > 0");
                    table.CheckConstraint("CK_Payments_Method_Valid", "[Method] BETWEEN 1 AND 4");
                    table.ForeignKey(
                        name: "FK_Payments_Sales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "Sales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SaleLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SaleId = table.Column<int>(type: "int", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    MedicineId = table.Column<int>(type: "int", nullable: false),
                    MedicineName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    UnitCost = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    GrossAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxableAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxRatePercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    TaxAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    LineTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaleLines", x => x.Id);
                    table.CheckConstraint("CK_SaleLines_Amounts_Consistent", "[GrossAmount] = [TaxableAmount] + [DiscountAmount]\r\nAND [LineTotal] = [TaxableAmount] + [TaxAmount]");
                    table.CheckConstraint("CK_SaleLines_Discount_Within_Gross", "[DiscountAmount] <= [GrossAmount]");
                    table.CheckConstraint("CK_SaleLines_LineNumber_Positive", "[LineNumber] > 0");
                    table.CheckConstraint("CK_SaleLines_Money_NonNegative", "[UnitPrice] >= 0 AND [UnitCost] >= 0 AND [GrossAmount] >= 0 AND [DiscountAmount] >= 0 AND [TaxableAmount] >= 0 AND [TaxRatePercent] >= 0 AND [TaxAmount] >= 0 AND [LineTotal] >= 0");
                    table.CheckConstraint("CK_SaleLines_Quantity_Positive", "[Quantity] > 0");
                    table.CheckConstraint("CK_SaleLines_TaxRate_Range", "[TaxRatePercent] <= 100");
                    table.ForeignKey(
                        name: "FK_SaleLines_Medicines_MedicineId",
                        column: x => x.MedicineId,
                        principalTable: "Medicines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleLines_Sales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "Sales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SaleLineBatchAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SaleLineId = table.Column<int>(type: "int", nullable: false),
                    MedicineBatchId = table.Column<int>(type: "int", nullable: false),
                    BatchNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaleLineBatchAllocations", x => x.Id);
                    table.CheckConstraint("CK_SaleLineBatchAllocations_Quantity_Positive", "[Quantity] > 0");
                    table.ForeignKey(
                        name: "FK_SaleLineBatchAllocations_MedicineBatches_MedicineBatchId",
                        column: x => x.MedicineBatchId,
                        principalTable: "MedicineBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleLineBatchAllocations_SaleLines_SaleLineId",
                        column: x => x.SaleLineId,
                        principalTable: "SaleLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_SaleId_Method",
                table: "Payments",
                columns: new[] { "SaleId", "Method" });

            migrationBuilder.CreateIndex(
                name: "IX_SaleLineBatchAllocations_MedicineBatchId",
                table: "SaleLineBatchAllocations",
                column: "MedicineBatchId");

            migrationBuilder.CreateIndex(
                name: "UX_SaleLineBatchAllocations_Line_Batch",
                table: "SaleLineBatchAllocations",
                columns: new[] { "SaleLineId", "MedicineBatchId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SaleLines_MedicineId",
                table: "SaleLines",
                column: "MedicineId");

            migrationBuilder.CreateIndex(
                name: "UX_SaleLines_Sale_LineNumber",
                table: "SaleLines",
                columns: new[] { "SaleId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sales_BusinessDate",
                table: "Sales",
                column: "BusinessDate");

            migrationBuilder.CreateIndex(
                name: "IX_Sales_SoldAtUtc",
                table: "Sales",
                column: "SoldAtUtc");

            migrationBuilder.CreateIndex(
                name: "UX_Sales_InvoiceNumber",
                table: "Sales",
                column: "InvoiceNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvoiceSequences");

            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropTable(
                name: "SaleLineBatchAllocations");

            migrationBuilder.DropTable(
                name: "SaleLines");

            migrationBuilder.DropTable(
                name: "Sales");

            migrationBuilder.DropColumn(
                name: "TaxRatePercent",
                table: "Medicines");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "MedicineBatches");
        }
    }
}
