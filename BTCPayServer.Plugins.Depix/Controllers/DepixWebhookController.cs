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
[Route("depix/webhooks/deposit/{storeId}")]
public class DepixWebhookController(
    StoreRepository stores,
    PaymentMethodHandlerDictionary handlers,
    DepixService depixService)
    : ControllerBase
{
    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> Deposit([FromRoute] string storeId, [FromBody] DepositWebhookBody body, CancellationToken ct)
    {
        var store = await stores.FindStore(storeId);
        if (store is null) return NotFound();

        var cfg = depixService.GetPixConfig(store, handlers);

        var storedHash = cfg?.WebhookSecretHashHex ?? null ;

        if (string.IsNullOrEmpty(storedHash)) return Unauthorized();

        if (!Request.Headers.TryGetValue("Authorization", out var auth) ||
            !auth.ToString().StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return Unauthorized();

        var param = auth.ToString()["Basic ".Length..].Trim();
        var candidateSecret = Utils.ExtractSecretFromBasic(param);
        if (candidateSecret is null) return Unauthorized();

        var candidateHash = Utils.ComputeSecretHash(candidateSecret);
        if (!Utils.FixedEqualsHex(candidateHash, storedHash)) return Unauthorized();

        _ = Task.Run(() => depixService.ProcessWebhookAsync(storeId, body, CancellationToken.None), CancellationToken.None);
        return Ok();
    }
}
