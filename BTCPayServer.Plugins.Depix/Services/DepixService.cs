#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Depix.Services;

public class DepixService(
    IServiceProvider serviceProvider,
    IServiceScopeFactory scopeFactory,
    StoreRepository storeRepository,
    InvoiceRepository invoiceRepository,
    ILogger<PixPaymentMethodHandler> logger,
    ApplicationDbContextFactory dbFactory,
    EventAggregator events
)
{
    private static readonly PaymentMethodId DePixPmid = new("DEPIX-CHAIN");

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

            var isConfigured = paymentMethods.ContainsKey(DePixPmid);
            var isExcluded = excludeFilters.Match(DePixPmid);
            
            return isConfigured && !isExcluded;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"[DePix] Error checking if {DePixPmid} is enabled for store {storeId}");
            return false;
        }
    }
    
    public async Task<bool> IsPixEnabled(string storeId)
    {
        var store = await storeRepository.FindStore(storeId);
        if (store is null) return false;

        using var scope = scopeFactory.CreateScope();
        var handlers = scope.ServiceProvider.GetRequiredService<PaymentMethodHandlerDictionary>();

        var cfg = store.GetPaymentMethodConfig<PixPaymentMethodConfig>(DePixPlugin.PixPmid, handlers);
        return cfg is not null && cfg.IsEnabled;
    }
    
    public async Task<string> GenerateFreshDePixAddress(string storeId)
    {
        logger.LogInformation("[DePix] Starting address generation for storeId: {StoreId}", storeId);

        var walletProvider = serviceProvider.GetRequiredService<BTCPayWalletProvider>();
        var networkProvider = serviceProvider.GetRequiredService<BTCPayNetworkProvider>();

        var store = await storeRepository.FindStore(storeId);
        if (store == null)
        {
            logger.LogError("[DePix] Store not found: {StoreId}", storeId);
            throw new PixPluginException("Store not found"); 
        }

        var depixNetwork = networkProvider.GetNetwork<ElementsBTCPayNetwork>("DePix");
        if (depixNetwork == null)
        {
            logger.LogError("[DePix] DePix network not configured");
            throw new PixPluginException("DePix asset network not configured");
        }

        var wallet = walletProvider.GetWallet(depixNetwork);
        if (wallet == null)
        {
            logger.LogError("[DePix] Wallet not configured for DePix network");
            throw new PixPaymentException("Depix wallet not configured");
        }

        const string generatedBy = "invoice";
        var handlers = serviceProvider.GetRequiredService<PaymentMethodHandlerDictionary>();

        var derivationSettings = store.GetDerivationSchemeSettings(handlers, "DePix", onlyEnabled: true);
        if (derivationSettings == null)
        {
            logger.LogError("[DePix] Derivation scheme not configured for storeId: {StoreId}", storeId);
            throw new PixPluginException("DePix derivation scheme not configured for this store.");
        }

        logger.LogInformation("[DePix] Reserving address from derivation: {Derivation}", derivationSettings.AccountDerivation.ToString());

        var addressData = await wallet.ReserveAddressAsync(storeId, derivationSettings.AccountDerivation, generatedBy);
        var address = addressData.Address.ToString();

        logger.LogInformation("[DePix] Generated fresh address: {Address}", address);

        return address;
    }
    
    public HttpClient CreateDepixClient(string apiKey)
    {
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri("https://depix.eulen.app/api/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }
    
    public async Task<DepixDepositResponse> RequestDepositAsync(HttpClient client, int amountInCents, string depixAddress, CancellationToken ct)
    {
        var payload = new { amountInCents, depixAddress };
        logger.LogInformation("[DePix] POST /deposit {@Payload}", payload);

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("deposit", content, ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogError("[DePix] Failed to generate QR code. Status: {StatusCode} {Reason}", (int)resp.StatusCode, resp.ReasonPhrase);
            throw new PaymentMethodUnavailableException("Failed to generate Pix QR Code");
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement.GetProperty("response");

        var qrId       = root.GetProperty("id").GetString();
        var qrImageUrl = root.GetProperty("qrImageUrl").GetString();
        var copyPaste  = root.GetProperty("qrCopyPaste").GetString();

        if (string.IsNullOrEmpty(qrId))
            throw new PaymentMethodUnavailableException("DePix response did not include id");

        return new DepixDepositResponse(qrId, qrImageUrl!, copyPaste!);
    }
    
    public void ApplyPromptDetails(PaymentMethodContext context, DepixDepositResponse depositResponse, string depixAddress)
    {
        context.Prompt.Destination = depositResponse.QrImageUrl;

        context.Prompt.Details ??= new JObject();
        var details = (JObject)context.Prompt.Details;

        details["qrId"]         = depositResponse.QrId;
        details["copyPaste"]    = depositResponse.QrCopyPaste;
        details["depixAddress"] = depixAddress;
    }
    
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
        if (!string.IsNullOrWhiteSpace(query.Status))
            where.Add("\"Blob2\"->'prompts'->'PIX'->'details'->>'status' = @status");
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
            status  = query.Status,
            search  = query.SearchTerm,
            fromUtc = query.From?.UtcDateTime,
            toUtc   = query.To?.UtcDateTime
        };

        var rows = await conn.QueryAsync<PixTxResponse>(
            new CommandDefinition(sql, args, cancellationToken: ct));

        var transactions = rows.ToList();
        foreach (var transaction in transactions)
            transaction.DepixStatus = DepixStatusExtensions.TryParse(transaction.DepixStatusRaw, out var st) ? st : null;

        return transactions;
    }
    
    public async Task ProcessWebhookAsync(string storeId, DepositWebhookBody body, CancellationToken cancellationToken)
    {
        try
        {
            var invoiceId = await FindInvoiceIdByQrIdAsync(storeId, body.QrId, cancellationToken);
            if (invoiceId is null)
            {
                logger.LogWarning("Depix webhook: invoice not found for store {StoreId} and qrId {QrId}", storeId, body.QrId);
                return;
            }

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

            var details = pixPrompt.Details as JObject ?? new JObject();
            if (body.BankTxId is not null)       details["bankTxId"]       = body.BankTxId;
            if (body.BlockchainTxId is not null) details["blockchainTxID"] = body.BlockchainTxId;
            if (body.Status is not null)         details["status"]         = body.Status;
            if (body.ValueInCents is not null)   details["valueInCents"]   = body.ValueInCents;
            if (body.Expiration is not null)     details["expiration"]     = body.Expiration;
            if (body.PixKey is not null)         details["pixKey"]         = body.PixKey;

            if (body.PayerName is not null || body.PayerEuid is not null ||
                body.PayerTaxNumber is not null || body.CustomerMessage is not null)
            {
                var payer = (JObject?)details["payer"] ?? new JObject();
                if (body.PayerName is not null)       payer["name"]      = body.PayerName;
                if (body.PayerEuid is not null)       payer["euid"]      = body.PayerEuid;
                if (body.PayerTaxNumber is not null)  payer["taxNumber"] = body.PayerTaxNumber;
                if (body.CustomerMessage is not null) payer["message"]   = body.CustomerMessage;
                details["payer"] = payer;
            }

            await invoiceRepository.UpdatePaymentDetails(invoiceId, pmid, details);

            if (DepixStatusExtensions.TryParse(body.Status, out var depixStatus))
                await ApplyStatusAndNotifyAsync(invoiceId, depixStatus);

            logger.LogInformation("Depix webhook: invoice {InvoiceId} updated for qrId {QrId}", invoiceId, body.QrId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Depix webhook processing failed for store {StoreId}", storeId);
        }
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

        logger.LogInformation("Invoice {InvoiceId} -> {Status} (notify UI)", invoiceId, newState);
    }
    
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

    private async Task<string?> FindInvoiceIdByQrIdAsync(string storeId, string qrId, CancellationToken ct)
    {
        await using var db = dbFactory.CreateContext();
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        const string sql = @"
            SELECT ""Id""
            FROM ""Invoices""
            WHERE ""StoreDataId"" = @storeId
              AND (""Blob2""->'prompts'->'PIX'->'details'->>'qrId') = @qrId
            LIMIT 1;";

        return await conn.QueryFirstOrDefaultAsync<string?>(new CommandDefinition(sql,
            new { storeId, qrId }, cancellationToken: ct));
    }
    
    public void InitDePix(IServiceCollection services)
    {
        using var sp = services.BuildServiceProvider();

        var nbxProvider    = sp.GetRequiredService<NBXplorerNetworkProvider>();
        var selectedChains = sp.GetRequiredService<SelectedChains>();

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
            CryptoCode        = "DePix",
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
            DisplayName       = "DePix",
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
                    PaymentTypes.CHAIN.GetPaymentMethodId("DePix"),
                    new DefaultTransactionLinkProvider(GetLiquidBlockExplorer(chainName)));

        services.AddCurrencyData(new CurrencyData
        {
            Code         = "DePix",
            Name         = "DePix",
            Divisibility = 2,
            Symbol       = null,
            Crypto       = true
        });

        logger.LogInformation("[DePix] Network registered (chain: {Chain})", chainName);
    }

    private static string GetLiquidBlockExplorer(ChainName chainName)
    {
        return chainName == ChainName.Mainnet
            ? "https://liquid.network/tx/{0}"
            : "https://liquid.network/testnet/tx/{0}";
    }
}