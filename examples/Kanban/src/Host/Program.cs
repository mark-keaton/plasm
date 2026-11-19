// Kanban's host as an application would write it (docs/kanban-host.md): ASP.NET
// Core with cookie sign-in, REST and GraphQL over the one F# facade,
// KanbanData. Every rule is the domain's; this host reads requests, keeps who
// is signed in, calls the facade with the person signed in as the actor, and
// turns each outcome into a response in one place (Outcomes.cs).
using Kanban.Data;
using Kanban.Host;
using Microsoft.AspNetCore.Authentication.Cookies;
using Npgsql;
using Plasm.HotChocolate;

// Started by a scenario (KANBAN_EXIT_WITH_STDIN=1): exit when the test
// process that holds this host's standard input goes, so no host outlives a
// test run that died.
if (Environment.GetEnvironmentVariable("KANBAN_EXIT_WITH_STDIN") == "1")
    _ = Task.Run(() =>
    {
        Console.In.ReadToEnd();
        Environment.Exit(0);
    });

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Kanban")
    ?? throw new InvalidOperationException("Set the connection string 'Kanban' (ConnectionStrings:Kanban).");

var dataSource = NpgsqlDataSource.Create(connectionString);
// The application's one clock: every change's time, the background job's
// "now" and the wait between its runs (Clock.cs).
var time = Clock.FromEnvironment();
// Opening checks every query, the tables, and every declared card field.
// KANBAN_SQL=1 writes each command's SQL to standard output, one line each.
var kanban = Environment.GetEnvironmentVariable("KANBAN_SQL") == "1"
    ? await KanbanData.OpenAsync(dataSource, sql => Console.WriteLine("SQL: " + sql.Replace('\n', ' ')), time, CancellationToken.None)
    : await KanbanData.OpenAsync(dataSource, time, CancellationToken.None);
builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton(kanban);
builder.Services.AddSingleton(time);
builder.Services.AddHostedService<StaleCards>();

// Nothing null crosses into the facade either: a request whose body leaves
// out a field, or gives it as null, is answered 400 before an endpoint runs.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.RespectNullableAnnotations = true;
    o.SerializerOptions.RespectRequiredConstructorParameters = true;
});

// Who is signed in is kept in a cookie. An API answers 401 and 403 where a
// site would redirect to a sign-in page.
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "kanban";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

builder.Services
    .AddGraphQLServer()
    .AddQueryType<Query>()
    .AddMutationType<Mutation>()
    .AddAuthorization()
    .AddTypeExtension<Boards>()
    .AddTypeExtension<BoardCards>()
    .AddDataLoader<BoardCardsLoader>()
    .AddPlasmFiltering()
    .AddPlasmSorting()
    // Each outcome a union of its cases, registered once.
    .AddPlasmUnion<StageView>()
    .AddPlasmUnion<Found<CardView>>("CardResult")
    .AddPlasmUnion<ChangeOutcome>()
    .AddPlasmUnion<AddCardOutcome>()
    .AddPlasmUnion<CreateBoardOutcome>()
    .AddPlasmUnion<SendAllBackOutcome>();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapAccounts();
app.MapRest();
app.MapGraphQL();
app.Run();

/// Visible to the scenarios, which start this program.
public partial class Program;
