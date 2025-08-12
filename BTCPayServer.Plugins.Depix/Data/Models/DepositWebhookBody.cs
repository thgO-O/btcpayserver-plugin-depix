#nullable enable
using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.Depix.Data.Models;

public sealed class DepositWebhookBody
{
    [JsonPropertyName("bankTxId")] public string? BankTxId { get; set; }
    [JsonPropertyName("blockchainTxID")] public string? BlockchainTxId { get; set; }
    [JsonPropertyName("customerMessage")] public string? CustomerMessage { get; set; }
    [JsonPropertyName("payerName")] public string? PayerName { get; set; }
    [JsonPropertyName("payerEUID")] public string? PayerEuid { get; set; }
    [JsonPropertyName("payerTaxNumber")] public string? PayerTaxNumber { get; set; }
    [JsonPropertyName("expiration")] public string? Expiration { get; set; } // ISO string
    [JsonPropertyName("pixKey")] public string? PixKey { get; set; }
    [JsonPropertyName("qrId")] public string QrId { get; set; } = null!;
    [JsonPropertyName("status")] public string? Status { get; set; } // ex: depix_sent
    [JsonPropertyName("valueInCents")] public int? ValueInCents { get; set; }
}