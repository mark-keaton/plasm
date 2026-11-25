// The home page's tour: one request followed through the solution the
// template makes. The stops are plain articles in index.html, readable in
// order without this script; with it, the section pins a file tree beside one
// stop at a time and scrolling steps through them. Every code block there is
// quoted from a repository file, which the site build checks.
(function () {
  var root = document.querySelector(".tour");
  if (!root) return;
  var $ = function (id) { return document.getElementById("tour-" + id); };
  var tree = $("tree"), dot = $("dot"), trail = $("trail"), pipe = $("pipe"), dots = $("dots"), scrolly = $("scrolly");
  var stops = Array.prototype.slice.call(root.querySelectorAll(".stop"));
  var N = stops.length, i = -1;
  var still = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  var CHAPTERS = ["Your C#", "The data layer", "Back out"];

  var S = stops.map(function (el) {
    return {
      el: el, row: el.dataset.row, up: el.hasAttribute("data-up"), seam: el.hasAttribute("data-seam"),
      lit: (el.dataset.lit || el.dataset.row).split(" "), path: el.dataset.path, chapter: +el.dataset.chapter
    };
  });

  // -- code: a span per line, a few token colours, the lines a stop points at
  function esc(s) { return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;"); }
  var KW = {
    cs: "var|await|async|return|switch|public|sealed|record|union|static|new|int|string|class|interface|using|if|else|throw",
    fs: "let|type|member|match|with|if|then|elif|else|private|static|of|for|in|do|fun|task|module|open|null|when",
    sql: "create|table|not|null|primary|key|default|constraint|unique|with|time|zone|references|on|delete|cascade"
  };
  function paint(line, lang) {
    if (!KW[lang]) return esc(line);
    var re = new RegExp('(\\/\\/.*$|--.*$)|("(?:[^"\\\\]|\\\\.)*")|\\b(' + KW[lang] + ')\\b|\\b([A-Z][A-Za-z0-9]*)\\b', "g");
    var out = "", at = 0, m;
    while ((m = re.exec(line))) {
      out += esc(line.slice(at, m.index));
      out += '<i class="' + (m[1] ? "c" : m[2] ? "s" : m[3] ? "k" : "t") + '">' + esc(m[0]) + "</i>";
      at = m.index + m[0].length;
    }
    return out + esc(line.slice(at));
  }
  // "2,6-12" -> the set of those line numbers, counted from 1
  function marked(spec) {
    var set = {};
    (spec || "").split(",").forEach(function (part) {
      var ends = part.split("-"), from = +ends[0], to = +(ends[1] || ends[0]);
      for (var n = from; n && n <= to; n++) set[n] = true;
    });
    return set;
  }
  Array.prototype.forEach.call(root.querySelectorAll("pre.tcode"), function (pre) {
    var hl = marked(pre.dataset.hl), lang = pre.dataset.lang;
    pre.innerHTML = pre.textContent.split("\n").map(function (line, n) {
      return "<span" + (hl[n + 1] ? ' class="hl"' : "") + ">" + (paint(line, lang) || " ") + "</span>";
    }).join("");
  });

  // -- the tree: where the dot rests, and how much of the pipe is travelled
  var rows = {};
  Array.prototype.forEach.call(tree.querySelectorAll("[data-row]"), function (r) { rows[r.dataset.row] = r; });
  function offsetIn(el) { var y = 0; while (el && el !== tree) { y += el.offsetTop; el = el.offsetParent; } return y; }
  function center(key) { var r = rows[key]; return offsetIn(r) + r.offsetHeight / 2; }
  function place() {
    if (i < 0) return;
    var top = center("program"), bottom = center("db"), y = center(S[i].row);
    pipe.style.top = top + "px"; pipe.style.height = (bottom - top) + "px";
    dot.style.transform = "translateY(" + (y - 8.5) + "px)";
    if (S[i].up) { trail.style.top = y + "px"; trail.style.height = (bottom - y) + "px"; }
    else { trail.style.top = top + "px"; trail.style.height = (y - top) + "px"; }
  }

  // -- a phone has no tree: one line says where the request is
  function crumbHtml(s) {
    var m = /^src\/([^/]+)\/(.+)$/.exec(s.path), arrow = s.up ? "↑" : "↓";
    if (m && /^Seam\//.test(m[2])) return arrow + ' <span class="where sm"><b>The seam</b></span><span class="sep">generated C#</span>';
    if (m) return arrow + ' <span class="where ' + (/Api$/.test(m[1]) ? "cs" : "fs") + '"><b>' + esc(m[1]) + '</b></span><span class="sep">›</span>' + esc(m[2]);
    if (s.row === "db") return arrow + ' <span class="where db"><b>Database</b></span><span class="sep">PostgreSQL or SQLite</span>';
    return "";
  }

  S.forEach(function (s, k) {
    var b = document.createElement("button");
    b.type = "button";
    if (k > 0 && s.chapter !== S[k - 1].chapter) b.className = "gap";
    b.setAttribute("aria-label", "Step " + (k + 1) + " of " + N);
    b.addEventListener("click", function () { go(k); });
    dots.appendChild(b);
  });

  // -- the loop: a dot per stop beside its layer, above the chip on the way
  // down, below it on the way back. A layer's data-rows names its tree rows,
  // or a stop's data-lane names the layer when it is not a row (the user).
  // On the way back a pill rides beside the dot, saying what the answer is there (data-carry).
  var loop = $("loop"), lanes = Array.prototype.slice.call(loop.querySelectorAll(".lane")), last = lanes.length - 1;
  var marks = S.map(function (s) {
    s.lane = -1;
    var key = s.el.dataset.lane || s.row;
    lanes.forEach(function (l, n) { if ((l.dataset.rows || "").split(" ").indexOf(key) >= 0) s.lane = n; });
    var d = document.createElement("i");
    d.className = "d " + (s.up ? "back" : s.el.hasAttribute("data-startup") ? "start" : "out");
    if (s.lane >= 0) lanes[s.lane].querySelector(s.up ? ".slot.back" : ".slot.out").appendChild(d);
    return d;
  });
  function strip(s) {
    var start = s.el.hasAttribute("data-startup");
    lanes.forEach(function (l, n) { l.classList.toggle("here", n === s.lane); });
    marks.forEach(function (d, n) { d.classList.toggle("on", n === i); d.classList.toggle("done", n < i); });
    loop.style.setProperty("--to", start ? 0 : s.up ? last : s.lane);
    loop.style.setProperty("--from", s.up ? s.lane : last);
    loop.classList.toggle("turned", s.up || s.lane === last);
    loop.classList.toggle("home", s.up && s.lane === 0);
    var carry = s.el.dataset.carry;
    loop.classList.toggle("carrying", !!carry);
    if (carry) {
      $("carry").textContent = carry;
      $("carry-short").textContent = s.el.dataset.carryShort || carry;
      loop.style.setProperty("--at", s.lane);
      loop.classList.toggle("carry-edge", s.lane === 0);
    }
    $("step").textContent = (i + 1) + " / " + N;
  }

  function show(k) {
    if (k === i) return;
    i = k;
    var s = S[i];
    strip(s);
    if (root.classList.contains("begun") !== i > 0) {
      root.classList.toggle("begun", i > 0);
      fit(); setTimeout(fit, 300);
    }
    root.classList.toggle("up", s.up);
    Object.keys(rows).forEach(function (key) { rows[key].classList.toggle("here", s.lit.indexOf(key) >= 0); });
    $("seam").classList.toggle("on", s.seam);
    place();
    $("chip").textContent = s.up ? "↑ coming back" : "↓ going down";
    $("path").textContent = s.path;
    $("count").textContent = (i + 1) + " of " + N;
    $("chapter").textContent = CHAPTERS[s.chapter];
    var crumb = crumbHtml(s);
    $("crumb").innerHTML = crumb;
    $("crumb").hidden = !crumb;
    Array.prototype.forEach.call(tree.querySelectorAll(".proj"), function (b) {
      b.classList.toggle("faded", !b.querySelector(".here") && !(b.id === "tour-seam" && s.seam));
    });
    Array.prototype.forEach.call(dots.children, function (d, n) {
      d.classList.toggle("on", n === i);
      d.classList.toggle("done", n < i);
    });
    S.forEach(function (other, n) { other.el.classList.toggle("on", n === i); });
    $("body").scrollTop = 0;
  }

  // How far the pinned picture has been scrolled through its section decides the stop.
  function span() { return scrolly.offsetHeight - window.innerHeight; }
  function onScroll() {
    var p = -scrolly.getBoundingClientRect().top / span();
    show(Math.max(0, Math.min(N - 1, Math.floor(p * N))));
  }
  function go(k) {
    var top = scrolly.getBoundingClientRect().top + window.scrollY + (k + 0.5) / N * span();
    window.scrollTo({ top: top, behavior: still ? "auto" : "smooth" });
  }

  root.classList.add("live");
  scrolly.style.height = (N * 84) + "vh";
  Object.keys(rows).forEach(function (key) {
    rows[key].addEventListener("click", function () {
      for (var k = 0; k < N; k++) if (S[k].lit.indexOf(key) >= 0) { go(k); break; }
    });
  });
  var waiting = false;
  window.addEventListener("scroll", function () {
    if (!waiting) { waiting = true; requestAnimationFrame(function () { waiting = false; onScroll(); }); }
  }, { passive: true });
  // The panel's height is what the pinned loop leaves of the screen.
  // The heading and the loop above it count: what is measured is from the heading's top to the loop's bottom.
  // The heading is there on the first stop only; it folds away on the next, and the panel takes its height.
  function fit() { root.style.setProperty("--loop-h", Math.ceil(loop.getBoundingClientRect().bottom - $("intro").getBoundingClientRect().top) + "px"); }
  fit();
  window.addEventListener("resize", function () { fit(); place(); onScroll(); });
  if (document.fonts && document.fonts.ready) document.fonts.ready.then(function () { place(); fit(); });
  onScroll();
  window.addEventListener("keydown", function (e) {
    var r = scrolly.getBoundingClientRect();
    if (r.top > window.innerHeight * 0.5 || r.bottom < window.innerHeight * 0.5) return;
    if (e.target && /^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName)) return;
    if (e.key === "ArrowRight") { go(Math.min(N - 1, i + 1)); e.preventDefault(); }
    if (e.key === "ArrowLeft") { go(Math.max(0, i - 1)); e.preventDefault(); }
  });
})();
