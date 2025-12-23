#nullable enable
namespace BTCPayServer.Plugins.Depix.Data.Models;

public class PixServerConfig
{
    public string? EncryptedApiKey { get; set; }
    public string? WebhookSecretHashHex { get; set; }
    public bool UseWhitelist { get; set; }
    public bool PassFeeToCustomer { get; set; }
}