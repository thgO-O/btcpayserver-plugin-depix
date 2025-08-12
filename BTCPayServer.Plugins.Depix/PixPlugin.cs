using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Hosting;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Depix.Data;
using BTCPayServer.Plugins.Depix.PaymentHandlers;
using BTCPayServer.Plugins.Depix.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Depix;
public class PixPlugin : BaseBTCPayServerPlugin
{
    public const string PluginNavKey = nameof(PixPlugin) + "Nav";
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.1.6" },
    ];

    internal static readonly PaymentMethodId PixPmid = new("PIX");
    private const string PixDisplayName = "Pix";

    public override void Execute(IServiceCollection services)
    {
        var plugins = (PluginServiceCollection)services;

        plugins.AddSingleton<DepixService>();
        plugins.AddSingleton<ISecretProtector, SecretProtector>();
        plugins.AddHttpClient();

        plugins.AddSingleton(provider =>
            (IPaymentMethodHandler)ActivatorUtilities.CreateInstance(provider, typeof(PixPaymentMethodHandler)));
        plugins.AddSingleton(provider =>
            (ICheckoutModelExtension)ActivatorUtilities.CreateInstance(provider, typeof(PixCheckoutModelExtension)));

        plugins.AddTransactionLinkProvider(PixPmid, new PixTransactionLinkProvider(PixDisplayName));
        plugins.AddDefaultPrettyName(PixPmid, PixDisplayName);

        plugins.AddUIExtension("store-wallets-nav", "PixStoreNav");
        plugins.AddUIExtension("checkout-payment", "PixCheckout");

        using var sp = plugins.BuildServiceProvider();
        var depixService = sp.GetRequiredService<DepixService>();
        depixService.InitDePix(plugins);
    }
}