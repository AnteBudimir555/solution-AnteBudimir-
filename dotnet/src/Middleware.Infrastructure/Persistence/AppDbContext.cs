using Microsoft.EntityFrameworkCore;

namespace Middleware.Infrastructure.Persistence;

/// <summary>
/// EF Core context for the application's persisted data. The only persisted aggregate is
/// <see cref="UserAccount"/> (products are always fetched live from the upstream source).
/// The Fluent configuration reproduces the original Hibernate schema exactly: table
/// <c>user_account</c>, an identity primary key, a unique non-null <c>username</c>, a
/// <c>password_hash</c> column and the role persisted by name (string).
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<UserAccount> Users => Set<UserAccount>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var user = modelBuilder.Entity<UserAccount>();
        user.ToTable("user_account");
        user.HasKey(u => u.Id);
        user.Property(u => u.Id).HasColumnName("id").ValueGeneratedOnAdd();
        user.Property(u => u.Username).HasColumnName("username").IsRequired();
        user.HasIndex(u => u.Username).IsUnique();
        user.Property(u => u.PasswordHash).HasColumnName("password_hash").IsRequired();
        user.Property(u => u.Role).HasColumnName("role").HasConversion<string>().IsRequired();
    }
}
