using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ApolloBreakdowns.Data;

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<ApolloDbContext>
{
    public ApolloDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ApolloDbContext>()
        .UseSqlServer("Server=localhost;Database=ApolloDesignOnly;Integrated Security=True;TrustServerCertificate=True",
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", ApolloDbContext.Schema)).Options);
}
