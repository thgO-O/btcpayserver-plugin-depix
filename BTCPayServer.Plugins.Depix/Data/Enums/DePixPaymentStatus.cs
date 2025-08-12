#nullable enable
using BTCPayServer.Client.Models;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.Depix.Data.Enums;
public enum DepixStatus
{
    Pending,
    UnderReview,
    DepixSent,
    Error,
    Refunded,
    Expired,
    Canceled
}

public static class DepixStatusExtensions
{
    public static bool TryParse(string? value, out DepixStatus result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var s = value.Trim().ToLowerInvariant().Replace("-", "_");
        switch (s)
        {
            case "pending":       result = DepixStatus.Pending;     return true;
            case "under_review":  result = DepixStatus.UnderReview; return true;
            case "depix_sent":    result = DepixStatus.DepixSent;   return true;
            case "error":         result = DepixStatus.Error;       return true;
            case "refunded":      result = DepixStatus.Refunded;    return true;
            case "expired":       result = DepixStatus.Expired;     return true;
            case "canceled":      result = DepixStatus.Canceled;    return true;
            default: return false;
        }
    }

    public static InvoiceState? ToInvoiceState(this DepixStatus s, InvoiceState current)
    {
        return s switch
        {
            DepixStatus.Pending => null,
            DepixStatus.UnderReview => current.Status == InvoiceStatus.Settled
                ? null
                : new InvoiceState(InvoiceStatus.Processing, InvoiceExceptionStatus.None),
            DepixStatus.DepixSent => new InvoiceState(InvoiceStatus.Settled, InvoiceExceptionStatus.None),
            DepixStatus.Expired => current.Status == InvoiceStatus.Settled
                ? null
                : new InvoiceState(InvoiceStatus.Expired, InvoiceExceptionStatus.None),
            DepixStatus.Canceled or DepixStatus.Error or DepixStatus.Refunded => current.Status == InvoiceStatus.Settled
                ? null
                : new InvoiceState(InvoiceStatus.Invalid, InvoiceExceptionStatus.Marked),
            _ => null
        };
    }
}
