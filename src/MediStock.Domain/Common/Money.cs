namespace MediStock.Domain.Common;

/// <summary>
/// Rounding policy for monetary amounts. MediStock stores money as <see cref="decimal"/> with two
/// decimal places on SQL Server and rounds half away from zero (the retail convention), never
/// banker's rounding, so till totals and printed invoices agree with manual arithmetic.
/// </summary>
public static class Money
{
    public const int DecimalPlaces = 2;

    public const MidpointRounding Rounding = MidpointRounding.AwayFromZero;

    public static decimal Round(decimal amount) => Math.Round(amount, DecimalPlaces, Rounding);
}
