/// What each koan teaches, read from the lesson feature files (the single
/// source of truth), which are copied next to the program.
module Plasm.Koans.Guidance

open System
open System.IO
open System.Text.RegularExpressions

type KoanGuidance =
    { Discovers: string
      Teaches: string }

type LessonGuidance =
    { Number: string
      Title: string
      Koans: Map<string, KoanGuidance> }

let private row = Regex(@"^\s*\|\s*(.+?)\s*(?<!\\)\|\s*(.+?)\s*(?<!\\)\|\s*(.+?)\s*\|\s*$")

let load () : LessonGuidance list =
    let dir = Path.Combine(AppContext.BaseDirectory, "lessons")

    if not (Directory.Exists dir) then
        []
    else
        Directory.GetFiles(dir, "*.feature")
        |> Array.sort
        |> Array.map (fun f ->
            let lines = File.ReadAllLines f

            let title =
                lines
                |> Array.tryPick (fun l ->
                    let t = l.Trim()
                    if t.StartsWith "Feature:" then Some(t.Substring("Feature:".Length).Trim()) else None)
                |> Option.defaultValue (Path.GetFileNameWithoutExtension f)

            let koans =
                lines
                |> Array.choose (fun l ->
                    let m = row.Match l

                    if m.Success && m.Groups.[1].Value <> "koan" then
                        Some(m.Groups.[1].Value, { Discovers = m.Groups.[2].Value.Replace("\\|", "|"); Teaches = m.Groups.[3].Value })
                    else
                        None)
                |> Map.ofArray

            { Number = (Path.GetFileName f).Substring(0, 2)
              Title = title
              Koans = koans })
        |> List.ofArray
