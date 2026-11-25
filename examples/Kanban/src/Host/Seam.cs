// What the seam is told (Plasm.CSharp; Seam/ holds what it writes, after a
// build, and seam.lock the contract): the F# type the host calls, and what C#
// leaves out of the domain's records. The requests (NewBoard and the others,
// F# records the methods take) cross by themselves. The C# compiler checks
// every typeof and nameof here.
using Kanban;
using Kanban.Data;
using Plasm.CSharp;

// Boards: Seam/Boards.g.cs, the members of Kanban.Data.Boards over views.
// CardQueries, GraphQL's projection, is not listed: what a request reads is
// chosen when it comes (CardQueries.fs).
[assembly: SeamData(typeof(Boards))]

// This host blocks and unblocks cards and keeps their checklists, so a card's
// view shows its blocker and checklist; it leaves out its board (asked by
// it, or beside it) and when it was created, which no client here reads.
[assembly: SeamView(typeof(Card), Omit = [nameof(Card.Board), nameof(Card.Created)])]
// A comment's id and its card's, a label's id and an account's, which C# never needs.
[assembly: SeamView(typeof(Comment), Omit = [nameof(Comment.Id), nameof(Comment.Card)])]
// (Tag in full: Hot Chocolate has one too.)
[assembly: SeamView(typeof(Kanban.Tag), Omit = [nameof(Kanban.Tag.Id)])]
[assembly: SeamView(typeof(Kanban.Server.Db.Account), Omit = [nameof(Kanban.Server.Db.Account.Id)])]
// A board with its cards is what the host answers for a board.
[assembly: SeamView(typeof(BoardWithCards), Name = "BoardView")]
