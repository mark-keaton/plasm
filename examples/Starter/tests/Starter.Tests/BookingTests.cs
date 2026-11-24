using Xunit;
#if (!STARTER_SQLITE)
using Npgsql;
#endif
using Plasm.CSharp;
using Starter.Data;
#if (!STARTER_SQLITE)
using Testcontainers.PostgreSql;
#endif

[assembly: AssemblyFixture(typeof(Database))]

#if STARTER_SQLITE
/// One SQLite file for the run, in a folder of its own, made by
/// BookingData.CreateDatabaseAsync; the folder is deleted when the run ends.
public sealed class Database : IAsyncLifetime
{
    readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("starter-tests-");

    public string ConnectionString { get; private set; } = null!;
    public BookingData Data { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        ConnectionString = $"Data Source={Path.Combine(folder.FullName, "starter.db")}";
        await BookingData.CreateDatabaseAsync(ConnectionString, CancellationToken.None);
        Data = await BookingData.OpenAsync(ConnectionString, TimeProvider.System, CancellationToken.None);
    }

    public ValueTask DisposeAsync()
    {
        folder.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }
}
#else
/// One PostgreSQL for the run, with the tables BookingData.Ddl creates.
public sealed class Database : IAsyncLifetime
{
    readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18").Build();

    public string ConnectionString { get; private set; } = null!;
    public BookingData Data { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await container.StartAsync();
        ConnectionString = container.GetConnectionString();
        await using (var dataSource = NpgsqlDataSource.Create(ConnectionString))
        await using (var create = dataSource.CreateCommand(BookingData.Ddl))
            await create.ExecuteNonQueryAsync();
        Data = await BookingData.OpenAsync(ConnectionString, TimeProvider.System, CancellationToken.None);
    }

    public ValueTask DisposeAsync() => container.DisposeAsync();
}
#endif

/// A clock that says what the test chose.
sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
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

    static EquipmentView Stored(Changed<EquipmentView> outcome) => outcome switch
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
    public Task Every_piece_of_equipment_is_listed_by_name() => Sandboxed(async data =>
    {
        Assert.Empty(await data.AllEquipmentAsync(CancellationToken.None));
        foreach (var name in new[] { "Projector", "Camera", "Easel" })
            await Add(data, name);
        var all = await data.AllEquipmentAsync(CancellationToken.None);
        Assert.Equal(["Camera", "Easel", "Projector"], all.Select(e => e.Name));
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
        var copy = Assert.IsType<Stale>(stale.Value);
        Assert.Equal("Equipment", copy.What);
        Assert.Equal([projector.Id.ToString("D")], copy.Key);
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
    public async Task Out_of_service_since_the_time_the_clock_says()
    {
        var data = await BookingData.OpenAsync(db.ConnectionString, new FixedTime(Nine), CancellationToken.None);
        await data.SandboxAsync(async data =>
        {
            var projector = await Add(data, "Projector");
            var withdrawn = Stored(await data.TakeOutOfServiceAsync(projector.Id, projector.Version, "lamp broken", CancellationToken.None));
            Assert.Equal(Nine, Assert.IsType<Withdrawn>(withdrawn.Condition.Value).Since);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    // Times from outside (a request's, a clock's) come with an offset; the
    // facade stores them as the instants they are, in UTC.
    static readonly TimeSpan Paris = TimeSpan.FromHours(1);

    [Fact]
    public Task A_booking_given_with_an_offset_is_stored_as_the_same_instants_in_UTC() => Sandboxed(async data =>
    {
        var projector = await Add(data, "Projector");
        Stored(await data.BookAsync(projector.Id, projector.Version, "Ada", Nine.ToOffset(Paris), Nine.AddHours(2).ToOffset(Paris), CancellationToken.None));
        var read = await data.EquipmentAsync(projector.Id, CancellationToken.None) switch
        {
            EquipmentView e => e,
            Missing m => throw new Exception(m.What),
        };
        var booking = Assert.Single(read.Bookings);
        Assert.Equal((Nine, Nine.AddHours(2)), (booking.From, booking.Until));
        Assert.Equal((TimeSpan.Zero, TimeSpan.Zero), (booking.From.Offset, booking.Until.Offset));
    });

    [Fact]
    public Task Time_given_with_an_offset_that_someone_else_has_in_UTC_is_refused() => Sandboxed(async data =>
    {
        var projector = await Add(data, "Projector");
        // 09:00 to 10:00 UTC, and 10:30 to 11:30 at +01:00, which is 09:30 to
        // 10:30 UTC: they overlap as instants, not by the clock's reading.
        var booked = Stored(await data.BookAsync(projector.Id, projector.Version, "Ada", Nine, Nine.AddHours(1), CancellationToken.None));
        var grace = new DateTimeOffset(2026, 11, 20, 10, 30, 0, Paris);
        var refused = Assert.IsType<Refused>((await data.BookAsync(booked.Id, booked.Version, "Grace", grace, grace.AddHours(1), CancellationToken.None)).Value);
        Assert.Equal("AlreadyBooked", refused.Code);
    });

    [Fact]
    public async Task Out_of_service_since_a_clock_that_answers_with_an_offset_is_that_instant_in_UTC()
    {
        var data = await BookingData.OpenAsync(db.ConnectionString, new FixedTime(Nine.ToOffset(Paris)), CancellationToken.None);
        await data.SandboxAsync(async data =>
        {
            var projector = await Add(data, "Projector");
            var withdrawn = Stored(await data.TakeOutOfServiceAsync(projector.Id, projector.Version, "lamp broken", CancellationToken.None));
            var since = Assert.IsType<Withdrawn>(withdrawn.Condition.Value).Since;
            Assert.Equal((Nine, TimeSpan.Zero), (since, since.Offset));
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("   ", "is required")]
    [InlineData("A name far longer than sixty characters, which no label would hold", "must be at most 60 characters")]
    public Task A_name_that_is_not_one_is_invalid_with_its_field(string name, string message) => Sandboxed(async data =>
    {
        var invalid = Assert.IsType<Invalid>((await data.AddEquipmentAsync(name, CancellationToken.None)).Value);
        var problem = Assert.Single(invalid.Problems);
        Assert.Equal(("name", message), (problem.Field, problem.Message));
    });

    [Fact]
    public Task A_name_is_read_back_as_it_was_made() => Sandboxed(async data =>
    {
        var projector = await Add(data, "  Projector  ");
        var read = Assert.IsType<EquipmentView>((await data.EquipmentAsync(projector.Id, CancellationToken.None)).Value);
        Assert.Equal("Projector", read.Name);
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
