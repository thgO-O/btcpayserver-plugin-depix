using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.Depix.Data;

public class DepixDbContextFactory(IOptions<DatabaseOptions> options)
    : BaseDbContextFactory<DepixDbContext>(options, DepixDbContext.DefaultPluginSchema)
{
    public override DepixDbContext CreateContext(
        Action<NpgsqlDbContextOptionsBuilder> npgsqlOptionsAction = null)
    {
        var builder = new DbContextOptionsBuilder<DepixDbContext>();
        ConfigureBuilder(builder, npgsqlOptionsAction);
        return new DepixDbContext(builder.Options);
    }
}