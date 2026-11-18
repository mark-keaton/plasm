// Registering, signing in and out. Whether a name and a password are
// acceptable is the domain's (Person, Password, through KanbanData); the host
// hashes the password and keeps who is signed in in a cookie.
using System.Security.Claims;
using Kanban.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace Kanban.Host;

public static class Accounts
{
    record Credentials(string Name, string Password);
    record SignedIn(string Name);

    /// ASP.NET Core Identity's hasher: PBKDF2, a salt per password. Its
    /// methods take the user the password is for, which this hasher does not
    /// read; Anyone stands for every user.
    sealed class User;
    static readonly User Anyone = new();
    static readonly PasswordHasher<User> Hasher = new();

    static string Hash(string password) => Hasher.HashPassword(Anyone, password);

    /// Checked when no account has the name, so that answer takes as long as
    /// a wrong password's.
    static readonly string NobodysHash = Hash("no account has this password");

    static bool Fits(string hash, string password) =>
        Hasher.VerifyHashedPassword(Anyone, hash, password) != PasswordVerificationResult.Failed;

    /// The person signed in, as the facade's actor.
    public static string Actor(this ClaimsPrincipal user) =>
        user.Identity?.Name ?? throw new InvalidOperationException("no one is signed in; the endpoint needs RequireAuthorization");

    static async Task<IResult> SignedInAs(HttpContext http, string name)
    {
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], CookieAuthenticationDefaults.AuthenticationScheme)));
        return Results.Ok(new SignedIn(name));
    }

    /// Refused as a wrong password is, after as long.
    static Task<IResult> NoSuchAccount(HttpContext http, string password)
    {
        _ = Fits(NobodysHash, password);
        return NobodyThen(http, Results.Unauthorized());
    }

    /// Whoever was signed in no longer is, and the answer is `answer`.
    static async Task<IResult> NobodyThen(HttpContext http, IResult answer)
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return answer;
    }

    public static void MapAccounts(this WebApplication app)
    {
        var account = app.MapGroup("/account");

        account.MapPost("/register", async (Credentials input, HttpContext http, KanbanData kanban, CancellationToken ct) =>
            await kanban.RegisterAsync(input.Name, input.Password, Hash, ct) switch
            {
                AccountView registered => await SignedInAs(http, registered.Name),
                Invalid invalid => await NobodyThen(http, Outcomes.Of(invalid)),
            });

        account.MapPost("/signin", async (Credentials input, HttpContext http, KanbanData kanban, CancellationToken ct) =>
        {
            var password = input.Password;
            return await kanban.AccountAsync(input.Name, ct) switch
            {
                AccountView found when Fits(found.PasswordHash, password) => await SignedInAs(http, found.Name),
                AccountView => await NobodyThen(http, Results.Unauthorized()),
                Missing => await NoSuchAccount(http, password),
            };
        });

        // A handler taking only the HttpContext would be read as a
        // RequestDelegate, whose result is discarded: as a Delegate, its
        // answer is written.
        account.MapPost("/signout", (Delegate)((HttpContext http) => NobodyThen(http, Results.Ok())));

        account.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new SignedIn(user.Actor()))).RequireAuthorization();
    }
}
