/// A tree for recursive-queries.feature: categories that refer to their own
/// kind, in an assembly of its own so that Repo.Start can be shown refusing
/// its one query, a recursive query whose step orders its rows.
namespace Plasm.Specs.Recursive

open Plasm

type CategoryId = CategoryId of int64

[<Table "rec_categories">]
type Category =
    { Id: CategoryId
      Name: string
      Parent: CategoryId option }

module Tree =
    /// A type to find this assembly by.
    type Marker = class end

    let model = Model.buildOrFail [] [ typeof<Category> ]

    let tables =
        "create table rec_categories (
           id bigint generated always as identity primary key,
           name text not null,
           parent bigint references rec_categories(id));"

module Refused =
    /// Ordering the step is refused: the query made from it orders the whole.
    let orderedStep =
        Query.recursive (
            plasm { for c in tableOf<Category> do where (c.Parent = None) },
            fun found ->
                plasm {
                    for c in tableOf<Category> do
                    join p in found on (c.Parent = Some p.Id)
                    orderBy c.Name
                    select c
                }
        )
