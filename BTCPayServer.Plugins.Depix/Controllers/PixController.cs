#nullable enable
using System;
using System.Globalization;
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

[Route("stores/{storeId}/pix")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
/// <summary>
/// Controller for Pix settings and transactions
/// </summary>
public class PixController(
    StoreRepository storeRepository,
    PaymentMethodHandlerDictionary handlers,
    ISecretProtector protector,
    DepixService depixService)
    : Controller
{
    private StoreData StoreData => HttpContext.GetStoreData();
    
    /// <summary>
    /// Displays the Pix settings for a store
    /// </summary>
    /// <param name="walletId">Optional wallet ID context</param>
    /// <returns>The settings view</returns>
    [HttpGet("settings")]
    public async Task<IActionResult> PixSettings([FromQuery] string? walletId)
    {
        var pmid = DePixPlugin.PixPmid;
        var cfg = StoreData.GetPaymentMethodConfig<PixPaymentMethodConfig>(pmid, handlers);
        var webhookUrl = Utils.BuildWebhookUrl(Request, StoreData.Id);

        var serverCfg = await depixService.GetServerConfigAsync();
        var isServerCfgValid = DepixService.IsConfigValid(serverCfg.EncryptedApiKey, serverCfg.WebhookSecretHashHex);
        var isStoreCfgValid = DepixService.IsConfigValid(cfg?.EncryptedApiKey, cfg?.WebhookSecretHashHex);
        var effectiveCfg = await depixService.GetEffectiveConfigAsync(cfg);
        var effectiveUsesServer = effectiveCfg.Source == DepixService.DepixConfigSource.Server;

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
        else if (isStoreCfgValid)
            secretDisplay = "<stored securely – regenerate to view a new one>";
        else if (effectiveUsesServer)
            secretDisplay = "<using server configuration>";
        else
            secretDisplay = "<not configured>";
        
        var telegramCommand = string.IsNullOrEmpty(oneShotSecret)
            ? null
            : $"/registerwebhook deposit {webhookUrl} {oneShotSecret}";
        
        var model = new PixStoreViewModel
        {
            IsEnabled = cfg is { IsEnabled: true },
            ApiKey = maskedApiKey,
            UseWhitelist = cfg is { UseWhitelist: true },
            PassFeeToCustomer = cfg is { PassFeeToCustomer: true },
            DepixSplitAddress = cfg?.DepixSplitAddress,
            SplitFee = cfg?.SplitFee,
            WebhookUrl = webhookUrl,
            WebhookSecretDisplay = secretDisplay,
            OneShotSecretToDisplay = oneShotSecret,
            RegenerateWebhookSecret = false,
            TelegramRegisterCommand = telegramCommand,
            IsStoreCfgValid = isStoreCfgValid,
            IsServerCfgValid = isServerCfgValid,
            EffectiveUsesServerConfig = effectiveUsesServer
        };
        return View(model);
    }

    /// <summary>
    /// Updates the Pix settings for a store
    /// </summary>
    /// <param name="viewModel">The view model with updated settings</param>
    /// <param name="walletId">Optional wallet ID context</param>
    /// <returns>Redirects to settings view</returns>
    [HttpPost("settings")]
    public async Task<IActionResult> PixSettings(PixStoreViewModel viewModel, [FromQuery] string? walletId)
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
                return RedirectToAction(nameof(PixSettings), new { walletId });
            }
            cfg.EncryptedApiKey = protector.Protect(candidate);
        }

        var splitAddress = viewModel.DepixSplitAddress?.Trim();
        var splitFeeRaw = viewModel.SplitFee?.Trim();
        var hasSplitAddress = !string.IsNullOrWhiteSpace(splitAddress);
        var hasSplitFee = !string.IsNullOrWhiteSpace(splitFeeRaw);
        if (hasSplitAddress ^ hasSplitFee)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Split Fee and DePix Split Address must be provided together.";
            return RedirectToAction(nameof(PixSettings), new { walletId });
        }

        if (hasSplitFee)
        {
            if (!TryNormalizeSplitFee(splitFeeRaw!, out var normalizedSplitFee))
            {
                TempData[WellKnownTempData.ErrorMessage] = "Split Fee must be greater than 0 and less than 100, with up to 2 decimal places.";
                return RedirectToAction(nameof(PixSettings), new { walletId });
            }
            viewModel.SplitFee = normalizedSplitFee;
            viewModel.DepixSplitAddress = splitAddress;
        }
        else
        {
            viewModel.SplitFee = null;
            viewModel.DepixSplitAddress = null;
        }
        
        string? oneShotSecretToDisplay = null;
        var storeHasApiKey = !string.IsNullOrEmpty(cfg.EncryptedApiKey);
        if (storeHasApiKey)
        {
            var needsInitialSecret = string.IsNullOrEmpty(cfg.WebhookSecretHashHex);
            if (needsInitialSecret || viewModel.RegenerateWebhookSecret)
            {
                var newSecret = Utils.GenerateHexSecret32();
                cfg.WebhookSecretHashHex = Utils.ComputeSecretHash(newSecret);
                oneShotSecretToDisplay = newSecret;
            }
        }
        
        var effective = await depixService.GetEffectiveConfigAsync(cfg);
        var effectiveConfigured = effective.Source != DepixService.DepixConfigSource.None;
        var requestedEnable = newApiKey || viewModel.IsEnabled;
        var willEnable = requestedEnable && effectiveConfigured;

        if (requestedEnable && !effectiveConfigured)
            TempData[WellKnownTempData.ErrorMessage] =
                "Cannot enable DePix: neither store nor server configuration is complete (API key + webhook secret).";

        cfg.IsEnabled = willEnable;
        cfg.UseWhitelist = viewModel.UseWhitelist;
        cfg.PassFeeToCustomer = viewModel.PassFeeToCustomer;
        cfg.DepixSplitAddress = viewModel.DepixSplitAddress;
        cfg.SplitFee = viewModel.SplitFee;
        store.SetPaymentMethodConfig(handlers[pmid], cfg);
        blob.SetExcluded(pmid, !willEnable);
        store.SetStoreBlob(blob);

        await storeRepository.UpdateStore(store);

        if (!string.IsNullOrEmpty(oneShotSecretToDisplay))
            TempData["OneShotSecret"] = oneShotSecretToDisplay;

        TempData[WellKnownTempData.SuccessMessage] = "Pix configuration applied";
        return RedirectToAction(nameof(PixSettings), new { storeId = StoreData.Id, walletId });
    }

    private static bool TryNormalizeSplitFee(string raw, out string normalized)
    {
        normalized = "";
        var trimmed = raw.Trim();
        if (trimmed.EndsWith("%", StringComparison.Ordinal))
            trimmed = trimmed[..^1].Trim();
        if (!decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
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
    /// Displays Pix transactions for a store
    /// </summary>
    /// <param name="storeId">The store ID</param>
    /// <param name="query">Filter parameters</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The transactions view</returns>
    [HttpGet("transactions")]
    public async Task<IActionResult> PixTransactions([FromRoute] string storeId, [FromQuery] PixTxQueryRequest query, CancellationToken ct)
    {
        query.StoreId = storeId;
        var depixWalletId = new WalletId(query.StoreId, DePixPlugin.DePixCryptoCode);
        
        var model = new PixTransactionsViewModel
        {
            StoreId = query.StoreId,
            WalletId = depixWalletId.ToString(),
            Transactions = await depixService.LoadPixTransactionsAsync(query, ct),
            ConfigStatus = await depixService.GetPixConfigStatus(query.StoreId)
        };

        ViewData["StatusFilter"] = query.Status;
        ViewData["Query"]        = query.SearchTerm;
        ViewData["From"]         = query.From?.ToString("yyyy-MM-dd");
        ViewData["To"]           = query.To?.ToString("yyyy-MM-dd");

        return View("PixTransactions", model);
    }
}
