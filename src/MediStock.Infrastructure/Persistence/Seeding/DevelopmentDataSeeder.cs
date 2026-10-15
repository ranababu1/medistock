using MediStock.Application.Common;
using MediStock.Domain.Catalogue;
using MediStock.Domain.Inventory;
using MediStock.Domain.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediStock.Infrastructure.Persistence.Seeding;

/// <summary>
/// Loads deliberately fictional sample data into an empty development database so the shell has
/// something to display. Guarded by <c>MediStock:SeedDevelopmentData</c> and skipped whenever the
/// catalogue already contains records, so it can never overwrite real pharmacy data.
/// </summary>
public sealed class DevelopmentDataSeeder
{
    private const string Operator = "Development seed";

    private readonly MediStockDbContext _db;
    private readonly IClock _clock;
    private readonly ILogger<DevelopmentDataSeeder> _logger;

    public DevelopmentDataSeeder(MediStockDbContext db, IClock clock, ILogger<DevelopmentDataSeeder> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Returns the number of movements written, or zero when seeding was skipped.</summary>
    public async Task<int> SeedAsync(CancellationToken cancellationToken = default)
    {
        if (await _db.Medicines.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Sample data seeding skipped because the catalogue is not empty.");
            return 0;
        }

        var businessDate = _clock.BusinessDate;
        var nowUtc = _clock.UtcNow;

        var suppliers = CreateSuppliers(nowUtc);
        var medicines = CreateMedicines(nowUtc);

        _db.Suppliers.AddRange(suppliers);
        _db.Medicines.AddRange(medicines);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Batches have to be persisted before their movements are recorded, because a stock movement
        // must reference the identity of the batch it belongs to.
        var batches = new List<(MedicineBatch Batch, SampleBatch Seed)>();
        foreach (var seed in SampleBatches())
        {
            var batch = MedicineBatch.Receive(
                medicines[seed.MedicineIndex].Id,
                seed.BatchNumber,
                businessDate.AddDays(seed.ExpiryInDays),
                seed.ReceivedQuantity,
                seed.PurchaseCost,
                seed.SellingPrice,
                businessDate.AddDays(-seed.ReceivedDaysAgo),
                suppliers[seed.SupplierIndex].Id,
                seed.InvoiceNumber,
                nowUtc);

            _db.MedicineBatches.Add(batch);
            batches.Add((batch, seed));
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var movements = new List<StockMovement>();
        foreach (var (batch, seed) in batches)
        {
            movements.Add(StockMovement.Record(
                batch.MedicineId,
                batch.Id,
                StockMovementType.Receipt,
                seed.ReceivedQuantity,
                batch.QuantityOnHand,
                businessDate,
                Operator,
                reference: seed.InvoiceNumber,
                nowUtc: nowUtc));

            if (seed.SoldQuantity > 0m)
            {
                batch.DecreaseQuantity(seed.SoldQuantity, nowUtc);
                movements.Add(StockMovement.Record(
                    batch.MedicineId,
                    batch.Id,
                    StockMovementType.Sale,
                    -seed.SoldQuantity,
                    batch.QuantityOnHand,
                    businessDate,
                    Operator,
                    reference: "SAMPLE-SALE",
                    nowUtc: nowUtc));
            }
        }

        _db.StockMovements.AddRange(movements);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Seeded {Medicines} sample medicines, {Suppliers} sample suppliers, and {Movements} stock movements.",
            medicines.Count,
            suppliers.Count,
            movements.Count);

        return movements.Count;
    }

    /// <summary>One sample batch, described relative to the current business date.</summary>
    private sealed record SampleBatch(
        int MedicineIndex,
        int SupplierIndex,
        string BatchNumber,
        int ExpiryInDays,
        decimal ReceivedQuantity,
        decimal PurchaseCost,
        decimal SellingPrice,
        int ReceivedDaysAgo,
        string InvoiceNumber,
        decimal SoldQuantity);

    private static List<SampleBatch> SampleBatches() =>
    [
        new(0, 0, "PCM2411", 540, 120m, 1.10m, 2.00m, 45, "INV-2025-0417", 30m),
        new(1, 0, "AMX2409", 300, 80m, 4.20m, 8.50m, 30, "INV-2025-0417", 12m),
        new(2, 1, "CTZ2510", 20, 200m, 0.65m, 1.50m, 20, "ML-88231", 200m),
        new(3, 1, "PAN2503", 365, 60m, 3.10m, 6.00m, 60, "ML-88231", 0m),
        new(4, 2, "MET2501", 420, 150m, 1.45m, 2.80m, 50, "CP-5512", 40m),
        new(5, 2, "AML2512", 700, 90m, 1.05m, 2.20m, 25, "CP-5512", 90m),
        new(6, 3, "SAL2502", -10, 15m, 145.00m, 220.00m, 120, "EDH-3001", 15m),
        new(7, 3, "ORS2408", 240, 300m, 4.00m, 7.50m, 70, "EDH-3001", 300m),
        new(8, 0, "CSY2509", 150, 40m, 42.00m, 68.00m, 15, "INV-2025-0455", 8m),
        new(9, 1, "DIC2511", 30, 25m, 58.00m, 92.00m, 10, "ML-88420", 25m),
    ];

    private static List<Supplier> CreateSuppliers(DateTimeOffset nowUtc) =>
    [
        Supplier.Create(
            "Sunrise Pharma Distributors (sample)",
            "Ravi Menon",
            "+91 98450 11223",
            "orders@sunrise-pharma.example",
            "14 Industrial Layout, 3rd Cross",
            "Bengaluru",
            "Karnataka",
            "560001",
            "29AAAAA0000A1Z5",
            "20B-KA-123456",
            "Fictional supplier used for development seeding only.",
            nowUtc),
        Supplier.Create(
            "MedLink Wholesale (sample)",
            "Priya Nair",
            "+91 98200 44556",
            "sales@medlink.example",
            "Plot 22, MIDC Estate",
            "Pune",
            "Maharashtra",
            "411018",
            "27BBBBB1111B1Z2",
            "20B-MH-654321",
            "Fictional supplier used for development seeding only.",
            nowUtc),
        Supplier.Create(
            "CarePlus Agencies (sample)",
            "Anil Kumar",
            "+91 90000 77881",
            "contact@careplus.example",
            "88 Anna Salai",
            "Chennai",
            "Tamil Nadu",
            "600002",
            "33CCCCC2222C1Z9",
            "20B-TN-998877",
            "Fictional supplier used for development seeding only.",
            nowUtc),
        Supplier.Create(
            "Eastern Drug House (sample)",
            "Sujata Bose",
            "+91 98300 22110",
            "info@eastern-drug.example",
            "5 Park Street",
            "Kolkata",
            "West Bengal",
            "700016",
            "19DDDDD3333D1Z1",
            "20B-WB-112233",
            "Inactive sample supplier retained to prove history survives deactivation.",
            nowUtc),
    ];

    private static List<Medicine> CreateMedicines(DateTimeOffset nowUtc)
    {
        var medicines = new List<Medicine>
        {
            Medicine.Create("Paracetamol", DosageForm.Tablet, brandName: "FeverKare (sample)", genericName: "Paracetamol", strength: "500 mg", manufacturer: "Northline Pharma (sample)", barcode: "8900000000011", packDescription: "Strip of 10 tablets", reorderThreshold: 50m, nowUtc: nowUtc),
            Medicine.Create("Amoxicillin", DosageForm.Capsule, brandName: "AmoxiCare (sample)", genericName: "Amoxicillin", strength: "250 mg", manufacturer: "Northline Pharma (sample)", barcode: "8900000000028", packDescription: "Strip of 10 capsules", reorderThreshold: 30m, nowUtc: nowUtc),
            Medicine.Create("Cetirizine", DosageForm.Tablet, brandName: "AllerFree (sample)", genericName: "Cetirizine Hydrochloride", strength: "10 mg", manufacturer: "Bluehill Labs (sample)", barcode: "8900000000035", packDescription: "Strip of 10 tablets", reorderThreshold: 40m, nowUtc: nowUtc),
            Medicine.Create("Pantoprazole", DosageForm.Tablet, brandName: "GastroEase (sample)", genericName: "Pantoprazole Sodium", strength: "40 mg", manufacturer: "Bluehill Labs (sample)", barcode: "8900000000042", packDescription: "Strip of 10 tablets", reorderThreshold: 25m, nowUtc: nowUtc),
            Medicine.Create("Metformin", DosageForm.Tablet, brandName: "GlucoNorm (sample)", genericName: "Metformin Hydrochloride", strength: "500 mg", manufacturer: "Vertex Generics (sample)", barcode: "8900000000059", packDescription: "Strip of 15 tablets", reorderThreshold: 60m, nowUtc: nowUtc),
            Medicine.Create("Amlodipine", DosageForm.Tablet, brandName: "CardioCalm (sample)", genericName: "Amlodipine Besylate", strength: "5 mg", manufacturer: "Vertex Generics (sample)", barcode: "8900000000066", packDescription: "Strip of 10 tablets", reorderThreshold: 30m, nowUtc: nowUtc),
            Medicine.Create("Salbutamol Inhaler", DosageForm.Inhaler, brandName: "BreatheWell (sample)", genericName: "Salbutamol Sulphate", strength: "100 mcg", manufacturer: "AeroMed Devices (sample)", barcode: "8900000000073", packDescription: "200 dose canister", reorderThreshold: 5m, nowUtc: nowUtc),
            Medicine.Create("Oral Rehydration Salts", DosageForm.Sachet, brandName: "Rehydra (sample)", genericName: "Oral Rehydration Salts", strength: "21.8 g", manufacturer: "Vertex Generics (sample)", barcode: "8900000000080", packDescription: "Box of 10 sachets", reorderThreshold: 40m, nowUtc: nowUtc),
            Medicine.Create("Dextromethorphan Cough Syrup", DosageForm.Syrup, brandName: "CalmCough (sample)", genericName: "Dextromethorphan Hydrobromide", strength: "10 mg/5 ml", manufacturer: "Bluehill Labs (sample)", barcode: "8900000000097", packDescription: "100 ml bottle", reorderThreshold: 10m, nowUtc: nowUtc),
            Medicine.Create("Diclofenac Gel", DosageForm.Gel, brandName: "Relievo (sample)", genericName: "Diclofenac Diethylamine", strength: "1% w/w", manufacturer: "AeroMed Devices (sample)", barcode: "8900000000103", packDescription: "30 g tube", reorderThreshold: 10m, nowUtc: nowUtc),
        };

        // Demonstrates that inactive catalogue entries survive without being deleted.
        medicines[9].Deactivate(nowUtc);

        return medicines;
    }
}
