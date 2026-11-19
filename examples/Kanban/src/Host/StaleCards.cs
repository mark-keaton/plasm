// Sends stale cards back to the backlog, when the host starts and then every
// `Kanban:StaleCardsEvery` (an hour unless configured). Which cards are stale,
// and what sending one back does, are the F# core's
// (KanbanData.SendStaleBackAsync, Workflows.sendStaleBack), on the facade's
// clock; this job says when. The wait between runs is the host's one clock,
// the TimeProvider in its services, which the facade was opened with.
using Kanban.Data;

namespace Kanban.Host;

public sealed class StaleCards(KanbanData kanban, TimeProvider time, IConfiguration configuration, ILogger<StaleCards> log) : BackgroundService
{
    readonly TimeSpan every = configuration.GetValue("Kanban:StaleCardsEvery", TimeSpan.FromHours(1));

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(every, time);
        do
        {
            try
            {
                var sent = await kanban.SendStaleBackAsync(stopping);
                log.LogInformation("Stale cards sent back: {Cards}", sent.Cards);
            }
            catch (KanbanUnavailableException unavailable)
            {
                // The database may come back; the next run tries again.
                log.LogWarning(unavailable, "Sending stale cards back failed; trying again in {Every}", every);
            }
        }
        while (await timer.WaitForNextTickAsync(stopping));
    }
}
