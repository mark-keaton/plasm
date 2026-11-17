module Kanban.Server.Program

[<EntryPoint>]
let main args =
    try
        let app = (Host.build args ignore).GetAwaiter().GetResult()
        app.Run()
        0
    with :? Plasm.PlasmStartupException as refusal ->
        eprintfn "%s" refusal.Message
        1
