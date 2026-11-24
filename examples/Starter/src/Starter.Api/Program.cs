// Every endpoint calls BookingData, the C# class the seam writes over the F#
// one (Seam/BookingData.g.cs), and ToHttp turns its outcome into a response
// (Seam/SeamHttp.g.cs). C# writes no query, no transaction, no view and no
// mapping.
using Starter.Api;

// `ddl`: print the SQL that creates the tables, and stop.
if (args is ["ddl"])
{
    Console.Write(BookingData.Ddl);
    return;
}

var builder = WebApplication.CreateSlimBuilder(args);
// A minimal API binds with these options, not the context's own: both refuse
// a null or missing argument, so none reaches F#.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, SeamJson.Default);
    o.SerializerOptions.RespectNullableAnnotations = true;
    o.SerializerOptions.RespectRequiredConstructorParameters = true;
});

#if STARTER_SQLITE
// Unset, the database is the file starter.db in the working directory.
var connectionString = builder.Configuration.GetConnectionString("Starter") ?? "Data Source=starter.db";
// `create-database`: make the file, with its tables, and stop. A file that is
// there already is refused, and left as it is.
var file = BookingData.DatabaseFile(connectionString);
if (args is ["create-database"])
{
    if (File.Exists(file))
    {
        Console.Error.WriteLine($"create-database: {file} exists; it makes only a new database, and leaves this one as it is.");
        Environment.ExitCode = 1;
        return;
    }
    await BookingData.CreateDatabaseAsync(connectionString, CancellationToken.None);
    Console.WriteLine($"create-database: made {file}, with the tables BookingData.Ddl creates.");
    return;
}
// A missing file is made at start in Development only. Anywhere else it is
// refused: a wrong path would otherwise be a new, empty database answering
// every request.
if (!File.Exists(file))
{
    if (!builder.Environment.IsDevelopment())
    {
        Console.Error.WriteLine($"No database at {file}. Outside Development none is made at start: make it with `dotnet run --project src/Starter.Api -- create-database` (or the published binary's `Starter.Api create-database`), or set ConnectionStrings__Starter to the file that holds it.");
        Environment.ExitCode = 1;
        return;
    }
    await BookingData.CreateDatabaseAsync(connectionString, CancellationToken.None);
    Console.WriteLine($"Development: no database at {file}, so it was made, with the tables BookingData.Ddl creates.");
}
#else
var connectionString = builder.Configuration.GetConnectionString("Starter")
    ?? throw new InvalidOperationException("Set the connection string 'Starter' (ConnectionStrings__Starter).");
#endif
// ClockStart=2030-01-02T09:00:00Z starts the API's clock at that instant, from
// where it runs on at the system's pace: what the rules read as now. Unset,
// the clock is the system's.
if (builder.Configuration["ClockStart"] is { } start)
    builder.Services.AddSingleton<TimeProvider>(new StartingAt(DateTimeOffset.Parse(start, System.Globalization.CultureInfo.InvariantCulture)));
// LogSql=true logs each command Plasm sends, its BEGIN and COMMIT included.
if (bool.TryParse(builder.Configuration["LogSql"], out var logSql) && logSql)
    builder.Logging.AddFilter(typeof(BookingData).FullName, LogLevel.Debug);
// BookingData, opened as the host starts: Plasm checks every query and the
// tables first, so one that would fail stops the API before it serves.
builder.Services.AddBookingData(connectionString);

var app = builder.Build();

// ToHttp: the value 200 (an add 201, at its Location), Missing 404, Invalid
// 400, Refused 422, Stale 409, each with the case as its body.
app.MapPost("/equipment", (NewEquipment input, BookingData d, CancellationToken ct) =>
    d.AddEquipmentAsync(input, ct).ToHttp(e => $"/equipment/{e.Id}"));

app.MapGet("/equipment", async (BookingData d, CancellationToken ct) =>
    Results.Ok(await d.AllEquipmentAsync(ct)));

app.MapGet("/equipment/{id:guid}", (Guid id, BookingData d, CancellationToken ct) =>
    d.EquipmentAsync(id, ct).ToHttp());

app.MapPost("/equipment/{id:guid}/bookings", (Guid id, NewBooking input, BookingData d, CancellationToken ct) =>
    d.BookAsync(id, input, ct).ToHttp());

app.MapPost("/equipment/{id:guid}/out-of-service", (Guid id, OutOfService input, BookingData d, CancellationToken ct) =>
    d.TakeOutOfServiceAsync(id, input, ct).ToHttp());

app.MapPost("/equipment/{id:guid}/in-service", (Guid id, InService input, BookingData d, CancellationToken ct) =>
    d.ReturnToServiceAsync(id, input, ct).ToHttp());

app.Run();

sealed class StartingAt(DateTimeOffset start) : TimeProvider
{
    readonly long started = System.GetTimestamp();

    // In UTC whatever offset ClockStart was written with, as GetUtcNow says.
    public override DateTimeOffset GetUtcNow() => start.ToUniversalTime() + System.GetElapsedTime(started);
}

