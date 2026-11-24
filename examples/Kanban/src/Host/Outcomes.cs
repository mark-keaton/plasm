// Each of the facade's outcomes as an HTTP answer, written once: REST's
// endpoints all answer through here. Each union is switched without a default
// arm, so a case the facade adds is a compile error here, not a 500.
using Kanban.Data;
using Plasm.CSharp;

namespace Kanban.Host;

public static class Outcomes
{
    public static IResult Of(Missing missing) => Results.NotFound(missing);
    public static IResult Of(Refused refused) => Results.UnprocessableEntity(refused);
    public static IResult Of(Stale stale) => Results.Conflict(stale);
    public static IResult Of(Invalid invalid) => Results.BadRequest(invalid);

    public static IResult Of(ChangeResult<CardView> outcome) => outcome switch
    {
        CardView card => Results.Ok(card),
        Missing missing => Of(missing),
        Refused refused => Of(refused),
        Stale stale => Of(stale),
        Invalid invalid => Of(invalid),
    };

    public static IResult Of(AddResult<BoardSummary> outcome) => outcome switch
    {
        BoardSummary board => Results.Created($"/boards/{board.Slug}", board),
        Invalid invalid => Of(invalid),
    };

    public static IResult Of(RequestResult<CardView> outcome) => outcome switch
    {
        CardView card => Results.Created($"/cards/{card.Id}", card),
        Missing missing => Of(missing),
        Invalid invalid => Of(invalid),
    };

    public static IResult Of(FindResult<BoardView> found) => found switch
    {
        BoardView board => Results.Ok(board),
        Missing missing => Of(missing),
    };

    public static IResult Of(FindResult<SentBack> outcome) => outcome switch
    {
        SentBack sent => Results.Ok(sent),
        Missing missing => Of(missing),
    };

    public static IResult Of(RequestResult<DonePage> outcome) => outcome switch
    {
        DonePage page => Results.Ok(page),
        Missing missing => Of(missing),
        Invalid invalid => Of(invalid),
    };
}
