#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Hosting;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Altcoins;
using BTCPayServer.Plugins.Depix.Data.Enums;
using BTCPayServer.Plugins.Depix.Data.Models;
using BTCPayServer.Plugins.Depix.Data.Models.ViewModels;
using BTCPayServer.Plugins.Depix.Errors;
using BTCPayServer.Plugins.Depix.PaymentHandlers;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Rates;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Depix.Services;

/// <summary>
/// Service for managing DePix integration
/// </summary>
public class DepixService(
    IServiceProvider serviceProvider,
    IServiceScopeFactory scopeFactory,
    StoreRepository storeRepository,
    InvoiceRepository invoiceRepository,
    ILogger<PixPaymentMethodHandler> logger,
    ApplicationDbContextFactory dbFactory,
    EventAggregator events,
    DisplayFormatter displayFormatter,
    ISettingsRepository settingsRepository 
)
{
    
    /// <summary>
    /// Source of the DePix configuration
    /// </summary>
    public enum DepixConfigSource
    {
        /// <summary>
        /// Not configured
        /// </summary>
        None,
        /// <summary>
        /// Configured at store level
        /// </summary>
        Store,
        /// <summary>
        /// Configured at server level
        /// </summary>
        Server
    }

    /// <summary>
    /// Effective DePix configuration
    /// </summary>
    public record EffectivePixConfig(
        DepixConfigSource Source,
        string? EncryptedApiKey,
        string? WebhookSecretHashHex,
        bool UseWhitelist,
        bool PassFeeToCustomer);
    
    /// <summary>
    /// Checks if DePix is enabled for a store
    /// </summary>
    /// <param name="storeId">The store ID</param>
    /// <returns>True if enabled, false otherwise</returns>
    public async Task<bool> IsDePixEnabled(string storeId)
    {
        try
        {
            var storeData = await storeRepository.FindStore(storeId);
            if (storeData == null)
            {
                logger.LogWarning($"[DePix] Store {storeId} not found.");
                return false;
            }

            var paymentMethods = storeData.GetPaymentMethodConfigs();

            var excludeFilters = storeData.GetStoreBlob().GetExcludedPaymentMethods();

            var isConfigured = paymentMethods.ContainsKey(DePixPlugin.DePixPmid);
            var isExcluded = excludeFilters.Match(DePixPlugin.DePixPmid);
            
            return isConfigured && !isExcluded;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"[DePix] Error checking if {DePixPlugin.DePixPmid} is enabled for store {storeId}");
            return false;
        }
    }
    
    /// <summary>
    /// Gets the Pix configuration status for a store
    /// </summary>
    /// <param name="storeId">The store ID</param>
    /// <returns>The Pix configuration status</returns>
    public async Task<PixConfigStatus> GetPixConfigStatus(string storeId)
    {
        var store = await storeRepository.FindStore(storeId);
        if (store is null)
            return new PixConfigStatus(DePixActive: false, PixEnabled: false, ApiKeyConfigured: false);

        using var scope = scopeFactory.CreateScope();
        var handlers = scope.ServiceProvider.GetRequiredService<PaymentMethodHandlerDictionary>();

        var dePixActive = await IsDePixEnabled(storeId);

        var pixCfg = store.GetPaymentMethodConfig<PixPaymentMethodConfig>(DePixPlugin.PixPmid, handlers);
        var effectiveConfig = await GetEffectiveConfigAsync(pixCfg);

        var pixEnabled       = pixCfg?.IsEnabled == true;
        var apiKeyConfigured = effectiveConfig.Source != DepixConfigSource.None;

        return new PixConfigStatus(dePixActive, pixEnabled, apiKeyConfigured);
    }
    
    /// <summary>
    /// Checks if Pix is enabled for a store
    /// </summary>
    /// <param name="storeId">The store ID</param>
    /// <returns>True if enabled, false otherwise</returns>
    public async Task<bool> IsPixEnabled(string storeId)
    {
        var store = await storeRepository.FindStore(storeId);
        if (store is null) return false;

        if (!await IsDePixEnabled(storeId)) return false;

        using var scope = scopeFactory.CreateScope();
        var handlers = scope.ServiceProvider.GetRequiredService<PaymentMethodHandlerDictionary>();

        var cfg = store.GetPaymentMethodConfig<PixPaymentMethodConfig>(DePixPlugin.PixPmid, handlers);
        return cfg is not null && cfg.IsEnabled;
    }
    
    /// <summary>
    /// Generates a fresh DePix address for a store
    /// </summary>
    /// <param name="storeId">The store ID</param>
    /// <returns>The generated address</returns>
    /// <exception cref="PixPluginException">Thrown if store or network not configured</exception>
    /// <exception cref="PixPaymentException">Thrown if wallet not configured</exception>
    public async Task<string> GenerateFreshDePixAddress(string storeId)
    {
        var walletProvider = serviceProvider.GetRequiredService<BTCPayWalletProvider>();
        var networkProvider = serviceProvider.GetRequiredService<BTCPayNetworkProvider>();

        var store = await storeRepository.FindStore(storeId);
        if (store == null)
            throw new PixPluginException("Store not found"); 
        

        var depixNetwork = networkProvider.GetNetwork<ElementsBTCPayNetwork>(DePixPlugin.DePixCryptoCode);
        if (depixNetwork == null)
            throw new PixPluginException("DePix asset network not configured");
        

        var wallet = walletProvider.GetWallet(depixNetwork);
        if (wallet == null)
            throw new PixPaymentException("Depix wallet not configured");
        

        const string generatedBy = "invoice";
        var handlers = serviceProvider.GetRequiredService<PaymentMethodHandlerDictionary>();

        var derivationSettings = store.GetDerivationSchemeSettings(handlers, DePixPlugin.DePixCryptoCode, onlyEnabled: true);
        if (derivationSettings == null)
            throw new PixPluginException("DePix derivation scheme not configured for this store.");
        
        var addressData = await wallet.ReserveAddressAsync(storeId, derivationSettings.AccountDerivation, generatedBy);
        var address = addressData.Address.ToString();
        
        return address;
    }
    
    /// <summary>
    /// Creates a DePix API client
    /// </summary>
    /// <param name="apiKey">The API key</param>
    /// <returns>The HttpClient</returns>
    public HttpClient CreateDepixClient(string apiKey)
    {
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri("https://depix.eulen.app/api/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }
    
    /// <summary>
    /// Validates a DePix API key
    /// </summary>
    /// <param name="apiKey">The API key to validate</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The validation response</returns>
    public async Task<ApiKeyValidationResponse> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        try
        {
            var client = CreateDepixClient(apiKey);
            using var req  = new HttpRequestMessage(HttpMethod.Get, "ping");
            using var resp = await client.SendAsync(req, ct);

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new ApiKeyValidationResponse(false, "Invalid API key (401/403).");

            // At the moment, despite docs, Depix API only return 200 or 500 so we can't know if it's a server error or apiKey error.
            // So, if doesn't return 200 we handle as ApiKey error for now. 
            if (!resp.IsSuccessStatusCode)
            {
                var code = (int)resp.StatusCode;
                var msg  = $"Invalid API key ({code}).";
                return new ApiKeyValidationResponse(false, msg);
            }

            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);

            if (doc.RootElement.TryGetProperty("response", out var response) &&
                response.TryGetProperty("msg", out var msgProp) &&
                string.Equals(msgProp.GetString(), "Pong!", StringComparison.OrdinalIgnoreCase))
            {
                return new ApiKeyValidationResponse(true, "OK");
            }

            return new ApiKeyValidationResponse(false, "API responded but did not confirm token (no Pong!).");
        }
        catch (TaskCanceledException)
        {
            return new ApiKeyValidationResponse(false, "Timed out reaching DePix API. Try again.");
        }
        catch (HttpRequestException)
        {
            return new ApiKeyValidationResponse(false, "Network error contacting DePix API.");
        }
        catch
        {
            return new ApiKeyValidationResponse(false, "API key validation failed due to an unexpected error.");
        }
    }
    
    /// <summary>
    /// Requests a new Pix deposit from the DePix API
    /// </summary>
    /// <param name="client">Authenticated HttpClient</param>
    /// <param name="amountInCents">Amount in cents</param>
    /// <param name="depixAddress">The DePix address to receive funds</param>
    /// <param name="pixCfg">Pix configuration containing optional split parameters</param>
    /// <param name="useWhitelist">Whether whitelist is enabled from effective config</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The deposit response containing QR code info</returns>
    /// <exception cref="PaymentMethodUnavailableException">Thrown if request fails</exception>
    public async Task<DepixDepositResponse> RequestDepositAsync(
        HttpClient client,
        int amountInCents,
        string depixAddress,
        PixPaymentMethodConfig pixCfg,
        bool useWhitelist,
        string endUserTaxNumber,
        CancellationToken ct,
        string? depixSplitAddressOverride = null,
        string? splitFeeOverride = null)
    {
        endUserTaxNumber = NormalizeEndUserTaxNumber(endUserTaxNumber);

        var payload = new Dictionary<string, object>
        {
            ["amountInCents"] = amountInCents,
            ["depixAddress"]  = depixAddress,
            ["endUserTaxNumber"] = endUserTaxNumber
        };
        
        if (useWhitelist)
            payload["whitelist"] = true;

        if (depixSplitAddressOverride is not null || splitFeeOverride is not null)
        {
            var splitAddressTrimmed = depixSplitAddressOverride?.Trim();
            if (!string.IsNullOrWhiteSpace(splitAddressTrimmed))
                payload["depixSplitAddress"] = splitAddressTrimmed;

            var splitFeeTrimmed = splitFeeOverride?.Trim();
            if (!string.IsNullOrWhiteSpace(splitFeeTrimmed))
                payload["splitFee"] = splitFeeTrimmed;
        }
        else
        {
            var splitAddressTrimmed = pixCfg.DepixSplitAddress?.Trim();
            var splitFeeTrimmed = pixCfg.SplitFee?.Trim();
            if (!string.IsNullOrWhiteSpace(splitAddressTrimmed) && !string.IsNullOrWhiteSpace(splitFeeTrimmed))
            {
                payload["depixSplitAddress"] = splitAddressTrimmed;
                payload["splitFee"] = splitFeeTrimmed;
            }
        }

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("deposit", content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var response = string.IsNullOrWhiteSpace(body)
                ? resp.ReasonPhrase
                : body.Length > 500
                    ? body[..500]
                    : body;
            throw new PaymentMethodUnavailableException(
                $"Failed to generate Pix QR Code ({(int)resp.StatusCode} {response})");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement.GetProperty("response");

        var qrId       = root.GetProperty("id").GetString();
        var qrImageUrl = root.GetProperty("qrImageUrl").GetString();
        var copyPaste  = root.GetProperty("qrCopyPaste").GetString();

        if (string.IsNullOrEmpty(qrId))
            throw new PaymentMethodUnavailableException("DePix response did not include id");
        if (string.IsNullOrEmpty(qrImageUrl))
            throw new PaymentMethodUnavailableException("DePix response did not include qrImageUrl");
        if (string.IsNullOrEmpty(copyPaste))
            throw new PaymentMethodUnavailableException("DePix response did not include qrCopyPaste");

        return new DepixDepositResponse(qrId, qrImageUrl!, copyPaste!);
    }

    private static string NormalizeEndUserTaxNumber(string endUserTaxNumber)
    {
        var digits = new StringBuilder(endUserTaxNumber.Length);
        foreach (var c in endUserTaxNumber)
        {
            if (c >= '0' && c <= '9')
                digits.Append(c);
        }

        if (digits.Length == 0)
            throw new PaymentMethodUnavailableException("Pix requires payer CPF/CNPJ.");

        return digits.ToString();
    }
    
    /// <summary>
    /// Applies the deposit details to the payment prompt
    /// </summary>
    /// <param name="context">The payment method context</param>
    /// <param name="depositResponse">The deposit response from DePix</param>
    /// <param name="depixAddress">The DePix address</param>
    /// <param name="amountInCents">The QR amount in cents</param>
    public void ApplyPromptDetails(
        PaymentMethodContext context,
        DepixDepositResponse depositResponse,
        string depixAddress,
        int amountInCents,
        bool p2PMode = false,
        string? depixSplitAddress = null,
        string? p2PCommissionPercent = null)
    {
        context.Prompt.Destination = depositResponse.QrImageUrl;

        var details = context.Prompt.Details is null
            ? new DePixPaymentMethodDetails()
            : context.Handler.ParsePaymentPromptDetails(context.Prompt.Details) as DePixPaymentMethodDetails ??
              new DePixPaymentMethodDetails();

        details.QrId = depositResponse.QrId;
        details.QrImageUrl = depositResponse.QrImageUrl;
        details.CopyPaste = depositResponse.QrCopyPaste;
        details.DepixAddress = depixAddress;
        if (p2PMode)
        {
            details.P2PMode = true;
            details.DepixSplitAddress = depixSplitAddress;
            details.P2PCommissionPercent = p2PCommissionPercent;
            details.SplitFee = null;
        }
        else
        {
            details.P2PMode = null;
            details.DepixSplitAddress = null;
            details.P2PCommissionPercent = null;
            details.SplitFee = null;
        }
        if (amountInCents > 0)
            details.ValueInCents = amountInCents;

        context.Prompt.Details = JToken.FromObject(details, context.Handler.Serializer);
    }
    
    /// <summary>
    /// Loads Pix transactions for a store
    /// </summary>
    /// <param name="query">The query parameters</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>A list of Pix transactions</returns>
    public async Task<List<PixTxResponse>> LoadPixTransactionsAsync(PixTxQueryRequest query, CancellationToken ct)
    {
        await using var db = invoiceRepository.DbContextFactory.CreateContext();
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        var where = new List<string>
        {
            "\"StoreDataId\" = @storeId",
            "(\"Blob2\"->'prompts'->'PIX'->'details'->>'qrId') IS NOT NULL"
        };
        var status = NormalizeStatusFilter(query.Status);
        if (!string.IsNullOrWhiteSpace(status))
        {
            where.Add("LOWER(REPLACE(COALESCE(\"Blob2\"->'prompts'->'PIX'->'details'->>'status', ''), '-', '_')) = @status");
        }
        if (query.From is not null)
            where.Add("\"Created\" >= @fromUtc");
        if (query.To is not null)
            where.Add("\"Created\" <= @toUtc");
        if (!string.IsNullOrWhiteSpace(query.SearchTerm))
        {
            where.Add("""
                      (
                          "Id" ILIKE '%' || @search || '%'
                          OR ("Blob2"->'prompts'->'PIX'->'details'->>'qrId') ILIKE '%' || @search || '%'
                          OR ("Blob2"->'prompts'->'PIX'->'details'->>'depixAddress') ILIKE '%' || @search || '%'
                      )
                      """);
        }

        var sql = $"""
                   SELECT
                     "Id" AS "InvoiceId",
                     "Created"::timestamptz AS "Created",
                     ("Blob2"->'prompts'->'PIX'->'details'->>'qrId')          AS "QrId",
                     ("Blob2"->'prompts'->'PIX'->'details'->>'depixAddress')  AS "DepixAddress",
                     NULLIF(("Blob2"->'prompts'->'PIX'->'details'->>'p2pMode'), '')::bool AS "P2PMode",
                     ("Blob2"->'prompts'->'PIX'->'details'->>'depixSplitAddress') AS "DepixSplitAddress",
                     COALESCE(
                         ("Blob2"->'prompts'->'PIX'->'details'->>'p2pCommissionPercent'),
                         ("Blob2"->'prompts'->'PIX'->'details'->>'splitFee')
                     ) AS "P2PCommissionPercent",
                     NULLIF(("Blob2"->'prompts'->'PIX'->'details'->>'valueInCents'), '')::int AS "ValueInCents",
                     COALESCE(("Blob2"->'prompts'->'PIX'->'details'->>'status'), 'pending')   AS "DepixStatusRaw"
                   FROM "Invoices"
                   WHERE {string.Join(" AND ", where)}
                   ORDER BY "Created" DESC
                   LIMIT 200;
                   """;

        var args = new
        {
            storeId = query.StoreId,
            status,
            search  = query.SearchTerm,
            fromUtc = query.From?.UtcDateTime,
            toUtc   = query.To?.UtcDateTime
        };

        var rows = await conn.QueryAsync<PixTxResponse>(
            new CommandDefinition(sql, args, cancellationToken: ct));

        var transactions = rows.ToList();
        foreach (var transaction in transactions)
        {
            transaction.DepixStatus = DepixStatusExtensions.TryParse(transaction.DepixStatusRaw, out var st) ? st : null;
            transaction.AmountBrl = transaction.ValueInCents is { } v ? displayFormatter.Currency(v / 100m, "BRL") : "-";
        }

        return transactions;
    }
    
    /// <summary>
    /// Processes a webhook for a specific store
    /// </summary>
    /// <param name="storeId">The store ID</param>
    /// <param name="body">The webhook body</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task ProcessWebhookAsync(string storeId, DepositWebhookBody body, CancellationToken cancellationToken)
    {
        try
        {
            var invoiceId = await FindInvoiceIdByQrIdAsync(body.QrId, storeId, cancellationToken);
            if (invoiceId is null)
            {
                logger.LogWarning("Depix webhook: invoice not found for store {StoreId} and qrId {QrId}", storeId, body.QrId);
                return;
            }

            await ProcessWebhookForInvoiceAsync(invoiceId, body, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Depix webhook processing failed for store {StoreId}", storeId);
        }
    }
    
    /// <summary>
    /// Processes a webhook (server-level or global)
    /// </summary>
    /// <param name="body">The webhook body</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task ProcessWebhookAsync(DepositWebhookBody body, CancellationToken cancellationToken)
    {
        try
        {
            var invoiceId = await FindInvoiceIdByQrIdAsync(body.QrId, storeId: null, cancellationToken);
            if (invoiceId is null)
            {
                logger.LogWarning("Depix webhook (server): invoice not found for qrId {QrId}", body.QrId);
                return;
            }

            await ProcessWebhookForInvoiceAsync(invoiceId, body, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Depix webhook (server) processing failed");
        }
    }
    
    private async Task ProcessWebhookForInvoiceAsync(string invoiceId, DepositWebhookBody body, CancellationToken cancellationToken)
    {
        var entity = await invoiceRepository.GetInvoice(invoiceId);
        if (entity is null)
        {
            logger.LogWarning("Depix webhook: invoice entity null for {InvoiceId}", invoiceId);
            return;
        }

        var pmid = DePixPlugin.PixPmid;
        var pixPrompt = entity.GetPaymentPrompt(pmid);
        if (pixPrompt is null)
        {
            logger.LogWarning("Depix webhook: PIX prompt not found on invoice {InvoiceId}", invoiceId);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var handlers = scope.ServiceProvider.GetRequiredService<PaymentMethodHandlerDictionary>();
        if (!handlers.TryGetValue(pmid, out var handler))
        {
            logger.LogWarning("Depix webhook: PIX handler missing for invoice {InvoiceId}", invoiceId);
            return;
        }

        var hasDepixStatus = DepixStatusExtensions.TryParse(body.Status, out var depixStatus);
        var expectedValueInCents = TryGetExpectedValueInCents(entity, pixPrompt, body.QrId, handler);
        var effectivePromptStatus = TryGetPromptStatus(pixPrompt, handler);
        try
        {
            effectivePromptStatus = await UpdatePromptDetailsFromWebhookAsync(
                invoiceId,
                body,
                handler,
                expectedValueInCents,
                cancellationToken) ?? effectivePromptStatus;
        }
        catch (Exception ex) when (hasDepixStatus &&
                                   ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Depix webhook: could not update PIX prompt details before applying status {Status} for invoice {InvoiceId}", body.Status, invoiceId);
        }

        if (!hasDepixStatus)
        {
            await TryUpdateExistingPaymentAndPublishAsync(entity, pixPrompt, body, expectedValueInCents, handler, cancellationToken);
            return;
        }

        if (!depixStatus.IsConfirmedPaymentStatus())
        {
            await TryUpdateExistingPaymentAndPublishAsync(entity, pixPrompt, body, expectedValueInCents, handler, cancellationToken);
            await ApplyStatusAndNotifyAsync(invoiceId, depixStatus);
            return;
        }

        if (!depixStatus.ShouldReplace(effectivePromptStatus))
        {
            await TryUpdateExistingPaymentAndPublishAsync(entity, pixPrompt, body, expectedValueInCents, handler, cancellationToken);
            return;
        }

        await TryRecordPaymentAsync(entity, pixPrompt, body, depixStatus, expectedValueInCents, handler, cancellationToken);
    }

    private async Task<string?> UpdatePromptDetailsFromWebhookAsync(
        string invoiceId,
        DepositWebhookBody body,
        IPaymentMethodHandler handler,
        int? expectedValueInCents,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var db = dbFactory.CreateContext();
            try
            {
                var invoiceData = await db.Invoices.FindAsync([invoiceId], cancellationToken);
                if (invoiceData is null)
                    return null;

                var invoiceEntity = invoiceData.GetBlob();
                var prompt = invoiceEntity.GetPaymentPrompt(handler.PaymentMethodId);
                if (prompt is null)
                    return null;

                var details = prompt.Details is null
                    ? new DePixPaymentMethodDetails()
                    : handler.ParsePaymentPromptDetails(prompt.Details) as DePixPaymentMethodDetails ??
                      new DePixPaymentMethodDetails();

                ApplyWebhookBodyToDetails(details, body, expectedValueInCents);

                prompt.Details = JToken.FromObject(details, handler.Serializer);
                invoiceEntity.SetPaymentPrompt(handler.PaymentMethodId, prompt);
                invoiceData.SetBlob(invoiceEntity);
                await db.SaveChangesAsync(cancellationToken);
                return details.Status;
            }
            catch (DbUpdateConcurrencyException) when (attempt + 1 < maxAttempts)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        return null;
    }

    private static void ApplyWebhookBodyToDetails(
        DePixPaymentMethodDetails details,
        DepositWebhookBody body,
        int? expectedValueInCents)
    {
        if (body.Status is not null)       details.Status = MergeStatus(body.Status, details.Status);
        if (TryGetWebhookValueInCents(body, expectedValueInCents) is { } valueInCents && details.ValueInCents is null)
            details.ValueInCents = valueInCents;
        if (body.Expiration is not null)   details.Expiration = body.Expiration;
        if (body.PixKey is not null)       details.PixKey = body.PixKey;

        if (body.PayerName is null && body.PayerEuid is null &&
            body.PayerTaxNumber is null && body.CustomerMessage is null)
        {
            return;
        }

        details.Payer ??= new DePixPaymentMethodDetails.PayerDetails();
        if (body.PayerName is not null)       details.Payer.Name = body.PayerName;
        if (body.PayerEuid is not null)       details.Payer.Euid = body.PayerEuid;
        if (body.PayerTaxNumber is not null)  details.Payer.TaxNumber = body.PayerTaxNumber;
        if (body.CustomerMessage is not null) details.Payer.Message = body.CustomerMessage;
    }

    private async Task TryRecordPaymentAsync(InvoiceEntity entity, PaymentPrompt pixPrompt, DepositWebhookBody body,
        DepixStatus depixStatus, int? expectedValueInCents, IPaymentMethodHandler handler, CancellationToken cancellationToken)
    {
        if (!depixStatus.IsConfirmedPaymentStatus())
            return;

        if (string.IsNullOrWhiteSpace(body.QrId))
        {
            logger.LogWarning("Depix webhook: confirmed status {Status} without payment id for invoice {InvoiceId}", body.Status, entity.Id);
            return;
        }

        var expectedAmount = expectedValueInCents is > 0 ? expectedValueInCents.Value / 100m : pixPrompt.Calculate().Due;
        var amount = TryGetWebhookAmount(body, expectedValueInCents) ?? expectedAmount;
        if (amount <= 0m)
        {
            logger.LogWarning("Depix webhook: confirmed status {Status} with invalid amount {Amount} for invoice {InvoiceId}", body.Status, amount, entity.Id);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var paymentService = scope.ServiceProvider.GetRequiredService<PaymentService>();

        var paymentId = GetPaymentId(entity.Id, body.QrId);
        var paymentData = new PaymentData
        {
            Id = paymentId,
            Created = DateTimeOffset.UtcNow,
            Status = PaymentStatus.Settled,
            Currency = pixPrompt.Currency,
            InvoiceDataId = entity.Id,
            Amount = amount
        }.Set(entity, handler, BuildPaymentData(body, valueInCents: TryGetWebhookValueInCents(body, expectedValueInCents)));

        var payment = await paymentService.AddPayment(paymentData, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { paymentId });
        if (payment is null)
        {
            if (await TryUpdateExistingPaymentAsync(paymentId, entity, pixPrompt, body, amount, handler, cancellationToken))
            {
                await PublishExistingPaymentUpdatedAsync(entity.Id);
                return;
            }

            events.Publish(new InvoiceNeedUpdateEvent(entity.Id));
            return;
        }

        var invoice = await invoiceRepository.GetInvoice(entity.Id);
        if (invoice is not null)
            events.Publish(new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment) { Payment = payment });
    }

    private async Task PublishExistingPaymentUpdatedAsync(string invoiceId)
    {
        events.Publish(new InvoiceNeedUpdateEvent(invoiceId));

        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice is not null)
            events.Publish(new InvoiceDataChangedEvent(invoice));
    }

    private async Task TryUpdateExistingPaymentAndPublishAsync(
        InvoiceEntity entity,
        PaymentPrompt pixPrompt,
        DepositWebhookBody body,
        int? expectedValueInCents,
        IPaymentMethodHandler handler,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body.QrId))
            return;

        if (await TryUpdateExistingPaymentAsync(
                GetPaymentId(entity.Id, body.QrId),
                entity,
                pixPrompt,
                body,
                TryGetWebhookAmount(body, expectedValueInCents),
                handler,
                cancellationToken))
        {
            await PublishExistingPaymentUpdatedAsync(entity.Id);
        }
    }

    private async Task<bool> TryUpdateExistingPaymentAsync(
        string paymentId,
        InvoiceEntity entity,
        PaymentPrompt pixPrompt,
        DepositWebhookBody body,
        decimal? amount,
        IPaymentMethodHandler handler,
        CancellationToken cancellationToken)
    {
        var paymentMethodId = handler.PaymentMethodId.ToString();
        await using var strategyContext = dbFactory.CreateContext();
        return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = dbFactory.CreateContext();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var existing = await db.Payments
                .FromSqlInterpolated($"""
                                      SELECT *
                                      FROM "Payments"
                                      WHERE "Id" = {paymentId} AND "PaymentMethodId" = {paymentMethodId}
                                      FOR UPDATE
                                      """)
                .SingleOrDefaultAsync(cancellationToken);
            if (existing is null)
                return false;

            if (!TryParsePaymentDetails(existing, handler, out var existingDetails))
                return false;

            var acceptedAmount = existing.Amount is > 0m ? existing.Amount : amount;
            if (amount is > 0m && existing.Amount is not > 0m)
            {
                existing.Amount = amount;
            }
            else if (amount is > 0m && existing.Amount != amount)
            {
                logger.LogWarning(
                    "Depix webhook: ignoring mismatched amount {IncomingAmount} for existing payment {PaymentId} with amount {ExistingAmount}",
                    amount,
                    paymentId,
                    existing.Amount);
            }

            var acceptedValueInCents = acceptedAmount is > 0m ? (int?)ToValueInCents(acceptedAmount.Value) : null;
            existing.Currency = pixPrompt.Currency;
            existing.Status = PaymentStatus.Settled;
            existing.Set(entity, handler, BuildPaymentData(
                body,
                existingDetails,
                TryGetWebhookValueInCents(body, acceptedValueInCents)));

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        });
    }

    private bool TryParsePaymentDetails(
        PaymentData paymentData,
        IPaymentMethodHandler handler,
        out DePixPaymentData? details)
    {
        try
        {
            details = paymentData.GetBlob().GetDetails<DePixPaymentData>(handler);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Depix webhook: could not parse existing payment details for payment {PaymentId}", paymentData.Id);
            details = null;
            return false;
        }
    }

    private static DePixPaymentData BuildPaymentData(
        DepositWebhookBody body,
        DePixPaymentData? existing = null,
        int? valueInCents = null)
    {
        return new DePixPaymentData
        {
            QrId = body.QrId ?? existing?.QrId,
            BankTxId = body.BankTxId ?? existing?.BankTxId,
            BlockchainTxId = body.BlockchainTxId ?? existing?.BlockchainTxId,
            Status = MergeStatus(body.Status, existing?.Status),
            ValueInCents = existing?.ValueInCents ?? valueInCents,
            PixKey = body.PixKey ?? existing?.PixKey,
            PayerName = body.PayerName ?? existing?.PayerName,
            PayerEuid = body.PayerEuid ?? existing?.PayerEuid,
            PayerTaxNumber = body.PayerTaxNumber ?? existing?.PayerTaxNumber,
            CustomerMessage = body.CustomerMessage ?? existing?.CustomerMessage
    };
}

    private static string GetPaymentId(string invoiceId, string qrId)
    {
        return $"{invoiceId}:{qrId}";
    }

    private static int? TryGetExpectedValueInCents(
        InvoiceEntity entity,
        PaymentPrompt prompt,
        string? qrId,
        IPaymentMethodHandler handler)
    {
        if (TryGetStoredPromptValueInCents(prompt, handler) is { } storedValueInCents)
            return storedValueInCents;

        var promptAmount = prompt.Calculate().Due;
        if (promptAmount > 0m)
            return ToValueInCents(promptAmount);

        if (string.IsNullOrWhiteSpace(qrId))
            return null;

        var existingPayment = entity
            .GetPayments(false)
            .FirstOrDefault(payment =>
                payment.PaymentMethodId == handler.PaymentMethodId &&
                string.Equals(payment.Id, GetPaymentId(entity.Id, qrId), StringComparison.Ordinal));

        return existingPayment?.Value is > 0m ? ToValueInCents(existingPayment.Value) : null;
    }

    private static int? TryGetStoredPromptValueInCents(PaymentPrompt prompt, IPaymentMethodHandler? handler)
    {
        if (prompt.Details is null || handler is null)
            return null;

        return handler.ParsePaymentPromptDetails(prompt.Details) is DePixPaymentMethodDetails { ValueInCents: > 0 } details
            ? details.ValueInCents
            : null;
    }

    private static string? TryGetPromptStatus(PaymentPrompt prompt, IPaymentMethodHandler handler)
    {
        if (prompt.Details is null)
            return null;

        return handler.ParsePaymentPromptDetails(prompt.Details) is DePixPaymentMethodDetails details
            ? details.Status
            : null;
    }

    private static int? TryGetWebhookValueInCents(DepositWebhookBody body)
    {
        return body.ValueInCents is > 0 ? body.ValueInCents.Value : null;
    }

    private static int? TryGetWebhookValueInCents(DepositWebhookBody body, int? expectedValueInCents)
    {
        var valueInCents = TryGetWebhookValueInCents(body);
        if (valueInCents is null || expectedValueInCents is null)
            return null;
        if (valueInCents == expectedValueInCents)
            return valueInCents;

        return null;
    }

    private static decimal? TryGetWebhookAmount(DepositWebhookBody body, int? expectedValueInCents)
    {
        return TryGetWebhookValueInCents(body, expectedValueInCents) is { } valueInCents ? valueInCents / 100m : null;
    }

    private static int ToValueInCents(decimal amount)
    {
        return (int)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
    }

    private static string? MergeStatus(string? incomingStatus, string? existingStatus)
    {
        if (string.IsNullOrWhiteSpace(incomingStatus))
            return existingStatus;

        if (DepixStatusExtensions.TryParse(incomingStatus, out var incoming))
            return incoming.ShouldReplace(existingStatus) ? incoming.ToApiString() : existingStatus;

        return DepixStatusExtensions.TryParse(existingStatus, out _) ? existingStatus : incomingStatus.Trim();
    }

    private static string? NormalizeStatusFilter(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return null;

        return DepixStatusExtensions.TryParse(status, out var parsed)
            ? parsed.ToApiString()
            : status.Trim().ToLowerInvariant().Replace("-", "_");
    }

    private async Task ApplyStatusAndNotifyAsync(string invoiceId, DepixStatus depix)
    {
        var entity = await invoiceRepository.GetInvoice(invoiceId);
        if (entity is null) return;

        var current  = entity.GetInvoiceState();
        var newState = depix.ToInvoiceState(current);
        if (newState is null) return;

        await invoiceRepository.UpdateInvoiceStatus(invoiceId, newState);

        events.Publish(new InvoiceNeedUpdateEvent(invoiceId));
        events.Publish(new InvoiceDataChangedEvent(entity));
    }
    
    /// <summary>
    /// Gets the Pix configuration for a store
    /// </summary>
    /// <param name="store">The store data</param>
    /// <param name="handlers">The payment method handlers</param>
    /// <returns>The Pix configuration or null</returns>
    public PixPaymentMethodConfig? GetPixConfig(StoreData store, PaymentMethodHandlerDictionary handlers)
    {
        var pmid = DePixPlugin.PixPmid;
        var configs = store.GetPaymentMethodConfigs();

        if (!configs.TryGetValue(pmid, out var raw))
            return null;

        if (!handlers.TryGetValue(pmid, out var handlerObj) || handlerObj is not PixPaymentMethodHandler handler)
            return null;

        return handler.ParsePaymentMethodConfig(raw) as PixPaymentMethodConfig;
    }
    
    /// <summary>
    /// Gets the server-level DePix configuration
    /// </summary>
    /// <returns>The server configuration</returns>
    public async Task<PixServerConfig> GetServerConfigAsync()
    {
        return await settingsRepository.GetSettingAsync<PixServerConfig>() ?? new PixServerConfig();
    }
    
    /// <summary>
    /// Validates the configuration
    /// </summary>
    /// <param name="encryptedApiKey">The encrypted API key</param>
    /// <param name="webhookSecretHashHex">The webhook secret hash</param>
    /// <returns>True if valid, false otherwise</returns>
    public static bool IsConfigValid(string? encryptedApiKey, string? webhookSecretHashHex)
        => !string.IsNullOrEmpty(encryptedApiKey) && !string.IsNullOrEmpty(webhookSecretHashHex);

    /// <summary>
    /// Normalizes a split percentage to the DePix API format.
    /// </summary>
    /// <param name="raw">Raw percentage, with or without a percent suffix</param>
    /// <param name="normalized">Normalized percentage with a percent suffix</param>
    /// <returns>True if valid; otherwise false</returns>
    public static bool TryNormalizeSplitFee(string raw, out string normalized)
    {
        normalized = "";
        var trimmed = raw.Trim();
        if (trimmed.EndsWith("%", StringComparison.Ordinal))
            trimmed = trimmed[..^1].Trim();

        if (string.IsNullOrEmpty(trimmed) || trimmed.Count(c => c == ',') > 1)
            return false;

        if (trimmed.Contains('.', StringComparison.Ordinal) && trimmed.Contains(',', StringComparison.Ordinal))
            return false;

        if (trimmed.Contains(',', StringComparison.Ordinal))
            trimmed = trimmed.Replace(',', '.');

        if (!decimal.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            return false;
        if (value <= 0m || value >= 100m)
            return false;
        var scale = (decimal.GetBits(value)[3] >> 16) & 0xFF;
        if (scale > 2)
            return false;
        normalized = value.ToString("0.##", CultureInfo.InvariantCulture) + "%";
        return true;
    }

    /// <summary>
    /// Gets the effective configuration (merging store and server configs)
    /// </summary>
    /// <param name="storeCfg">The store configuration</param>
    /// <returns>The effective configuration</returns>
    public async Task<EffectivePixConfig> GetEffectiveConfigAsync(PixPaymentMethodConfig? storeCfg)
    {
        if (storeCfg is not null && IsConfigValid(storeCfg.EncryptedApiKey, storeCfg.WebhookSecretHashHex))
        {
            return new EffectivePixConfig(
                DepixConfigSource.Store,
                storeCfg.EncryptedApiKey,
                storeCfg.WebhookSecretHashHex,
                storeCfg.UseWhitelist,
                storeCfg.PassFeeToCustomer);
        }

        var serverCfg = await GetServerConfigAsync();
        if (IsConfigValid(serverCfg.EncryptedApiKey, serverCfg.WebhookSecretHashHex))
        {
            return new EffectivePixConfig(
                DepixConfigSource.Server,
                serverCfg.EncryptedApiKey,
                serverCfg.WebhookSecretHashHex,
                serverCfg.UseWhitelist,
                serverCfg.PassFeeToCustomer);
        }

        return new EffectivePixConfig(
            DepixConfigSource.None,
            null,
            null,
            false,
            false);
    }
    
    private async Task<string?> FindInvoiceIdByQrIdAsync(string qrId, string? storeId, CancellationToken ct)
    {
        await using var db = dbFactory.CreateContext();
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        const string sql = """
                           SELECT "Id"
                           FROM "Invoices"
                           WHERE (@storeId IS NULL OR "StoreDataId" = @storeId)
                             AND ("Blob2"->'prompts'->'PIX'->'details'->>'qrId') = @qrId
                           ORDER BY "Created" DESC
                           LIMIT 1;
                           """;

        return await conn.QueryFirstOrDefaultAsync<string?>(
            new CommandDefinition(sql, new { qrId, storeId }, cancellationToken: ct));
    }
    
    /// <summary>
    /// Initializes the DePix plugin
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="nbxProvider">The NBXplorer network provider</param>
    /// <param name="selectedChains">The selected chains registry</param>
    public void InitDePix(IServiceCollection services, NBXplorerNetworkProvider nbxProvider, SelectedChains selectedChains)
    {
        var lbtc = nbxProvider.GetFromCryptoCode("LBTC");
        if (lbtc is null)
        {
            logger.LogWarning("[DePix] LBTC network not available. Skipping DePix init.");
            return;
        }

        if (!selectedChains.Contains("LBTC"))
            selectedChains.Add("LBTC");

        var chainName = lbtc.NBitcoinNetwork.ChainName;

        var network = new ElementsBTCPayNetwork
        {
            CryptoCode        = DePixPlugin.DePixCryptoCode,
            NetworkCryptoCode = "LBTC",
            ShowSyncSummary   = false,
            DefaultRateRules =
            [
                "BTC_USD = kraken(BTC_USD)",
                "USD_BTC = 1 / BTC_USD",

                "BTC_BRL = binance(BTC_BRL)",
                "BRL_BTC = 1 / BTC_BRL",

                // Swap BRL ↔ USD via BTC
                "BRL_USD = BTC_USD / BTC_BRL",
                "USD_BRL = 1 / BRL_USD",

                // DePix (1 DEPIX = 1 BRL)
                "DEPIX_BRL = 1",
                "DEPIX_USD = BRL_USD",
                "USD_DEPIX = USD_BRL",
                "DEPIX_BTC = BRL_BTC",
                "BTC_DEPIX = BTC_BRL"
            ],
            AssetId           = new uint256("02f22f8d9c76ab41661a2729e4752e2c5d1a263012141b86ea98af5472df5189"),
            DisplayName       = DePixPlugin.DePixCryptoCode,
            NBXplorerNetwork  = lbtc,
            CryptoImagePath   = "~/Resources/img/depix.svg",
            DefaultSettings   = BTCPayDefaultSettings.GetDefaultSettings(chainName),
            CoinType          = chainName == ChainName.Mainnet ? new KeyPath("1776'") : new KeyPath("1'"),
            SupportRBF        = true,
            SupportLightning  = false,
            SupportPayJoin    = false,
            VaultSupported    = false,
            ReadonlyWallet    = true
        }.SetDefaultElectrumMapping(chainName);

        services.AddBTCPayNetwork(network)
                .AddTransactionLinkProvider(
                    PaymentTypes.CHAIN.GetPaymentMethodId(DePixPlugin.DePixCryptoCode),
                    new DefaultTransactionLinkProvider(GetLiquidBlockExplorer(chainName)));

        services.AddCurrencyData(new CurrencyData
        {
            Code         = DePixPlugin.DePixCryptoCode,
            Name         = DePixPlugin.DePixCryptoCode,
            Divisibility = 2,
            Symbol       = null,
            Crypto       = true
        });
    }

    private static string GetLiquidBlockExplorer(ChainName chainName)
    {
        return chainName == ChainName.Mainnet
            ? "https://liquid.network/tx/{0}"
            : "https://liquid.network/testnet/tx/{0}";
    }
}

public sealed class P2PInvoicePaymentMethodRestrictor(
    EventAggregator eventAggregator,
    ApplicationDbContextFactory dbContextFactory)
    : IHostedService
{
    private IEventAggregatorSubscription? subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        subscription = eventAggregator.SubscribeAsync<InvoiceEvent>(HandleInvoiceEvent);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        subscription?.Dispose();
        subscription = null;
        return Task.CompletedTask;
    }

    public static bool TryRestrictInvoiceToPixIfP2P(InvoiceEntity invoice)
    {
        var prompts = invoice.GetPaymentPrompts().ToList();
        var pixPrompt = prompts.FirstOrDefault(prompt => prompt.PaymentMethodId == DePixPlugin.PixPmid);
        if (pixPrompt is null || !IsP2PPixPrompt(pixPrompt) ||
            prompts.All(prompt => prompt.PaymentMethodId == DePixPlugin.PixPmid))
        {
            return false;
        }

        invoice.SetPaymentPrompts(new PaymentPromptDictionary([pixPrompt]));
        return true;
    }

    private async Task HandleInvoiceEvent(InvoiceEvent invoiceEvent)
    {
        if (invoiceEvent.Name != InvoiceEvent.Created)
            return;

        await RestrictInvoiceToPixIfP2PAsync(invoiceEvent.Invoice);
    }

    private async Task RestrictInvoiceToPixIfP2PAsync(InvoiceEntity invoice)
    {
        await using var strategyContext = dbContextFactory.CreateContext();
        await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var dbContext = dbContextFactory.CreateContext();
            await using var transaction = await dbContext.Database.BeginTransactionAsync();
            var invoiceData = await dbContext.Invoices
                .FromSqlInterpolated($"""
                                      SELECT *, xmin
                                      FROM "Invoices"
                                      WHERE "Id" = {invoice.Id}
                                      FOR UPDATE
                                      """)
                .SingleOrDefaultAsync();
            if (invoiceData is null)
                return;

            var storedInvoice = invoiceData.GetBlob();
            if (!TryRestrictInvoiceToPixIfP2P(storedInvoice))
                return;

            invoiceData.SetBlob(storedInvoice);

            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "Invoices"
                SET "Blob2" = CAST({invoiceData.Blob2} AS jsonb)
                WHERE "Id" = {invoice.Id}
                """);
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM "AddressInvoices"
                WHERE "InvoiceDataId" = {invoice.Id}
                  AND "PaymentMethodId" <> {DePixPlugin.PixPmid.ToString()}
                """);
            await transaction.CommitAsync();
        });
    }

    private static bool IsP2PPixPrompt(PaymentPrompt pixPrompt)
    {
        var token = pixPrompt.Details?["p2pMode"] ?? pixPrompt.Details?["P2PMode"];
        return token is { Type: JTokenType.Boolean } && token.Value<bool>();
    }
}
