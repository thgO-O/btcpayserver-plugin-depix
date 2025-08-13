#nullable enable
namespace BTCPayServer.Plugins.Depix.PaymentHandlers;

public class PixPaymentMethodConfig
{
    public string? EncryptedApiKey { get; set; }
    public string? WebhookSecretHashHex { get; set; }
}