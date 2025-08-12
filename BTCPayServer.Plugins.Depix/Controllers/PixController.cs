#nullable enable
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Depix.Data.Models;
using BTCPayServer.Plugins.Depix.Data.Models.ViewModels;
using BTCPayServer.Plugins.Depix.PaymentHandlers;
using BTCPayServer.Plugins.Depix.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StoreData = BTCPayServer.Data.StoreData;

namespace BTCPayServer.Plugins.Depix.Controllers;

[Route("stores/{storeId}/depix")]
[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class PixController(
    StoreRepository storeRepository,
    PaymentMethodHandlerDictionary handlers,
    ISecretProtector protector,
    DepixService depixService)
    : Controller
{
    private StoreData StoreData => HttpContext.GetStoreData();
    
    [HttpGet]
    public async Task<IActionResult> StoreConfig(string storeId)
    {
        var pmid = PixPlugin.PixPmid;
        var cfg = StoreData.GetPaymentMethodConfig<PixPaymentMethodConfig>(pmid, handlers);
        var enabled = await depixService.DePixEnabled(StoreData.Id);

        var webhookUrl = Utils.BuildWebhookUrl(Request, StoreData.Id);
        var secret = cfg?.WebhookSecretHex; 
        
        var hasApiKey = !string.IsNullOrEmpty(cfg?.EncryptedApiKey);
        var masked = string.Empty;
        
        if (hasApiKey)
        {
            var plain = protector.Unprotect(cfg?.EncryptedApiKey);
            if (!string.IsNullOrEmpty(plain))
                masked = "••••••••" + plain[^4..];
        }

        var model = new PixStoreViewModel
        {
            Enabled = enabled,
            ApiKey = hasApiKey ? masked : string.Empty,
            WebhookUrl = webhookUrl,
            WebhookSecretHex = secret ?? "<will be generated on Save>",
            TelegramRegisterCommand = secret is null
                ? $"/registerwebhook deposit {webhookUrl} <SECRET_WILL_BE_GENERATED>"
                : $"/registerwebhook deposit {webhookUrl} {secret}"
        };

        return View(model);
    }
    
    /// <summary>
    /// Api route for setting plugin configuration for this store
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> StoreConfig(
        PixStoreViewModel viewModel, string storeId)
    {
        var store = StoreData;
        var blob  = store.GetStoreBlob();
        var pmid  = PixPlugin.PixPmid;

        var isEnabled = await depixService.DePixEnabled(store.Id);
        blob.SetExcluded(pmid, !isEnabled);

        if (isEnabled)
        {
            var cfg = store.GetPaymentMethodConfig<PixPaymentMethodConfig>(pmid, handlers)
                     ?? new PixPaymentMethodConfig();

            if (!string.IsNullOrWhiteSpace(viewModel.ApiKey))
            {
                cfg.EncryptedApiKey = protector.Protect(viewModel.ApiKey.Trim());
            }

            if (string.IsNullOrEmpty(cfg.WebhookSecretHex))
                cfg.WebhookSecretHex = Utils.GenerateHexSecret32();

            store.SetPaymentMethodConfig(handlers[pmid], cfg);
        }

        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);

        var saved = store.GetPaymentMethodConfig<PixPaymentMethodConfig>(pmid, handlers);
        var webhookUrl = Utils.BuildWebhookUrl(Request, store.Id);

        TempData[WellKnownTempData.SuccessMessage] = "Configuração do DePix aplicada.";

        var model = new PixStoreViewModel
        {
            Enabled = isEnabled,
            ApiKey = string.IsNullOrEmpty(saved?.EncryptedApiKey) ? "" : "••••••••••",
            WebhookUrl = webhookUrl,
            WebhookSecretHex = saved?.WebhookSecretHex,
            TelegramRegisterCommand = $"/registerwebhook deposit {webhookUrl} {saved?.WebhookSecretHex}"
        };

        return View(model);
    }
    
    [HttpGet("~/plugins/depix/{storeId}/transactions")]
    public async Task<IActionResult> PixTransactions(PixTxQueryRequest query, string storeId, CancellationToken ct)
    {
        var model = new PixTransactionsViewModel
        {
            Transactions = await depixService.LoadPixTransactionsAsync(query, ct)
        };

        ViewData["StatusFilter"] = query.Status;
        ViewData["Query"]        = query.SearchTerm;
        ViewData["From"]         = query.From?.ToString("yyyy-MM-dd");
        ViewData["To"]           = query.To?.ToString("yyyy-MM-dd");

        return View("PixTransactions", model);
    }
}