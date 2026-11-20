/// What each koan teaches, read from the lesson feature files (the single
/// source of truth), which are copied next to the program.
module Plasm.Koans.Guidance

open System
open System.IO
open System.Text.RegularExpressions

type KoanGuidance =
    { Discovers: string
      Teaches: string
      /// One sentence pointing at where on the page the answer comes from,
      /// shown after a wrong answer; "" when the koan has none.
      Hint: string }

type LessonGuidance =
    { Number: string
      Title: string
      /// The lesson's goal: the first line of the feature's description.
      Goal: string
      Koans: Map<string, KoanGuidance> }

// koan | the learner discovers | teaches | hint (the hint's cell may be empty).
let private row = Regex(@"^\s*\|\s*(.+?)\s*(?<!\\)\|\s*(.+?)\s*(?<!\\)\|\s*(.+?)\s*(?:(?<!\\)\|\s*(.*?)\s*)?\|\s*$")

let load () : LessonGuidance list =
    // Next to the koans assembly (also when it is loaded into F# Interactive).
    let dir = Path.Combine(Path.GetDirectoryName(typeof<KoanGuidance>.Assembly.Location), "lessons")

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

            let goal =
                lines
                |> Array.skipWhile (fun l -> not (l.Trim().StartsWith "Feature:"))
                |> Array.tryItem 1
                |> Option.map (fun l -> l.Trim())
                |> Option.defaultValue ""

            let koans =
                lines
                |> Array.choose (fun l ->
                    let m = row.Match l

                    if m.Success && m.Groups.[1].Value <> "koan" then
                        Some(
                            m.Groups.[1].Value,
                            { Discovers = m.Groups.[2].Value.Replace("\\|", "|")
                              Teaches = m.Groups.[3].Value
                              Hint = m.Groups.[4].Value.Replace("\\|", "|") }
                        )
                    else
                        None)
                |> Map.ofArray

            { Number = (Path.GetFileName f).Substring(0, 2)
              Title = title
              Goal = goal
              Koans = koans })
        |> List.ofArray
