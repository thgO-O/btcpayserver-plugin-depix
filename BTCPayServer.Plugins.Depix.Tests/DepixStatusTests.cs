using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Depix.Data.Enums;
using BTCPayServer.Services.Invoices;
using Xunit;

namespace BTCPayServer.Plugins.Depix.Tests;

public class DepixStatusTests
{
    [Theory]
    [InlineData("approved", DepixStatus.Approved)]
    [InlineData("APPROVED", DepixStatus.Approved)]
    [InlineData("DEPIX-SENT", DepixStatus.DepixSent)]
    public void TryParseRecognizesKnownDepositStatuses(string value, DepixStatus expected)
    {
        Assert.True(DepixStatusExtensions.TryParse(value, out var status));
        Assert.Equal(expected, status);
        Assert.DoesNotContain('-', status.ToApiString());
    }

    [Fact]
    public void ApprovedSettlesInvoice()
    {
        var state = DepixStatus.Approved.ToInvoiceState(new InvoiceState(InvoiceStatus.New, InvoiceExceptionStatus.None));

        Assert.NotNull(state);
        Assert.Equal(InvoiceStatus.Settled, state!.Status);
        Assert.Equal(InvoiceExceptionStatus.None, state.ExceptionStatus);
    }

    [Theory]
    [InlineData(DepixStatus.Approved, true)]
    [InlineData(DepixStatus.DepixSent, true)]
    [InlineData(DepixStatus.UnderReview, false)]
    public void OnlyApprovedAndDepixSentAreConfirmedPaymentStatuses(DepixStatus status, bool expected)
    {
        Assert.Equal(expected, status.IsConfirmedPaymentStatus());
    }

    [Theory]
    [InlineData(DepixStatus.Approved, "depix_sent", false)]
    [InlineData(DepixStatus.DepixSent, "approved", true)]
    [InlineData(DepixStatus.Approved, "refunded", false)]
    public void StatusPrecedencePreventsDowngrades(DepixStatus incoming, string current, bool expected)
    {
        Assert.Equal(expected, incoming.ShouldReplace(current));
    }
}
