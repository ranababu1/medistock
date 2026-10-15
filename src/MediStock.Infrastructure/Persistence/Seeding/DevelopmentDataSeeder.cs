using MediStock.Application.Common;
using MediStock.Domain.Catalogue;
using MediStock.Domain.Inventory;
using MediStock.Domain.Purchasing;
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

    private readonly IDbContextFactory<MediStockDbContext> _factory;
    private readonly IClock _clock;
    private readonly ILogger<DevelopmentDataSeeder> _logger;

    public DevelopmentDataSeeder(
        IDbContextFactory<MediStockDbContext> factory,
        IClock clock,
        ILogger<DevelopmentDataSeeder> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Returns the number of stock movements written, or zero when seeding was skipped.</summary>
    public async Task<int> SeedAsync(CancellationToken cancellationToken = default)
    {
        // Seeding is a single unit of work that relies on change tracking and on SaveChanges assigning
        // identities, so one context is used for the whole method.
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        if (await db.Medicines.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Sample data seeding skipped because the catalogue is not empty.");
            return 0;
        }

        var businessDate = _clock.BusinessDate;
        var nowUtc = _clock.UtcNow;

        var suppliers = CreateSuppliers(nowUtc);
        var medicines = CreateMedicines(nowUtc);

        db.Suppliers.AddRange(suppliers);
        db.Medicines.AddRange(medicines);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // One delivery note becomes one purchase, so the sample batches are grouped by supplier and
        // invoice number. Sample stock is created through the real receiving workflow rather than by
        // inserting batches directly, which keeps the data honest: every batch has a purchase line
        // behind it and the movement ledger reconciles by construction.
        var deliveries = new Dictionary<Purchase, List<SampleBatch>>();

        foreach (var group in SampleBatches().GroupBy(seed => (seed.SupplierIndex, seed.InvoiceNumber)))
        {
            var seeds = group.ToList();
            var purchase = Purchase.Create(
                suppliers[group.Key.SupplierIndex].Id,
                group.Key.InvoiceNumber,
                businessDate.AddDays(-seeds[0].ReceivedDaysAgo),
                "Sample delivery used for development seeding only.",
                nowUtc);

            foreach (var seed in seeds)
            {
                purchase.AddLine(
                    medicines[seed.MedicineIndex].Id,
                    seed.BatchNumber,
                    businessDate.AddDays(seed.ExpiryInDays),
                    seed.ReceivedQuantity,
                    seed.PurchaseCost,
                    seed.SellingPrice,
                    nowUtc);
            }

            deliveries.Add(purchase, seeds);
            db.Purchases.Add(purchase);
        }

        // A purchase that has not been received yet, so the purchasing screen has one of each status.
        var draft = Purchase.Create(
            suppliers[2].Id,
            "CP-5610",
            businessDate.AddDays(-1),
            "Sample draft awaiting delivery.",
            nowUtc);
        draft.AddLine(medicines[3].Id, "PAN2512A", businessDate.AddDays(400), 40m, 3.20m, 6.00m, nowUtc);
        draft.AddLine(medicines[4].Id, "MET2511A", businessDate.AddDays(390), 80m, 1.50m, 2.90m, nowUtc);
        db.Purchases.Add(draft);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var movements = new List<StockMovement>();
        var batches = new List<(MedicineBatch Batch, SampleBatch Seed)>();

        foreach (var (purchase, seeds) in deliveries)
        {
            var received = await ReceivePurchaseAsync(
                    db,
                    purchase,
                    purchase.InvoiceDate,
                    movements,
                    cancellationToken)
                .ConfigureAwait(false);

            for (var index = 0; index < received.Count; index++)
            {
                batches.Add((received[index], seeds[index]));
            }
        }

        // Sample sales, recorded as movement history only. Point of sale arrives in a later milestone.
        foreach (var (batch, seed) in batches)
        {
            if (seed.SoldQuantity <= 0m)
            {
                continue;
            }

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

        // One received purchase is then reversed, so the correction path and its ledger entries are
        // visible in the sample data.
        var reversedPurchase = Purchase.Create(
            suppliers[3].Id,
            "EDH-3100",
            businessDate.AddDays(-8),
            "Sample receipt corrected by reversal.",
            nowUtc);
        reversedPurchase.AddLine(
            medicines[8].Id,
            "CSY-R001",
            businessDate.AddDays(200),
            12m,
            41.00m,
            66.00m,
            nowUtc);
        db.Purchases.Add(reversedPurchase);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        const string reversalReason = "Invoice keyed against the wrong supplier (sample data).";
        var reversalBatches = await ReceivePurchaseAsync(
                db,
                reversedPurchase,
                reversedPurchase.InvoiceDate,
                movements,
                cancellationToken)
            .ConfigureAwait(false);

        reversedPurchase.Reverse(reversalReason, Operator, nowUtc);

        foreach (var batch in reversalBatches)
        {
            var quantity = batch.QuantityOnHand;
            batch.DecreaseQuantity(quantity, nowUtc);
            movements.Add(StockMovement.Record(
                batch.MedicineId,
                batch.Id,
                StockMovementType.ReceiptReversal,
                -quantity,
                batch.QuantityOnHand,
                businessDate,
                Operator,
                reason: reversalReason,
                reference: $"REV-{reversedPurchase.InvoiceNumber}",
                nowUtc: nowUtc));
        }

        db.StockMovements.AddRange(movements);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Seeded {Medicines} sample medicines, {Suppliers} sample suppliers, {Purchases} sample purchases, " +
            "{Batches} batches, and {Movements} stock movements.",
            medicines.Count,
            suppliers.Count,
            deliveries.Count + 2,
            batches.Count + reversalBatches.Count,
            movements.Count);

        return movements.Count;
    }

    /// <summary>
    /// Confirms a draft purchase and puts its lines into stock. Batches are saved before their receipt
    /// movements because a movement has to reference the identity of the batch it belongs to.
    /// </summary>
    private async Task<IReadOnlyList<MedicineBatch>> ReceivePurchaseAsync(
        MediStockDbContext db,
        Purchase purchase,
        DateOnly receivedOn,
        List<StockMovement> movements,
        CancellationToken cancellationToken)
    {
        var receipts = purchase.Receive(receivedOn, Operator, _clock.UtcNow);

        var batches = receipts
            .Select(receipt => MedicineBatch.Receive(
                receipt.MedicineId,
                receipt.BatchNumber,
                receipt.ExpiryDate,
                receipt.Quantity,
                receipt.UnitCost,
                receipt.SellingPrice,
                receivedOn,
                purchase.SupplierId,
                purchase.InvoiceNumber,
                _clock.UtcNow))
            .ToList();

        db.MedicineBatches.AddRange(batches);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < receipts.Count; index++)
        {
            purchase.AttachBatch(receipts[index].LineId, batches[index]);
            movements.Add(StockMovement.Record(
                receipts[index].MedicineId,
                batches[index].Id,
                StockMovementType.Receipt,
                receipts[index].Quantity,
                batches[index].QuantityOnHand,
                receivedOn,
                Operator,
                reference: purchase.InvoiceNumber,
                nowUtc: _clock.UtcNow));
        }

        return batches;
    }

    /// <summary>One sample batch, described relative to the current business date.</summary>
    /// <remarks>
    /// Every batch sharing a supplier and invoice number must also share
    /// <see cref="ReceivedDaysAgo"/>: one delivery note is received once, and that delivery becomes one
    /// received purchase.
    /// </remarks>
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
        new(1, 0, "AMX2409", 300, 80m, 4.20m, 8.50m, 45, "INV-2025-0417", 12m),
        new(2, 1, "CTZ2510", 20, 200m, 0.65m, 1.50m, 60, "ML-88231", 200m),
        new(3, 1, "PAN2503", 365, 60m, 3.10m, 6.00m, 60, "ML-88231", 0m),
        new(4, 2, "MET2501", 420, 150m, 1.45m, 2.80m, 50, "CP-5512", 40m),
        new(5, 2, "AML2512", 700, 90m, 1.05m, 2.20m, 50, "CP-5512", 90m),
        new(6, 3, "SAL2502", -10, 15m, 145.00m, 220.00m, 120, "EDH-3001", 15m),
        new(7, 3, "ORS2408", 240, 300m, 4.00m, 7.50m, 120, "EDH-3001", 300m),
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
