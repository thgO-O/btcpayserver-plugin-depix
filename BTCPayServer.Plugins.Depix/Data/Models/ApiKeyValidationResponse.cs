#nullable enable
namespace BTCPayServer.Plugins.Depix.Data.Models;

public sealed record ApiKeyValidationResponse(bool IsValid, string Message);
