using MediStock.Domain.Common;
using MediStock.Domain.Suppliers;

namespace MediStock.Domain.Tests.Suppliers;

public class SupplierTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_requires_a_name(string? name)
        => Assert.Throws<DomainValidationException>(() => Supplier.Create(name!, nowUtc: Now));

    [Fact]
    public void Create_starts_active_and_records_contact_details()
    {
        var supplier = Supplier.Create(
            "Sunrise Pharma Distributors",
            contactPerson: "Ravi Menon",
            phone: "+91 98450 11223",
            email: "orders@sunrise-pharma.example",
            city: "Bengaluru",
            nowUtc: Now);

        Assert.True(supplier.IsActive);
        Assert.Equal("Ravi Menon", supplier.ContactPerson);
        Assert.Equal("Bengaluru", supplier.City);
        Assert.Equal(Now, supplier.CreatedAtUtc);
    }

    [Fact]
    public void Create_leaves_blank_optional_details_null()
    {
        var supplier = Supplier.Create("Sunrise Pharma Distributors", phone: "  ", nowUtc: Now);

        Assert.Null(supplier.Phone);
        Assert.Null(supplier.Gstin);
    }

    [Fact]
    public void Deactivate_keeps_the_record_for_historical_documents()
    {
        var supplier = Supplier.Create("Eastern Drug House", nowUtc: Now);

        supplier.Deactivate(Now.AddDays(1));

        Assert.False(supplier.IsActive);
        Assert.Equal("Eastern Drug House", supplier.Name);
        Assert.Equal(Now.AddDays(1), supplier.UpdatedAtUtc);
    }

    [Fact]
    public void Deactivate_is_idempotent_and_does_not_move_the_timestamp()
    {
        var supplier = Supplier.Create("Eastern Drug House", nowUtc: Now);
        supplier.Deactivate(Now.AddDays(1));

        supplier.Deactivate(Now.AddDays(2));

        Assert.Equal(Now.AddDays(1), supplier.UpdatedAtUtc);
    }

    [Fact]
    public void Reactivate_restores_the_supplier()
    {
        var supplier = Supplier.Create("Eastern Drug House", nowUtc: Now);
        supplier.Deactivate(Now);

        supplier.Reactivate(Now.AddDays(1));

        Assert.True(supplier.IsActive);
    }

    [Fact]
    public void Update_validates_before_mutating_the_name()
    {
        var supplier = Supplier.Create("Eastern Drug House", nowUtc: Now);

        Assert.Throws<DomainValidationException>(() => supplier.Update(
            "  ", null, null, null, null, null, null, null, null, null, null, Now));

        Assert.Equal("Eastern Drug House", supplier.Name);
    }
}
