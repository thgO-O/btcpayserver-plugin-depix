using BTCPayServer.Services;

namespace BTCPayServer.Plugins.Depix.PaymentHandlers;

public class PixTransactionLinkProvider(string blockExplorerLink) : DefaultTransactionLinkProvider(blockExplorerLink)
{
    public override string? GetTransactionLink(string paymentId)
    {
        return null;
    }
}