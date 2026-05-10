#nullable enable
using BTCPayServer.Client.Models;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.Depix.Data.Enums;
public enum DepixStatus
{
    Pending,
    UnderReview,
    Approved,
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
            case "pending":        result = DepixStatus.Pending;       return true;
            case "under_review":   result = DepixStatus.UnderReview;   return true;
            case "approved":       result = DepixStatus.Approved;      return true;
            case "depix_sent":     result = DepixStatus.DepixSent;     return true;
            case "error":          result = DepixStatus.Error;         return true;
            case "refunded":       result = DepixStatus.Refunded;      return true;
            case "expired":        result = DepixStatus.Expired;       return true;
            case "canceled":       result = DepixStatus.Canceled;      return true;
            default: return false;
        }
    }

    public static bool IsConfirmedPaymentStatus(this DepixStatus s)
    {
        return s is DepixStatus.Approved or DepixStatus.DepixSent;
    }

    public static bool IsTerminalStatus(this DepixStatus s)
    {
        return s is DepixStatus.Canceled or DepixStatus.Error or DepixStatus.Expired or DepixStatus.Refunded;
    }

    public static string ToApiString(this DepixStatus s)
    {
        return s switch
        {
            DepixStatus.Pending => "pending",
            DepixStatus.UnderReview => "under_review",
            DepixStatus.Approved => "approved",
            DepixStatus.DepixSent => "depix_sent",
            DepixStatus.Error => "error",
            DepixStatus.Refunded => "refunded",
            DepixStatus.Expired => "expired",
            DepixStatus.Canceled => "canceled",
            _ => s.ToString()
        };
    }

    public static bool ShouldReplace(this DepixStatus incoming, string? currentStatus)
    {
        if (!TryParse(currentStatus, out var current))
            return true;
        if (incoming == current)
            return true;
        if (current.IsTerminalStatus())
            return false;
        if (incoming.IsTerminalStatus())
            return true;

        return incoming.ProgressionRank() >= current.ProgressionRank();
    }

    private static int ProgressionRank(this DepixStatus s)
    {
        return s switch
        {
            DepixStatus.DepixSent => 3,
            DepixStatus.Approved => 2,
            DepixStatus.Pending => 0,
            _ => 1
        };
    }

    public static InvoiceState? ToInvoiceState(this DepixStatus s, InvoiceState current)
    {
        return s switch
        {
            DepixStatus.Pending => null,
            DepixStatus.UnderReview => current.Status == InvoiceStatus.Settled
                ? null
                : new InvoiceState(InvoiceStatus.Processing, InvoiceExceptionStatus.None),
            DepixStatus.Approved or DepixStatus.DepixSent => new InvoiceState(InvoiceStatus.Settled, InvoiceExceptionStatus.None),
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
