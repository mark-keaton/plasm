module Kanban.Server.Program

[<EntryPoint>]
let main args =
    try
        let app = (Host.build args ignore).GetAwaiter().GetResult()
        app.Run()
        0
    with Host.SchemaMismatch _ as mismatch ->
        eprintfn "%s" mismatch.Message
        1
