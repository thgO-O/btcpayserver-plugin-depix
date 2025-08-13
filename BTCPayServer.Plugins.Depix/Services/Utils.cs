#nullable enable
using System;
using System.Linq;
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
    
    public static string ComputeSecretHash(string secretPlain)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(secretPlain ?? ""));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
    
    public static string? ExtractSecretFromBasic(string parameter)
    {
        if (parameter.Length >= 32 && parameter.All(c => !char.IsWhiteSpace(c)))
            return parameter;

        try
        {
            var bytes = Convert.FromBase64String(parameter);
            var decoded = Encoding.UTF8.GetString(bytes);
            var parts = decoded.Split(':', 2);
            if (parts.Length == 2) return parts[1];
        }
        catch { /* ignore */ }

        return null;
    }

    public static bool FixedEqualsHex(string hexA, string hexB)
    {
        if (hexA.Length != hexB.Length) return false;
        try
        {
            var a = Convert.FromHexString(hexA);
            var b = Convert.FromHexString(hexB);
            return CryptographicOperations.FixedTimeEquals(a, b);
        }
        catch { return false; }
    }
}