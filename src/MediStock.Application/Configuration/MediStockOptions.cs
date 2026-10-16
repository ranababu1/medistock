using MediStock.Domain.Identity;

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

    /// <summary>
    /// Folder that exported receipts are written to. A relative path is resolved against the user's
    /// documents folder, so the default does not depend on where the executable was started from.
    /// </summary>
    public string ReceiptFolder { get; set; } = @"MediStock\Receipts";

    /// <summary>Folder that exported report files are written to, resolved the same way as receipts.</summary>
    public string ReportFolder { get; set; } = @"MediStock\Reports";

    /// <summary>
    /// Folder that database backups are written to and listed from, resolved the same way as receipts. Kept
    /// out of the installation folder so that a reinstall cannot delete the pharmacy's only backup.
    /// </summary>
    public string BackupFolder { get; set; } = @"MediStock\Backups";

    /// <summary>
    /// How many PBKDF2 rounds are used when a password is written. Left at the recommended value in
    /// production; lowered in automated tests, where hashing is not what is being tested and 210,000 rounds
    /// per account would dominate the run time.
    /// </summary>
    public int PasswordIterations { get; set; } = PasswordHash.RecommendedIterations;
}
