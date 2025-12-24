#nullable enable
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Depix.Data.Models;
using BTCPayServer.Plugins.Depix.Data.Models.ViewModels;
using BTCPayServer.Plugins.Depix.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Depix.Controllers;

[Route("server/depix")]
[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class PixServerSettingsController(
    ISettingsRepository settingsRepository,
    ISecretProtector protector,
    DepixService depixService)
    : Controller
{
    private const string OneShotSecretKey = "ServerOneShotSecret";

    [HttpGet("settings")]
    public async Task<IActionResult> PixServerSettings()
    {
        var cfg = await settingsRepository.GetSettingAsync<PixServerConfig>() ?? new PixServerConfig();

        var webhookUrl = Utils.BuildWebhookUrl(Request);

        var maskedApiKey = "";
        if (!string.IsNullOrEmpty(cfg.EncryptedApiKey))
        {
            var plain = protector.Unprotect(cfg.EncryptedApiKey);
            if (!string.IsNullOrEmpty(plain))
                maskedApiKey = "••••••••" + plain[^4..];
        }

        var oneShotSecret = TempData[OneShotSecretKey] as string;

        var apiKeyConfigured = !string.IsNullOrEmpty(cfg.EncryptedApiKey);
        var isServerCfgValid = DepixService.IsConfigValid(cfg.EncryptedApiKey, cfg.WebhookSecretHashHex);

        string secretDisplay;
        if (!string.IsNullOrEmpty(oneShotSecret))
            secretDisplay = "";
        else if (!string.IsNullOrEmpty(cfg.WebhookSecretHashHex))
            secretDisplay = "<stored securely – regenerate to view a new one>";
        else
            secretDisplay = "<will be generated on Save>";

        var model = new PixServerSettingsViewModel
        {
            ApiKey = maskedApiKey,

            WebhookUrl = webhookUrl,
            WebhookSecretDisplay = secretDisplay,

            OneShotSecretToDisplay = oneShotSecret,
            RegenerateWebhookSecret = false,

            TelegramRegisterCommand = string.IsNullOrEmpty(oneShotSecret)
                ? null
                : $"/registerwebhook deposit {webhookUrl} {oneShotSecret}",

            ApiKeyConfigured = apiKeyConfigured,
            IsServerCfgValid = isServerCfgValid,
            UseWhitelist = cfg.UseWhitelist,
            PassFeeToCustomer = cfg.PassFeeToCustomer
        };

        return View(model);
    }

    [HttpPost("settings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PixServerSettings(PixServerSettingsViewModel viewModel)
    {
        var cfg = await settingsRepository.GetSettingAsync<PixServerConfig>() ?? new PixServerConfig();
        cfg.UseWhitelist = viewModel.UseWhitelist;
        cfg.PassFeeToCustomer = viewModel.PassFeeToCustomer;

        var newApiKey = !string.IsNullOrWhiteSpace(viewModel.ApiKey) && !viewModel.ApiKey.Contains('•');
        if (newApiKey)
        {
            var candidate = viewModel.ApiKey!.Trim();

            var validationResult = await depixService.ValidateApiKeyAsync(candidate, HttpContext.RequestAborted);
            if (!validationResult.IsValid)
            {
                TempData[WellKnownTempData.ErrorMessage] = validationResult.Message;
                return RedirectToAction(nameof(PixServerSettings));
            }

            cfg.EncryptedApiKey = protector.Protect(candidate);
        }

        string? oneShotSecretToDisplay = null;
        var serverHasApiKey = !string.IsNullOrEmpty(cfg.EncryptedApiKey);

        if (serverHasApiKey)
        {
            var needsInitialSecret = string.IsNullOrEmpty(cfg.WebhookSecretHashHex);
            if (needsInitialSecret || viewModel.RegenerateWebhookSecret)
            {
                var newSecret = Utils.GenerateHexSecret32();
                cfg.WebhookSecretHashHex = Utils.ComputeSecretHash(newSecret);
                oneShotSecretToDisplay = newSecret;
            }
        }
        else
        {
            TempData[WellKnownTempData.ErrorMessage] = "Set the DePix API key first.";
            return RedirectToAction(nameof(PixServerSettings));
        }

        await settingsRepository.UpdateSetting(cfg);

        if (!string.IsNullOrEmpty(oneShotSecretToDisplay))
            TempData[OneShotSecretKey] = oneShotSecretToDisplay;

        TempData[WellKnownTempData.SuccessMessage] = "DePix server configuration applied";
        return RedirectToAction(nameof(PixServerSettings));
    }
}
