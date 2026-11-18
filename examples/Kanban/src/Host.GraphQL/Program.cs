// Kanban's cards over GraphQL. A request's where and order are read from Hot
// Chocolate's filter and sorting contexts by Plasm.HotChocolate and built on
// Kanban's cards query from the fields Kanban.Data declares (CardFields), so
// the database does both; C# writes no query.
using HotChocolate.Data;
using HotChocolate.Resolvers;
using Kanban;
using Kanban.Data;
using Npgsql;
using Plasm.HotChocolate;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Kanban")
    ?? throw new InvalidOperationException("Set the connection string 'Kanban' (ConnectionStrings:Kanban).");

// KANBAN_SQL=1 writes each command's SQL to standard output, one line each.
Action<string>? onSql = Environment.GetEnvironmentVariable("KANBAN_SQL") == "1"
    ? sql => Console.WriteLine("SQL: " + sql.Replace('\n', ' '))
    : null;
var dataSource = NpgsqlDataSource.Create(connectionString);
// Opening checks every query, the tables, and every declared card field.
var kanban = await KanbanData.OpenAsync(dataSource, onSql!, CancellationToken.None);
builder.Services.AddSingleton(kanban);
builder.Services
    .AddGraphQLServer()
    .AddQueryType<Query>()
    .AddPlasmFiltering()
    .AddPlasmSorting()
    .AddPlasmUnion<StageView>();

var app = builder.Build();
app.MapGraphQL();
app.Run();

public class Query
{
    [UseFiltering(typeof(CardFilter))]
    [UseSorting(typeof(CardSort))]
    public Task<CardView[]> Cards(IResolverContext context, [Service] KanbanData kanban, CancellationToken ct) =>
        kanban.CardsAsync(query => PlasmRequest.Apply(context, CardFields.all, query), ct);
}

/// The filter and sort inputs, generated from Kanban's declared card fields.
public class CardFilter() : PlasmFilterInputType<Card>("CardFilterInput", CardFields.all);
public class CardSort() : PlasmSortInputType<Card>("CardSortInput", CardFields.all);

/// Visible to the scenarios, which start this program.
public partial class Program;
