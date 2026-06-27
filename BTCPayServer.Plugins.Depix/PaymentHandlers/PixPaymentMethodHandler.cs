#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Altcoins;
using BTCPayServer.Plugins.Depix.Errors;
using BTCPayServer.Plugins.Depix.Services;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;

namespace BTCPayServer.Plugins.Depix.PaymentHandlers;

/// <summary>
/// Handler for Pix payment method
/// </summary>
public class PixPaymentMethodHandler(
    BTCPayNetworkProvider networkProvider,
    DepixService depixService,
    IHttpContextAccessor httpContextAccessor,
    ISecretProtector secretProtector)
    : IPaymentMethodHandler, IHasNetwork
{
    private const string P2PDepixAddressMetadataKey = "depixAddress";
    private const string EndUserTaxNumberMetadataKey = "endUserTaxNumber";

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

        var amountInCents = (int)Math.Round(context.Prompt.Calculate().Due * 100m, MidpointRounding.AwayFromZero);

        using var client = depixService.CreateDepixClient(apiKey);

        var metadata = context.InvoiceEntity.Metadata?.AdditionalData;
        var formResponse = GetCurrentFormResponse();
        var endUserTaxNumber = ResolvePayerTaxNumber(metadata, formResponse);

        string? p2PDestinationAddress = null;
        var p2PMode = pixCfg.P2PMode && TryGetP2PDestinationAddress(
            metadata,
            metadata?.ContainsKey(P2PDepixAddressMetadataKey) is true ? null : formResponse,
            out p2PDestinationAddress);
        string address;

        string? depixSplitAddress = null;
        string? p2PCommissionPercent = null;
        try
        {
            address = p2PMode
                ? p2PDestinationAddress!
                : await depixService.GenerateFreshDePixAddress(store.Id);

            if (p2PMode)
            {
                if (string.IsNullOrWhiteSpace(pixCfg.P2PCommissionPercent) ||
                    !DepixService.TryNormalizeSplitFee(pixCfg.P2PCommissionPercent, out p2PCommissionPercent))
                {
                    throw new PaymentMethodUnavailableException("P2P mode requires a seller commission percentage");
                }

                depixSplitAddress = await depixService.GenerateFreshDePixAddress(store.Id);
            }
        }
        catch (Exception ex) when (ex is PixPluginException or PixPaymentException)
        {
            throw new PaymentMethodUnavailableException(ex.Message);
        }

        var deposit = await depixService.RequestDepositAsync(
            client, 
            amountInCents, 
            address, 
            pixCfg,
            effectiveConfig.UseWhitelist,
            endUserTaxNumber,
            CancellationToken.None,
            depixSplitAddress,
            p2PCommissionPercent);

        depixService.ApplyPromptDetails(context, deposit, address, amountInCents, p2PMode, depixSplitAddress, p2PCommissionPercent);
    }

    private bool TryGetP2PDestinationAddress(PaymentMethodContext context, out string? address)
    {
        var metadata = context.InvoiceEntity.Metadata?.AdditionalData;
        return TryGetP2PDestinationAddress(
            metadata,
            metadata?.ContainsKey(P2PDepixAddressMetadataKey) is true ? null : GetCurrentFormResponse(),
            out address);
    }

    private static bool TryGetP2PDestinationAddress(
        IDictionary<string, JToken>? metadata,
        string? formResponse,
        out string? address)
    {
        address = null;
        var token = metadata is not null && metadata.TryGetValue(P2PDepixAddressMetadataKey, out var value)
            ? value
            : TryGetP2PDestinationAddressFromFormResponse(formResponse);

        if (token is null)
            return false;

        if (token.Type != JTokenType.String)
            throw new PaymentMethodUnavailableException("P2P mode requires a DePix address");

        address = token.Value<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(address))
            throw new PaymentMethodUnavailableException("P2P mode requires a DePix address");

        return true;
    }

    private string? GetCurrentFormResponse()
    {
        var request = httpContextAccessor.HttpContext?.Request;
        if (request is null)
            return null;

        if (request.HasFormContentType && request.Form.TryGetValue("formResponse", out var formResponse))
            return formResponse.ToString();

        return request.Query.TryGetValue("formResponse", out var queryFormResponse)
            ? queryFormResponse.ToString()
            : null;
    }

    private static JToken? TryGetP2PDestinationAddressFromFormResponse(string? formResponse)
    {
        if (string.IsNullOrWhiteSpace(formResponse))
            return null;

        try
        {
            var values = JObject.Parse(formResponse);
            return values.TryGetValue(P2PDepixAddressMetadataKey, out var token) ? token : null;
        }
        catch (JsonReaderException)
        {
            return null;
        }
    }

    private static string ResolvePayerTaxNumber(
        IDictionary<string, JToken>? metadata,
        string? formResponse)
    {
        var formValues = TryParseFormResponse(formResponse);
        var taxNumber = TryGetString(metadata, EndUserTaxNumberMetadataKey) ??
                        TryGetString(formValues, EndUserTaxNumberMetadataKey);

        if (string.IsNullOrWhiteSpace(taxNumber))
        {
            throw new PaymentMethodUnavailableException(
                "Pix requires payer CPF/CNPJ. Provide endUserTaxNumber.");
        }

        return taxNumber.Trim();
    }

    private static JObject? TryParseFormResponse(string? formResponse)
    {
        if (string.IsNullOrWhiteSpace(formResponse))
            return null;

        try
        {
            return JObject.Parse(formResponse);
        }
        catch (JsonReaderException)
        {
            return null;
        }
    }

    private static string? TryGetString(IDictionary<string, JToken>? values, string key)
    {
        if (values is null || !values.TryGetValue(key, out var token) || token.Type != JTokenType.String)
            return null;

        var value = token.Value<string>()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? TryGetString(JObject? values, string key)
    {
        if (values is null || !values.TryGetValue(key, out var token) || token.Type != JTokenType.String)
            return null;

        var value = token.Value<string>()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
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
    /// Whether this Pix deposit was created in P2P mode
    /// </summary>
    [JsonProperty("p2pMode")]
    public bool? P2PMode { get; set; }
    /// <summary>
    /// The DePix split address used for seller commission
    /// </summary>
    public string? DepixSplitAddress { get; set; }
    /// <summary>
    /// The seller commission percentage used for P2P sales
    /// </summary>
    [JsonProperty("p2pCommissionPercent")]
    public string? P2PCommissionPercent { get; set; }
    /// <summary>
    /// Legacy split percentage field used by older P2P prompt details
    /// </summary>
    public string? SplitFee { get; set; }
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
