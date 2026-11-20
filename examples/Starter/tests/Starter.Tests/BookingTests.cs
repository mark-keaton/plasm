using Xunit;
using Npgsql;
using Starter.Data;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Database))]

/// One PostgreSQL for the run, with the tables BookingData.Ddl creates.
public sealed class Database : IAsyncLifetime
{
    readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18").Build();

    public BookingData Data { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await container.StartAsync();
        var dataSource = NpgsqlDataSource.Create(container.GetConnectionString());
        await using (var create = dataSource.CreateCommand(BookingData.Ddl))
            await create.ExecuteNonQueryAsync();
        Data = await BookingData.OpenAsync(dataSource, CancellationToken.None);
    }

    public ValueTask DisposeAsync() => container.DisposeAsync();
}

public class BookingTests(Database db)
{
    static readonly DateTimeOffset Nine = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    /// Each test's changes are rolled back when it ends.
    Task Sandboxed(Func<BookingData, Task> test) =>
        db.Data.SandboxAsync(async data => { await test(data); return true; }, TestContext.Current.CancellationToken);

    static async Task<EquipmentView> Add(BookingData data, string name) =>
        await data.AddEquipmentAsync(name, CancellationToken.None) switch
        {
            EquipmentView e => e,
            var other => throw new Exception($"not added: {other.Value}"),
        };

    static EquipmentView Stored(ChangeOutcome outcome) => outcome switch
    {
        EquipmentView e => e,
        var other => throw new Exception($"not changed: {other.Value}"),
    };

    [Fact]
    public Task New_equipment_is_bookable() => Sandboxed(async data =>
    {
        var projector = await Add(data, "Projector");
        Assert.IsType<Bookable>(projector.Condition.Value);
        Assert.Equal(1, projector.Version);
    });

    [Fact]
    public Task A_booking_is_stored_with_the_equipment() => Sandboxed(async data =>
    {
        var projector = await Add(data, "Projector");
        Stored(await data.BookAsync(projector.Id, projector.Version, "Ada", Nine, Nine.AddHours(2), CancellationToken.None));
        var read = await data.EquipmentAsync(projector.Id, CancellationToken.None) switch
        {
            EquipmentView e => e,
            Missing m => throw new Exception(m.What),
        };
        Assert.Equal(["Ada"], read.Bookings.Select(b => b.Who));
        Assert.Equal(2, read.Version);
    });

    [Fact]
    public Task Time_someone_else_has_is_refused() => Sandboxed(async data =>
    {
        var projector = await Add(data, "Projector");
        var booked = Stored(await data.BookAsync(projector.Id, projector.Version, "Ada", Nine, Nine.AddHours(2), CancellationToken.None));
        var refused = Assert.IsType<Refused>((await data.BookAsync(booked.Id, booked.Version, "Grace", Nine.AddHours(1), Nine.AddHours(3), CancellationToken.None)).Value);
        Assert.Equal("AlreadyBooked", refused.Code);
    });

    [Fact]
    public Task A_change_from_a_stale_copy_is_stale() => Sandboxed(async data =>
    {
        var projector = await Add(data, "Projector");
        Stored(await data.BookAsync(projector.Id, projector.Version, "Ada", Nine, Nine.AddHours(2), CancellationToken.None));
        var stale = await data.BookAsync(projector.Id, projector.Version, "Grace", Nine.AddDays(1), Nine.AddDays(1).AddHours(1), CancellationToken.None);
        Assert.Equal(projector.Id, Assert.IsType<Stale>(stale.Value).Equipment);
    });

    [Fact]
    public Task Equipment_out_of_service_says_why_and_cannot_be_booked() => Sandboxed(async data =>
    {
        var projector = await Add(data, "Projector");
        var withdrawn = Stored(await data.TakeOutOfServiceAsync(projector.Id, projector.Version, "lamp broken", CancellationToken.None));
        Assert.Equal("lamp broken", Assert.IsType<Withdrawn>(withdrawn.Condition.Value).Reason);
        var refused = Assert.IsType<Refused>((await data.BookAsync(withdrawn.Id, withdrawn.Version, "Ada", Nine, Nine.AddHours(1), CancellationToken.None)).Value);
        Assert.Equal("NotAvailable", refused.Code);
    });

    [Fact]
    public Task Two_pieces_of_equipment_cannot_share_a_name() => Sandboxed(async data =>
    {
        await Add(data, "Projector");
        var invalid = Assert.IsType<Invalid>((await data.AddEquipmentAsync("Projector", CancellationToken.None)).Value);
        Assert.Equal("name", Assert.Single(invalid.Problems).Field);
    });

    [Fact]
    public async Task A_sandbox_keeps_nothing()
    {
        var id = await db.Data.SandboxAsync(async data => (await Add(data, "Kept nowhere")).Id, TestContext.Current.CancellationToken);
        Assert.IsType<Missing>((await db.Data.EquipmentAsync(id, CancellationToken.None)).Value);
    }
}
