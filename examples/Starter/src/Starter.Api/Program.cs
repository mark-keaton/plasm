// Every endpoint calls BookingData, the F# side, and turns what it returns
// into a response. C# writes no query and composes no transaction.
using System.Text.Json;
using System.Text.Json.Serialization;
using Plasm.CSharp;
using Starter.Data;

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
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, StarterJson.Default);
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
var time = builder.Configuration["ClockStart"] is { } start
    ? new StartingAt(DateTimeOffset.Parse(start, System.Globalization.CultureInfo.InvariantCulture))
    : TimeProvider.System;
// LogSql=true prints each command Plasm sends, its BEGIN and COMMIT included.
var data = bool.TryParse(builder.Configuration["LogSql"], out var logSql) && logSql
    ? await BookingData.OpenAsync(connectionString, sql => Console.WriteLine("SQL: " + sql.Replace('\n', ' ')), time, CancellationToken.None)
    : await BookingData.OpenAsync(connectionString, time, CancellationToken.None);
builder.Services.AddSingleton(data);

var app = builder.Build();

app.MapPost("/equipment", async (NewEquipment input, BookingData d, CancellationToken ct) =>
    await d.AddEquipmentAsync(input.Name, ct) switch
    {
        EquipmentView e => Results.Created($"/equipment/{e.Id}", e),
        Invalid invalid => Results.BadRequest(invalid),
    });

app.MapGet("/equipment/{id:guid}", async (Guid id, BookingData d, CancellationToken ct) =>
    await d.EquipmentAsync(id, ct) switch
    {
        EquipmentView e => Results.Ok(e),
        Missing missing => Results.NotFound(missing),
    });

app.MapPost("/equipment/{id:guid}/bookings", async (Guid id, NewBooking input, BookingData d, CancellationToken ct) =>
    Changed(await d.BookAsync(id, input.Version, input.Who, input.From, input.Until, ct)));

app.MapPost("/equipment/{id:guid}/out-of-service", async (Guid id, OutOfService input, BookingData d, CancellationToken ct) =>
    Changed(await d.TakeOutOfServiceAsync(id, input.Version, input.Reason, ct)));

app.MapPost("/equipment/{id:guid}/in-service", async (Guid id, InService input, BookingData d, CancellationToken ct) =>
    Changed(await d.ReturnToServiceAsync(id, input.Version, ct)));

app.Run();

static IResult Changed(Changed<EquipmentView> outcome) => outcome switch
{
    EquipmentView e => Results.Ok(e),
    Missing missing => Results.NotFound(missing),
    Refused refused => Results.UnprocessableEntity(refused),
    Stale stale => Results.Conflict(stale),
    Invalid invalid => Results.BadRequest(invalid),
};

sealed class StartingAt(DateTimeOffset start) : TimeProvider
{
    readonly long started = System.GetTimestamp();

    // In UTC whatever offset ClockStart was written with, as GetUtcNow says.
    public override DateTimeOffset GetUtcNow() => start.ToUniversalTime() + System.GetElapsedTime(started);
}

record NewEquipment(string Name);
record NewBooking(int Version, string Who, DateTimeOffset From, DateTimeOffset Until);
record OutOfService(int Version, string Reason);
record InService(int Version);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(EquipmentView))]
[JsonSerializable(typeof(Bookable))]
[JsonSerializable(typeof(Withdrawn))]
[JsonSerializable(typeof(Missing))]
[JsonSerializable(typeof(Refused))]
[JsonSerializable(typeof(Stale))]
[JsonSerializable(typeof(Invalid))]
[JsonSerializable(typeof(NewEquipment))]
[JsonSerializable(typeof(NewBooking))]
[JsonSerializable(typeof(OutOfService))]
[JsonSerializable(typeof(InService))]
partial class StarterJson : JsonSerializerContext;
