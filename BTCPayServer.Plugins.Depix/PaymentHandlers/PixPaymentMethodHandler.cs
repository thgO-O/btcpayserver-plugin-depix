using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Altcoins;
using BTCPayServer.Plugins.Depix.Services;
using Newtonsoft.Json.Linq;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;

namespace BTCPayServer.Plugins.Depix.PaymentHandlers;
public class PixPaymentMethodHandler(
    BTCPayNetworkProvider networkProvider,
    DepixService depixService,
    ISecretProtector secretProtector)
    : IPaymentMethodHandler, IHasNetwork
{
    public PaymentMethodId PaymentMethodId => DePixPlugin.PixPmid;
    public BTCPayNetwork Network { get; } = networkProvider.GetNetwork<ElementsBTCPayNetwork>("DePix");

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = "BRL";
        context.Prompt.Divisibility = 2;
        
        var cfgToken = context.Store.GetPaymentMethodConfigs().TryGetValue(PaymentMethodId, out var token) ? token : null;
        var pixCfg = cfgToken is null ? null : ParsePaymentMethodConfig(cfgToken) as PixPaymentMethodConfig;
        var merchantPays = pixCfg?.PassFeeToCustomer != true;
        context.Prompt.PaymentMethodFee = merchantPays ? 0.00m : 1.00m;
        
        return Task.CompletedTask;
    }

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        var store = context.Store;
        if (ParsePaymentMethodConfig(store.GetPaymentMethodConfigs()[PaymentMethodId]) is not PixPaymentMethodConfig pixCfg)
            throw new PaymentMethodUnavailableException("DePix payment method not configured");

        var apiKey = secretProtector.Unprotect(pixCfg.EncryptedApiKey ?? "");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new PaymentMethodUnavailableException("DePix API key not configured");

        var amountInBrl = context.Prompt.Calculate().Due;
        var amountInCents = (int)Math.Round(amountInBrl * 100m, MidpointRounding.AwayFromZero);

        using var client = depixService.CreateDepixClient(apiKey);

        var address = await depixService.GenerateFreshDePixAddress(store.Id);

        var deposit = await depixService.RequestDepositAsync(client, amountInCents, address, pixCfg.UseWhitelist, CancellationToken.None);

        depixService.ApplyPromptDetails(context, deposit, address);
    }
    
    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;
    
    public object ParsePaymentPromptDetails(JToken details)
    {
        return details.ToObject<DePixPaymentMethodDetails>(Serializer);
    }

    public object ParsePaymentMethodConfig(JToken config)
    {
        return config.ToObject<PixPaymentMethodConfig>(Serializer) ??
               throw new FormatException($"Invalid {nameof(PixPaymentMethodHandler)}");
    }

    public object ParsePaymentDetails(JToken details)
    {
        return details.ToObject<DePixPaymentData>(Serializer) ??
               throw new FormatException($"Invalid {nameof(PixPaymentMethodHandler)}");
    }
}

public class DePixPaymentData;

public class DePixPaymentMethodDetails;