/// The ASP.NET Core application: services, start-up checks and the HTTP pipeline.
module Kanban.Server.Host

open System
open System.Security.Claims
open System.Threading.Tasks
open Giraffe
open Fable.Remoting.Server
open Fable.Remoting.Giraffe
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Diagnostics.HealthChecks
open Microsoft.Extensions.Logging
open Npgsql
open Plasm
open Kanban.Api

/// /health: healthy while the tables still fit the model.
type SchemaHealthCheck(repo: Repo) =
    interface IHealthCheck with
        member _.CheckHealthAsync(_, _) =
            task {
                match! Database.differences repo with
                | [] -> return HealthCheckResult.Healthy()
                | differences -> return HealthCheckResult.Unhealthy(String.concat "; " differences)
            }

/// Who is signed in, from the authentication cookie ASP.NET Core read (and
/// verified) before the request reached the API.
let private session (ctx: HttpContext) : Service.Session =
    let scheme = CookieAuthenticationDefaults.AuthenticationScheme

    { Who =
        fun () ->
            match ctx.User.Identity with
            | null -> None
            | identity when identity.IsAuthenticated -> Kanban.Person.create identity.Name |> Result.toOption
            | _ -> None
      SignIn =
        fun person ->
            let identity = ClaimsIdentity([ Claim(ClaimTypes.Name, person.Value) ], scheme)
            ctx.SignInAsync(scheme, ClaimsPrincipal identity)
      SignOut = fun () -> ctx.SignOutAsync scheme }

let private api: HttpHandler =
    Remoting.createApi ()
    |> Remoting.withRouteBuilder Route.builder
    |> Remoting.fromContext (fun (ctx: HttpContext) ->
        Service.create (ctx.RequestServices.GetRequiredService<Service.Deps>()) (session ctx) ctx.RequestAborted)
    |> Remoting.withErrorHandler (fun ex info ->
        let log = info.httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger "Kanban.Api"
        log.LogError(ex, "Unhandled failure in {Path}", info.path)
        Propagate "The request failed.")
    |> Remoting.buildHttpHandler

/// Builds the application and checks its database; the caller runs it.
/// `configure` lets a test host change the builder (a test server, another
/// connection string).
let build (args: string[]) (configure: WebApplicationBuilder -> unit) : Task<WebApplication> =
    task {
        let builder = WebApplication.CreateBuilder args
        configure builder

        let connectionString =
            match builder.Configuration.GetConnectionString "Kanban" with
            | null
            | "" -> failwith "Set the connection string 'Kanban' (ConnectionStrings:Kanban)."
            | cs -> cs

        // The repository is started below, after the migrations: starting it
        // reads the database. The services ask for it only once it is there.
        let started = TaskCompletionSource<Repo>()

        builder.Services
            .AddSingleton<NpgsqlDataSource>(fun _ -> NpgsqlDataSource.Create connectionString)
            .AddSingleton<Repo>(fun _ -> started.Task.Result)
            .AddSingleton<Service.Deps>(fun services ->
                { Repo = services.GetRequiredService<Repo>()
                  Clock = fun () -> DateTimeOffset.UtcNow
                  NewId = Guid.CreateVersion7
                  Log = services.GetRequiredService<ILoggerFactory>().CreateLogger "Kanban.Service" }
                : Service.Deps)
            .AddGiraffe()
            .AddHealthChecks()
            .AddCheck<SchemaHealthCheck>("database")
        |> ignore

        // Who is signed in travels in a cookie that scripts cannot read and
        // that other sites' requests do not carry.
        builder.Services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(fun options ->
                options.Cookie.Name <- "kanban"
                options.Cookie.HttpOnly <- true
                options.Cookie.SameSite <- SameSiteMode.Strict
                options.ExpireTimeSpan <- TimeSpan.FromDays 14.0
                options.SlidingExpiration <- true)
        |> ignore

        let app = builder.Build()

        if builder.Configuration.GetValue("Database:Migrate", true) then
            match Database.migrate connectionString with
            | Ok() -> ()
            | Error message -> failwith $"Migrating the database failed: {message}"

        // Plasm refuses to start with a query that would fail the first time
        // it is used, or tables the model cannot read or write.
        try
            let! repo =
                Database.start
                    (app.Services.GetRequiredService<NpgsqlDataSource>())
                    (app.Services.GetRequiredService<ILoggerFactory>().CreateLogger "Kanban.Sql")

            started.SetResult repo
        with :? PlasmStartupException as refusal ->
            do! app.DisposeAsync()
            Runtime.ExceptionServices.ExceptionDispatchInfo.Throw refusal

        // The client's files, when it has been built into wwwroot (npm run build).
        app.UseDefaultFiles().UseStaticFiles() |> ignore
        app.UseAuthentication() |> ignore
        app.MapHealthChecks "/health" |> ignore
        app.UseGiraffe api
        return app
    }
