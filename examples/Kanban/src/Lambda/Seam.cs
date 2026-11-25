// What the seam is told (Plasm.CSharp; Seam/ holds what it writes, after a
// build, and seam.lock the contract): the F# type the function calls, and what
// C# leaves out of a card. Not a web project: the seam writes no ToHttp and
// no AddBoards here. The C# compiler checks every typeof and nameof here.
using Kanban;
using Kanban.Data;
using Plasm.CSharp;

// Boards: Seam/Boards.g.cs, the members of Kanban.Data.Boards over views.
[assembly: SeamData(typeof(Boards))]

// The function reads a board and adds, starts and renames cards: a card's
// view leaves out its board (the event asked by it), when it was created,
// and its blocker and checklist, which no call here changes.
[assembly: SeamView(typeof(Card), Omit = [nameof(Card.Board), nameof(Card.Created), nameof(Card.Blocked), nameof(Card.Checklist)])]
// A comment's id and its card's, and a label's id, which C# never needs.
[assembly: SeamView(typeof(Comment), Omit = [nameof(Comment.Id), nameof(Comment.Card)])]
[assembly: SeamView(typeof(Tag), Omit = [nameof(Tag.Id)])]
// A board with its cards is what the function answers for a board.
[assembly: SeamView(typeof(BoardWithCards), Name = "BoardView")]
