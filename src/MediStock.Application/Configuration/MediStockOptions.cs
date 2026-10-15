namespace MediStock.Application.Configuration;

/// <summary>
/// Settings bound from the <c>MediStock</c> configuration section. Kept free of anything secret;
/// the database connection string lives under <c>ConnectionStrings</c> instead.
/// </summary>
public sealed class MediStockOptions
{
    public const string SectionName = "MediStock";

    /// <summary>Shown in the shell and on printed documents.</summary>
    public string PharmacyName { get; set; } = "MediStock Pharmacy";

    /// <summary>Windows time zone used to derive the local business date.</summary>
    public string TimeZoneId { get; set; } = "India Standard Time";

    /// <summary>Batches expiring within this many days appear in the near-expiry list.</summary>
    public int NearExpiryDays { get; set; } = 90;

    /// <summary>When true the shell applies pending EF Core migrations at startup.</summary>
    public bool ApplyMigrationsOnStartup { get; set; } = true;

    /// <summary>When true development sample data is inserted if the database is empty.</summary>
    public bool SeedDevelopmentData { get; set; }
}
