namespace Plasm.Koans.Specs

open Reqnroll
open Plasm.Koans.Specs.Workspace

/// The lesson features: one scenario per koan.
[<Binding>]
type LessonSteps(w: KoansWorld) =

    let statusOf (report: RunResult) (koan: string) =
        report.Report |> List.tryFind (fun e -> field "koan" e = koan) |> Option.map (field "status")

    [<StepDefinition("the koans database with its starting data")>]
    member _.Database() = serverConnectionString () |> unit

    [<StepDefinition("the koan \"(.*)\" in this lesson")>]
    member _.Koan(koan: string) = w.Koan <- koan

    [<StepDefinition("it passes with its reference answer and fails until its blanks are filled")>]
    member _.PassesAndFails() =
        task {
            let! a = fullReport "answered" answered
            let! s = fullReport "shipped" shipped
            let answeredStatus = statusOf a w.Koan
            let shippedStatus = statusOf s w.Koan
            check (answeredStatus = Some "passed") $"with its reference answer the koan is %A{answeredStatus}\n{a.Output}"
            check (shippedStatus = Some "unanswered") $"as shipped the koan is %A{shippedStatus}"
        }
        |> unit

    [<StepDefinition("it teaches the acceptance scenario \"(.*)\"")>]
    member _.Teaches(scenario: string) =
        check ((implementedScenarios ()).Contains scenario) $"'{scenario}' is not an implemented acceptance scenario of Plasm"
