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
    public Task<IActionResult> PixSettings(string storeId)
    {
        var pmid = DePixPlugin.PixPmid;
        var cfg = StoreData.GetPaymentMethodConfig<PixPaymentMethodConfig>(pmid, handlers);
        var webhookUrl = Utils.BuildWebhookUrl(Request, StoreData.Id);

        var maskedApiKey = "";
        if (!string.IsNullOrEmpty(cfg?.EncryptedApiKey))
        {
            var plain = protector.Unprotect(cfg.EncryptedApiKey);
            if (!string.IsNullOrEmpty(plain))
                maskedApiKey = "••••••••" + plain[^4..];
        }

        var oneShotSecret = TempData["OneShotSecret"] as string;

        string secretDisplay;
        if (!string.IsNullOrEmpty(oneShotSecret))
            secretDisplay = "";
        else if (!string.IsNullOrEmpty(cfg?.WebhookSecretHashHex))
            secretDisplay = "<stored securely – regenerate to view a new one>";
        else
            secretDisplay = "<will be generated on Save>";
        
        var model = new PixStoreViewModel
        {
            IsEnabled              = cfg is { IsEnabled: true },
            ApiKey                 = maskedApiKey,
            WebhookUrl             = webhookUrl,
            WebhookSecretDisplay   = secretDisplay,
            OneShotSecretToDisplay = oneShotSecret,
            RegenerateWebhookSecret = false,
            TelegramRegisterCommand = string.IsNullOrEmpty(oneShotSecret)
                ? null
                : $"/registerwebhook deposit {webhookUrl} {oneShotSecret}"
        };

        return Task.FromResult<IActionResult>(View(model));
    }

    [HttpPost]
    public async Task<IActionResult> PixSettings(PixStoreViewModel viewModel, string storeId)
    {
        var pmid  = DePixPlugin.PixPmid;
        var store = StoreData;
        var blob  = store.GetStoreBlob();

        var cfg = store.GetPaymentMethodConfig<PixPaymentMethodConfig>(pmid, handlers)
                  ?? new PixPaymentMethodConfig();

        var newApiKey = !string.IsNullOrWhiteSpace(viewModel.ApiKey) && !viewModel.ApiKey.Contains('•');
        if (newApiKey)
        {
            var candidate = viewModel.ApiKey!.Trim();

            var validationResult = await depixService.ValidateApiKeyAsync(candidate, HttpContext.RequestAborted);
            if (!validationResult.IsValid)
            {
                TempData[WellKnownTempData.ErrorMessage] = validationResult.Message;
                return RedirectToAction(nameof(PixSettings), new { storeId });
            }
            cfg.EncryptedApiKey = protector.Protect(candidate);
        }

        var hasApiKey  = !string.IsNullOrEmpty(cfg.EncryptedApiKey);
        var willEnable = (newApiKey || viewModel.IsEnabled) && hasApiKey;
        
        string? oneShotSecretToDisplay = null;
        var needsInitialSecret = string.IsNullOrEmpty(cfg.WebhookSecretHashHex);
        if (needsInitialSecret || viewModel.RegenerateWebhookSecret)
        {
            var newSecret = Utils.GenerateHexSecret32();               // 32 bytes -> 64 hex chars
            cfg.WebhookSecretHashHex = Utils.ComputeSecretHash(newSecret);
            oneShotSecretToDisplay = newSecret;
        }

        cfg.IsEnabled = willEnable;
        store.SetPaymentMethodConfig(handlers[pmid], cfg);
        blob.SetExcluded(pmid, !willEnable);
        store.SetStoreBlob(blob);

        await storeRepository.UpdateStore(store);

        if (!string.IsNullOrEmpty(oneShotSecretToDisplay))
            TempData["OneShotSecret"] = oneShotSecretToDisplay;

        TempData[WellKnownTempData.SuccessMessage] = "Pix configuration applied";
        return RedirectToAction(nameof(PixSettings), new { storeId });
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