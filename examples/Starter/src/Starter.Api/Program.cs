// Every endpoint calls BookingData, the F# side, and turns what it returns
// into a response. C# writes no query and composes no transaction.
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
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

var connectionString = builder.Configuration.GetConnectionString("Starter")
    ?? throw new InvalidOperationException("Set the connection string 'Starter' (ConnectionStrings__Starter).");
var dataSource = NpgsqlDataSource.Create(connectionString);
// LogSql=true prints each command Plasm sends, its BEGIN and COMMIT included.
var data = bool.TryParse(builder.Configuration["LogSql"], out var logSql) && logSql
    ? await BookingData.OpenAsync(dataSource, sql => Console.WriteLine("SQL: " + sql.Replace('\n', ' ')), TimeProvider.System, CancellationToken.None)
    : await BookingData.OpenAsync(dataSource, TimeProvider.System, CancellationToken.None);
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

static IResult Changed(ChangeOutcome outcome) => outcome switch
{
    EquipmentView e => Results.Ok(e),
    Missing missing => Results.NotFound(missing),
    Refused refused => Results.UnprocessableEntity(refused),
    Stale stale => Results.Conflict(stale),
};

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
