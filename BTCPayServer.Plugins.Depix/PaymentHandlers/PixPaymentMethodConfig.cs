#nullable enable
namespace BTCPayServer.Plugins.Depix.PaymentHandlers;

public class PixPaymentMethodConfig
{
    public string? EncryptedApiKey { get; set; }
    public string? WebhookSecretHashHex { get; set; }
    public bool IsEnabled { get; set; }
    public bool UseWhitelist { get; set; }
    public bool PassFeeToCustomer { get; set; }
    public string? DepixSplitAddress { get; set; }
    public string? SplitFee { get; set; }
}