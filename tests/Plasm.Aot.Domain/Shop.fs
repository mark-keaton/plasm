/// The records of the native-AOT check (docs/aot.md).
module Plasm.Aot.Shop

open Plasm

type CustomerId = CustomerId of int64

[<Table "customers">]
type Customer =
    { Id: CustomerId
      Name: string
      [<Unique>]
      Email: string }
