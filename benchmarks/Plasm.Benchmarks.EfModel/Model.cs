// EF Core mirror of tests/Plasm.TestDomain schema (existing tables, no migrations).
// EF needs mutable classes with settable navigation collections.
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Plasm.Benchmarks.EfModel;

public class Customer { public long Id { get; set; } public string Name { get; set; } public string Email { get; set; } public List<Order> Orders { get; set; } = new(); }
public class Product { public long Id { get; set; } public string Sku { get; set; } public string Name { get; set; } }
public class Order
{
    public long Id { get; set; }
    public long? CustomerId { get; set; }
    public Customer Customer { get; set; }
    public string Status { get; set; }
    public decimal Total { get; set; }
    public string CancellationReason { get; set; }
    public DateTime PlacedAt { get; set; }
    public int Version { get; set; }
    public List<OrderLine> Lines { get; set; } = new();
    public List<Payment> Payments { get; set; } = new();
}
public class OrderLine
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public long ProductId { get; set; }
    public Product Product { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}
public class Payment
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// Captures every executed command (text + parameters) so rows can be counted afterwards.
public sealed record CapturedParameter(string Name, object Value);
public sealed record CapturedCommand(string Sql, List<CapturedParameter> Parameters);

/// Per-reader duration (from execution until the reader is disposed, i.e. including
/// row reads) and rows read, as reported by EF Core.
public sealed class TimingInterceptor : DbCommandInterceptor
{
    public readonly List<(TimeSpan Duration, int Rows)> Readers = new();
    public List<TimeSpan> Durations => Readers.Select(r => r.Duration).ToList();
    public List<int> RowCounts => Readers.Select(r => r.Rows).ToList();
    public override InterceptionResult DataReaderDisposing(DbCommand c, DataReaderDisposingEventData e, InterceptionResult r) { Readers.Add((e.Duration, e.ReadCount)); return r; }
}

public sealed class CaptureInterceptor : DbCommandInterceptor
{
    public readonly List<CapturedCommand> Commands = new();
    public override DbDataReader ReaderExecuted(DbCommand c, CommandExecutedEventData e, DbDataReader r) { Capture(c); return r; }
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c, CommandExecutedEventData e, DbDataReader r, CancellationToken ct = default) { Capture(c); return ValueTask.FromResult(r); }
    void Capture(DbCommand c)
    {
        var ps = new List<CapturedParameter>();
        foreach (DbParameter p in c.Parameters) ps.Add(new CapturedParameter(p.ParameterName, p.Value));
        Commands.Add(new CapturedCommand(c.CommandText, ps));
    }
}

public class ShopContext : DbContext
{
    readonly string _cs; readonly IInterceptor _capture;
    public ShopContext(string cs, IInterceptor capture = null) { _cs = cs; _capture = capture; }
    public DbSet<Order> Orders => Set<Order>();
    protected override void OnConfiguring(DbContextOptionsBuilder b)
    {
        b.UseNpgsql(_cs);
        if (_capture != null) b.AddInterceptors(_capture);
    }
    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Customer>(e => { e.ToTable("customers"); e.Property(x => x.Id).HasColumnName("id"); e.Property(x => x.Name).HasColumnName("name"); e.Property(x => x.Email).HasColumnName("email"); });
        m.Entity<Product>(e => { e.ToTable("products"); e.Property(x => x.Id).HasColumnName("id"); e.Property(x => x.Sku).HasColumnName("sku"); e.Property(x => x.Name).HasColumnName("name"); });
        m.Entity<Order>(e =>
        {
            e.ToTable("orders");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.Total).HasColumnName("total");
            e.Property(x => x.CancellationReason).HasColumnName("cancellation_reason");
            e.Property(x => x.PlacedAt).HasColumnName("placed_at");
            e.Property(x => x.Version).HasColumnName("version");
            e.HasOne(x => x.Customer).WithMany(c => c.Orders).HasForeignKey(x => x.CustomerId);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.OrderId);
            e.HasMany(x => x.Payments).WithOne().HasForeignKey(p => p.OrderId);
        });
        m.Entity<OrderLine>(e =>
        {
            e.ToTable("order_lines");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrderId).HasColumnName("order_id");
            e.Property(x => x.ProductId).HasColumnName("product_id");
            e.Property(x => x.Quantity).HasColumnName("quantity");
            e.Property(x => x.UnitPrice).HasColumnName("unit_price");
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId);
        });
        m.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrderId).HasColumnName("order_id");
            e.Property(x => x.Amount).HasColumnName("amount");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });
    }
}

public static class Queries
{
    /// Page of published orders with their Lines and their successful Payments ordered by creation time.
    public static IQueryable<Order> Page(ShopContext ctx, int take, bool split) =>
        Shape(ctx.Orders.AsNoTracking()
            .Where(o => o.Status == "published")
            .OrderByDescending(o => o.PlacedAt).ThenBy(o => o.Id)
            .Take(take)
            .Include(o => o.Lines)
            .Include(o => o.Payments.Where(p => p.Status == "succeeded").OrderBy(p => p.CreatedAt)), split);

    /// One order with all its lines and all its payments (sibling collections).
    public static IQueryable<Order> Siblings(ShopContext ctx, long orderId, bool split) =>
        Shape(ctx.Orders.AsNoTracking().Where(o => o.Id == orderId)
            .Include(o => o.Lines).Include(o => o.Payments), split);

    static IQueryable<Order> Shape(IQueryable<Order> q, bool split) => split ? q.AsSplitQuery() : q.AsSingleQuery();
}
