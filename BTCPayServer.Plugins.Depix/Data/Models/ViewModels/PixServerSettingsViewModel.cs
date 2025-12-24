#nullable enable
namespace BTCPayServer.Plugins.Depix.Data.Models.ViewModels;

public class PixServerSettingsViewModel
{
    public string? ApiKey { get; set; }

    public string? WebhookUrl { get; set; }
    public string? WebhookSecretDisplay { get; set; }

    public string? OneShotSecretToDisplay { get; set; }
    public bool RegenerateWebhookSecret { get; set; }

    public string? TelegramRegisterCommand { get; set; }

    public bool ApiKeyConfigured { get; set; }
    public bool IsServerCfgValid { get; set; }

    public bool UseWhitelist { get; set; }
    public bool PassFeeToCustomer { get; set; }
}