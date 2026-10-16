using MediStock.Application.Common;
using MediStock.Application.Sales;
using MediStock.Domain.Identity;

namespace MediStock.Application.Identity.Authorization;

/// <summary>
/// Guards the point of sale with <see cref="Permission.Sell"/>.
/// </summary>
/// <remarks>
/// Only the checkout is gated. Quoting a basket, looking an old invoice up and previewing the next invoice
/// number all stay open, because a cashier who cannot ring a sale up still has to be able to answer "what
/// did I charge you on Tuesday?".
/// </remarks>
internal sealed class GuardedSaleCatalog : ISaleCatalog
{
    private readonly ISaleCatalog _inner;
    private readonly IUserSession _session;

    public GuardedSaleCatalog(ISaleCatalog inner, IUserSession session)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public Task<SaleQuote> QuoteAsync(SaleDraft draft, CancellationToken cancellationToken = default)
        => _inner.QuoteAsync(draft, cancellationToken);

    public Task<SaleDto?> FindAsync(int saleId, CancellationToken cancellationToken = default)
        => _inner.FindAsync(saleId, cancellationToken);

    public Task<SaleDto?> FindByInvoiceNumberAsync(string invoiceNumber, CancellationToken cancellationToken = default)
        => _inner.FindByInvoiceNumberAsync(invoiceNumber, cancellationToken);

    public Task<IReadOnlyList<SaleDto>> SearchAsync(SaleQuery query, CancellationToken cancellationToken = default)
        => _inner.SearchAsync(query, cancellationToken);

    public Task<string> PreviewInvoiceNumberAsync(CancellationToken cancellationToken = default)
        => _inner.PreviewInvoiceNumberAsync(cancellationToken);

    public Task<OperationResult<SaleDto>> CheckoutAsync(SaleDraft draft, CancellationToken cancellationToken = default)
    {
        _session.Require(Permission.Sell, "take a payment and issue this invoice");
        return _inner.CheckoutAsync(draft, cancellationToken);
    }
}
