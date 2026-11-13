// "F# | Generated SQL" toggles for the examples page.
// The SQL comes from sql.js, captured by running each example against
// PostgreSQL; each example's distinctive fragments are highlighted.
(function () {
  const data = window.PLASM_SQL;
  if (!data) return; // sql.js missing: the page stays F#-only.

  const esc = (s) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  const sqlKeywords = /\b(SELECT|FROM|INNER JOIN|LEFT JOIN|ON|WHERE|AND|OR|NOT|EXISTS|ORDER BY|ASC|DESC|NULLS FIRST|NULLS LAST|LIMIT|OFFSET|INSERT INTO|VALUES|RETURNING|UPDATE|SET|DELETE FROM|FOR UPDATE|AS|ANY|ROW_NUMBER|OVER|PARTITION BY|IS NULL|IS NOT NULL|COALESCE|TRUE|FALSE)\b/g;

  // Plain text -> HTML with SQL keywords coloured.
  const colour = (text) => esc(text).replace(sqlKeywords, '<span class="k">$1</span>');

  // Wraps every occurrence of the example's marks (longest first, no overlaps).
  function render(sql, marks) {
    const spans = [];
    [...marks].sort((a, b) => b.length - a.length).forEach((m) => {
      let from = 0, at;
      while ((at = sql.indexOf(m, from)) !== -1) {
        const end = at + m.length;
        if (!spans.some(([s, e]) => at < e && end > s)) spans.push([at, end]);
        from = end;
      }
    });
    spans.sort((a, b) => a[0] - b[0]);
    let html = "", last = 0;
    for (const [s, e] of spans) {
      html += colour(sql.slice(last, s)) + "<mark>" + colour(sql.slice(s, e)) + "</mark>";
      last = e;
    }
    return html + colour(sql.slice(last));
  }

  function buildPanel(id, entry) {
    const panel = document.createElement("div");
    panel.className = "view-sql";
    panel.id = `sql-${id}`;
    panel.setAttribute("role", "tabpanel");

    const caption = document.createElement("p");
    caption.className = "note teal";
    caption.innerHTML = `<strong>What's distinctive here.</strong> ${esc(entry.caption)}`;
    panel.appendChild(caption);

    entry.commands.forEach((cmd, i) => {
      const block = document.createElement("figure");
      block.className = "sql-command";
      const pre = document.createElement("pre");
      pre.className = "code sql";
      pre.innerHTML = render(cmd.sql, entry.marks);
      const meta = document.createElement("figcaption");
      const params = cmd.parameters.length ? cmd.parameters.join(" · ") : "no parameters";
      meta.innerHTML = `<span>${i + 1} of ${entry.commands.length}</span><span>${esc(params)}</span><span>${cmd.rows} row${cmd.rows === 1 ? "" : "s"}</span>`;
      block.append(pre, meta);
      panel.appendChild(block);
    });

    // Static companions (e.g. EF Core's SQL for comparison) join the panel.
    return panel;
  }

  function attachExtras(section, panel) {
    section.querySelectorAll(".sql-extra").forEach((extra) => {
      extra.hidden = false;
      panel.appendChild(extra);
    });
  }

  function addEmpty(panel, entry) {
    if (entry.empty) {
      const none = document.createElement("p");
      none.className = "note";
      none.textContent = entry.empty;
      panel.appendChild(none);
    }
  }

  const sections = [...document.querySelectorAll("section.example[data-sql]")];
  const setters = [];

  sections.forEach((section) => {
    const id = section.dataset.sql;
    const entry = data[id];
    const fs = section.querySelector(".view-fs");
    if (!entry || !fs) return;

    fs.id = `fs-${id}`;
    fs.setAttribute("role", "tabpanel");
    const panel = buildPanel(id, entry);
    addEmpty(panel, entry);
    attachExtras(section, panel);
    panel.hidden = true;
    fs.after(panel);

    const tabs = document.createElement("div");
    tabs.className = "view-tabs";
    tabs.setAttribute("role", "tablist");
    tabs.setAttribute("aria-label", "Show code or generated SQL");
    const mk = (label, controls) => {
      const b = document.createElement("button");
      b.type = "button";
      b.setAttribute("role", "tab");
      b.setAttribute("aria-controls", controls);
      b.textContent = label;
      return b;
    };
    const fsTab = mk(section.dataset.codeLabel || "F#", fs.id);
    const sqlTab = mk(`Generated SQL (${entry.commands.length})`, panel.id);
    tabs.append(fsTab, sqlTab);
    fs.before(tabs);

    const show = (sql) => {
      fs.hidden = sql;
      panel.hidden = !sql;
      fsTab.setAttribute("aria-selected", String(!sql));
      sqlTab.setAttribute("aria-selected", String(sql));
      fsTab.tabIndex = sql ? -1 : 0;
      sqlTab.tabIndex = sql ? 0 : -1;
    };
    fsTab.addEventListener("click", () => show(false));
    sqlTab.addEventListener("click", () => show(true));
    tabs.addEventListener("keydown", (e) => {
      if (e.key === "ArrowRight" || e.key === "ArrowLeft") {
        const toSql = fsTab.getAttribute("aria-selected") === "true";
        show(toSql);
        (toSql ? sqlTab : fsTab).focus();
        e.preventDefault();
      }
    });
    show(false);
    setters.push(show);
  });

  // One switch for every example; the choice is remembered on this device.
  const all = document.getElementById("sql-all");
  if (all && setters.length) {
    all.hidden = false;
    let stored = null;
    try { stored = localStorage.getItem("plasm-sql-all"); } catch (_) {}
    const apply = (on) => {
      all.setAttribute("aria-pressed", String(on));
      all.textContent = on ? "Show F# for every example" : "Show generated SQL for every example";
      setters.forEach((s) => s(on));
    };
    const fromUrl = new URLSearchParams(location.search).get("view");
    if (fromUrl === "sql" || (fromUrl !== "fs" && stored === "1")) apply(true);
    all.addEventListener("click", () => {
      const on = all.getAttribute("aria-pressed") !== "true";
      apply(on);
      try { localStorage.setItem("plasm-sql-all", on ? "1" : "0"); } catch (_) {}
    });
  }
})();
