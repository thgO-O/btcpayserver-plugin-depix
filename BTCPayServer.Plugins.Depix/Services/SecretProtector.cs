namespace BTCPayServer.Plugins.Depix.Services;

using Microsoft.AspNetCore.DataProtection;

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedPayload);
}

public class SecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _dp = provider.CreateProtector("BTCPay.Depix.ApiKey.v1");

    public string Protect(string plaintext) => _dp.Protect(plaintext ?? "");
    public string Unprotect(string protectedPayload)
    {
        try { return string.IsNullOrEmpty(protectedPayload) ? "" : _dp.Unprotect(protectedPayload); }
        catch { return ""; }
    }
}