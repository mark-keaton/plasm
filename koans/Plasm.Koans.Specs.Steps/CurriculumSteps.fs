namespace Plasm.Koans.Specs

open System.IO
open System.Text.RegularExpressions
open Reqnroll
open Plasm.Koans.Specs.Workspace

/// Feature: The curriculum is verified
[<Binding>]
type CurriculumSteps(w: KoansWorld) =

    [<StepDefinition("every koan is run with its reference answer")>]
    member _.RunAnswered() =
        task {
            let! r = fullReport "answered" answered
            w.Runs <- [ r ]
        }
        |> unit

    [<StepDefinition("every koan passes")>]
    member _.AllPass() =
        let specified = koanSpecs () |> List.map (fun s -> s.Koan)
        let ran = w.Last.Report |> List.map (field "koan")
        let missing = specified |> List.filter (fun k -> not (List.contains k ran))
        let unspecified = ran |> List.filter (fun k -> not (List.contains k specified))
        check missing.IsEmpty $"koans in the lesson specifications but not in the code: %A{missing}"
        check unspecified.IsEmpty $"koans in the code but not in the lesson specifications: %A{unspecified}"

        let failing =
            w.Last.Report
            |> List.filter (fun e -> field "status" e <> "passed")
            |> List.map (fun e -> $"""{field "koan" e}: {field "status" e} {field "expected" e} {field "actual" e} {field "message" e}""")

        check failing.IsEmpty (String.concat "\n" failing)

    [<StepDefinition("every koan is run as shipped")>]
    member _.RunShipped() =
        task {
            let! r = fullReport "shipped" shipped
            w.Runs <- [ r ]
        }
        |> unit

    [<StepDefinition("every koan fails because a blank is unfilled")>]
    member _.AllUnanswered() =
        check (not w.Last.Report.IsEmpty) w.Last.Output
        let wrong = w.Last.Report |> List.filter (fun e -> field "status" e <> "unanswered") |> List.map (field "koan")
        check wrong.IsEmpty $"koans that do not stop at a blank: %A{wrong}"

    [<StepDefinition("each failure points at the file and line of that blank")>]
    member _.PointsAtBlank() =
        for e in w.Last.Report do
            let file = Path.Combine(koansDir, field "file" e)
            let line = int (field "line" e)
            let text = (File.ReadAllLines file).[line - 1]
            check (Regex.IsMatch(text, @"(?<![\w.])__(?!\w)")) $"""{field "koan" e}: line {line} has no blank: {text}"""

    [<StepDefinition("the koans are built as shipped")>]
    member _.BuildShipped() =
        task {
            let! p = shipped ()
            w.Koan <- p
        }
        |> unit

    [<StepDefinition("they compile without errors, because blanks are typed placeholders")>]
    member _.Compiled() = check (File.Exists w.Koan) $"no program at {w.Koan}"

    [<StepDefinition("the lesson specifications are read")>]
    member _.ReadSpecs() = ()

    [<StepDefinition("every koan after the orientation lesson names the acceptance scenario of Plasm that it teaches")>]
    member _.AllTeach() =
        let later = koanSpecs () |> List.filter (fun s -> s.Number <> orientationLesson)
        check (not later.IsEmpty) "no lesson after the orientation lesson"

        for s in later do
            check (Regex.IsMatch(s.Teaches, @"^[a-z-]+: .+")) $"{s.Koan}: '{s.Teaches}' is not 'feature: scenario'"

    [<StepDefinition("every named scenario exists in Plasm's features and is implemented")>]
    member _.ScenariosExist() =
        let known = implementedScenarios ()

        let unknown =
            koanSpecs ()
            |> List.filter (fun s -> s.Number <> orientationLesson && not (known.Contains s.Teaches))
            |> List.map (fun s -> s.Koan, s.Teaches)

        check unknown.IsEmpty $"unknown or unimplemented scenarios: %A{unknown}"

    [<StepDefinition("every koan of lesson 01 names the F# topic it teaches, as \"F#: topic\"")>]
    member _.OrientationTeaches() =
        let orientation = koanSpecs () |> List.filter (fun s -> s.Number = orientationLesson)
        check (not orientation.IsEmpty) "there is no lesson 01"

        for s in orientation do
            check (isFSharpTopic s.Teaches) $"{s.Koan}: '{s.Teaches}' is not 'F#: topic'"

    [<StepDefinition("no koan of a later lesson names an F# topic instead of a scenario")>]
    member _.OnlyOrientationIsExempt() =
        let exempt =
            koanSpecs ()
            |> List.filter (fun s -> s.Number <> orientationLesson && s.Teaches.StartsWith "F#")
            |> List.map (fun s -> s.Koan, s.Teaches)

        check exempt.IsEmpty $"koans after lesson 01 that name an F# topic: %A{exempt}"

    [<StepDefinition("the koans' source is checked against the list of API areas ready to teach")>]
    member _.ReadKeptOut() = ()

    [<StepDefinition("no koan uses an API area that is kept out of the course")>]
    member _.NoKeptOut() =
        let keptOut =
            File.ReadAllLines(Path.Combine(root, "koans", "kept-out.txt"))
            |> Array.map (fun l -> (l.Split('#').[0]).Trim())
            |> Array.filter (fun l -> l <> "")

        let uses =
            [ for f in lessonFiles () do
                  let text = File.ReadAllText f

                  for api in keptOut do
                      if Regex.IsMatch(text, $@"(?<![\w]){Regex.Escape api}(?![\w])") then
                          yield Path.GetFileName f, api ]

        check uses.IsEmpty $"kept-out API used: %A{uses}"

    [<StepDefinition("the lesson order is checked")>]
    member _.ReadOrder() = ()

    [<StepDefinition("every lesson's prerequisites come before it")>]
    member _.Prerequisites() =
        for f in Directory.GetFiles(lessonSpecsDir, "*.feature") do
            let number = int ((Path.GetFileName f).Substring(0, 2))
            let text = File.ReadAllText f

            for m in Regex.Matches(text, @"lesson (\d\d)") do
                let pre = int m.Groups.[1].Value
                check (pre < number) $"{Path.GetFileName f}: prerequisite lesson {pre} does not come before it"

        // The code walks the lessons in the specifications' order.
        let titles =
            koanSpecs () |> List.map (fun s -> s.Number, s.Lesson) |> List.distinct |> List.sortBy fst |> List.map snd

        let sourceOrder =
            lessonFiles ()
            |> List.map (fun f ->
                let m = Regex.Match(File.ReadAllText f, "Lesson.make\\s*\"(.*?)\"")
                m.Groups.[1].Value)

        check (sourceOrder = (titles |> List.take (min titles.Length sourceOrder.Length))) $"lesson order differs: %A{sourceOrder} vs %A{titles}"
