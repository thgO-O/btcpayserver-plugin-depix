using System;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Depix.PaymentHandlers;
using BTCPayServer.Rating;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Tests;
using Microsoft.Playwright;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.Depix.Tests;

[Collection(SharedPluginTestCollection.CollectionName)]
public class PixTransactionsTests : PlaywrightBaseTest
{
    private static readonly PaymentMethodId PixPmid = new("PIX");

    public PixTransactionsTests(SharedPluginTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact(Timeout = TestUtils.TestTimeout)]
    [Trait("Category", "PlaywrightUITest")]
    public async Task ShowsP2PCommissionDetailsOnTransactionsPage()
    {
        await InitializeStoreOwnerAsync();
        await SeedValidStorePixConfigAsync(
            isEnabled: true,
            p2PMode: true,
            p2PCommissionPercent: "5%");

        var invoice = await CreateP2PPixInvoiceAsync(
            qrId: "qr-p2p-transactions",
            depixAddress: "buyer-depix-address",
            depixSplitAddress: "fresh-store-commission-address",
            p2PCommissionPercent: "5%");

        await Tester.GoToUrl($"/stores/{Tester.StoreId}/pix/transactions");

        var row = Page.Locator("#DepixTransactions tbody tr").Filter(new() { HasText = invoice.Id });
        await row.GetByText(invoice.Id).WaitForAsync();
        await row.GetByText("qr-p2p-transactions").WaitForAsync();
        await row.GetByText("P2P", new() { Exact = true }).WaitForAsync();
        await row.GetByText("buyer-depix-address").WaitForAsync();
        await row.GetByText("5%").WaitForAsync();
        await row.GetByText("fresh-store-commission-address").WaitForAsync();
    }

    private async Task<InvoiceEntity> CreateP2PPixInvoiceAsync(
        string qrId,
        string depixAddress,
        string depixSplitAddress,
        string p2PCommissionPercent)
    {
        var storeRepository = Server.PayTester.GetService<StoreRepository>();
        var handlers = Server.PayTester.GetService<PaymentMethodHandlerDictionary>();
        var handler = handlers[PixPmid];
        var invoiceRepository = Server.PayTester.InvoiceRepository;
        var store = await storeRepository.FindStore(Tester.StoreId!)
                    ?? throw new InvalidOperationException("Store not found.");

        var invoice = invoiceRepository.CreateNewInvoice(Tester.StoreId!);
        invoice.Currency = "BRL";
        invoice.Price = 12.34m;
        invoice.AddRate(new CurrencyPair("BRL", "BRL"), 1m);
        invoice.SetPaymentPrompt(PixPmid, new PaymentPrompt
        {
            Currency = "BRL",
            Divisibility = 2,
            Destination = "https://example.invalid/pix.png",
            Details = JToken.FromObject(new DePixPaymentMethodDetails
            {
                QrId = qrId,
                DepixAddress = depixAddress,
                P2PMode = true,
                DepixSplitAddress = depixSplitAddress,
                P2PCommissionPercent = p2PCommissionPercent,
                Status = "pending",
                ValueInCents = 1234
            }, handler.Serializer)
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
}
