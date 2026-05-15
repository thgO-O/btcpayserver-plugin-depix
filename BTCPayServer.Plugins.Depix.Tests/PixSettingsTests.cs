using System.Threading.Tasks;
using System.Linq;
using BTCPayServer.Abstractions.Form;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using BTCPayServer.Forms;
using BTCPayServer.Plugins.PointOfSale;
using BTCPayServer.Services.Apps;
using BTCPayServer.Tests;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.Depix.Tests;

[Collection(SharedPluginTestCollection.CollectionName)]
public class PixSettingsTests : PlaywrightBaseTest
{
    public PixSettingsTests(SharedPluginTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact(Timeout = TestUtils.TestTimeout)]
    [Trait("Category", "PlaywrightUITest")]
    public async Task CanOpenPixSettingsWhenPluginLoaded()
    {
        await InitializeStoreOwnerAsync();
        await GoToPixSettingsAsync();

        await Page.GetByRole(AriaRole.Heading, new() { Name = "Pix Settings" }).WaitForAsync();
        await Page.Locator("#ApiKey").WaitForAsync();
        await Page.GetByText("DePix is not configured.", new() { Exact = false }).WaitForAsync();

        Assert.Contains($"/stores/{Tester.StoreId}/pix/settings", Page.Url);
    }

    [Fact(Timeout = TestUtils.TestTimeout)]
    [Trait("Category", "PlaywrightUITest")]
    public async Task CanSaveAndPersistPixStoreSettings()
    {
        await InitializeStoreOwnerAsync();
        await SeedValidStorePixConfigAsync();
        await GoToPixSettingsAsync();

        await Page.Locator("#IsEnabled").SetCheckedAsync(true);
        await Page.GetByText("Payment options").ClickAsync();
        await Page.Locator("#PassFeeToCustomer").SetCheckedAsync(true);
        await Page.Locator("#UseWhitelist").SetCheckedAsync(true);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        await Tester.FindAlertMessage(partialText: "Pix configuration applied");

        Assert.True(await Page.Locator("#IsEnabled").IsCheckedAsync());
        await Page.GetByText("Payment options").ClickAsync();
        Assert.True(await Page.Locator("#PassFeeToCustomer").IsCheckedAsync());
        Assert.True(await Page.Locator("#UseWhitelist").IsCheckedAsync());
    }

    [Fact(Timeout = TestUtils.TestTimeout)]
    [Trait("Category", "PlaywrightUITest")]
    public async Task CanSaveP2PModeAndCreateIndependentP2PPointOfSale()
    {
        await InitializeStoreOwnerAsync();
        await SeedValidStorePixConfigAsync(
            depixSplitAddress: "saved-normal-split-address",
            splitFee: "2%");
        var appService = Server.PayTester.GetService<AppService>();
        var normalPos = new AppData
        {
            StoreDataId = Tester.StoreId!,
            Name = "Normal POS",
            AppType = PointOfSaleAppType.AppType
        };
        normalPos.SetSettings(new PointOfSaleSettings
        {
            Title = "Normal POS",
            Currency = "BRL"
        });
        await appService.UpdateOrCreateApp(normalPos);

        await GoToPixSettingsAsync();

        await Page.GetByText("P2P mode", new() { Exact = true }).ClickAsync();
        await Page.Locator("#P2PMode").SetCheckedAsync(true);
        await Page.Locator("#P2PCommissionPercent").FillAsync("5");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        await Tester.FindAlertMessage(partialText: "DePix P2P POS app was created");

        var storeConfig = await GetStorePixConfigAsync();
        Assert.NotNull(storeConfig);
        Assert.True(storeConfig!.P2PMode);
        Assert.Equal("5%", storeConfig.P2PCommissionPercent);
        Assert.Equal("2%", storeConfig.SplitFee);
        Assert.Equal("saved-normal-split-address", storeConfig.DepixSplitAddress);

        Assert.Equal(0, await Page.GetByRole(AriaRole.Heading, new() { Name = "Pix", Exact = true }).CountAsync());
        Assert.Equal(0, await Page.GetByRole(AriaRole.Heading, new() { Name = "Normal Pix" }).CountAsync());
        await Page.GetByText("Split", new() { Exact = true }).WaitForAsync();
        await Page.GetByText("P2P mode", new() { Exact = true }).ClickAsync();
        await Page.GetByText("P2P commission (%)").WaitForAsync();
        await Page.GetByText("Split", new() { Exact = true }).ClickAsync();
        await Page.GetByText("DePix address that receives the split portion", new() { Exact = false }).WaitForAsync();
        await Page.GetByText("Percentage of each regular Pix payment", new() { Exact = false }).WaitForAsync();
        Assert.Null(await Page.Locator("#DepixSplitAddress").GetAttributeAsync("readonly"));

        var apps = (await appService.GetApps(PointOfSaleAppType.AppType))
            .Where(app => app.StoreDataId == Tester.StoreId)
            .ToList();
        var persistedNormalPos = Assert.Single(apps, app => app.Name == "Normal POS");
        Assert.True(string.IsNullOrEmpty(persistedNormalPos.GetSettings<PointOfSaleSettings>().FormId));

        var p2PPos = Assert.Single(apps, app => app.Name == "DePix P2P");
        var p2PPosSettings = p2PPos.GetSettings<PointOfSaleSettings>();
        Assert.Equal("BRL", p2PPosSettings.Currency);
        Assert.Equal(PosViewType.Light, p2PPosSettings.DefaultView);
        Assert.True(p2PPosSettings.ShowCustomAmount);
        Assert.False(p2PPosSettings.ShowItems);
        Assert.False(string.IsNullOrEmpty(p2PPosSettings.FormId));

        var formDataService = Server.PayTester.GetService<FormDataService>();
        var formData = await formDataService.GetForm(Tester.StoreId!, p2PPosSettings.FormId);
        Assert.NotNull(formData);
        var form = Form.Parse(formData!.Config);
        var depixAddressField = form.GetFieldByFullName("depixAddress");
        Assert.NotNull(depixAddressField);
        Assert.True(depixAddressField!.Required);
    }

    [Fact(Timeout = TestUtils.TestTimeout)]
    [Trait("Category", "PlaywrightUITest")]
    public async Task CanDisableP2PWithoutNormalSplit()
    {
        await InitializeStoreOwnerAsync();
        await SeedValidStorePixConfigAsync(
            p2PMode: true,
            p2PCommissionPercent: "5.0%");
        await GoToPixSettingsAsync();

        await Page.GetByText("P2P mode", new() { Exact = true }).ClickAsync();
        await Page.Locator("#P2PMode").SetCheckedAsync(false);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        await Tester.FindAlertMessage(partialText: "Pix configuration applied");

        var storeConfig = await GetStorePixConfigAsync();
        Assert.NotNull(storeConfig);
        Assert.False(storeConfig!.P2PMode);
        Assert.Equal("5%", storeConfig.P2PCommissionPercent);
        Assert.Null(storeConfig.SplitFee);
        Assert.Null(storeConfig.DepixSplitAddress);
    }

    [Fact(Timeout = TestUtils.TestTimeout)]
    [Trait("Category", "PlaywrightUITest")]
    public async Task RequiresP2PCommissionWhenP2PEnabled()
    {
        await InitializeStoreOwnerAsync();
        await SeedValidStorePixConfigAsync();
        await GoToPixSettingsAsync();

        await Page.GetByText("P2P mode", new() { Exact = true }).ClickAsync();
        await Page.Locator("#P2PMode").SetCheckedAsync(true);
        await Page.Locator("#P2PCommissionPercent").FillAsync("");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        await Tester.FindAlertMessage(StatusMessageModel.StatusSeverity.Error, "P2P commission is required in P2P mode");

        var storeConfig = await GetStorePixConfigAsync();
        Assert.NotNull(storeConfig);
        Assert.False(storeConfig!.P2PMode);
        Assert.Null(storeConfig.P2PCommissionPercent);
    }

    [Fact(Timeout = TestUtils.TestTimeout)]
    [Trait("Category", "PlaywrightUITest")]
    public async Task RejectsInvalidP2PCommissionValues()
    {
        await InitializeStoreOwnerAsync();
        await SeedValidStorePixConfigAsync();
        await GoToPixSettingsAsync();

        foreach (var invalidCommission in new[] { "0", "100", "5.123", "five" })
        {
            await Page.GetByText("P2P mode", new() { Exact = true }).ClickAsync();
            await Page.Locator("#P2PMode").SetCheckedAsync(true);
            await Page.Locator("#P2PCommissionPercent").FillAsync(invalidCommission);
            await Page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

            await Tester.FindAlertMessage(
                StatusMessageModel.StatusSeverity.Error,
                "P2P commission must be greater than 0 and less than 100, with up to 2 decimal places.");

            var storeConfig = await GetStorePixConfigAsync();
            Assert.NotNull(storeConfig);
            Assert.False(storeConfig!.P2PMode);
            Assert.Null(storeConfig.P2PCommissionPercent);
        }
    }

    [Fact(Timeout = TestUtils.TestTimeout)]
    [Trait("Category", "PlaywrightUITest")]
    public async Task RepairsExistingP2PFormAndUpdatesExistingP2PPointOfSale()
    {
        await InitializeStoreOwnerAsync();
        await SeedValidStorePixConfigAsync();

        var formDataService = Server.PayTester.GetService<FormDataService>();
        var customForm = new Form();
        customForm.Fields.Add(Field.Create(
            "Customer note",
            "customerNote",
            null,
            false,
            "Optional note."));
        var brokenForm = new FormData
        {
            StoreId = Tester.StoreId!,
            Name = "DePix P2P checkout",
            Config = customForm.ToString()
        };
        await formDataService.AddOrUpdateForm(brokenForm);

        var appService = Server.PayTester.GetService<AppService>();
        var p2PPos = new AppData
        {
            StoreDataId = Tester.StoreId!,
            Name = "DePix P2P",
            AppType = PointOfSaleAppType.AppType
        };
        p2PPos.SetSettings(new PointOfSaleSettings
        {
            Title = "DePix P2P",
            Currency = "BRL",
            FormId = "stale-form-id"
        });
        await appService.UpdateOrCreateApp(p2PPos);

        await GoToPixSettingsAsync();

        await Page.GetByText("P2P mode", new() { Exact = true }).ClickAsync();
        await Page.Locator("#P2PMode").SetCheckedAsync(true);
        await Page.Locator("#P2PCommissionPercent").FillAsync("5");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        await Tester.FindAlertMessage(partialText: "DePix P2P POS app was updated");

        var forms = await formDataService.GetForms(Tester.StoreId!);
        var repairedForm = Assert.Single(forms, form => form.Name == "DePix P2P checkout");
        var parsedForm = Form.Parse(repairedForm.Config);
        var depixAddressField = parsedForm.GetFieldByFullName("depixAddress");
        Assert.NotNull(depixAddressField);
        Assert.True(depixAddressField!.Required);
        Assert.NotNull(parsedForm.GetFieldByFullName("customerNote"));

        var apps = (await appService.GetApps(PointOfSaleAppType.AppType))
            .Where(app => app.StoreDataId == Tester.StoreId && app.Name == "DePix P2P")
            .ToList();
        var updatedP2PPos = Assert.Single(apps);
        Assert.Equal(repairedForm.Id, updatedP2PPos.GetSettings<PointOfSaleSettings>().FormId);
    }

    [Fact(Timeout = TestUtils.TestTimeout)]
    [Trait("Category", "PlaywrightUITest")]
    public async Task CanEnablePixUsingServerLevelConfiguration()
    {
        await InitializeStoreOwnerAsync();
        await SeedValidServerPixConfigAsync();
        await GoToPixSettingsAsync();

        await Page.GetByText("Server-level DePix configuration", new() { Exact = false }).WaitForAsync();
        Assert.Equal(0, await Page.Locator("#PassFeeToCustomer").CountAsync());
        Assert.Equal(0, await Page.Locator("#UseWhitelist").CountAsync());

        await Page.Locator("#IsEnabled").SetCheckedAsync(true);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        await Tester.FindAlertMessage(partialText: "Pix configuration applied");
        Assert.True(await Page.Locator("#IsEnabled").IsCheckedAsync());

        var storeConfig = await GetStorePixConfigAsync();
        Assert.NotNull(storeConfig);
        Assert.True(storeConfig!.IsEnabled);
        Assert.Null(storeConfig.EncryptedApiKey);
        Assert.Null(storeConfig.WebhookSecretHashHex);
    }
}
