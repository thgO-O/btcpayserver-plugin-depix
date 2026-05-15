using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Depix.Data.Models;
using BTCPayServer.Plugins.Depix.PaymentHandlers;
using BTCPayServer.Plugins.Depix.Services;
using BTCPayServer.Rating;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Tests;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.Depix.Tests;

[Collection(SharedPluginTestCollection.CollectionName)]
public sealed class DepixWebhookServiceTests : IAsyncLifetime
{
    private static readonly PaymentMethodId PixPmid = new("PIX");
    private readonly UnitTestBase _unitTestBase;
    private ServerTester _server = null!;

    public DepixWebhookServiceTests(SharedPluginTestFixture fixture, ITestOutputHelper output)
    {
        _ = fixture;
        _unitTestBase = new UnitTestBase(output);
    }

    public async Task InitializeAsync()
    {
        _server = _unitTestBase.CreateServerTester(scope: CreateScopePath(), newDb: true);
        await _server.StartAsync();
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    [Fact(Timeout = TestUtils.TestTimeout)]
    public async Task ApprovedWebhookCreatesPaymentAndDepixSentUpdatesExistingPayment()
    {
        var storeId = await CreateStoreAsync();
        var invoice = await CreatePixInvoiceAsync(storeId, qrId: "qr-approved-1", valueInCents: 1234);
        var service = _server.PayTester.GetService<DepixService>();
        var events = _server.PayTester.GetService<EventAggregator>();
        var receivedPaymentEvents = new List<InvoiceEvent>();
        var needUpdateEvents = new List<InvoiceNeedUpdateEvent>();
        var dataChangedEvents = new List<InvoiceDataChangedEvent>();
        using var receivedPaymentSubscription = events.Subscribe<InvoiceEvent>(evt =>
        {
            if (evt.InvoiceId == invoice.Id && evt.Name == InvoiceEvent.ReceivedPayment)
                receivedPaymentEvents.Add(evt);
        });
        using var needUpdateSubscription = events.Subscribe<InvoiceNeedUpdateEvent>(evt =>
        {
            if (evt.InvoiceId == invoice.Id)
                needUpdateEvents.Add(evt);
        });
        using var dataChangedSubscription = events.Subscribe<InvoiceDataChangedEvent>(evt =>
        {
            if (evt.InvoiceId == invoice.Id)
                dataChangedEvents.Add(evt);
        });

        await service.ProcessWebhookAsync(storeId, new DepositWebhookBody
        {
            QrId = "qr-approved-1",
            Status = "approved",
            ValueInCents = 1234,
            PayerName = "Alice"
        }, CancellationToken.None);

        var approvedPayment = await GetPixPaymentAsync(invoice.Id);
        Assert.Equal(PaymentStatus.Settled, approvedPayment.Status);
        var receivedPaymentEvent = Assert.Single(receivedPaymentEvents);
        Assert.Equal(approvedPayment.Id, receivedPaymentEvent.Payment.Id);
        var approvedDetails = GetPixPaymentDetails(approvedPayment);
        Assert.Equal("approved", approvedDetails.Status);
        Assert.Equal("Alice", approvedDetails.PayerName);

        await service.ProcessWebhookAsync(storeId, new DepositWebhookBody
        {
            QrId = "qr-approved-1",
            Status = "depix_sent",
            ValueInCents = 1234,
            BlockchainTxId = "liquid-tx-1"
        }, CancellationToken.None);

        var invoiceAfterDepixSent = await _server.PayTester.InvoiceRepository.GetInvoice(invoice.Id);
        var payments = invoiceAfterDepixSent.GetPayments(false).Where(p => p.PaymentMethodId == PixPmid).ToList();
        var updatedPayment = Assert.Single(payments);
        var updatedDetails = GetPixPaymentDetails(updatedPayment);
        Assert.Equal("depix_sent", updatedDetails.Status);
        Assert.Equal("liquid-tx-1", updatedDetails.BlockchainTxId);
        Assert.Equal("Alice", updatedDetails.PayerName);
        Assert.Equal("depix_sent", (await GetPixPromptDetailsAsync(invoice.Id)).Status);
        Assert.Contains(needUpdateEvents, evt => evt.InvoiceId == invoice.Id);
        Assert.Contains(dataChangedEvents, evt => evt.InvoiceId == invoice.Id);
    }

    [Fact(Timeout = TestUtils.TestTimeout)]
    public async Task ApprovedAfterDepixSentDoesNotDowngradeStoredStatus()
    {
        var storeId = await CreateStoreAsync();
        var invoice = await CreatePixInvoiceAsync(storeId, qrId: "qr-out-of-order", valueInCents: 1234);
        var service = _server.PayTester.GetService<DepixService>();

        await service.ProcessWebhookAsync(storeId, new DepositWebhookBody
        {
            QrId = "qr-out-of-order",
            Status = "depix_sent",
            ValueInCents = 1234,
            BlockchainTxId = "liquid-tx-1"
        }, CancellationToken.None);

        await service.ProcessWebhookAsync(storeId, new DepositWebhookBody
        {
            QrId = "qr-out-of-order",
            Status = "approved",
            ValueInCents = 1234,
            BankTxId = "bank-tx-late",
            PayerName = "Late payer"
        }, CancellationToken.None);

        var invoiceAfterApproved = await _server.PayTester.InvoiceRepository.GetInvoice(invoice.Id);
        var payment = Assert.Single(invoiceAfterApproved.GetPayments(false), p => p.PaymentMethodId == PixPmid);
        var details = GetPixPaymentDetails(payment);
        Assert.Equal("depix_sent", details.Status);
        Assert.Equal("liquid-tx-1", details.BlockchainTxId);
        Assert.Equal("bank-tx-late", details.BankTxId);
        Assert.Equal("Late payer", details.PayerName);

        var promptDetails = await GetPixPromptDetailsAsync(invoice.Id);
        Assert.Equal("depix_sent", promptDetails.Status);
        Assert.Equal("Late payer", promptDetails.Payer?.Name);
    }

    [Fact(Timeout = TestUtils.TestTimeout)]
    public async Task DuplicateWebhookWithMismatchedAmountDoesNotChangeStoredAmount()
    {
        var storeId = await CreateStoreAsync();
        var invoice = await CreatePixInvoiceAsync(storeId, qrId: "qr-amount-mismatch", valueInCents: 1234);
        var service = _server.PayTester.GetService<DepixService>();

        await service.ProcessWebhookAsync(storeId, new DepositWebhookBody
        {
            QrId = "qr-amount-mismatch",
            Status = "approved",
            ValueInCents = 1234,
            PayerName = "Alice"
        }, CancellationToken.None);

        await service.ProcessWebhookAsync(storeId, new DepositWebhookBody
        {
            QrId = "qr-amount-mismatch",
            Status = "depix_sent",
            ValueInCents = 9999,
            BlockchainTxId = "liquid-tx-mismatch"
        }, CancellationToken.None);

        var invoiceAfterMismatch = await _server.PayTester.InvoiceRepository.GetInvoice(invoice.Id);
        var payment = Assert.Single(invoiceAfterMismatch.GetPayments(false), p => p.PaymentMethodId == PixPmid);
        var details = GetPixPaymentDetails(payment);
        Assert.Equal(12.34m, payment.Value);
        Assert.Equal("depix_sent", details.Status);
        Assert.Equal(1234, details.ValueInCents);
        Assert.Equal("liquid-tx-mismatch", details.BlockchainTxId);
        Assert.Equal("Alice", details.PayerName);

        var promptDetails = await GetPixPromptDetailsAsync(invoice.Id);
        Assert.Equal("depix_sent", promptDetails.Status);
        Assert.Equal(1234, promptDetails.ValueInCents);
    }

    [Fact(Timeout = TestUtils.TestTimeout)]
    public async Task CreatedP2PInvoiceIsRestrictedToPixPaymentPrompt()
    {
        var storeId = await CreateStoreAsync();
        var handlers = _server.PayTester.GetService<PaymentMethodHandlerDictionary>();
        var pixHandler = handlers[PixPmid];
        var invoiceRepository = _server.PayTester.InvoiceRepository;
        var storeRepository = _server.PayTester.GetService<StoreRepository>();
        var store = await storeRepository.FindStore(storeId) ?? throw new InvalidOperationException("Store not found.");
        var invoice = invoiceRepository.CreateNewInvoice(storeId);
        invoice.Currency = "BRL";
        invoice.Price = 12.34m;
        invoice.AddRate(new CurrencyPair("BRL", "BRL"), 1m);
        invoice.SetPaymentPrompts(new PaymentPromptDictionary([
            new PaymentPrompt
            {
                PaymentMethodId = PixPmid,
                Currency = "BRL",
                Divisibility = 2,
                Destination = "https://example.invalid/pix.png",
                Details = JToken.FromObject(new DePixPaymentMethodDetails
                {
                    QrId = "qr-p2p-restricted",
                    P2PMode = true,
                    ValueInCents = 1234
                }, pixHandler.Serializer)
            },
            new PaymentPrompt
            {
                PaymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId("BTC"),
                Currency = "BRL",
                Divisibility = 2,
                Destination = "bitcoin-address",
                Details = new JObject()
            }
        ]));

        await invoiceRepository.CreateInvoiceAsync(new InvoiceCreationContext(
            store,
            store.GetStoreBlob(),
            invoice,
            new InvoiceLogs(),
            handlers,
            invoicePaymentMethodFilter: null));

        _server.PayTester.GetService<EventAggregator>().Publish(new InvoiceEvent(invoice, InvoiceEvent.Created));

        var storedInvoice = await invoiceRepository.GetInvoice(invoice.Id);
        var prompt = Assert.Single(storedInvoice.GetPaymentPrompts());
        Assert.Equal(PixPmid, prompt.PaymentMethodId);
    }

    [Fact(Timeout = TestUtils.TestTimeout)]
    public async Task FirstConfirmedWebhookWithMismatchedAmountUsesPromptAmount()
    {
        var storeId = await CreateStoreAsync();
        var invoice = await CreatePixInvoiceAsync(storeId, qrId: "qr-first-amount-mismatch", valueInCents: 1234);
        var service = _server.PayTester.GetService<DepixService>();

        await service.ProcessWebhookAsync(storeId, new DepositWebhookBody
        {
            QrId = "qr-first-amount-mismatch",
            Status = "approved",
            ValueInCents = 9999,
            PayerName = "Alice"
        }, CancellationToken.None);

        var payment = await GetPixPaymentAsync(invoice.Id);
        Assert.Equal(12.34m, payment.Value);
        var details = GetPixPaymentDetails(payment);
        Assert.Equal("approved", details.Status);
        Assert.Null(details.ValueInCents);
        Assert.Equal("Alice", details.PayerName);

        var promptDetails = await GetPixPromptDetailsAsync(invoice.Id);
        Assert.Equal("approved", promptDetails.Status);
        Assert.Equal(1234, promptDetails.ValueInCents);
    }

    [Fact(Timeout = TestUtils.TestTimeout)]
    public async Task ConfigurePromptReturnsUnavailableWhenP2PCommissionAddressCannotBeGenerated()
    {
        var storeId = await CreateStoreAsync();
        var storeRepository = _server.PayTester.GetService<StoreRepository>();
        var handlers = _server.PayTester.GetService<PaymentMethodHandlerDictionary>();
        var handler = handlers[PixPmid];
        var protector = _server.PayTester.GetService<ISecretProtector>();
        var store = await storeRepository.FindStore(storeId) ?? throw new InvalidOperationException("Store not found.");

        store.SetPaymentMethodConfig(handler, new PixPaymentMethodConfig
        {
            EncryptedApiKey = protector.Protect("fixture-api-key"),
            WebhookSecretHashHex = BTCPayServer.Plugins.Depix.Services.Utils.ComputeSecretHash(
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            IsEnabled = true,
            P2PMode = true,
            P2PCommissionPercent = "5%"
        });
        var storeBlob = store.GetStoreBlob();
        storeBlob.SetExcluded(PixPmid, false);
        store.SetStoreBlob(storeBlob);
        await storeRepository.UpdateStore(store);

        var invoice = _server.PayTester.InvoiceRepository.CreateNewInvoice(storeId);
        invoice.Currency = "BRL";
        invoice.Price = 12.34m;
        invoice.AddRate(new CurrencyPair("BRL", "BRL"), 1m);
#pragma warning disable CS0618
        invoice.Payments = [];
#pragma warning restore CS0618
        invoice.UpdateTotals();
        invoice.Metadata = new InvoiceMetadata
        {
            AdditionalData = new Dictionary<string, JToken>
            {
                ["depixAddress"] = "buyer-depix-address"
            }
        };
        var context = new PaymentMethodContext(
            store,
            store.GetStoreBlob(),
            new JObject(),
            handler,
            invoice,
            new InvoiceLogs());
        context.Prompt.ParentEntity = invoice;
        context.Prompt.PaymentMethodId = PixPmid;
        context.Prompt.Currency = "BRL";
        context.Prompt.Divisibility = 2;

        var ex = await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => handler.ConfigurePrompt(context));
        Assert.Contains("DePix", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> CreateStoreAsync()
    {
        var account = _server.NewAccount();
        await account.RegisterAsync(isAdmin: true);
        await account.CreateStoreAsync();
        return account.StoreId;
    }

    private async Task<InvoiceEntity> CreatePixInvoiceAsync(
        string storeId,
        string qrId,
        int valueInCents)
    {
        var storeRepository = _server.PayTester.GetService<StoreRepository>();
        var handlers = _server.PayTester.GetService<PaymentMethodHandlerDictionary>();
        var handler = handlers[PixPmid];
        var invoiceRepository = _server.PayTester.InvoiceRepository;
        var store = await storeRepository.FindStore(storeId) ?? throw new InvalidOperationException("Store not found.");

        var invoice = invoiceRepository.CreateNewInvoice(storeId);
        invoice.Currency = "BRL";
        invoice.Price = valueInCents / 100m;
        invoice.AddRate(new CurrencyPair("BRL", "BRL"), 1m);
        var details = new DePixPaymentMethodDetails
        {
            QrId = qrId,
            Status = "pending",
            ValueInCents = valueInCents
        };

        invoice.SetPaymentPrompt(PixPmid, new PaymentPrompt
        {
            Currency = "BRL",
            Divisibility = 2,
            Destination = "https://example.invalid/pix.png",
            Details = JToken.FromObject(details, handler.Serializer)
        });

        await invoiceRepository.CreateInvoiceAsync(new InvoiceCreationContext(
            store,
            store.GetStoreBlob(),
            invoice,
            new InvoiceLogs(),
            handlers,
            invoicePaymentMethodFilter: null));

        return invoice;
    }

    private async Task<PaymentEntity> GetPixPaymentAsync(string invoiceId)
    {
        var invoice = await _server.PayTester.InvoiceRepository.GetInvoice(invoiceId);
        return Assert.Single(invoice.GetPayments(false), p => p.PaymentMethodId == PixPmid);
    }

    private DePixPaymentData GetPixPaymentDetails(PaymentEntity payment)
    {
        var handler = _server.PayTester.GetService<PaymentMethodHandlerDictionary>()[PixPmid];
        return payment.GetDetails<DePixPaymentData>(handler) ?? throw new InvalidOperationException("Missing Pix payment details.");
    }

    private async Task<DePixPaymentMethodDetails> GetPixPromptDetailsAsync(string invoiceId)
    {
        var invoice = await _server.PayTester.InvoiceRepository.GetInvoice(invoiceId);
        var prompt = invoice.GetPaymentPrompt(PixPmid) ?? throw new InvalidOperationException("Missing Pix prompt.");
        var handler = _server.PayTester.GetService<PaymentMethodHandlerDictionary>()[PixPmid];
        return handler.ParsePaymentPromptDetails(prompt.Details) as DePixPaymentMethodDetails ??
               throw new InvalidOperationException("Missing Pix prompt details.");
    }

    private static string CreateScopePath()
    {
        return Path.Combine(Path.GetTempPath(), "depix-webhook-service", Guid.NewGuid().ToString("N"));
    }
}
