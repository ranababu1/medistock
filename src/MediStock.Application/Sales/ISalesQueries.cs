using MediStock.Domain.Sales;

namespace MediStock.Application.Sales;

/// <summary>Takings for one business date, as shown on the dashboard and the sales history.</summary>
public sealed record DailySalesSummary(
    DateOnly BusinessDate,
    int SaleCount,
    decimal SubTotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal Total,
    decimal CostTotal,
    decimal UnitsSold)
{
    public decimal Margin => Total - TaxTotal - CostTotal;

    public static DailySalesSummary Empty(DateOnly businessDate)
        => new(businessDate, 0, 0m, 0m, 0m, 0m, 0m, 0m);
}

/// <summary>Takings grouped by how they were paid for, over a date range.</summary>
public sealed record PaymentMethodTotal(PaymentMethod Method, int SaleCount, decimal Amount);

/// <summary>One medicine's contribution to revenue over a date range.</summary>
public sealed record MedicineSalesTotal(
    int MedicineId,
    string MedicineName,
    decimal UnitsSold,
    decimal GrossAmount,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal Total,
    decimal CostTotal)
{
    public decimal Margin => Total - TaxAmount - CostTotal;
}

/// <summary>Read-only sales queries behind the dashboard and the reports.</summary>
public interface ISalesQueries
{
    /// <summary>Takings for one business date. Returns a zeroed summary when nothing was sold.</summary>
    Task<DailySalesSummary> GetDailySummaryAsync(
        DateOnly businessDate,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DailySalesSummary>> GetDailySummariesAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PaymentMethodTotal>> GetPaymentMethodTotalsAsync(
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MedicineSalesTotal>> GetTopMedicinesAsync(
        DateOnly? from = null,
        DateOnly? to = null,
        int take = 25,
        CancellationToken cancellationToken = default);
}
