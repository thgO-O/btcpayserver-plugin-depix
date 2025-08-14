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
            Enabled                = enabled,
            ApiKey                 = maskedApiKey,
            WebhookUrl             = webhookUrl,
            WebhookSecretDisplay   = secretDisplay,
            OneShotSecretToDisplay = oneShotSecret,
            RegenerateWebhookSecret = false,
            TelegramRegisterCommand = string.IsNullOrEmpty(oneShotSecret)
                ? null
                : $"/registerwebhook deposit {webhookUrl} {oneShotSecret}"
        };

        return View(model);
    }


   [HttpPost]
    public async Task<IActionResult> StoreConfig(PixStoreViewModel viewModel, string storeId)
    {
        var blob  = StoreData.GetStoreBlob();
        var pmid  = DePixPlugin.PixPmid;

        var isEnabled = await depixService.DePixEnabled(StoreData.Id);
        blob.SetExcluded(pmid, !isEnabled);

        string? oneShotSecretToDisplay = null;

        if (isEnabled)
        {
            var cfg = StoreData.GetPaymentMethodConfig<PixPaymentMethodConfig>(pmid, handlers)
                      ?? new PixPaymentMethodConfig();

            if (!string.IsNullOrWhiteSpace(viewModel.ApiKey) && !viewModel.ApiKey.Contains('•'))
                cfg.EncryptedApiKey = protector.Protect(viewModel.ApiKey.Trim());
            
            if (string.IsNullOrEmpty(cfg.WebhookSecretHashHex) || viewModel.RegenerateWebhookSecret)
            {
                var newSecret = Utils.GenerateHexSecret32();
                cfg.WebhookSecretHashHex = Utils.ComputeSecretHash(newSecret);
                oneShotSecretToDisplay = newSecret;
            }

            StoreData.SetPaymentMethodConfig(handlers[pmid], cfg);
        }

        StoreData.SetStoreBlob(blob);
        await storeRepository.UpdateStore(StoreData);

        if (!string.IsNullOrEmpty(oneShotSecretToDisplay))
            TempData["OneShotSecret"] = oneShotSecretToDisplay;

        TempData[WellKnownTempData.SuccessMessage] = "Depix configuration applied";
        return RedirectToAction(nameof(StoreConfig), new { storeId });
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