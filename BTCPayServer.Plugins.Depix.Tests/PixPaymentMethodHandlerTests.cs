using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Depix.Data.Models;
using BTCPayServer.Plugins.Depix.PaymentHandlers;
using BTCPayServer.Plugins.Depix.Services;
using BTCPayServer.Services.Invoices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.Depix.Tests;

public class PixPaymentMethodHandlerTests
{
    private static readonly PaymentMethodId PixPmid = new("PIX");

    [Fact]
    public void ApplyPromptDetailsPersistsQrAmountValueInCents()
    {
        var handler = new TestPixHandler();
        var invoice = new InvoiceEntity
        {
            Id = "invoice-prompt-details",
            Currency = "BRL",
            Price = 12.34m,
            Status = InvoiceStatus.New
        };
        var context = new PaymentMethodContext(
            new BTCPayServer.Data.StoreData { Id = "store-prompt-details" },
            new StoreBlob(),
            new JObject(),
            handler,
            invoice,
            new InvoiceLogs());
        var service = new DepixService(null!, null!, null!, null!, null!, null!, null!, null!, null!);

        service.ApplyPromptDetails(
            context,
            new DepixDepositResponse("qr-prompt-details", "https://example.invalid/qr.png", "copy-paste"),
            "depix-address",
            1234);

        var details = Assert.IsType<DePixPaymentMethodDetails>(handler.ParsePaymentPromptDetails(context.Prompt.Details));
        Assert.Equal("qr-prompt-details", details.QrId);
        Assert.Equal(1234, details.ValueInCents);
    }

    private sealed class TestPixHandler : IPaymentMethodHandler
    {
        public PaymentMethodId PaymentMethodId => PixPmid;
        public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;

        public Task ConfigurePrompt(PaymentMethodContext context)
        {
            return Task.CompletedTask;
        }

        public Task BeforeFetchingRates(PaymentMethodContext context)
        {
            return Task.CompletedTask;
        }

        public object ParsePaymentPromptDetails(JToken details)
        {
            return details.ToObject<DePixPaymentMethodDetails>(Serializer)!;
        }

        public object ParsePaymentMethodConfig(JToken config)
        {
            return new object();
        }

        public object ParsePaymentDetails(JToken details)
        {
            return details.ToObject<DePixPaymentData>(Serializer)!;
        }
    }
}
