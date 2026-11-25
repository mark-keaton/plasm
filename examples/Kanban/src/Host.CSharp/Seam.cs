// What the seam is told (Plasm.CSharp; Seam/ holds what it writes, after a
// build, and seam.lock the contract): the F# type the host calls, and what C#
// leaves out of a card. The requests (NewBoard and the others, F# records the
// methods take) cross by themselves. The C# compiler checks every typeof and
// nameof here.
using Kanban;
using Kanban.Data;
using Plasm.CSharp;

// Boards: Seam/Boards.g.cs, the members of Kanban.Data.Boards over views.
[assembly: SeamData(typeof(Boards))]

// This host reads a board and adds, starts and renames cards: a card's view
// leaves out its board (the host asked by it), when it was created, and its
// blocker and checklist, which no endpoint here changes.
[assembly: SeamView(typeof(Card), Omit = [nameof(Card.Board), nameof(Card.Created), nameof(Card.Blocked), nameof(Card.Checklist)])]
// A comment's id and its card's, and a label's id, which C# never needs.
[assembly: SeamView(typeof(Comment), Omit = [nameof(Comment.Id), nameof(Comment.Card)])]
[assembly: SeamView(typeof(Tag), Omit = [nameof(Tag.Id)])]
// A board with its cards is what the host answers for a board.
[assembly: SeamView(typeof(BoardWithCards), Name = "BoardView")]
