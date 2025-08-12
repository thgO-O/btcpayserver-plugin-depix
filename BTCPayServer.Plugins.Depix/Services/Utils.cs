using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace BTCPayServer.Plugins.Depix.Services;

public static class Utils
{
    public static string GenerateHexSecret32()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string BuildWebhookUrl(HttpRequest req, string storeId)
    {
        var baseUrl = $"{req.Scheme}://{req.Host}";
        return $"{baseUrl}/depix/webhooks/deposit/{storeId}";
    }

    public static string ToBasic(string user, string pass)
    {
        var raw = $"{user}:{pass}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }
}