// Sets SpecialName on every property getter of the compiled assembly that
// lacks it (the F# compiler leaves it off a union case field's getter), and
// changes nothing else. Usage: dotnet fsi patch.fsx <assembly.dll>
#r "nuget: Mono.Cecil, 0.11.6"

open Mono.Cecil

let path = fsi.CommandLineArgs.[1]
let m = ModuleDefinition.ReadModule(new System.IO.MemoryStream(System.IO.File.ReadAllBytes path))
let mutable n = 0

let rec visit (t: TypeDefinition) =
    for p in t.Properties do
        if not (isNull p.GetMethod) && not p.GetMethod.IsSpecialName then
            p.GetMethod.IsSpecialName <- true
            n <- n + 1

    for nested in t.NestedTypes do
        visit nested

for t in m.Types do
    visit t

m.Write path
printfn "marked %d getters SpecialName" n
