# Testing

Test against a real PostgreSQL, with nothing mocked. A sandbox makes that
cheap: every test starts from the same data and leaves nothing behind.

## A sandbox

`repo.Sandbox` gives its body a repository on one connection inside one
transaction, which is always rolled back. It returns whatever the body
returns. Transactions inside it become savepoints, and each operation runs in
its own savepoint, so a database error does not end the sandbox. Sandboxes
nest.

These are from the koans' lesson on testing,
[`koans/Plasm.Koans/Lessons/L21_TestingWithPlasm.fs`](../../koans/Plasm.Koans/Lessons/L21_TestingWithPlasm.fs),
where `__` is the blank a learner fills in.

An insert is visible inside its sandbox:

<!-- from: koans/Plasm.Koans/Lessons/L21_TestingWithPlasm.fs -->
```fsharp
                      let! inside =
                          repo.Sandbox(fun repo ->
                              task {
                                  let! _ = repo.Transact addDan
                                  return! customers repo
                              })
```

And gone afterwards:

<!-- from: koans/Plasm.Koans/Lessons/L21_TestingWithPlasm.fs -->
```fsharp
                      let! _ = repo.Sandbox(fun repo -> repo.Transact addDan)
                      // The sandbox has ended: count with the outer repository.
                      let! after = customers repo
```

## What a sandbox cannot test

Inside a sandbox, isolation levels and snapshot reads see the sandbox's
transaction, so serialisation retries cannot be tested there.

## The start-up check is a test

Starting the repository translates every query value and compares the model
with the database. A test that starts the application's repository against a
migrated database therefore checks every query and every table at once.
[Starting a repository](starting-a-repository.md) describes it.

## How Plasm tests itself

Plasm's own behaviour is specified in Gherkin (`features/`) and run with
Reqnroll against PostgreSQL 18 started by Testcontainers. To use an existing
server, set `PLASM_TEST_PG` to an admin connection string.

```sh
dotnet test tests/Plasm.Specs
dotnet test tests/Plasm.Tests
```
