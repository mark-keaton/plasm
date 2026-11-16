// The browser's side: an Elmish application over the API in Shared/Api.fs.
//
// The same Domain.fs the server decides with is compiled into this bundle:
// forms are checked with the domain's own constructors before anything is
// sent, and a refusal is put into words by the domain's own `describe`.
module Kanban.Client.App

open System
open Elmish
open Elmish.React
open Fable.Core
open Fable.Remoting.Client
open Feliz
open Kanban
open Kanban.Api

let private api: IKanbanApi =
    Remoting.createApi () |> Remoting.withRouteBuilder Route.builder |> Remoting.buildProxy<IKanbanApi>

module private Browser =
    [<Emit("window.location.hash")>]
    let hash () : string = jsNative

    [<Emit("window.addEventListener('hashchange', $0)")>]
    let onHashChange (handler: unit -> unit) : unit = jsNative

type Page =
    | BoardList
    | BoardPage of slug: string
    | WorkPage

module Page =
    /// "#/apollo" is a board, "#/~work" what the person signed in is working
    /// on ("~" cannot be in a slug); anything else is the list.
    let ofHash (hash: string) =
        match hash.TrimStart('#').Trim('/') with
        | "" -> BoardList
        | "~work" -> WorkPage
        | slug -> BoardPage slug

type Credentials' = { Name: string; Password: string }

type BoardForm =
    { Slug: string
      Name: string
      Limit: string
      Cards: string }

type Drafts =
    { Comment: string
      Title: string
      Reason: string
      Labels: string }

type CardAction =
    | Start
    | Ship
    | SendBack
    | Reopen
    | Drop
    | Rename
    | Comment
    | Relabel

type Model =
    { Page: Page
      /// Who is signed in, once the server has said.
      Me: string option
      Sign: Credentials'
      Boards: BoardSummary[] option
      Board: BoardView option
      /// The label the board is narrowed to, and the cards that carry it.
      Filter: (string * CardView[]) option
      /// The page of done cards on show.
      Finished: CardPage option
      Work: WorkItem[] option
      Form: BoardForm
      NewCard: string
      /// The card whose details are open, and what is being typed into them.
      Open: Guid option
      Drafts: Drafts
      Problems: (string * string) list
      Notice: string option }

type Msg =
    | HashChanged
    | GotMe of Result<string, ApiError>
    | SignedIn of Result<string, ApiError>
    | SignedOut of Result<unit, ApiError>
    | GotBoards of Result<BoardSummary[], ApiError>
    | GotBoard of Result<BoardView, ApiError>
    | GotFiltered of string * Result<CardView[], ApiError>
    | GotFinished of Result<CardPage, ApiError>
    | GotWork of Result<WorkItem[], ApiError>
    | GotCard of Result<CardView, ApiError>
    | BoardCreated of Result<BoardSummary, ApiError>
    | AllSentBack of Result<int, ApiError>
    | Failed of exn
    | EditSign of (Credentials' -> Credentials')
    | SignIn
    | Register
    | SignOut
    | EditForm of (BoardForm -> BoardForm)
    | CreateBoard
    | SetNewCard of string
    | AddCard
    | Toggle of CardView
    | EditDrafts of (Drafts -> Drafts)
    | Act of CardAction * CardView
    | SendAllBack
    | FilterBy of string option
    | ShowFinished of page: int
    | Dismiss

let private noDrafts =
    { Comment = ""
      Title = ""
      Reason = ""
      Labels = "" }

let private emptyForm =
    { Slug = ""
      Name = ""
      Limit = "3"
      Cards = "" }

/// Done cards are read five at a time.
let private pageSize = 5

let private loadFinished (slug: string) (page: int) : Cmd<Msg> =
    Cmd.OfAsync.either api.finished { Board = slug; Page = page; Size = pageSize } GotFinished Failed

let private loadFiltered (slug: string) (label: string) : Cmd<Msg> =
    Cmd.OfAsync.either api.labelled { Board = slug; Label = label } (fun cards -> GotFiltered(label, cards)) Failed

let private load (page: Page) : Cmd<Msg> =
    match page with
    | BoardList -> Cmd.OfAsync.either api.boards () GotBoards Failed
    | BoardPage slug -> Cmd.batch [ Cmd.OfAsync.either api.board slug GotBoard Failed; loadFinished slug 1 ]
    | WorkPage -> Cmd.OfAsync.either api.myWork () GotWork Failed

/// Reads again what a change to a card can have altered besides the card.
let private refresh (model: Model) : Cmd<Msg> =
    match model.Page with
    | BoardPage slug ->
        Cmd.batch
            [ Cmd.OfAsync.either api.board slug GotBoard Failed
              loadFinished slug (model.Finished |> Option.map (fun p -> p.Page) |> Option.defaultValue 1)
              match model.Filter with
              | Some(label, _) -> loadFiltered slug label
              | None -> Cmd.none ]
    | page -> load page

let private init () =
    let page = Page.ofHash (Browser.hash ())

    { Page = page
      Me = None
      Sign = { Name = ""; Password = "" }
      Boards = None
      Board = None
      Filter = None
      Finished = None
      Work = None
      Form = emptyForm
      NewCard = ""
      Open = None
      Drafts = noDrafts
      Problems = []
      Notice = None },
    Cmd.batch [ Cmd.OfAsync.either api.whoAmI () GotMe Failed; load page ]

/// What the server said, in words. A refusal is described by the domain.
let private explain (error: ApiError) =
    match error with
    | Invalid fields -> fields |> Array.map (fun (field, message) -> $"{field} {message}") |> String.concat "; "
    | Rejected refusal -> DomainError.describe refusal
    | NotFound what -> $"There is no {what}."
    | Stale -> "Someone changed this card while you were looking at it. Here is how it is now."
    | SignInRequired -> "Sign in to do that."
    | WrongNameOrPassword -> "The name or the password is wrong."
    | Unavailable message -> message

let private field (name: string) (parsed: Result<'T, string>) =
    match parsed with
    | Ok _ -> []
    | Error message -> [ name, message ]

let private separated (text: string) =
    text.Split(',') |> Array.map (fun part -> part.Trim()) |> Array.filter (fun part -> part <> "")

/// The form's problems, found by the constructors the server will use.
let private formProblems (form: BoardForm) =
    let limit =
        match Int32.TryParse form.Limit with
        | true, n -> WipLimit.create n
        | _ -> Error "must be a number"

    field "slug" (Slug.create form.Slug) @ field "name" (Title.create form.Name) @ field "wipLimit" limit

let private signProblems (sign: Credentials') =
    field "name" (Person.create sign.Name) @ field "password" (Password.create sign.Password)

let private request (action: CardAction) (card: CardView) (model: Model) : Async<Result<CardView, ApiError>> =
    let ref = { Card = card.Id; Version = card.Version }

    match action with
    | Start -> api.start ref
    | Ship -> api.ship ref
    | SendBack -> api.sendBack ref
    | Reopen -> api.reopen ref
    | Drop -> api.drop { Ref = ref; Reason = model.Drafts.Reason }
    | Rename -> api.rename { Ref = ref; Title = model.Drafts.Title }
    | Comment -> api.comment { Card = card.Id; Body = model.Drafts.Comment }
    | Relabel -> api.label { Ref = ref; Labels = separated model.Drafts.Labels }

let private withCard (card: CardView) (board: BoardView) =
    if board.Cards |> Array.exists (fun c -> c.Id = card.Id) then
        { board with Cards = board.Cards |> Array.map (fun c -> if c.Id = card.Id then card else c) }
    else
        { board with Cards = Array.append board.Cards [| card |] }

let private update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | HashChanged ->
        let page = Page.ofHash (Browser.hash ())

        { model with
            Page = page
            Board = None
            Filter = None
            Finished = None
            Work = None
            Open = None
            Problems = []
            Notice = None },
        load page
    | GotMe(Ok name) -> { model with Me = Some name }, Cmd.none
    // Nobody is signed in: that is an answer, not a problem.
    | GotMe(Error _) -> { model with Me = None }, Cmd.none
    | SignedIn(Ok name) ->
        { model with
            Me = Some name
            Sign = { Name = ""; Password = "" }
            Problems = []
            Notice = None },
        load model.Page
    | SignedIn(Error(Invalid problems)) -> { model with Problems = List.ofArray problems }, Cmd.none
    | SignedIn(Error error) -> { model with Notice = Some(explain error) }, Cmd.none
    | SignedOut _ -> { model with Me = None; Work = None; Open = None }, Cmd.none
    | GotBoards(Ok boards) -> { model with Boards = Some boards }, Cmd.none
    | GotBoard(Ok board) -> { model with Board = Some board }, Cmd.none
    | GotFiltered(label, Ok cards) -> { model with Filter = Some(label, cards) }, Cmd.none
    | GotFinished(Ok page) -> { model with Finished = Some page }, Cmd.none
    | GotWork(Ok work) -> { model with Work = Some work }, Cmd.none
    | GotCard(Ok card) ->
        { model with
            Board = model.Board |> Option.map (withCard card)
            Drafts = { noDrafts with Labels = String.concat ", " card.Labels }
            NewCard = ""
            Problems = []
            Notice = None },
        refresh model
    | BoardCreated(Ok board) ->
        { model with
            Boards = model.Boards |> Option.map (fun boards -> Array.append boards [| board |] |> Array.sortBy (fun b -> b.Name))
            Form = emptyForm
            Problems = [] },
        Cmd.none
    | AllSentBack(Ok count) ->
        { model with Notice = Some(if count = 1 then "1 card went back to the backlog." else $"{count} cards went back to the backlog.") },
        refresh model
    | BoardCreated(Error(Invalid problems))
    | GotCard(Error(Invalid problems)) -> { model with Problems = List.ofArray problems }, Cmd.none
    // The copy on screen is old: say so, and fetch the board as it is now.
    | GotCard(Error Stale) -> { model with Notice = Some(explain Stale) }, refresh model
    // Asking for one's work while signed out is shown as such by the page.
    | GotWork(Error SignInRequired) -> { model with Work = None }, Cmd.none
    | GotBoards(Error error)
    | GotBoard(Error error)
    | GotFiltered(_, Error error)
    | GotFinished(Error error)
    | GotWork(Error error)
    | BoardCreated(Error error)
    | AllSentBack(Error error)
    | GotCard(Error error) -> { model with Notice = Some(explain error) }, Cmd.none
    | Failed problem -> { model with Notice = Some $"The server could not be reached ({problem.Message})." }, Cmd.none
    | EditSign edit -> { model with Sign = edit model.Sign }, Cmd.none
    | SignIn ->
        let credentials: Credentials = { Name = model.Sign.Name; Password = model.Sign.Password }
        { model with Problems = [] }, Cmd.OfAsync.either api.signIn credentials SignedIn Failed
    | Register ->
        match signProblems model.Sign with
        | [] ->
            let credentials: Credentials = { Name = model.Sign.Name; Password = model.Sign.Password }
            { model with Problems = [] }, Cmd.OfAsync.either api.register credentials SignedIn Failed
        | problems -> { model with Problems = problems }, Cmd.none
    | SignOut -> model, Cmd.OfAsync.either api.signOut () SignedOut Failed
    | EditForm edit -> { model with Form = edit model.Form }, Cmd.none
    | CreateBoard ->
        match formProblems model.Form with
        | [] ->
            let board: NewBoard =
                { Slug = model.Form.Slug
                  Name = model.Form.Name
                  WipLimit = int model.Form.Limit
                  Cards = separated model.Form.Cards }

            { model with Problems = [] }, Cmd.OfAsync.either api.createBoard board BoardCreated Failed
        | problems -> { model with Problems = problems }, Cmd.none
    | SetNewCard title -> { model with NewCard = title }, Cmd.none
    | AddCard ->
        match model.Page, Title.create model.NewCard with
        | BoardPage slug, Ok _ -> model, Cmd.OfAsync.either api.addCard { Board = slug; Title = model.NewCard } GotCard Failed
        | _, Error message -> { model with Problems = [ "title", message ] }, Cmd.none
        | _, Ok _ -> model, Cmd.none
    | Toggle card ->
        { model with
            Open = (if model.Open = Some card.Id then None else Some card.Id)
            Drafts = { noDrafts with Labels = String.concat ", " card.Labels }
            Problems = [] },
        Cmd.none
    | EditDrafts edit -> { model with Drafts = edit model.Drafts }, Cmd.none
    | Act(action, card) -> model, Cmd.OfAsync.either (request action card) model GotCard Failed
    | SendAllBack ->
        match model.Page with
        | BoardPage slug -> model, Cmd.OfAsync.either api.sendAllBack slug AllSentBack Failed
        | _ -> model, Cmd.none
    | FilterBy None -> { model with Filter = None }, Cmd.none
    | FilterBy(Some label) ->
        match model.Page with
        | BoardPage slug -> model, loadFiltered slug label
        | _ -> model, Cmd.none
    | ShowFinished page ->
        match model.Page with
        | BoardPage slug -> model, loadFinished slug page
        | _ -> model, Cmd.none
    | Dismiss -> { model with Notice = None }, Cmd.none

// -- View ----------------------------------------------------------------------

let private clock (at: DateTimeOffset) = at.LocalDateTime.ToString "yyyy-MM-dd HH:mm"

let private problem (name: string) (model: Model) =
    match model.Problems |> List.tryFind (fun (field, _) -> field.StartsWith name) with
    | Some(field, message) -> Html.p [ prop.className "problem"; prop.text $"{field} {message}" ]
    | None -> Html.none

let private input (kind: string) (label: string) (value: string) (onChange: string -> unit) (onEnter: unit -> unit) =
    Html.input
        [ prop.type' kind
          prop.ariaLabel label
          prop.placeholder label
          prop.value value
          prop.onChange onChange
          prop.onKeyDown (fun e -> if e.key = "Enter" then onEnter ()) ]

let private textInput label value onChange onEnter = input "text" label value onChange onEnter

let private button (label: string) (kind: string) (enabled: bool) (onClick: unit -> unit) =
    Html.button
        [ prop.className kind
          prop.disabled (not enabled)
          prop.onClick (fun _ -> onClick ())
          prop.text label ]

let private viewBoardList (model: Model) dispatch =
    Html.main
        [ Html.h1 "Boards"
          match model.Boards with
          | None -> Html.p [ prop.className "quiet"; prop.text "Loading…" ]
          | Some [||] -> Html.p [ prop.className "quiet"; prop.text "No boards yet." ]
          | Some boards ->
              Html.ul
                  [ prop.className "boards"
                    prop.children
                        [ for board in boards do
                              Html.li
                                  [ prop.key board.Slug
                                    prop.children
                                        [ Html.a
                                              [ prop.href $"#/{board.Slug}"
                                                prop.children
                                                    [ Html.strong board.Name
                                                      Html.span
                                                          [ prop.className "counts"
                                                            prop.text
                                                                $"{board.Backlog} waiting · {board.Doing} of {board.WipLimit} in progress · {board.Done} done" ]
                                                      if board.Newest.Length > 0 then
                                                          Html.span
                                                              [ prop.className "newest"
                                                                prop.text ("Newest: " + String.concat " · " board.Newest) ] ] ] ] ] ] ]
          if model.Me.IsSome then
              Html.section
                  [ prop.className "form"
                    prop.children
                        [ Html.h2 "New board"
                          textInput "Name" model.Form.Name (fun v -> dispatch (EditForm(fun f -> { f with Name = v }))) ignore
                          problem "name" model
                          textInput "Slug (its address, like apollo-11)" model.Form.Slug (fun v -> dispatch (EditForm(fun f -> { f with Slug = v }))) ignore
                          problem "slug" model
                          textInput "Cards in progress at once" model.Form.Limit (fun v -> dispatch (EditForm(fun f -> { f with Limit = v }))) ignore
                          problem "wipLimit" model
                          textInput "First cards, separated by commas (optional)" model.Form.Cards (fun v -> dispatch (EditForm(fun f -> { f with Cards = v }))) ignore
                          problem "cards" model
                          button "Create board" "primary" true (fun () -> dispatch CreateBoard) ] ]
          else
              Html.p [ prop.className "quiet"; prop.text "Sign in to create a board." ] ]

let private isDone (card: CardView) =
    match card.Stage with
    | WasShipped _
    | WasDropped _ -> true
    | InBacklog
    | InProgress _ -> false

let private viewCard (model: Model) dispatch (card: CardView) =
    let isOpen = model.Open = Some card.Id
    let act action = fun () -> dispatch (Act(action, card))
    let draft (edit: string -> Drafts -> Drafts) = fun (v: string) -> dispatch (EditDrafts(edit v))
    let signedIn = model.Me.IsSome

    let status =
        match card.Stage with
        | InBacklog -> Html.none
        | InProgress(who, since) -> Html.p [ prop.className "fact"; prop.text $"{who}, since {clock since}" ]
        | WasShipped at -> Html.p [ prop.className "fact"; prop.text $"Shipped {clock at}" ]
        | WasDropped(reason, at) -> Html.p [ prop.className "fact"; prop.text $"Dropped {clock at}: {reason}" ]

    let actions =
        match card.Stage with
        | InBacklog -> [ button "Start" "primary" true (act Start) ]
        | InProgress _ -> [ button "Ship" "primary" true (act Ship); button "Send back" "plain" true (act SendBack) ]
        | WasShipped _
        | WasDropped _ -> [ button "Reopen" "plain" true (act Reopen) ]

    Html.article
        [ prop.className "card"
          // Without a key React reuses a card's element for whichever card is
          // now in that place, and a click in flight lands on the wrong card.
          prop.key (string card.Id)
          prop.children
              [ Html.header
                    [ Html.h3 card.Title
                      Html.button
                          [ prop.className "link"
                            prop.onClick (fun _ -> dispatch (Toggle card))
                            prop.text (
                                if isOpen then "Close"
                                elif card.Comments.Length = 0 then "Details"
                                else $"Details · {card.Comments.Length}"
                            ) ] ]
                if card.Labels.Length > 0 then
                    Html.div
                        [ prop.className "labels"
                          prop.children
                              [ for label in card.Labels do
                                    Html.button
                                        [ prop.key label
                                          prop.className "label"
                                          prop.title $"Show only cards labelled {label}"
                                          prop.onClick (fun _ -> dispatch (FilterBy(Some label)))
                                          prop.text label ] ] ]
                status
                if signedIn then
                    Html.div [ prop.className "actions"; prop.children actions ]
                if isOpen then
                    Html.div
                        [ prop.className "details"
                          prop.children
                              [ for c in card.Comments do
                                    Html.p
                                        [ prop.className "comment"
                                          prop.children [ Html.strong c.Author; Html.text $" {c.Body}" ] ]
                                if card.Comments.Length = 0 && (isDone card || not signedIn) then
                                    Html.p [ prop.className "quiet"; prop.text "No comments." ]
                                if signedIn && not (isDone card) then
                                    Html.div
                                        [ prop.className "row"
                                          prop.children
                                              [ textInput "Add a comment" model.Drafts.Comment (draft (fun v d -> { d with Comment = v })) (act Comment)
                                                button "Comment" "plain" true (act Comment) ] ]

                                    Html.div
                                        [ prop.className "row"
                                          prop.children
                                              [ textInput "Labels, separated by commas" model.Drafts.Labels (draft (fun v d -> { d with Labels = v })) (act Relabel)
                                                button "Label" "plain" true (act Relabel) ] ]

                                    problem "labels" model

                                    Html.div
                                        [ prop.className "row"
                                          prop.children
                                              [ textInput "A new title" model.Drafts.Title (draft (fun v d -> { d with Title = v })) (act Rename)
                                                button "Rename" "plain" true (act Rename) ] ]

                                    problem "title" model

                                    Html.div
                                        [ prop.className "row"
                                          prop.children
                                              [ textInput "Why drop it?" model.Drafts.Reason (draft (fun v d -> { d with Reason = v })) (act Drop)
                                                button "Drop" "danger" true (act Drop) ] ] ] ] ] ]

let private viewColumn (title: string) (note: string) (cards: CardView list) (model: Model) dispatch (footer: ReactElement) =
    Html.section
        [ prop.className "column"
          prop.children
              [ Html.h2 [ Html.text title; Html.span [ prop.className "counts"; prop.text note ] ]
                for card in cards do
                    viewCard model dispatch card
                if cards.IsEmpty then
                    Html.p [ prop.className "quiet"; prop.text "Nothing here." ]
                footer ] ]

let private viewBoard (model: Model) dispatch =
    match model.Board with
    | None ->
        Html.main
            [ Html.p [ Html.a [ prop.href "#/"; prop.text "← Boards" ] ]
              if model.Notice.IsNone then
                  Html.p [ prop.className "quiet"; prop.text "Loading…" ] ]
    | Some board ->
        let shown =
            match model.Filter with
            | Some(_, cards) -> List.ofArray cards
            | None -> List.ofArray board.Cards

        let backlog = shown |> List.filter (fun c -> c.Stage = InBacklog)

        let doing =
            shown
            |> List.filter (fun c ->
                match c.Stage with
                | InProgress _ -> true
                | _ -> false)

        // Without a filter the done column is read a page at a time.
        let finished, finishedNote, pager =
            match model.Filter, model.Finished with
            | Some _, _ ->
                let cards = shown |> List.filter isDone
                cards, string cards.Length, Html.none
            | None, None -> [], "", Html.none
            | None, Some page ->
                List.ofArray page.Cards,
                string page.Total,
                (if page.Pages > 1 then
                     Html.div
                         [ prop.className "pager"
                           prop.children
                               [ button "Newer" "plain" (page.Page > 1) (fun () -> dispatch (ShowFinished(page.Page - 1)))
                                 Html.span [ prop.className "counts"; prop.text $"Page {page.Page} of {page.Pages}" ]
                                 button "Older" "plain" (page.Page < page.Pages) (fun () -> dispatch (ShowFinished(page.Page + 1))) ] ]
                 else
                     Html.none)

        Html.main
            [ Html.p [ Html.a [ prop.href "#/"; prop.text "← Boards" ] ]
              Html.h1 board.Name
              match model.Filter with
              | Some(label, _) ->
                  Html.p
                      [ prop.className "filter"
                        prop.children
                            [ Html.text "Only cards labelled "
                              Html.span [ prop.className "label"; prop.text label ]
                              button "Show all" "link" true (fun () -> dispatch (FilterBy None)) ] ]
              | None -> Html.none
              Html.div
                  [ prop.className "columns"
                    prop.children
                        [ viewColumn
                              "Backlog"
                              (string backlog.Length)
                              backlog
                              model
                              dispatch
                              (if model.Me.IsSome then
                                   Html.div
                                       [ prop.className "form"
                                         prop.children
                                             [ Html.div
                                                   [ prop.className "row"
                                                     prop.children
                                                         [ textInput "A new card" model.NewCard (SetNewCard >> dispatch) (fun () -> dispatch AddCard)
                                                           button "Add" "plain" true (fun () -> dispatch AddCard) ] ]
                                               if model.Open.IsNone then
                                                   problem "title" model ] ]
                               else
                                   Html.p [ prop.className "quiet"; prop.text "Sign in to change this board." ])
                          viewColumn
                              "In progress"
                              (if model.Filter.IsSome then string doing.Length else $"{doing.Length} of {board.WipLimit}")
                              doing
                              model
                              dispatch
                              (if model.Me.IsSome && not doing.IsEmpty && model.Filter.IsNone then
                                   button "Send all back" "plain" true (fun () -> dispatch SendAllBack)
                               else
                                   Html.none)
                          viewColumn "Done" finishedNote finished model dispatch pager ] ] ]

let private viewWork (model: Model) =
    Html.main
        [ Html.h1 "My work"
          match model.Me, model.Work with
          | None, _ -> Html.p [ prop.className "quiet"; prop.text "Sign in to see what you are working on." ]
          | Some _, None -> Html.p [ prop.className "quiet"; prop.text "Loading…" ]
          | Some _, Some [||] -> Html.p [ prop.className "quiet"; prop.text "You are not working on anything." ]
          | Some _, Some work ->
              Html.ul
                  [ prop.className "boards"
                    prop.children
                        [ for item in work do
                              Html.li
                                  [ prop.key (string item.Card)
                                    prop.children
                                        [ Html.a
                                              [ prop.href $"#/{item.Board}"
                                                prop.children
                                                    [ Html.strong item.Title
                                                      Html.span
                                                          [ prop.className "counts"
                                                            prop.text $"{item.BoardName} · since {clock item.Since}" ] ] ] ] ] ] ] ]

let private viewSession (model: Model) dispatch =
    match model.Me with
    | Some name ->
        Html.div
            [ prop.className "session"
              prop.children
                  [ Html.a [ prop.href "#/~work"; prop.text "My work" ]
                    Html.span [ prop.className "quiet"; prop.text name ]
                    button "Sign out" "plain" true (fun () -> dispatch SignOut) ] ]
    | None ->
        Html.div
            [ prop.className "session"
              prop.children
                  [ textInput "Name" model.Sign.Name (fun v -> dispatch (EditSign(fun s -> { s with Name = v }))) ignore
                    input "password" "Password" model.Sign.Password (fun v -> dispatch (EditSign(fun s -> { s with Password = v }))) (fun () -> dispatch SignIn)
                    button "Sign in" "primary" true (fun () -> dispatch SignIn)
                    button "Register" "plain" true (fun () -> dispatch Register) ] ]

let private view (model: Model) dispatch =
    Html.div
        [ prop.className "app"
          prop.children
              [ Html.nav [ Html.a [ prop.href "#/"; prop.className "brand"; prop.text "Kanban" ]; viewSession model dispatch ]
                if model.Me.IsNone then
                    Html.div [ prop.className "session-problems"; prop.children [ problem "name" model; problem "password" model ] ]
                match model.Notice with
                | Some notice ->
                    Html.div
                        [ prop.className "notice"
                          prop.role "alert"
                          prop.children
                              [ Html.span notice
                                Html.button [ prop.className "link"; prop.onClick (fun _ -> dispatch Dismiss); prop.text "Dismiss" ] ] ]
                | None -> Html.none
                match model.Page with
                | BoardList -> viewBoardList model dispatch
                | BoardPage _ -> viewBoard model dispatch
                | WorkPage -> viewWork model ] ]

let private hashChanges (_: Model) : Sub<Msg> =
    let subscribe (dispatch: Msg -> unit) =
        Browser.onHashChange (fun () -> dispatch HashChanged)

        { new IDisposable with
            member _.Dispose() = () }

    [ [ "hash" ], subscribe ]

Program.mkProgram init update view
|> Program.withSubscription hashChanges
|> Program.withReactSynchronous "root"
|> Program.run
