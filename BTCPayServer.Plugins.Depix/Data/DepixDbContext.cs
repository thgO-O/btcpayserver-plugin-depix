using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Depix.Data;

public class DepixDbContext(DbContextOptions<DepixDbContext> options, bool designTime = false)
    : DbContext(options)

{
    public static string DefaultPluginSchema = "BTCPayServer.Plugins.Depix";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(DefaultPluginSchema);
    }
}

