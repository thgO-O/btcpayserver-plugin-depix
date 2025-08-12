namespace BTCPayServer.Plugins.Depix.PaymentHandlers;

public class PixPaymentMethodConfig
{
    public string EncryptedApiKey { get; set; }
    public string WebhookSecretHex { get; set; }
}