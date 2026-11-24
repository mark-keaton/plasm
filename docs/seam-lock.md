# seam.lock's format

`seam.lock` is the contract the seam (Plasm.CSharp) writes beside a C# host
that has a `Seam.cs`: what C# is given of the F# data project, one fact a
line. The build writes it, and it is never edited by hand. Commit it.

- A build that changes the contract rewrites it and says, change by change,
  what changed.
- A build in CI (`CI=true` in a project made by the template,
  `ContinuousIntegrationBuild=true`, or `-p:PlasmSeamDrift=fail`) stops
  instead, naming each change. No change to what C# is given passes unseen.

Its job is a readable diff, so it is plain lines of text and no other format.
This page is its definition: the seam's reader and writer
(`src/Plasm.Seam/LockFile.fs`) are checked against it. Every example below is
read by them, and every line shown as refused is refused.

## The grammar

This is EBNF, ISO 14977: `"..."` is a literal and `(* *)` a comment. A line
ends with a line feed. A carriage return before it, and spaces at the line's
end, are ignored.

```ebnf
lock          = { comment | blank } , version-line , { comment | blank } ,
                { section , { comment | blank } } ;
comment       = "#" , { any } , eol ;            (* never part of the contract *)
blank         = eol ;
version-line  = "seam.lock " , digit , { digit } , eol ;

section       = record | union | refusal | class | http | json ;

record        = "record " , name , " = " , fsharp , [ ", and from C#" ] ,
                [ ", Missing and Stale say " , quoted ] , eol , { field-line } ;
field-line    = "  " , field , eol ;

union         = "union " , name , " = " , fsharp , eol , { case-line } ;
case-line     = "  " , name , " kind " , quoted , [ " with " , field , { ", " , field } ] , eol ;

refusal       = "refusal " , fsharp , eol , { code-line } ;
code-line     = "  " , name , eol ;

class         = "class " , name , " = " , fsharp , [ ", opened from " , fsharp ] ,
                [ ", its database a file" ] , [ ", registered by " , name ] , eol ,
                { member-line } ;
member-line   = "  " , [ "static " ] , cs-type , " " , name , [ type-params ] ,
                [ parameters ] , { constraint } , eol ;
type-params   = "<" , name , { ", " , name } , ">" ;
parameters    = "(" , [ parameter , { ", " , parameter } ] , ")" ;
parameter     = cs-type , " " , name ;
constraint    = " where " , name , " : " , ( "struct" | "class" ) ;

http          = "http " , name , eol , { status-line } ;
status-line   = "  " , ( "value" | name ) , " " , digit , digit , digit , eol ;

json          = "json SeamJson" , eol , { type-line } ;
type-line     = "  " , cs-type , eol ;

field         = name , ": " , cs-type ;
name          = ( letter | "_" ) , { letter | digit | "_" } ;     (* a C# identifier *)
fsharp        = name , { "." , name } , [ "<" , { any } , ">" ] ;  (* an F# type, dotted *)
cs-type       = name , { "." , name } , [ "<" , cs-type , { ", " , cs-type } , ">" ] ,
                [ "?" ] , { "[]" , [ "?" ] } ;
quoted        = '"' , { any - '"' } , '"' ;
```

The sections come in this order: every `record`, every `union`, every
`refusal`, every `class`, every `http`, and last the one `json`. An `http`
section appears in a web project only.

- Records and unions come in the order the seam meets them, from each data
  type's members in declaration order.
- Members come in their declaration order.
- The `json` lines are the seam's own in ordinal order, then the types Seam.cs
  lists with SeamJson, in the order it lists them.

A header's clauses come in the order the grammar gives them.

## What each line says

- **version-line**: the format the lock is written in.
  ```example version-line
  seam.lock 1
  ```
- **record**: a view of an F# record. `, and from C#`: a data method takes it
  as a parameter, so C# can give it too. `, Missing and Stale say "x"`: the
  word Seam.cs gives the record (SeamView What).
  ```example record field-line field name fsharp cs-type
  record NewBooking = Starter.Data.NewBooking, and from C#
    Version: int
    Who: string
    From: DateTimeOffset
  record EquipmentView = Starter.Equipment, Missing and Stale say "equipment"
    Id: Guid
    Bookings: BookingView[]
    Note: string?
  ```
- **union**: a C# union of an F# union. Each case line holds the case's C#
  name, its JSON kind, and its fields.
  ```example union case-line quoted
  union ConditionView = Starter.Condition
    Bookable kind "bookable"
    Withdrawn kind "withdrawn" with Reason: string, Since: DateTimeOffset
  ```
- **refusal**: a refusal union's codes, which are its cases' names. C# is
  given each one as Refused.Code.
  ```example refusal code-line
  refusal Starter.Refusal
    WhoRequired
    AlreadyBooked
  ```
- **class**: the C# class over an F# data type, and its members.
  - `, opened from S`: the data project's Store S, from which the seam writes
    OpenAsync, Ddl and SandboxAsync.
  - `, its database a file`: DatabaseFile and CreateDatabaseAsync as well.
  - `, registered by AddX`: the registration for ASP.NET.
  ```example class member-line type-params parameters parameter constraint
  class BookingData = Starter.Data.BookingData, opened from Starter.Data.BookingStore.store, its database a file, registered by AddBookingData
    Task<AddResult<EquipmentView>> AddEquipmentAsync(NewEquipment request, CancellationToken cancellationToken)
    Task<T> RunAsync<T>(Func<BookingData, Task<T>> body, CancellationToken cancellationToken) where T : class
    static string Version
  ```
- **http**: what ToHttp answers for each case of an outcome. `value` is the
  outcome's own value.
  ```example http status-line
  http AddResult
    value 201
    Invalid 400
  ```
- **json**: every type the JSON context lists.
  ```example json type-line
  json SeamJson
    EquipmentView
    EquipmentView[]
    Missing
  ```
- **comment** and **blank**: never part of the contract. A lock that differs
  only in them is the same contract.
  ```example comment blank lock
  # The C# contract Plasm.Seam wrote.

  seam.lock 1
  # A note of the team's own.
  json SeamJson
    Missing
  ```

Each line below is refused, and the build names it:

```refused
seam.lock 1
record OrderView = Shop.Order
  Customer string
json SeamJson
```

```refused
seam.lock 1
json SeamJson
record OrderView = Shop.Order
```

```refused
seam.lock 1
table orders
json SeamJson
```

## Versions, and what the format promises

The first line that is not a comment names the format.

- **A lock with no version line** was written by the seam before 0.5.0 was
  released (format 0). It is read and written again; the contract it states
  is compared as it is.
- **A lock of a format newer than the seam knows** is refused, naming both
  versions. An older seam cannot know what a newer line means: build with the
  Plasm.CSharp that wrote it, or a newer one.

What may change, and when:

- **Comments, in any release.** A lock that differs from what a build writes
  only in its comments, blank lines or version is the same contract. A CI
  build passes on it, and another build writes it again quietly.
- **A line's form never changes.** A new section, a new clause on a header or
  a new form of member line comes only with a new format version, in a
  release whose upgrade notes say so. Every lock is then written once in the
  new form. Where the facts are the same, a CI build still passes, because
  the comparison is between the contracts the two formats state, not their
  text.
- **The build never reads the lock to decide what it writes.** It only
  compares. A lock edited by hand is a contract changed by hand, and the next
  build says so.

## A contract per variant

A project whose contract differs between two builds names a lock for each,
with the `PlasmSeamLock` property. The Starter, for example, builds for
PostgreSQL or SQLite, and its SQLite build has two more members (DatabaseFile,
CreateDatabaseAsync). It commits `seam.lock` for PostgreSQL and
`seam.sqlite.lock` for SQLite. A project made from the template has one
database, and one `seam.lock`.

## The Starter's lock

The Starter's SQLite lock, `examples/Starter/src/Starter.Api/seam.sqlite.lock`,
is a whole lock written by the seam. Its first lines:

```example lock section record union refusal class http json
# The C# contract Plasm.Seam wrote from the F# data assembly and Seam.cs. Commit this file.

seam.lock 1
record EquipmentView = Starter.Equipment
  Id: Guid
  Name: string
  Condition: ConditionView
  Bookings: BookingView[]
  Version: int
record NewEquipment = Starter.Data.NewEquipment, and from C#
  Name: string
union ConditionView = Starter.Condition
  Bookable kind "bookable"
  Withdrawn kind "withdrawn" with Reason: string, Since: DateTimeOffset
refusal Starter.Refusal
  WhoRequired
class BookingData = Starter.Data.BookingData, opened from Starter.Data.BookingStore.store, its database a file, registered by AddBookingData
  Task<FindResult<EquipmentView>> EquipmentAsync(Guid id, CancellationToken cancellationToken)
http FindResult
  value 200
  Missing 404
json SeamJson
  EquipmentView
```
