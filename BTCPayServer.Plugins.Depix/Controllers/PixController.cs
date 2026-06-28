#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Form;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Forms;
using BTCPayServer.Plugins.Depix.Data.Models;
using BTCPayServer.Plugins.Depix.Data.Models.ViewModels;
using BTCPayServer.Plugins.Depix.PaymentHandlers;
using BTCPayServer.Plugins.Depix.Services;
using BTCPayServer.Plugins.PointOfSale;
using BTCPayServer.Services.Apps;
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
    DepixService depixService,
    FormDataService formDataService,
    AppService appService)
    : Controller
{
    private const string P2PDepixAddressFieldName = "depixAddress";
    private const string EndUserTaxNumberFieldName = "endUserTaxNumber";
    private const string PixIdentificationFormName = "DePix payer identification";
    private const string P2PDefaultFormName = "DePix P2P checkout";
    private const string P2PDefaultPosName = "DePix P2P";
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
            P2PMode = cfg is { P2PMode: true },
            P2PCommissionPercent = cfg?.P2PCommissionPercent,
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
        var p2PCommissionRaw = viewModel.P2PCommissionPercent?.Trim();
        var hasSplitAddress = !string.IsNullOrWhiteSpace(splitAddress);
        var hasSplitFee = !string.IsNullOrWhiteSpace(splitFeeRaw);
        var hasP2PCommission = !string.IsNullOrWhiteSpace(p2PCommissionRaw);

        if (hasSplitAddress ^ hasSplitFee)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Split Fee and DePix Split Address must be provided together.";
            return RedirectToAction(nameof(PixSettings), new { walletId });
        }
        else if (hasSplitFee)
        {
            if (!DepixService.TryNormalizeSplitFee(splitFeeRaw!, out var normalizedSplitFee))
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

        if (hasP2PCommission)
        {
            if (!DepixService.TryNormalizeSplitFee(p2PCommissionRaw!, out var normalizedP2PCommission))
            {
                TempData[WellKnownTempData.ErrorMessage] = "P2P commission must be greater than 0 and less than 100, with up to 2 decimal places.";
                return RedirectToAction(nameof(PixSettings), new { walletId });
            }

            viewModel.P2PCommissionPercent = normalizedP2PCommission;
        }
        else if (viewModel.P2PMode)
        {
            TempData[WellKnownTempData.ErrorMessage] = "P2P commission is required in P2P mode.";
            return RedirectToAction(nameof(PixSettings), new { walletId });
        }
        else
        {
            viewModel.P2PCommissionPercent = null;
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
        cfg.P2PMode = viewModel.P2PMode;
        cfg.P2PCommissionPercent = viewModel.P2PCommissionPercent;
        cfg.DepixSplitAddress = viewModel.DepixSplitAddress;
        cfg.SplitFee = viewModel.SplitFee;
        store.SetPaymentMethodConfig(handlers[pmid], cfg);
        blob.SetExcluded(pmid, !willEnable);
        store.SetStoreBlob(blob);

        await storeRepository.UpdateStore(store);

        var pixFormSetup = willEnable
            ? await EnsurePixIdentificationFormAsync(store.Id)
            : null;
        var p2PPosSetup = viewModel.P2PMode
            ? await EnsureP2PPointOfSaleAsync(store.Id)
            : null;

        if (!string.IsNullOrEmpty(oneShotSecretToDisplay))
            TempData["OneShotSecret"] = oneShotSecretToDisplay;

        var successMessage = "Pix configuration applied.";
        if (pixFormSetup is { FormCreated: true })
            successMessage += " DePix payer identification form was created.";
        else if (pixFormSetup is { FormUpdated: true })
            successMessage += " DePix payer identification form was updated.";

        if (p2PPosSetup is { PosAppCreated: true })
            successMessage += " DePix P2P POS app was created.";
        else if (p2PPosSetup is { PosAppUpdated: true })
            successMessage += " DePix P2P POS app was updated.";
        else if (viewModel.P2PMode)
            successMessage += " DePix P2P POS app is ready.";

        TempData[WellKnownTempData.SuccessMessage] = successMessage;
        return RedirectToAction(nameof(PixSettings), new { storeId = StoreData.Id, walletId });
    }

    private async Task<PixFormSetupResult> EnsurePixIdentificationFormAsync(string storeId)
    {
        var forms = await formDataService.GetForms(storeId);
        var existing = forms.FirstOrDefault(form => string.Equals(form.Name, PixIdentificationFormName, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (FormDataHasRequiredPayerTaxNumberField(existing))
                return new PixFormSetupResult(false, false);

            if (TryParseForm(existing, out var form))
            {
                EnsurePayerTaxNumberField(form);
                existing.Config = form.ToString();
            }
            else
            {
                existing.Config = CreatePixIdentificationForm().ToString();
            }

            await formDataService.AddOrUpdateForm(existing);
            return new PixFormSetupResult(false, true);
        }

        if (forms.Any(FormDataHasRequiredPayerTaxNumberField))
            return new PixFormSetupResult(false, false);

        var formData = new FormData
        {
            StoreId = storeId,
            Name = PixIdentificationFormName,
            Config = CreatePixIdentificationForm().ToString()
        };
        await formDataService.AddOrUpdateForm(formData);
        return new PixFormSetupResult(true, false);
    }

    private async Task<P2PPosSetupResult> EnsureP2PPointOfSaleAsync(string storeId)
    {
        var defaultFormId = await EnsureDefaultP2PFormAsync(storeId);
        var posApps = (await appService.GetApps(PointOfSaleAppType.AppType))
            .Where(app => app.StoreDataId == storeId && !app.Archived)
            .ToList();

        if (posApps.Any(app => string.Equals(app.GetSettings<PointOfSaleSettings>().FormId, defaultFormId, StringComparison.Ordinal)))
            return new P2PPosSetupResult(defaultFormId, false, false);

        var existingP2PPos = posApps.FirstOrDefault(app => string.Equals(app.Name, P2PDefaultPosName, StringComparison.Ordinal));
        if (existingP2PPos is not null)
        {
            var settings = existingP2PPos.GetSettings<PointOfSaleSettings>();
            settings.FormId = defaultFormId;
            existingP2PPos.SetSettings(settings);
            await appService.UpdateOrCreateApp(existingP2PPos);
            return new P2PPosSetupResult(defaultFormId, false, true);
        }

        var p2PPos = new AppData
        {
            StoreDataId = storeId,
            Name = P2PDefaultPosName,
            AppType = PointOfSaleAppType.AppType
        };
        p2PPos.SetSettings(CreateP2PPointOfSaleSettings(defaultFormId));
        await appService.UpdateOrCreateApp(p2PPos);
        return new P2PPosSetupResult(defaultFormId, true, false);
    }

    private static PointOfSaleSettings CreateP2PPointOfSaleSettings(string formId)
    {
        return new PointOfSaleSettings
        {
            Title = P2PDefaultPosName,
            Currency = "BRL",
            Template = AppService.SerializeTemplate(Array.Empty<AppItem>()),
            DefaultView = BTCPayServer.Plugins.PointOfSale.PosViewType.Light,
            ShowItems = false,
            ShowCustomAmount = true,
            ShowDiscount = false,
            ShowSearch = false,
            ShowCategories = false,
            EnableTips = false,
            FormId = formId
        };
    }

    private async Task<string> EnsureDefaultP2PFormAsync(string storeId)
    {
        var forms = await formDataService.GetForms(storeId);
        var existing = forms.FirstOrDefault(form => string.Equals(form.Name, P2PDefaultFormName, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (FormDataHasRequiredP2PFields(existing)) return existing.Id;
            if (TryParseForm(existing, out var form))
            {
                EnsureP2PFields(form);
                existing.Config = form.ToString();
            }
            else
            {
                existing.Config = CreateP2PForm().ToString();
            }
            await formDataService.AddOrUpdateForm(existing);
            return existing.Id;
        }

        var formData = new FormData
        {
            StoreId = storeId,
            Name = P2PDefaultFormName,
            Config = CreateP2PForm().ToString()
        };
        await formDataService.AddOrUpdateForm(formData);
        return formData.Id;
    }

    private static bool FormDataHasRequiredP2PFields(FormData? formData)
    {
        return TryParseForm(formData, out var form) &&
               form.GetFieldByFullName(P2PDepixAddressFieldName) is { Required: true } &&
               form.GetFieldByFullName(EndUserTaxNumberFieldName) is { Required: true };
    }

    private static bool FormDataHasRequiredPayerTaxNumberField(FormData? formData)
    {
        return TryParseForm(formData, out var form) &&
               form.GetFieldByFullName(EndUserTaxNumberFieldName) is { Required: true };
    }

    private static bool TryParseForm(FormData? formData, out Form form)
    {
        form = null!;
        if (formData is null || string.IsNullOrWhiteSpace(formData.Config))
            return false;

        try
        {
            form = Form.Parse(formData.Config);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Form CreateP2PForm()
    {
        var form = new Form();
        EnsureP2PFields(form);
        return form;
    }

    private static Form CreatePixIdentificationForm()
    {
        var form = new Form();
        EnsurePayerTaxNumberField(form);
        return form;
    }

    private static void EnsureP2PFields(Form form)
    {
        EnsureRequiredField(
            form,
            "DePix address",
            P2PDepixAddressFieldName,
            "Buyer Liquid/DePix address that receives the purchased DePix.");
        EnsureRequiredField(
            form,
            "CPF/CNPJ",
            EndUserTaxNumberFieldName,
            "CPF/CNPJ of the Pix payer.");
    }

    private static void EnsurePayerTaxNumberField(Form form)
    {
        EnsureRequiredField(
            form,
            "CPF/CNPJ",
            EndUserTaxNumberFieldName,
            "CPF/CNPJ of the Pix payer. Send as text to preserve leading zeros.");
    }

    private static void EnsureRequiredField(Form form, string label, string name, string helpText)
    {
        var existing = form.GetFieldByFullName(name);
        if (existing is not null)
        {
            existing.Required = true;
            return;
        }

        form.Fields.Add(Field.Create(label, name, null, true, helpText));
    }

    private sealed record PixFormSetupResult(bool FormCreated, bool FormUpdated);
    private sealed record P2PPosSetupResult(string FormId, bool PosAppCreated, bool PosAppUpdated);

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
