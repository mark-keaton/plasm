// A small F# highlighter: comments, strings, numbers, keywords, query operations
// and capitalised type names. Code blocks stay readable plain text without it.
(function () {
  const keywords = new Set(("let let! do do! return return! yield match match! with fun function if then else elif for in to " +
    "type of and rec mutable private open module namespace member static new use use! try finally when as not true false null").split(" "));
  const operations = new Set(("where orderBy orderByDescending thenBy thenByDescending take skip " +
    "select join leftJoin on groupBy having").split(" "));
  const token = /(\/\/[^\n]*)|("(?:\\.|[^"\\])*")|(\b\d[\d_]*(?:\.\d+)?(?:L|m|M)?\b)|([A-Za-z_][\w']*!?)/g;

  const esc = (s) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

  function highlight(src) {
    let out = "";
    let last = 0;
    for (const m of src.matchAll(token)) {
      out += esc(src.slice(last, m.index));
      const [text, comment, string, number, word] = m;
      let cls = null;
      if (comment) cls = "c";
      else if (string) cls = "s";
      else if (number) cls = "n";
      else if (word && keywords.has(word)) cls = "k";
      else if (word && operations.has(word)) cls = "f";
      else if (word && /^[A-Z]/.test(word) && src[m.index - 1] !== ".") cls = "t";
      out += cls ? `<span class="${cls}">${esc(text)}</span>` : esc(text);
      last = m.index + text.length;
    }
    return out + esc(src.slice(last));
  }

  document.querySelectorAll("pre.code.fsharp").forEach((pre) => {
    pre.innerHTML = highlight(pre.textContent);
  });

  // Mark the table-of-contents entry for the section in view.
  const toc = document.querySelectorAll(".toc a[href^='#']");
  if (toc.length && "IntersectionObserver" in window) {
    const byId = new Map([...toc].map((a) => [a.getAttribute("href").slice(1), a]));
    const io = new IntersectionObserver((entries) => {
      for (const e of entries) {
        if (e.isIntersecting) {
          toc.forEach((a) => a.classList.remove("active"));
          byId.get(e.target.id)?.classList.add("active");
        }
      }
    }, { rootMargin: "-20% 0px -70% 0px" });
    byId.forEach((_, id) => { const el = document.getElementById(id); if (el) io.observe(el); });
  }
})();
