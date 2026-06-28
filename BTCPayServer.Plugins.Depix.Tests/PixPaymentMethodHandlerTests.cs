using System.Net;
using System.Reflection;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Depix.Data.Models;
using BTCPayServer.Plugins.Depix.PaymentHandlers;
using BTCPayServer.Plugins.Depix.Services;
using BTCPayServer.Rating;
using BTCPayServer.Services.Invoices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.Depix.Tests;

public class PixPaymentMethodHandlerTests
{
    private static readonly PaymentMethodId PixPmid = new("PIX");
    private const string TestEndUserTaxNumber = "01234567890";

    [Theory]
    [InlineData("0,5", "0.5%")]
    [InlineData("0.5", "0.5%")]
    [InlineData("5%", "5%")]
    [InlineData("5,25%", "5.25%")]
    public void TryNormalizeSplitFeeAcceptsDecimalCommaWithoutThousands(string raw, string expected)
    {
        Assert.True(DepixService.TryNormalizeSplitFee(raw, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("1,000")]
    [InlineData("1.000")]
    [InlineData("1,000.00")]
    [InlineData("1.000,00")]
    [InlineData("5 percent")]
    public void TryNormalizeSplitFeeRejectsThousandsAndFreeText(string raw)
    {
        Assert.False(DepixService.TryNormalizeSplitFee(raw, out _));
    }

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

    [Fact]
    public void ApplyPromptDetailsPersistsP2PCommissionDetails()
    {
        var handler = new TestPixHandler();
        var invoice = new InvoiceEntity
        {
            Id = "invoice-prompt-p2p-details",
            Currency = "BRL",
            Price = 12.34m,
            Status = InvoiceStatus.New
        };
        var context = new PaymentMethodContext(
            new BTCPayServer.Data.StoreData { Id = "store-prompt-p2p-details" },
            new StoreBlob(),
            new JObject(),
            handler,
            invoice,
            new InvoiceLogs());
        var service = new DepixService(null!, null!, null!, null!, null!, null!, null!, null!, null!);

        service.ApplyPromptDetails(
            context,
            new DepixDepositResponse("qr-p2p-details", "https://example.invalid/qr.png", "copy-paste"),
            "buyer-depix-address",
            1234,
            p2PMode: true,
            depixSplitAddress: "fresh-store-commission-address",
            p2PCommissionPercent: "5%");

        var details = Assert.IsType<DePixPaymentMethodDetails>(handler.ParsePaymentPromptDetails(context.Prompt.Details));
        Assert.True(details.P2PMode);
        Assert.Equal("buyer-depix-address", details.DepixAddress);
        Assert.Equal("fresh-store-commission-address", details.DepixSplitAddress);
        Assert.Equal("5%", details.P2PCommissionPercent);
        Assert.Null(details.SplitFee);
    }

    [Fact]
    public void ApplyPromptDetailsClearsP2PCommissionDetailsForNormalPrompt()
    {
        var handler = new TestPixHandler();
        var invoice = new InvoiceEntity
        {
            Id = "invoice-prompt-normal-clears-p2p-details",
            Currency = "BRL",
            Price = 12.34m,
            Status = InvoiceStatus.New
        };
        var context = new PaymentMethodContext(
            new BTCPayServer.Data.StoreData { Id = "store-prompt-normal-clears-p2p-details" },
            new StoreBlob(),
            new JObject(),
            handler,
            invoice,
            new InvoiceLogs());
        var service = new DepixService(null!, null!, null!, null!, null!, null!, null!, null!, null!);

        service.ApplyPromptDetails(
            context,
            new DepixDepositResponse("qr-p2p-details", "https://example.invalid/qr-p2p.png", "copy-paste-p2p"),
            "buyer-depix-address",
            1234,
            p2PMode: true,
            depixSplitAddress: "fresh-store-commission-address",
            p2PCommissionPercent: "5%");

        service.ApplyPromptDetails(
            context,
            new DepixDepositResponse("qr-normal-details", "https://example.invalid/qr-normal.png", "copy-paste-normal"),
            "store-depix-address",
            1234);

        var details = Assert.IsType<DePixPaymentMethodDetails>(handler.ParsePaymentPromptDetails(context.Prompt.Details));
        Assert.Null(details.P2PMode);
        Assert.Null(details.DepixSplitAddress);
        Assert.Null(details.P2PCommissionPercent);
        Assert.Null(details.SplitFee);
        Assert.Equal("store-depix-address", details.DepixAddress);
        Assert.Equal("qr-normal-details", details.QrId);
    }

    [Fact]
    public void RestrictInvoiceToPixIfP2PKeepsOnlyPixPrompt()
    {
        var invoice = new InvoiceEntity
        {
            Id = "invoice-p2p-restrict-payment-methods",
            Currency = "BRL",
            Price = 12.34m,
            Status = InvoiceStatus.New
        };
        invoice.AddRate(new CurrencyPair("BRL", "BRL"), 1m);
        var pixPrompt = new PaymentPrompt
        {
            PaymentMethodId = PixPmid,
            Currency = "BRL",
            Divisibility = 2,
            Destination = "https://example.invalid/pix.png",
            Details = JToken.FromObject(new DePixPaymentMethodDetails
            {
                QrId = "qr-p2p-restrict",
                P2PMode = true
            })
        };
        var bitcoinPrompt = new PaymentPrompt
        {
            PaymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId("BTC"),
            Currency = "BRL",
            Divisibility = 2,
            Destination = "bitcoin-address",
            Details = new JObject()
        };
        invoice.SetPaymentPrompts(new PaymentPromptDictionary([pixPrompt, bitcoinPrompt]));

        Assert.True(P2PInvoicePaymentMethodRestrictor.TryRestrictInvoiceToPixIfP2P(invoice));

        var prompt = Assert.Single(invoice.GetPaymentPrompts());
        Assert.Equal(PixPmid, prompt.PaymentMethodId);
    }

    [Fact]
    public async Task RequestDepositUsesConfiguredSplitWhenNoOverrideIsProvided()
    {
        var capture = new CaptureDepositRequestHandler();
        using var client = new HttpClient(capture) { BaseAddress = new Uri("https://example.invalid/api/") };
        var service = new DepixService(null!, null!, null!, null!, null!, null!, null!, null!, null!);

        await service.RequestDepositAsync(
            client,
            10000,
            "merchant-depix-address",
            new PixPaymentMethodConfig
            {
                DepixSplitAddress = "configured-split-address",
                SplitFee = "3%"
            },
            useWhitelist: true,
            TestEndUserTaxNumber,
            CancellationToken.None);

        var payload = JObject.Parse(capture.Body!);
        Assert.Equal(10000, payload.Value<int>("amountInCents"));
        Assert.Equal("merchant-depix-address", payload.Value<string>("depixAddress"));
        Assert.Equal(TestEndUserTaxNumber, payload.Value<string>("endUserTaxNumber"));
        Assert.False(payload.ContainsKey("endUserFullName"));
        Assert.Equal("configured-split-address", payload.Value<string>("depixSplitAddress"));
        Assert.Equal("3%", payload.Value<string>("splitFee"));
        Assert.True(payload.Value<bool>("whitelist"));
    }

    [Fact]
    public async Task RequestDepositDoesNotSendConfiguredSplitFeeWithoutConfiguredSplitAddress()
    {
        var capture = new CaptureDepositRequestHandler();
        using var client = new HttpClient(capture) { BaseAddress = new Uri("https://example.invalid/api/") };
        var service = new DepixService(null!, null!, null!, null!, null!, null!, null!, null!, null!);

        await service.RequestDepositAsync(
            client,
            10000,
            "merchant-depix-address",
            new PixPaymentMethodConfig
            {
                P2PMode = true,
                P2PCommissionPercent = "5%"
            },
            useWhitelist: false,
            TestEndUserTaxNumber,
            CancellationToken.None);

        var payload = JObject.Parse(capture.Body!);
        Assert.Equal("merchant-depix-address", payload.Value<string>("depixAddress"));
        Assert.False(payload.ContainsKey("depixSplitAddress"));
        Assert.False(payload.ContainsKey("splitFee"));
    }

    [Fact]
    public async Task RequestDepositUsesP2PGeneratedSplitOverride()
    {
        var capture = new CaptureDepositRequestHandler();
        using var client = new HttpClient(capture) { BaseAddress = new Uri("https://example.invalid/api/") };
        var service = new DepixService(null!, null!, null!, null!, null!, null!, null!, null!, null!);

        await service.RequestDepositAsync(
            client,
            10000,
            "buyer-depix-address",
            new PixPaymentMethodConfig
            {
                DepixSplitAddress = "configured-split-address",
                SplitFee = "3%"
            },
            useWhitelist: false,
            TestEndUserTaxNumber,
            CancellationToken.None,
            depixSplitAddressOverride: "fresh-store-commission-address",
            splitFeeOverride: "5%");

        var payload = JObject.Parse(capture.Body!);
        Assert.Equal("buyer-depix-address", payload.Value<string>("depixAddress"));
        Assert.Equal("fresh-store-commission-address", payload.Value<string>("depixSplitAddress"));
        Assert.Equal("5%", payload.Value<string>("splitFee"));
        Assert.False(payload.ContainsKey("whitelist"));
    }

    [Fact]
    public async Task RequestDepositIncludesDepixApiErrorBody()
    {
        var capture = new CaptureDepositRequestHandler(
            HttpStatusCode.BadRequest,
            """{"error":"invalid depixSplitAddress"}""");
        using var client = new HttpClient(capture) { BaseAddress = new Uri("https://example.invalid/api/") };
        var service = new DepixService(null!, null!, null!, null!, null!, null!, null!, null!, null!);

        var ex = await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() =>
            service.RequestDepositAsync(
                client,
                10000,
                "buyer-depix-address",
                new PixPaymentMethodConfig(),
                useWhitelist: false,
                TestEndUserTaxNumber,
                CancellationToken.None));

        Assert.Contains("400", ex.Message);
        Assert.Contains("invalid depixSplitAddress", ex.Message);
    }

    [Fact]
    public void PayerTaxNumberReadsInvoiceMetadata()
    {
        var method = typeof(PixPaymentMethodHandler).GetMethod(
            "ResolvePayerTaxNumber",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        object?[] args =
        [
            new Dictionary<string, JToken>
            {
                ["endUserTaxNumber"] = $" {TestEndUserTaxNumber} "
            },
            null
        ];

        var taxNumber = Assert.IsType<string>(method.Invoke(null, args));
        Assert.Equal(TestEndUserTaxNumber, taxNumber);
    }

    [Fact]
    public void PayerTaxNumberReadsPendingPosFormResponse()
    {
        var method = typeof(PixPaymentMethodHandler).GetMethod(
            "ResolvePayerTaxNumber",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        object?[] args =
        [
            null,
            JObject.Parse($$"""{"endUserTaxNumber":"{{TestEndUserTaxNumber}}"}""")
        ];

        var taxNumber = Assert.IsType<string>(method.Invoke(null, args));
        Assert.Equal(TestEndUserTaxNumber, taxNumber);
    }

    [Fact]
    public void PayerTaxNumberRejectsNumericMetadata()
    {
        var method = typeof(PixPaymentMethodHandler).GetMethod(
            "ResolvePayerTaxNumber",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        object?[] args =
        [
            new Dictionary<string, JToken>
            {
                ["endUserTaxNumber"] = new JValue(99999999999L)
            },
            null
        ];

        var ex = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, args));
        var unavailable = Assert.IsType<PaymentMethodUnavailableException>(ex.InnerException);
        Assert.Contains("string", unavailable.Message);
    }

    [Fact]
    public void PayerTaxNumberIsRequired()
    {
        var method = typeof(PixPaymentMethodHandler).GetMethod(
            "ResolvePayerTaxNumber",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        object?[] args =
        [
            new Dictionary<string, JToken>
            {
                ["buyerName"] = "Alice Buyer"
            },
            null
        ];

        var ex = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, args));
        var unavailable = Assert.IsType<PaymentMethodUnavailableException>(ex.InnerException);
        Assert.Contains("CPF/CNPJ", unavailable.Message);
    }

    [Fact]
    public void P2PDestinationAddressRejectsNonStringMetadataAsUnavailable()
    {
        var method = typeof(PixPaymentMethodHandler).GetMethod(
            "TryGetP2PDestinationAddress",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        object?[] args =
        [
            new Dictionary<string, JToken>
            {
                ["depixAddress"] = new JValue(123)
            },
            null,
            null
        ];

        var ex = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, args));
        var unavailable = Assert.IsType<PaymentMethodUnavailableException>(ex.InnerException);
        Assert.Equal("P2P mode requires a DePix address", unavailable.Message);
    }

    [Fact]
    public void P2PDestinationAddressReadsPendingPosFormResponse()
    {
        var method = typeof(PixPaymentMethodHandler).GetMethod(
            "TryGetP2PDestinationAddress",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        object?[] args =
        [
            null,
            JObject.Parse("""{"depixAddress":" buyer-depix-address "}"""),
            null
        ];

        var found = Assert.IsType<bool>(method.Invoke(null, args));

        Assert.True(found);
        Assert.Equal("buyer-depix-address", args[2]);
    }

    [Fact]
    public void P2PDestinationAddressPrefersInvoiceMetadataOverPendingPosFormResponse()
    {
        var method = typeof(PixPaymentMethodHandler).GetMethod(
            "TryGetP2PDestinationAddress",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        object?[] args =
        [
            new Dictionary<string, JToken>
            {
                ["depixAddress"] = "metadata-depix-address"
            },
            JObject.Parse("""{"depixAddress":"form-depix-address"}"""),
            null
        ];

        var found = Assert.IsType<bool>(method.Invoke(null, args));

        Assert.True(found);
        Assert.Equal("metadata-depix-address", args[2]);
    }

    [Fact]
    public void P2PDestinationAddressRejectsNonStringFormResponseAsUnavailable()
    {
        var method = typeof(PixPaymentMethodHandler).GetMethod(
            "TryGetP2PDestinationAddress",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        object?[] args =
        [
            null,
            JObject.Parse("""{"depixAddress":123}"""),
            null
        ];

        var ex = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, args));
        var unavailable = Assert.IsType<PaymentMethodUnavailableException>(ex.InnerException);
        Assert.Equal("P2P mode requires a DePix address", unavailable.Message);
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

    private sealed class CaptureDepositRequestHandler(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string responseBody = """
                              {
                                "response": {
                                  "id": "qr-id",
                                  "qrImageUrl": "https://example.invalid/qr.png",
                                  "qrCopyPaste": "copy-paste"
                                }
                              }
                              """) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody)
            };
        }
    }
}
