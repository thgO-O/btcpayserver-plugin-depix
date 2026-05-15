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
    /// Whether Pix deposits should sell DePix directly to a buyer-supplied DePix address
    /// </summary>
    public bool P2PMode { get; set; }

    /// <summary>
    /// Seller commission percentage for P2P sales
    /// </summary>
    public string? P2PCommissionPercent { get; set; }

    /// <summary>
    /// Optional address to split normal Pix payments to
    /// </summary>
    public string? DepixSplitAddress { get; set; }

    /// <summary>
    /// Fee configuration for normal Pix split payments
    /// </summary>
    public string? SplitFee { get; set; }
}
