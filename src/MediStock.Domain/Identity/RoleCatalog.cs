namespace MediStock.Domain.Identity;

/// <summary>
/// Which permissions each role carries. The mapping is code rather than data on purpose: it is a
/// business decision that changes with a release, and keeping it here means it is covered by tests
/// instead of by whoever last edited a lookup table.
/// </summary>
public static class RoleCatalog
{
    private static readonly IReadOnlyDictionary<Role, PermissionSet> Map = Build();

    /// <summary>Every role, in the order it is offered on screen.</summary>
    public static IReadOnlyList<Role> All { get; } =
    [
        Role.Administrator,
        Role.Pharmacist,
        Role.Cashier,
        Role.InventoryOperator,
    ];

    /// <summary>The permissions a role carries.</summary>
    public static PermissionSet PermissionsFor(Role role) => Map[role];

    /// <summary>
    /// Parses a role that was stored as text, for example in a backup or an import. Returns false rather
    /// than throwing so a caller can report the bad value it found.
    /// </summary>
    public static bool TryParse(string? value, out Role role)
    {
        role = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.ToString(), trimmed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Label(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                role = candidate;
                return true;
            }
        }

        // A role stored in an older release may since have been renamed. Fall back to the declared
        // name so a value the application itself wrote still round-trips.
        return Enum.TryParse(trimmed, ignoreCase: true, out role) && Enum.IsDefined(role);
    }

    private static IReadOnlyDictionary<Role, PermissionSet> Build()
    {
        var pharmacist = new[]
        {
            Permission.Sell,
            Permission.ProcessReturns,
            Permission.ManageMedicines,
            Permission.ChangePrices,
            Permission.ReceivePurchases,
            Permission.ManageSuppliers,
            Permission.RunReports,
        };

        return new Dictionary<Role, PermissionSet>
        {
            [Role.Administrator] = PermissionSet.All,
            [Role.Pharmacist] = PermissionSet.Of(pharmacist),

            // A cashier rings up sales and nothing else. Deliberately no RunReports: daily takings are
            // a management figure, and the plan keeps reporting away from the till.
            [Role.Cashier] = PermissionSet.Of([Permission.Sell]),

            // The store room may keep the catalogue and the suppliers, but may not touch a price the
            // pharmacist set, and may not sell.
            [Role.InventoryOperator] = PermissionSet.Of(
            [
                Permission.ManageMedicines,
                Permission.ManageSuppliers,
                Permission.ReceivePurchases,
                Permission.RunReports,
            ]),
        };
    }
}
