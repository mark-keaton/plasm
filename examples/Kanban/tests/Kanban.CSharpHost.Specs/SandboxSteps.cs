using Kanban.Data;
using Kanban.Hosting;
using Npgsql;
using Reqnroll;

namespace Kanban.CSharpHost.Specs;

/// features/csharp/facade-sandbox.feature: the facade itself, with no host.
[Binding]
public sealed class SandboxSteps
{
    NpgsqlDataSource? dataSource;
    KanbanData kanban = null!;
    string connectionString = "";
    readonly List<string> inside = new();

    [AfterScenario]
    public async Task Close()
    {
        if (dataSource is not null) await dataSource.DisposeAsync();
        if (connectionString != "") await Databases.Drop(connectionString);
    }

    static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what}: expected\n  {expected}\nbut was\n  {actual}");
    }

    [Given("KanbanData is opened over a migrated database")]
    public async Task Open()
    {
        connectionString = await Databases.Migrated();
        dataSource = NpgsqlDataSource.Create(connectionString);
        kanban = await KanbanData.OpenAsync(dataSource, CancellationToken.None);
    }

    static string Cards(Found<BoardView> found) => found switch
    {
        BoardView board => string.Join(", ", board.Cards.Select(c => c.Title)),
        Missing missing => "missing: " + missing.What,
    };

    [When("a test in a sandbox creates board {string} and adds the card {string} to it")]
    public async Task CreateAndAdd(string slug, string title) =>
        inside.Add(await kanban.SandboxAsync(async k =>
        {
            Equal(true, await k.CreateBoardAsync(slug, slug, 3, CancellationToken.None) is { Value: BoardSummary }, "created");
            Equal(true, await k.AddCardAsync(slug, title, CancellationToken.None) is { Value: CardView }, "added");
            return Cards(await k.BoardAsync(slug, CancellationToken.None));
        }, CancellationToken.None));

    [Then("inside the sandbox, board {string} had the card {string}")]
    public void HadCard(string slug, string title) => Equal(title, inside.Single(), $"board {slug}'s cards inside the sandbox");

    [Then("after the sandbox, there is no board {string}")]
    public async Task NoBoard(string slug) =>
        Equal($"missing: board {slug}", Cards(await kanban.BoardAsync(slug, CancellationToken.None)), "the board after the sandbox");

    [When("a test in a sandbox creates board {string} allowing {int} card in progress, starts one card and then a second")]
    public async Task Refused(string slug, int limit) =>
        inside.AddRange(await kanban.SandboxAsync(async k =>
        {
            await k.CreateBoardAsync(slug, slug, limit, CancellationToken.None);
            var first = (CardView)(await k.AddCardAsync(slug, "One", CancellationToken.None)).Value;
            var second = (CardView)(await k.AddCardAsync(slug, "Two", CancellationToken.None)).Value;
            await k.StartAsync(first.Id, first.Version, "Ada", CancellationToken.None);
            var refused = await k.StartAsync(second.Id, second.Version, "Bob", CancellationToken.None) switch
            {
                Refused r => r.Code,
                CardView => "started",
                Missing m => "missing " + m.What,
                Stale => "stale",
                Invalid => "invalid",
            };
            var doing = await k.BoardAsync(slug, CancellationToken.None) switch
            {
                BoardView board => board.Cards.Count(c => c.Stage.Value is InProgress).ToString(),
                Missing => "missing",
            };
            return new List<string> { refused, doing };
        }, CancellationToken.None));

    [Then("inside the sandbox, the second start was refused with {string}")]
    public void RefusedWith(string code) => Equal(code, inside[0], "the second start");

    [Then("inside the sandbox, board {string} was read after the refusal with {int} card in progress")]
    public void ReadAfter(string slug, int doing) => Equal(doing.ToString(), inside[1], $"cards in progress on {slug}");
}
