// Each of the facade's outcomes as an HTTP answer, written once: REST's
// endpoints all answer through here. Each union is switched without a default
// arm, so a case the facade adds is a compile error here, not a 500.
using Kanban.Data;

namespace Kanban.Host;

public static class Outcomes
{
    public static IResult Of(Missing missing) => Results.NotFound(missing);
    public static IResult Of(Refused refused) => Results.UnprocessableEntity(refused);
    public static IResult Of(Stale stale) => Results.Conflict(stale);
    public static IResult Of(Invalid invalid) => Results.BadRequest(invalid);

    public static IResult Of(ChangeOutcome outcome) => outcome switch
    {
        CardView card => Results.Ok(card),
        Missing missing => Of(missing),
        Refused refused => Of(refused),
        Stale stale => Of(stale),
        Invalid invalid => Of(invalid),
    };
}
