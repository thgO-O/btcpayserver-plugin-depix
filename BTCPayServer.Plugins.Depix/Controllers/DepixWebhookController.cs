#nullable enable
using System;
using System.Text;
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
        var secret = cfg?.WebhookSecretHex;
        if (string.IsNullOrEmpty(secret)) return Unauthorized();

        if (!Request.Headers.TryGetValue("Authorization", out var auth) ||
            !auth.ToString().StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return Unauthorized();

        var token = auth.ToString()["Basic ".Length..].Trim();
        var ok = token.Equals(secret, StringComparison.OrdinalIgnoreCase);
        if (!ok)
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token));
                var parts = decoded.Split(':', 2);
                ok = parts.Length == 2 && parts[1].Equals(secret, StringComparison.OrdinalIgnoreCase);
            }
            catch { /* ignore */ }
        }
        if (!ok) return Unauthorized();

        _ = Task.Run(() => depixService.ProcessWebhookAsync(storeId, body, CancellationToken.None), CancellationToken.None);
        
        return Ok();
    }
}
