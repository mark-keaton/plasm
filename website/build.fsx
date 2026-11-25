// Builds the Plasm site into _site/ from website/, docs/, README.md and ROADMAP.md.
//
//   dotnet fsi website/build.fsx                  build
//   dotnet fsi website/build.fsx --koans <dir>    also copy the browser koans' build to _site/koans/
//
// Each fact stays written once: the docs pages are the repository's Markdown,
// the docs menu is docs/README.md, and the roadmap is ROADMAP.md. A code block
// that names its source (`<!-- from: path -->` in Markdown, `data-from="path"`
// in HTML) must still be in that file, line for line and in order, or the
// build fails.
#r "nuget: Markdig, 0.37.0"

open System
open System.IO
open System.Net
open System.Text
open System.Text.RegularExpressions
open Markdig
open Markdig.Syntax
open Markdig.Syntax.Inlines

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let site = Path.Combine(root, "_site")
let repoUrl = "https://github.com/mark-keaton/plasm"
let siteUrl = "https://mark-keaton.github.io/plasm"
let branch = "main"

let args = fsi.CommandLineArgs |> Array.skip 1

let koansBuild =
    match args |> Array.tryFindIndex ((=) "--koans") with
    | Some i when i + 1 < args.Length -> Some(Path.GetFullPath args[i + 1])
    | _ -> None

let problems = ResizeArray<string>()
let read (path: string) = File.ReadAllText(Path.Combine(root, path))
let enc (s: string) = WebUtility.HtmlEncode s
let slash (path: string) = path.Replace('\\', '/')

let write (path: string) (text: string) =
    let full = Path.Combine(site, path)
    Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
    File.WriteAllText(full, text, UTF8Encoding false)

// -- Snippets must still be in their source ----------------------------------

/// Every line of `block`, trimmed, is a line of the file at `source`, in order.
/// A line reading `// ...` marks lines left out.
let checkSnippet (where: string) (source: string) (block: string) =
    let full = Path.Combine(root, source)

    if not (File.Exists full) then
        problems.Add $"{where}: a snippet names {source}, which does not exist"
    else
        let lines = File.ReadAllLines full |> Array.map (fun l -> l.Trim())
        let mutable at = 0
        let mutable failed = false

        for line in block.Split('\n') |> Array.map (fun l -> l.Trim()) do
            if not failed && line <> "" && line <> "// ..." then
                match Array.IndexOf(lines, line, at) with
                | -1 ->
                    failed <- true
                    problems.Add $"{where}: this line is not in {source} (or not after the lines before it):\n    {line}"
                | found -> at <- found + 1

let fromBlock = Regex(@"<!-- from: (?<src>[^ ]+) -->\r?\n```(?<lang>\w+)\r?\n(?<body>.*?)\r?\n```", RegexOptions.Singleline)
let anyCodeBlock = Regex(@"^```(fsharp|csharp)\r?$", RegexOptions.Multiline)

/// Checks a Markdown file's snippets. With `strict`, every F# or C# block must name its source.
let checkMarkdown (path: string) (strict: bool) =
    let text = read path
    let named = fromBlock.Matches text

    for m in named do
        checkSnippet path m.Groups["src"].Value m.Groups["body"].Value

    if strict && anyCodeBlock.Matches(text).Count <> named.Count then
        problems.Add $"{path}: every fsharp or csharp block here must be preceded by <!-- from: path -->"

let htmlBlock = Regex(@"<pre[^>]*data-from=""(?<src>[^""]+)""[^>]*>(?<body>.*?)</pre>", RegexOptions.Singleline)

let checkHtml (path: string) =
    for m in htmlBlock.Matches(read path) do
        checkSnippet path m.Groups["src"].Value (WebUtility.HtmlDecode m.Groups["body"].Value)

// -- The docs menu: docs/README.md -------------------------------------------

type Page = { Section: string; Title: string; Source: string } // Source is relative to docs/

let pages =
    let item = Regex(@"^- \[(?<title>[^\]]+)\]\((?<path>[^)]+\.md)\)")
    let mutable section = ""

    [ for line in (read "docs/README.md").Split('\n') do
          if line.StartsWith "## " then
              section <- line.Substring(3).Trim()
          else
              let m = item.Match line

              if m.Success then
                  { Section = section; Title = m.Groups["title"].Value; Source = m.Groups["path"].Value } ]

let htmlOf (page: Page) = Path.ChangeExtension(page.Source, ".html") |> slash
let bySource = pages |> List.map (fun p -> slash (Path.GetFullPath(Path.Combine(root, "docs", p.Source))), p) |> dict

/// A relative link from the folder of one site path to another site path.
let relative (fromPath: string) (toPath: string) =
    let fromDir = Path.GetDirectoryName(Path.Combine(site, fromPath))
    Path.GetRelativePath(fromDir, Path.Combine(site, toPath)) |> slash

/// Where a link written in a repository file leads on the site: a docs page
/// when its target is one, the file on GitHub otherwise.
let resolveLink (fileDir: string) (sitePath: string) (url: string) =
    if url.StartsWith "#" || url.Contains "://" || url.StartsWith "mailto:" then
        url
    else
        let path, fragment =
            match url.IndexOf '#' with
            | -1 -> url, ""
            | i -> url.Substring(0, i), url.Substring i

        let full = Path.GetFullPath(Path.Combine(fileDir, path))

        match bySource.TryGetValue(slash full) with
        | true, page -> relative sitePath ("docs/" + htmlOf page) + fragment
        | _ ->
            let inRepo = Path.GetRelativePath(root, full) |> slash

            if inRepo.StartsWith ".." then
                url
            elif Directory.Exists full then
                $"{repoUrl}/tree/{branch}/{inRepo}{fragment}"
            elif File.Exists full then
                $"{repoUrl}/blob/{branch}/{inRepo}{fragment}"
            else
                problems.Add $"{slash (Path.GetRelativePath(root, fileDir))}: the link {url} leads nowhere"
                url

let pipeline = MarkdownPipelineBuilder().UseAdvancedExtensions().Build()

let rec inlineText (inl: Inline) : string =
    match inl with
    | :? LiteralInline as l -> l.Content.ToString()
    | :? CodeInline as c -> c.Content
    | :? ContainerInline as c -> c |> Seq.map inlineText |> String.concat ""
    | _ -> ""

let codeFence = Regex(@"<pre><code class=""language-(\w+)"">")

/// Markdown to HTML with the site's code blocks, the links resolved, and each
/// snippet's source shown above it. Returns the HTML, the title and the h2s.
let render (repoPath: string) (sitePath: string) =
    let fileDir = Path.GetDirectoryName(Path.Combine(root, repoPath))

    let text =
        Regex.Replace(
            read repoPath,
            @"<!-- from: ([^ ]+) -->",
            fun m ->
                let src = m.Groups[1].Value
                $"<p class=\"code-from\"><a href=\"{repoUrl}/blob/{branch}/{src}\">{src}</a></p>\n"
        )

    let doc = Markdown.Parse(text, pipeline)

    for link in doc.Descendants<LinkInline>() do
        if not (isNull link.Url) then
            link.Url <- resolveLink fileDir sitePath link.Url

    let headings = doc.Descendants<HeadingBlock>() |> Seq.toList
    let textOf (h: HeadingBlock) = if isNull h.Inline then "" else inlineText h.Inline

    let title =
        headings |> List.tryFind (fun h -> h.Level = 1) |> Option.map textOf |> Option.defaultValue repoPath

    let sections =
        [ for h in headings do
              if h.Level = 2 then
                  Markdig.Renderers.Html.HtmlAttributesExtensions.GetAttributes(h).Id, textOf h ]

    let html = Markdown.ToHtml(doc, pipeline)
    let html = codeFence.Replace(html, "<pre class=\"code $1\">").Replace("<pre><code>", "<pre class=\"code\">").Replace("</code></pre>", "</pre>")
    let html = html.Replace("<table>", "<div class=\"table-box\"><table>").Replace("</table>", "</table></div>")
    html, title, sections

// -- The page shell -----------------------------------------------------------

let head (prefix: string) (title: string) (description: string) =
    $"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{enc title}</title>
<meta name="description" content="{enc description}">
<link rel="icon" href="{prefix}logo.svg" type="image/svg+xml">
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600&amp;family=JetBrains+Mono:wght@400;500&amp;family=Space+Grotesk:wght@500;600;700&amp;display=swap" rel="stylesheet">
<link rel="stylesheet" href="{prefix}styles.css">
</head>
<body data-root="{prefix}">
"""

let firstDoc = "docs/" + htmlOf pages.Head

let nav (prefix: string) (current: string) =
    let link name href =
        let here = if name = current then " aria-current=\"page\"" else ""
        $"""<a href="{href}"{here}>{name}</a>"""

    let search =
        if current = "Docs" then
            """<label class="search"><span class="sr">Search the docs</span><input type="search" id="search" placeholder="Search the docs" autocomplete="off"><div id="search-results" hidden></div></label>"""
        else
            ""

    $"""<header class="nav">
  <div class="wrap">
    <a class="brand" href="{prefix}index.html"><img src="{prefix}logo.svg" width="28" height="28" alt=""><span>Plasm</span></a>
    {if current = "Docs" then search else "<span class=\"pill\">version 0.3 · PostgreSQL and SQLite · native AOT</span>"}
    <span class="spacer"></span>
    <nav class="links" aria-label="Main">
      {link "Docs" (prefix + firstDoc)}
      {link "Koans" (prefix + "koans/")}
      {link "Examples" (prefix + "examples.html")}
      {link "Roadmap" (prefix + "roadmap.html")}
      <a href="{repoUrl}">GitHub</a>
      <a class="mono" href="{prefix}llms.txt">llms.txt</a>
    </nav>
  </div>
</header>
"""

let footer (prefix: string) =
    $"""<footer>
  <div class="wrap">
    <span>Plasm: persistence for an F# domain over PostgreSQL or SQLite. Its design comes from Ecto, whose behavioural test cases it adapts (Apache-2.0).</span>
    <nav aria-label="Footer">
      <a href="{prefix}{firstDoc}">Docs</a>
      <a href="{prefix}koans/">Koans</a>
      <a href="{prefix}roadmap.html">Roadmap</a>
      <a href="{repoUrl}">GitHub</a>
      <a href="{repoUrl}/blob/{branch}/LICENSE">MIT licence</a>
    </nav>
  </div>
</footer>
<script src="{prefix}highlight.js"></script>
<script src="{prefix}site.js"></script>
"""

// -- Roadmap: ROADMAP.md ------------------------------------------------------

type RoadmapSection = { Pill: string; Heading: string; Items: string list; Notes: string list }

let inlineHtml (sitePath: string) (markdown: string) =
    let doc = Markdown.Parse(markdown, pipeline)

    for link in doc.Descendants<LinkInline>() do
        if not (isNull link.Url) then
            link.Url <- resolveLink root sitePath link.Url

    let html = Markdown.ToHtml(doc, pipeline).Trim()
    if html.StartsWith "<p>" && html.EndsWith "</p>" then html.Substring(3, html.Length - 7) else html

let roadmapIntro, roadmap =
    let lines = (read "ROADMAP.md").Replace("\r", "").Split('\n')
    let intro = ResizeArray<string>()
    let sections = ResizeArray<RoadmapSection>()
    let paragraph = StringBuilder()

    let flush () =
        if paragraph.Length > 0 then
            let text = paragraph.ToString().Trim()
            paragraph.Clear() |> ignore

            if sections.Count = 0 then
                intro.Add text
            else
                let last = sections[sections.Count - 1]
                sections[sections.Count - 1] <- { last with Notes = last.Notes @ [ text ] }

    for line in lines do
        if line.StartsWith "## " then
            flush ()
            let heading = line.Substring 3

            let pill, rest =
                match heading.IndexOf ':' with
                | -1 -> heading, ""
                | i -> heading.Substring(0, i), heading.Substring(i + 1).Trim()

            sections.Add { Pill = pill; Heading = (if rest = "" then pill else rest.Substring(0, 1).ToUpper() + rest.Substring 1); Items = []; Notes = [] }
        elif line.StartsWith "- " && sections.Count > 0 then
            flush ()
            let last = sections[sections.Count - 1]
            sections[sections.Count - 1] <- { last with Items = last.Items @ [ line.Substring 2 ] }
        elif line.StartsWith "# " then
            ()
        elif line.Trim() = "" then
            flush ()
        else
            paragraph.Append(line).Append(' ') |> ignore

    flush ()
    String.concat " " intro, List.ofSeq sections

let section (pill: string) =
    match roadmap |> List.tryFind (fun s -> s.Pill = pill) with
    | Some s -> s
    | None ->
        problems.Add $"ROADMAP.md has no section named {pill}"
        { Pill = pill; Heading = pill; Items = []; Notes = [] }

let roadmapPage () =
    let sitePath = "roadmap.html"
    let items (s: RoadmapSection) = s.Items |> List.map (fun i -> $"<li>{inlineHtml sitePath i}</li>") |> String.concat "\n"
    let notes (s: RoadmapSection) = s.Notes |> List.map (fun n -> $"<p class=\"small\">{inlineHtml sitePath n}</p>") |> String.concat "\n"

    let column (pill: string) (cls: string) =
        let s = section pill
        $"""<section class="road-card">
<div class="road-head"><span class="chip {cls}">{enc s.Pill}</span><h2>{enc s.Heading}</h2></div>
<ul>
{items s}
</ul>
{notes s}
</section>"""

    let shipped = section "Shipped"
    let notPlanned = section "Not planned"

    head "" "Plasm roadmap" "What Plasm does today, what is being built, and what it will not do."
    + nav "" "Roadmap"
    + $"""<main class="wrap road">
<div class="lede">
<h1>Roadmap</h1>
<p>{inlineHtml sitePath roadmapIntro}</p>
</div>
<div class="road-row">
{column "Now" "now"}
{column "Next" "next"}
{column "Later" "later"}
</div>
<div class="road-row">
<section class="road-card wide">
<h2>{enc shipped.Heading}</h2>
<ul class="two">
{items shipped}
</ul>
{notes shipped}
</section>
<section class="road-card sunk">
<h2>{enc notPlanned.Heading}</h2>
<ul>
{items notPlanned}
</ul>
</section>
</div>
<p class="small">Want something here? <a href="{repoUrl}/issues">Open an issue on GitHub</a>. This page is generated from <a href="{repoUrl}/blob/{branch}/ROADMAP.md">ROADMAP.md</a>.</p>
</main>
"""
    + footer ""

let roadmapTeaser () =
    let row (pill: string) (cls: string) (count: int) =
        (section pill).Items
        |> List.truncate count
        |> List.map (fun i -> $"""<div class="teaser-row"><span class="chip {cls}">{pill}</span><span>{inlineHtml "index.html" i}</span></div>""")

    row "Now" "now" 2 @ row "Next" "next" 2 @ row "Later" "later" 1 |> String.concat "\n"

// -- Docs pages ---------------------------------------------------------------

let docsMenu (current: Page) =
    let sitePath = "docs/" + htmlOf current

    pages
    |> List.groupBy (fun p -> p.Section)
    |> List.map (fun (name, group) ->
        let links =
            group
            |> List.map (fun p ->
                let here = if p = current then " aria-current=\"page\"" else ""
                $"""<a href="{relative sitePath ("docs/" + htmlOf p)}"{here}>{enc p.Title}</a>""")
            |> String.concat "\n"

        $"""<div class="menu-group"><span class="label">{enc name}</span>
{links}
</div>""")
    |> String.concat "\n"

let searchIndex = ResizeArray<string>()
let json (s: string) = "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "").Replace("\t", " ") + "\""

let docsPage (index: int) (page: Page) =
    let repoPath = "docs/" + page.Source
    let sitePath = "docs/" + htmlOf page
    let prefix = relative sitePath "index.html" |> fun p -> p.Substring(0, p.Length - "index.html".Length)
    let html, title, sections = render repoPath sitePath

    let onThisPage =
        if sections.IsEmpty then
            ""
        else
            let links = sections |> List.map (fun (id, text) -> $"""<a href="#{id}">{enc text}</a>""") |> String.concat "\n"

            $"""<aside class="toc on-page" aria-label="On this page"><span class="label">On this page</span>
{links}
</aside>"""

    let neighbour (label: string) (other: Page option) =
        match other with
        | Some p -> $"""<a class="{label.ToLower()}" href="{relative sitePath ("docs/" + htmlOf p)}"><span>{label}</span><strong>{enc p.Title}</strong></a>"""
        | None -> "<span></span>"

    let previous = if index > 0 then Some pages[index - 1] else None
    let next = if index + 1 < pages.Length then Some pages[index + 1] else None

    let plain = Regex.Replace(Regex.Replace(html, "<[^>]+>", " "), @"\s+", " ") |> WebUtility.HtmlDecode
    let headings = sections |> List.map snd |> String.concat " · "
    searchIndex.Add $"""{{"t":{json page.Title},"s":{json page.Section},"u":{json (htmlOf page)},"h":{json headings},"x":{json plain}}}"""

    write
        sitePath
        (head prefix $"{title} · Plasm" $"{page.Title}: Plasm documentation."
         + nav prefix "Docs"
         + $"""<div class="wrap docs">
<nav class="toc menu" aria-label="Docs">
{docsMenu page}
</nav>
<main class="content prose">
<p class="crumbs">Docs / {enc page.Section} / {enc page.Title}</p>
{html}
<div class="pager">
{neighbour "Previous" previous}
{neighbour "Next" next}
</div>
<p class="small">This page comes from the repository's Markdown. <a href="{repoUrl}/edit/{branch}/{repoPath}">Edit it on GitHub</a></p>
</main>
{onThisPage}
</div>
"""
         + footer prefix)

    // The page as plain Markdown, for llms.txt: a link to another docs page
    // stays relative, and a link that leaves the docs leads to GitHub.
    let fileDir = Path.GetDirectoryName(Path.Combine(root, repoPath))

    let markdown =
        Regex.Replace(
            Regex.Replace(read repoPath, @"<!-- from: ([^ ]+) -->\r?\n", "From `$1`:\n\n"),
            @"\]\((?<url>[^)\s]+)\)",
            fun m ->
                let url = m.Groups["url"].Value
                let resolved = resolveLink fileDir sitePath url
                "](" + (if resolved.Contains "://" then resolved else url) + ")"
        )

    write ("docs/" + page.Source) markdown

// -- llms.txt -----------------------------------------------------------------

let llms () =
    let summary =
        "Plasm is a stricter data layer for a C# application, over PostgreSQL or SQLite. "
        + "The domain, its queries and its transactions are written in one small F# project, and C# calls it. "
        + "It is native AOT first, and Hot Chocolate friendly on the runtime. Plasm is new: this is version 0.4, and its API may change before 1.0."

    let index = StringBuilder()
    index.Append($"# Plasm\n\n> {summary}\n\n") |> ignore
    index.Append($"Source: {repoUrl}. Every page below is plain Markdown. All of them in one file: {siteUrl}/llms-full.txt\n") |> ignore

    for name, group in pages |> List.groupBy (fun p -> p.Section) do
        index.Append($"\n## {name}\n\n") |> ignore

        for p in group do
            index.Append($"- [{p.Title}]({siteUrl}/docs/{slash p.Source})\n") |> ignore

    index.Append($"\n## Optional\n\n- [Roadmap]({repoUrl}/blob/{branch}/ROADMAP.md)\n- [README]({repoUrl}/blob/{branch}/README.md)\n") |> ignore
    write "llms.txt" (index.ToString())

    let full = StringBuilder()
    full.Append($"# Plasm\n\n> {summary}\n") |> ignore

    for p in pages do
        full.Append($"\n\n---\n\nSource: {siteUrl}/docs/{slash p.Source}\n\n") |> ignore
        full.Append(File.ReadAllText(Path.Combine(site, "docs", p.Source))) |> ignore

    write "llms-full.txt" (full.ToString())

// -- Build --------------------------------------------------------------------

let rec copyTree (source: string) (target: string) =
    Directory.CreateDirectory target |> ignore

    // GitHub Pages cannot serve the pre-compressed copies the koans' build writes.
    for file in Directory.GetFiles source do
        if not (file.EndsWith ".br" || file.EndsWith ".gz") then
            File.Copy(file, Path.Combine(target, Path.GetFileName file), true)

    for dir in Directory.GetDirectories source do
        copyTree dir (Path.Combine(target, Path.GetFileName dir))

let koansFallback () =
    head "../" "The Plasm Koans" "Learn Plasm by filling in blanks."
    + nav "../" "Koans"
    + $"""<main class="wrap road">
<div class="lede">
<h1>The Plasm Koans</h1>
<p>Small exercises, each with a blank to fill in, run against a real PostgreSQL. The browser version is not part of this build of the site. In a terminal:</p>
</div>
<pre class="code shell">git clone {repoUrl}
cd plasm
dotnet run --project koans/Plasm.Koans</pre>
<p class="small">It needs the .NET 11 SDK and Docker. <a href="{repoUrl}/blob/{branch}/koans/CURRICULUM.md">The curriculum</a></p>
</main>
"""
    + footer "../"

/// The prompt as the page shows it: a line per paragraph or bullet, bullets
/// hanging. What Copy copies is the file's own text, kept beside it.
let promptHtml () =
    let text = (read "website/prompt.txt").Trim()

    let lines =
        [ for line in text.Replace("\r", "").Split('\n') do
              if line = "" then "<div class=\"gap\"></div>"
              elif line.StartsWith "- " then $"<div class=\"bullet\">{enc line}</div>"
              elif line.EndsWith ":" then $"<div class=\"head\">{enc line}</div>"
              else $"<div>{enc line}</div>" ]

    String.concat "\n" lines + $"\n<textarea id=\"prompt-text\" hidden>{enc text}</textarea>"

let build () =
    if Directory.Exists site then
        Directory.Delete(site, true)

    Directory.CreateDirectory site |> ignore

    checkMarkdown "README.md" true

    for page in pages do
        let strict = page.Section = "Start" || page.Section = "Guide"
        checkMarkdown ("docs/" + page.Source) strict

    // Static files, with the shared header and footer put into the pages.
    for file in Directory.GetFiles(Path.Combine(root, "website")) do
        let name = Path.GetFileName file

        if name.EndsWith ".html" then
            checkHtml ("website/" + name)

            let current =
                match name with
                | "examples.html" -> "Examples"
                | _ -> ""

            let text =
                File.ReadAllText(file)
                    .Replace("<!--nav-->", nav "" current)
                    .Replace("<!--footer-->", footer "")
                    .Replace("<!--roadmap-teaser-->", roadmapTeaser ())
                    .Replace("<!--prompt-->", promptHtml ())
                    .Replace("{{docs}}", firstDoc)
                    .Replace("{{repo}}", repoUrl)

            write name text
        elif name <> "build.fsx" then
            File.Copy(file, Path.Combine(site, name), true)

    pages |> List.iteri docsPage
    write "docs/index.html" $"""<!doctype html><meta charset="utf-8"><title>Plasm documentation</title><meta http-equiv="refresh" content="0; url={htmlOf pages.Head}"><a href="{htmlOf pages.Head}">Plasm documentation</a>"""
    write "docs/search-index.json" ("[" + String.concat ",\n" searchIndex + "]")
    write "roadmap.html" (roadmapPage ())
    llms ()

    match koansBuild with
    | Some dir when Directory.Exists dir ->
        copyTree dir (Path.Combine(site, "koans"))
        // The koans are their own application; on the site, its header leads back to the rest.
        let page = Path.Combine(site, "koans", "index.html")
        let own = """<nav class="links" aria-label="Elsewhere">"""
        let text = File.ReadAllText page

        if text.Contains own then
            File.WriteAllText(page, text.Replace(own, own + $"""<a href="../index.html">Plasm</a><a href="../{firstDoc}">Docs</a><a href="../roadmap.html">Roadmap</a><a href="{repoUrl}">GitHub</a>"""))
        else
            problems.Add "the koans' index.html no longer has the header links the site adds its own to"
    | Some dir -> problems.Add $"--koans {dir}: no such directory"
    | None -> write "koans/index.html" (koansFallback ())

    write "404.html" (head "/plasm/" "Not found · Plasm" "Not found." + nav "/plasm/" "" + """<main class="wrap road"><div class="lede"><h1>Not found</h1><p>That page is not here. The menu above leads everywhere.</p></div></main>""" + footer "/plasm/")
    // GitHub Pages must serve folders whose names start with an underscore.
    write ".nojekyll" ""

build ()

// A typed pre-release version goes stale at the next release, and each of
// these pages can do without one: --prerelease, a floating reference, or the
// template's plain install.
let typedPrerelease = Regex @"\b\d+\.\d+\.\d+-(alpha|beta|rc|preview)[\w.]*"

for path in
    [ "README.md"; "ROADMAP.md"; "docs/README.md"; "docs/csharp.md"; "website/prompt.txt" ]
    @ [ for dir, pattern in [ "docs/start", "*.md"; "docs/guide", "*.md"; "website", "*.html" ] do
          for file in Directory.GetFiles(Path.Combine(root, dir), pattern) -> dir + "/" + Path.GetFileName file ] do
    for m in typedPrerelease.Matches(read path) do
        problems.Add $"{path}: names the version {m.Value}, which the next release makes stale; say it without a version"

if problems.Count > 0 then
    for p in problems do
        eprintfn "error: %s" p

    eprintfn "%d problem(s); the site was not built cleanly." problems.Count
    exit 1
else
    printfn "Built %s: %d docs pages, roadmap, llms.txt%s" (Path.GetRelativePath(root, site)) pages.Length (if koansBuild.IsSome then ", koans" else "")
