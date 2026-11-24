Feature: A data project names its Store, and the seam writes the rest
  A data type's plumbing is the same in every project: open with Plasm's
  start-up check, print the DDL, make a database where it is a file, run a
  test in a sandbox, register with ASP.NET. The data project gives only what
  it knows, once, as a Store (or a FileStore, for SQLite): its model, how a
  connection string becomes a provider, a provider that reaches no database
  for the DDL, the assembly of its queries, and how its data type is made
  over a repository and the clock. The seam finds it and gives the C# data
  type OpenAsync, Ddl, SandboxAsync (and DatabaseFile and CreateDatabaseAsync
  for a file), and, in a web project, AddXxx: the data type opened as the
  host starts, before any request, its clock the container's TimeProvider,
  its SQL logged at Debug. The F# data type holds only its operations.

  Background:
    Given the F# data project:
      """
      namespace Shop

      open System
      open System.IO
      open System.Threading.Tasks
      open Microsoft.Data.Sqlite
      open Plasm
      open Plasm.Sqlite
      open Plasm.CSharp

      type OrderId = OrderId of Guid

      [<Table "orders">]
      type Order =
          { Id: OrderId
            Customer: string
            [<Version>]
            Version: int }

      module Db =
          let model = Model.buildOrFail [] [ typeof<Order> ]
          let byCustomer = plasm { for o in tableOf<Order> do orderBy o.Customer }

      /// The orders: only what they do.
      [<Sealed>]
      type Orders(repo: Repo, time: TimeProvider) =
          member _.AddOrderAsync(customer: string) : Task<AddResult<Order>> =
              task {
                  let! r = repo.Transact(Tx.insert { Id = OrderId(Guid.CreateVersion7()); Customer = customer; Version = 1 }: Tx<_, unit>)
                  return Outcome.added r
              }

          member _.OrdersAsync() : Task<Order[]> =
              task {
                  let! r = repo.All Db.byCustomer
                  return Outcome.all r
              }

      /// Where the orders live: a SQLite file.
      module OrderStore =
          let store: FileStore<Orders> =
              { Store =
                  { Model = Db.model
                    Provider = fun cs -> Sqlite.connect cs :> IProvider
                    Printing = fun () -> Sqlite.connect "Data Source=:memory:" :> IProvider
                    Queries = typeof<Order>.Assembly
                    Make = fun repo time -> Orders(repo, time) }
                File = fun cs -> Path.GetFullPath(SqliteConnectionStringBuilder(cs).DataSource) }
      """
    And Seam.cs:
      """
      using Plasm.CSharp;

      [assembly: SeamData(typeof(Shop.Orders))]
      """

  Scenario: The C# data type is created, opened, sandboxed and described from the Store
    When the seam is written into the namespace Shop.Api
    Then seam.lock holds
      """
      class Orders = Shop.Orders
        opened from Shop.OrderStore.store: OpenAsync, Ddl, SandboxAsync, DatabaseFile, CreateDatabaseAsync
        Task<AddResult<OrderView>> AddOrderAsync(string customer)
        Task<OrderView[]> OrdersAsync()
      """
    And the host, run, prints "the file named; the sandbox saw 1, and kept 0; then 1 stored: Ada; the DDL creates orders"
      """
      using System.IO;
      using Microsoft.Data.Sqlite;

      namespace Shop.Api;

      public static class Host
      {
          public static string Run()
          {
              var file = Path.Combine(Path.GetTempPath(), $"plasm-seam-store-{Guid.NewGuid():N}.db");
              var cs = $"Data Source={file}";
              try
              {
                  Orders.CreateDatabaseAsync(cs, default).Wait();
                  var orders = Orders.OpenAsync(cs, TimeProvider.System, default).Result;
                  var seen = orders.SandboxAsync(async o =>
                  {
                      await o.AddOrderAsync("Grace");
                      return (await o.OrdersAsync()).Length;
                  }, default).Result;
                  var kept = orders.OrdersAsync().Result.Length;
                  orders.AddOrderAsync("Ada").Wait();
                  var all = orders.OrdersAsync().Result;
                  var named = Orders.DatabaseFile(cs) == Path.GetFullPath(file) ? "the file named" : "another file";
                  var ddl = Orders.Ddl.Contains("create table orders") ? "the DDL creates orders" : Orders.Ddl;
                  return $"{named}; the sandbox saw {seen}, and kept {kept}; then {all.Length} stored: {all[0].Customer}; {ddl}";
              }
              finally
              {
                  SqliteConnection.ClearAllPools();
                  File.Delete(file);
              }
          }
      }
      """

  Scenario: In a web project, AddOrders registers the data type, opened as the host starts
    When the seam is written into the namespace Shop.Api, as a web project
    Then OrdersServices.g.cs declares "public static IServiceCollection AddOrders(this IServiceCollection services, string connectionString)"
    And seam.lock holds
      """
      class Orders = Shop.Orders
        opened from Shop.OrderStore.store: OpenAsync, Ddl, SandboxAsync, DatabaseFile, CreateDatabaseAsync
        AddOrders
      """
    And the C# compiles with the host:
      """
      using Microsoft.AspNetCore.Builder;
      using Microsoft.Extensions.DependencyInjection;

      namespace Shop.Api;

      public static class Program
      {
          public static WebApplication Build(string[] args)
          {
              var builder = WebApplication.CreateSlimBuilder(args);
              builder.Services.AddOrders("Data Source=orders.db");
              var app = builder.Build();
              app.MapGet("/orders", async (Orders orders) => (await orders.OrdersAsync()).Length);
              return app;
          }
      }
      """

  Scenario: A Store whose database is not a file gives no file members
    Given the F# data project:
      """
      namespace Shop

      open System
      open System.Threading.Tasks
      open Plasm
      open Plasm.Sqlite
      open Plasm.CSharp

      [<Table "notes">]
      type Note = { Id: Guid; Text: string }

      module Db =
          let model = Model.buildOrFail [] [ typeof<Note> ]
          let all = plasm { for n in tableOf<Note> do select n }

      [<Sealed>]
      type Notes(repo: Repo, time: TimeProvider) =
          member _.CountAsync() : Task<int> = Task.FromResult 0

      module NoteStore =
          let store: Store<Notes> =
              { Model = Db.model
                Provider = fun cs -> Sqlite.connect cs :> IProvider
                Printing = fun () -> Sqlite.connect "Data Source=:memory:" :> IProvider
                Queries = typeof<Note>.Assembly
                Make = fun repo time -> Notes(repo, time) }
      """
    And Seam.cs:
      """
      using Plasm.CSharp;

      [assembly: SeamData(typeof(Shop.Notes))]
      """
    When the seam is written into the namespace Shop.Api
    Then seam.lock holds
      """
      class Notes = Shop.Notes
        opened from Shop.NoteStore.store: OpenAsync, Ddl, SandboxAsync
      """
    And Notes.g.cs does not declare "DatabaseFile"
