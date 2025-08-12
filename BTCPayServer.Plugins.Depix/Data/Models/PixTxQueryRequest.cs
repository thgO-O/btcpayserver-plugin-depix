#nullable enable
using System;

namespace BTCPayServer.Plugins.Depix.Data.Models;

public sealed class PixTxQueryRequest
{
    public required string StoreId { get; set; }
    public string? Status { get; set; }
    public string? SearchTerm { get; set; }
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
}