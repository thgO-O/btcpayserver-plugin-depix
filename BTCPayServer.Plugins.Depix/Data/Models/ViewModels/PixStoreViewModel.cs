namespace BTCPayServer.Plugins.Depix.Data.Models.ViewModels;

public class PixStoreViewModel
{
    public bool Enabled { get; set; }
    public string ApiKey { get; set; }
    
    public string WebhookUrl { get; set; } 
    public string WebhookSecretHex { get; set; } // 64 chars (32 bytes hex)
    public string TelegramRegisterCommand { get; set; }
}
