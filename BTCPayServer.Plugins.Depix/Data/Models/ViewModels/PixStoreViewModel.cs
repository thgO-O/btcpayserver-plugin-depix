#nullable enable
namespace BTCPayServer.Plugins.Depix.Data.Models.ViewModels;

public class PixStoreViewModel
{
    /// <summary>
    /// Whether Pix is enabled for this store
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// The DePix API Key
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The webhook URL to be configured in DePix
    /// </summary>
    public string? WebhookUrl { get; set; } 

    /// <summary>
    /// Display value for the webhook secret
    /// </summary>
    public string? WebhookSecretDisplay { get; set; }

    /// <summary>
    /// Flag to regenerate the webhook secret
    /// </summary>
    public bool RegenerateWebhookSecret { get; set; }

    /// <summary>
    /// The secret to display immediately after generation (one-time view)
    /// </summary>
    public string? OneShotSecretToDisplay { get; set; }

    /// <summary>
    /// Command to register for Telegram notifications
    /// </summary>
    public string? TelegramRegisterCommand { get; set; }

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

    /// <summary>
    /// Whether the store configuration is valid
    /// </summary>
    public bool IsStoreCfgValid { get; set; }

    /// <summary>
    /// Whether the server configuration is valid
    /// </summary>
    public bool IsServerCfgValid { get; set; }

    /// <summary>
    /// Whether the effective configuration uses the server configuration
    /// </summary>
    public bool EffectiveUsesServerConfig { get; set; }
}
