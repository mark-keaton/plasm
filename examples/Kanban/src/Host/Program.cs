// Kanban's host as an application would write it (docs/kanban-host.md): ASP.NET
// Core with cookie sign-in, REST and GraphQL over Boards, the C# the seam
// writes over one F# type (Seam/Boards.g.cs), and GraphQL's projection,
// CardQueries. Every rule is the domain's; this host reads requests, keeps who
// is signed in, calls Boards with the person signed in as the actor, and
// answers with ToHttp (Seam/SeamHttp.g.cs).
using Kanban;
using Kanban.Data;
using Plasm.CSharp;
using Kanban.Host;
using Microsoft.AspNetCore.Authentication.Cookies;
using Plasm.HotChocolate;
// The C# Boards the seam writes, not the F# one it calls.
using Boards = Kanban.Host.Boards;

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

// The application's one clock: every change's time, the background job's
// "now" and the wait between its runs (Clock.cs).
var time = Clock.FromEnvironment();
// Opened before the host is built, as GraphQL's projection is made over it:
// opening checks every query and the tables, and CardQueries every declared
// card field. KANBAN_SQL=1 writes each command's SQL to standard output, one
// line each.
var kanban = await Boards.OpenAsync(connectionString, time,
    Environment.GetEnvironmentVariable("KANBAN_SQL") == "1" ? sql => Console.WriteLine("SQL: " + sql.Replace('\n', ' ')) : null,
    CancellationToken.None);
builder.Services.AddSingleton(kanban);
builder.Services.AddSingleton(CardQueries.Over(kanban.Inner));
builder.Services.AddSingleton(time);
builder.Services.AddHostedService<StaleCards>();

// Nothing null crosses into F# either: a request whose body leaves out a
// field, or gives it as null, is answered 400 before an endpoint runs. The
// seam's context reads and writes what crosses.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, SeamJson.Default);
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
    .AddTypeExtension<BoardList>()
    .AddTypeExtension<BoardCards>()
    .AddDataLoader<BoardCardsLoader>()
    .AddDataLoader<BoardCountsLoader>()
    .AddPlasmFiltering()
    .AddPlasmSorting()
    // A card, read with only the fields a request selects, or whole as a
    // change's outcome: one type, the seam's CardView, its fields from
    // CardRead, each domain value made its view by SeamMap.
    .AddPlasmReadType<Card, CardView>(CardRead.all, typeof(SeamMap))
    // Each union the seam writes, and each outcome, a union of its cases,
    // registered once.
    .AddPlasmUnion<StageView>()
    .AddPlasmUnion<ResolutionView>()
    .AddPlasmUnion<FindResult<CardView>>("CardResult")
    .AddPlasmUnion<ChangeResult<CardView>>("ChangeOutcome")
    .AddPlasmUnion<RequestResult<CardView>>("AddCardOutcome")
    .AddPlasmUnion<AddResult<BoardSummaryView>>("CreateBoardOutcome")
    .AddPlasmUnion<FindResult<SentBackView>>("SendAllBackOutcome");

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapAccounts();
app.MapRest();
app.MapGraphQL();
app.Run();

/// Visible to the scenarios, which start this program.
public partial class Program;
