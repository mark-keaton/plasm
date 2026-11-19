// Copy buttons on the docs' code blocks, and the docs search.
(function () {
  const root = document.body.dataset.root || "";

  document.querySelectorAll(".prose pre.code").forEach((pre) => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "copy";
    button.textContent = "Copy";
    const text = pre.textContent;
    button.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(text);
        button.textContent = "Copied";
      } catch {
        button.textContent = "Press Ctrl+C";
      }
      setTimeout(() => (button.textContent = "Copy"), 1600);
    });
    pre.appendChild(button);
  });

  const promptButton = document.getElementById("copy-prompt");
  if (promptButton) {
    promptButton.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(document.getElementById("prompt").textContent);
        promptButton.textContent = "Copied";
      } catch {
        promptButton.textContent = "Select and copy";
      }
      setTimeout(() => (promptButton.textContent = "Copy"), 1600);
    });
  }

  const input = document.getElementById("search");
  const results = document.getElementById("search-results");
  if (!input || !results) return;

  let pages = null;
  const load = async () => {
    if (!pages) pages = await (await fetch(root + "docs/search-index.json")).json();
    return pages;
  };

  const show = (found, words) => {
    results.replaceChildren();
    if (!found.length) {
      const none = document.createElement("p");
      none.textContent = "Nothing found.";
      results.appendChild(none);
    }
    for (const page of found.slice(0, 8)) {
      const a = document.createElement("a");
      a.href = root + "docs/" + page.u;
      a.textContent = page.t;
      const at = page.x.toLowerCase().indexOf(words[0]);
      const span = document.createElement("span");
      span.textContent = at < 0 ? page.s : "… " + page.x.slice(Math.max(0, at - 40), at + 80) + " …";
      a.appendChild(span);
      results.appendChild(a);
    }
    results.hidden = false;
  };

  input.addEventListener("input", async () => {
    const words = input.value.toLowerCase().split(/\s+/).filter(Boolean);
    if (!words.length) { results.hidden = true; return; }
    const all = await load();
    const scored = [];
    for (const page of all) {
      const title = (page.t + " " + page.h).toLowerCase();
      const text = page.x.toLowerCase();
      if (!words.every((w) => title.includes(w) || text.includes(w))) continue;
      scored.push([words.reduce((n, w) => n + (title.includes(w) ? 10 : 1), 0), page]);
    }
    scored.sort((a, b) => b[0] - a[0]);
    show(scored.map((s) => s[1]), words);
  });

  input.addEventListener("keydown", (e) => { if (e.key === "Escape") { results.hidden = true; input.blur(); } });
  document.addEventListener("click", (e) => { if (!e.target.closest(".search")) results.hidden = true; });
})();
