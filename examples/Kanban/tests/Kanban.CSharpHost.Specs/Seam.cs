// The seam for these scenarios' own use (facade-sandbox.feature): the C# a
// test of the data layer calls, Boards and its views, written here rather
// than taken from the host, whose assembly this project does not reference
// (its FSharp.Core is the F# server's). seam.lock is its contract.
using Kanban;
using Kanban.Data;
using Plasm.CSharp;

[assembly: SeamData(typeof(Boards))]
[assembly: SeamView(typeof(Card), Omit = [nameof(Card.Board), nameof(Card.Created), nameof(Card.Blocked), nameof(Card.Checklist)])]
[assembly: SeamView(typeof(Comment), Omit = [nameof(Comment.Id), nameof(Comment.Card)])]
[assembly: SeamView(typeof(Tag), Omit = [nameof(Tag.Id)])]
[assembly: SeamView(typeof(BoardWithCards), Name = "BoardView")]
