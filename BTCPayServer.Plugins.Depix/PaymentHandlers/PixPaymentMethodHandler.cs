#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Altcoins;
using BTCPayServer.Plugins.Depix.Services;
using Newtonsoft.Json.Linq;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;

namespace BTCPayServer.Plugins.Depix.PaymentHandlers;

/// <summary>
/// Handler for Pix payment method
/// </summary>
public class PixPaymentMethodHandler(
    BTCPayNetworkProvider networkProvider,
    DepixService depixService,
    ISecretProtector secretProtector)
    : IPaymentMethodHandler, IHasNetwork
{
    /// <summary>
    /// The payment method ID for Pix
    /// </summary>
    public PaymentMethodId PaymentMethodId => DePixPlugin.PixPmid;

    /// <summary>
    /// The network associated with this handler
    /// </summary>
    public BTCPayNetwork Network { get; } = networkProvider.GetNetwork<ElementsBTCPayNetwork>("DePix");

    /// <summary>
    /// Called before fetching rates to configure the prompt
    /// </summary>
    /// <param name="context">The payment method context</param>
    public async Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = "BRL";
        context.Prompt.Divisibility = 2;

        var cfgToken = context.Store.GetPaymentMethodConfigs().TryGetValue(PaymentMethodId, out var token) ? token : null;
        var pixCfg = cfgToken is null ? null : ParsePaymentMethodConfig(cfgToken) as PixPaymentMethodConfig;
        var effectiveConfig = await depixService.GetEffectiveConfigAsync(pixCfg);
        
        context.Prompt.PaymentMethodFee = effectiveConfig.Source != DepixService.DepixConfigSource.None &&
                                          effectiveConfig.PassFeeToCustomer
            ? 1.00m
            : 0.00m;
    }

    /// <summary>
    /// Configures the payment prompt with Pix details
    /// </summary>
    /// <param name="context">The payment method context</param>
    /// <exception cref="PaymentMethodUnavailableException">Thrown if configuration is missing or invalid</exception>
    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        var store = context.Store;
        if (ParsePaymentMethodConfig(store.GetPaymentMethodConfigs()[PaymentMethodId]) is not PixPaymentMethodConfig pixCfg)
            throw new PaymentMethodUnavailableException("DePix payment method not configured");

        var effectiveConfig = await depixService.GetEffectiveConfigAsync(pixCfg);
        if (effectiveConfig.Source == DepixService.DepixConfigSource.None)
            throw new PaymentMethodUnavailableException("DePix API key/webhook secret not configured (store or server)");
        
        var apiKey = secretProtector.Unprotect(effectiveConfig.EncryptedApiKey);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new PaymentMethodUnavailableException("DePix API key not configured");

        var amountInBrl = context.Prompt.Calculate().Due;
        if (effectiveConfig.PassFeeToCustomer) amountInBrl += 1.00m;
        var amountInCents = (int)Math.Round(amountInBrl * 100m, MidpointRounding.AwayFromZero);

        using var client = depixService.CreateDepixClient(apiKey);

        var address = await depixService.GenerateFreshDePixAddress(store.Id);
        var deposit = await depixService.RequestDepositAsync(
            client, 
            amountInCents, 
            address, 
            pixCfg,
            effectiveConfig.UseWhitelist,
            CancellationToken.None);

        depixService.ApplyPromptDetails(context, deposit, address);
    }
    
    /// <summary>
    /// JSON serializer for the handler
    /// </summary>
    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;
    
    /// <summary>
    /// Parses payment prompt details from JSON
    /// </summary>
    /// <param name="details">The JSON details</param>
    /// <returns>The parsed DePixPaymentMethodDetails</returns>
    public object ParsePaymentPromptDetails(JToken details)
    {
        return details.ToObject<DePixPaymentMethodDetails>(Serializer);
    }

    /// <summary>
    /// Parses payment method configuration from JSON
    /// </summary>
    /// <param name="config">The JSON configuration</param>
    /// <returns>The parsed PixPaymentMethodConfig</returns>
    public object ParsePaymentMethodConfig(JToken config)
    {
        return config.ToObject<PixPaymentMethodConfig>(Serializer) ??
               throw new FormatException($"Invalid {nameof(PixPaymentMethodHandler)}");
    }

    /// <summary>
    /// Parses payment details from JSON
    /// </summary>
    /// <param name="details">The JSON details</param>
    /// <returns>The parsed DePixPaymentData</returns>
    public object ParsePaymentDetails(JToken details)
    {
        return details.ToObject<DePixPaymentData>(Serializer) ??
               throw new FormatException($"Invalid {nameof(PixPaymentMethodHandler)}");
    }
}

/// <summary>
/// Data model for DePix payment data
/// </summary>
public class DePixPaymentData
{
    /// <summary>
    /// The QR code ID
    /// </summary>
    public string? QrId { get; set; }
    /// <summary>
    /// The bank transaction ID
    /// </summary>
    public string? BankTxId { get; set; }
    /// <summary>
    /// The blockchain transaction ID
    /// </summary>
    public string? BlockchainTxId { get; set; }
    /// <summary>
    /// The status of the payment
    /// </summary>
    public string? Status { get; set; }
    /// <summary>
    /// The value in cents
    /// </summary>
    public int? ValueInCents { get; set; }
    /// <summary>
    /// The Pix key used
    /// </summary>
    public string? PixKey { get; set; }
    /// <summary>
    /// The payer's name
    /// </summary>
    public string? PayerName { get; set; }
    /// <summary>
    /// The payer's EUID
    /// </summary>
    public string? PayerEuid { get; set; }
    /// <summary>
    /// The payer's tax number
    /// </summary>
    public string? PayerTaxNumber { get; set; }
    /// <summary>
    /// Message from the customer
    /// </summary>
    public string? CustomerMessage { get; set; }
}

/// <summary>
/// Details for DePix payment method
/// </summary>
public class DePixPaymentMethodDetails
{
    /// <summary>
    /// The QR code ID
    /// </summary>
    public string? QrId { get; set; }
    /// <summary>
    /// The QR code image URL
    /// </summary>
    public string? QrImageUrl { get; set; }
    /// <summary>
    /// The copy-paste code for the QR
    /// </summary>
    public string? CopyPaste { get; set; }
    /// <summary>
    /// The DePix address
    /// </summary>
    public string? DepixAddress { get; set; }
    /// <summary>
    /// The status of the payment
    /// </summary>
    public string? Status { get; set; }
    /// <summary>
    /// The value in cents
    /// </summary>
    public int? ValueInCents { get; set; }
    /// <summary>
    /// Expiration time
    /// </summary>
    public string? Expiration { get; set; }
    /// <summary>
    /// The Pix key
    /// </summary>
    public string? PixKey { get; set; }
    /// <summary>
    /// Payer details
    /// </summary>
    public PayerDetails? Payer { get; set; }

    /// <summary>
    /// Payer details class
    /// </summary>
    public class PayerDetails
    {
        /// <summary>
        /// Payer name
        /// </summary>
        public string? Name { get; set; }
        /// <summary>
        /// Payer EUID
        /// </summary>
        public string? Euid { get; set; }
        /// <summary>
        /// Payer tax number
        /// </summary>
        public string? TaxNumber { get; set; }
        /// <summary>
        /// Payer message
        /// </summary>
        public string? Message { get; set; }
    }
}
