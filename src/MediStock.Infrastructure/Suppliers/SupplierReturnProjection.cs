using MediStock.Application.Suppliers;
using MediStock.Domain.Suppliers;

namespace MediStock.Infrastructure.Suppliers;

/// <summary>
/// Maps a recorded supplier return onto the shapes the screens show.
/// </summary>
/// <remarks>
/// The supplier's name is not on the return: the domain stores the identifier only and takes the name as a
/// parameter for the messages it composes, so the catalog looks it up and hands it in here.
/// </remarks>
internal static class SupplierReturnProjection
{
    internal static SupplierReturnDto Detail(SupplierReturn supplierReturn, string supplierName)
    {
        return new SupplierReturnDto
        {
            Id = supplierReturn.Id,
            ReturnNumber = supplierReturn.ReturnNumber,
            BusinessDate = supplierReturn.BusinessDate,
            ReturnedAtUtc = supplierReturn.ReturnedAtUtc,
            ReturnedBy = supplierReturn.ReturnedBy,
            SupplierId = supplierReturn.SupplierId,
            SupplierName = supplierName,
            Reason = supplierReturn.Reason,
            Notes = supplierReturn.Notes,
            SupplierCreditNoteNumber = supplierReturn.SupplierCreditNoteNumber,
            TotalCostValue = supplierReturn.TotalCostValue,
            CreatedAtUtc = supplierReturn.CreatedAtUtc,
            Lines =
            [
                .. supplierReturn.Lines
                    .OrderBy(line => line.LineNumber)
                    .Select(line => new SupplierReturnLineDto
                    {
                        LineNumber = line.LineNumber,
                        MedicineBatchId = line.MedicineBatchId,
                        BatchNumber = line.MedicineBatch?.BatchNumber ?? string.Empty,
                        MedicineName = line.MedicineBatch?.Medicine?.DisplayName ?? string.Empty,
                        Quantity = line.Quantity,
                        UnitCost = line.UnitCost,
                        CostValue = line.CostValue,
                        Notes = line.Notes,
                    }),
            ],
        };
    }
}