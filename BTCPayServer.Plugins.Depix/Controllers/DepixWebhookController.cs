#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Depix.Data.Models;
using BTCPayServer.Plugins.Depix.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Depix.Controllers;

[ApiController]
[Route("depix/webhooks")]
public class DepixWebhookController(
    StoreRepository stores,
    PaymentMethodHandlerDictionary handlers,
    DepixService depixService)
    : ControllerBase
{
    // Store-scoped webhook: /depix/webhooks/deposit/{storeId}
    [HttpPost("deposit/{storeId}")]
    [AllowAnonymous]
    public async Task<IActionResult> DepositStore([FromRoute] string storeId, [FromBody] DepositWebhookBody body)
    {
        var store = await stores.FindStore(storeId);
        if (store is null) return NotFound();

        var cfg = depixService.GetPixConfig(store, handlers);
        if (cfg is null) return NotFound();
        
        var storedHash = cfg.WebhookSecretHashHex;
        if (!ValidateWebhookSecret(storedHash))
            return Unauthorized();

        _ = Task.Run(() => depixService.ProcessWebhookAsync(storeId, body, CancellationToken.None), CancellationToken.None);
        return Ok();
    }

    // Server-scoped webhook: /depix/webhooks/deposit
    [HttpPost("deposit")]
    [AllowAnonymous]
    public async Task<IActionResult> DepositServer([FromBody] DepositWebhookBody body)
    {
        var server = await depixService.GetServerConfigAsync();
        var storedHash = server.WebhookSecretHashHex;

        if (!ValidateWebhookSecret(storedHash))
            return Unauthorized();

        _ = Task.Run(() => depixService.ProcessWebhookAsync(body, CancellationToken.None), CancellationToken.None);
        return Ok();
    }

    private bool ValidateWebhookSecret(string? storedHash)
    {
        if (string.IsNullOrEmpty(storedHash))
            return false;

        if (!Request.Headers.TryGetValue("Authorization", out var auth) ||
            !auth.ToString().StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var param = auth.ToString()["Basic ".Length..].Trim();
        var candidateSecret = Utils.ExtractSecretFromBasic(param);
        if (candidateSecret is null)
            return false;

        var candidateHash = Utils.ComputeSecretHash(candidateSecret);
        return Utils.FixedEqualsHex(candidateHash, storedHash);
    }
}
