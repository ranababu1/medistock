namespace MediStock.Domain.Catalogue;

/// <summary>Dosage form of a medicine. Stored as an integer so the list stays stable.</summary>
public enum DosageForm
{
    Unknown = 0,
    Tablet = 1,
    Capsule = 2,
    Syrup = 3,
    Suspension = 4,
    Injection = 5,
    Ointment = 6,
    Cream = 7,
    Drops = 8,
    Inhaler = 9,
    Powder = 10,
    Sachet = 11,
    Suppository = 12,
    Patch = 13,
    Gel = 14,
    Solution = 15,
    Spray = 16,
    Other = 99,
}
