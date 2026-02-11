#nullable enable
namespace BTCPayServer.Plugins.Depix.PaymentHandlers;

public class PixPaymentMethodConfig
{
    /// <summary>
    /// Encrypted API Key for DePix service
    /// </summary>
    public string? EncryptedApiKey { get; set; }

    /// <summary>
    /// Hex-encoded hash of the webhook secret
    /// </summary>
    public string? WebhookSecretHashHex { get; set; }

    /// <summary>
    /// Whether the Pix payment method is enabled
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Whether to use whitelist mode
    /// </summary>
    public bool UseWhitelist { get; set; }

    /// <summary>
    /// Whether to pass the DePix fee to the customer
    /// </summary>
    public bool PassFeeToCustomer { get; set; }

    /// <summary>
    /// Optional address to split the payment to
    /// </summary>
    public string? DepixSplitAddress { get; set; }

    /// <summary>
    /// Fee configuration for split payments
    /// </summary>
    public string? SplitFee { get; set; }
}