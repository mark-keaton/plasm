"""The provider tags hide nothing (docs/status.md: one model on both databases).

Each scenario in features/ runs on PostgreSQL and on SQLite unless tagged for
one. A tag filter could hide scenarios as well as @ignore could, so this
fails unless:
  - no scenario is tagged both @postgres and @sqlite;
  - every @postgres scenario has an @sqlite sibling in its feature, titled
    as it is followed by " on SQLite" (Reqnroll refuses two of one title), or is named in postgres-only.txt with its reason and the
    scenario that covers it on SQLite (its refusal), which must exist, or "-"
    for a test dialect's scenario (its reason says so);
  - every @sqlite scenario has an @postgres sibling (its title without
    " on SQLite"), or is named in
    sqlite-only.txt with its reason;
  - a listed scenario exists, is tagged for its provider, and is listed once;
  - docs/sqlite.md's table of differences is differences.txt's;
  - an untagged scenario (tags on its feature or Rule count) has a Then that is not scoped to one provider
    ("Then on PostgreSQL ...", "And on SQLite ..."): else it would assert
    nothing on the other provider's run.
It prints how many scenarios run on both, on PostgreSQL only and on SQLite
only, and how many steps are scoped to each provider.
    check-tags.py [repository root]
"""
import glob, os, re, sys

root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "..", "..")
here = os.path.dirname(os.path.abspath(__file__))
SCOPES = {"PostgreSQL": "postgres", "SQLite": "sqlite"}
TEST_DIALECT = "a test dialect wrapping PostgreSQL's provider"
SUFFIX = " on SQLite"


def scope_of(step):
    m = re.match(r"on (PostgreSQL|SQLite)\b", step)
    return SCOPES[m.group(1)] if m else None


def scenarios(path):
    """(title, tags, thens) for each scenario: thens as (scope or None)."""
    feature_tags, rule_tags, pending, out, current, phase = set(), set(), set(), [], None, None
    for line in open(path, encoding="utf-8"):
        s = line.strip()
        if s.startswith("@"):
            pending |= {t for t in s.split() if t.startswith("@")}
        elif s.startswith("Feature:"):
            feature_tags, pending = pending, set()
        elif s.startswith("Rule:"):
            rule_tags, pending, current = pending, set(), None
        elif s.startswith("Background:"):
            pending, current = set(), None
        elif re.match(r"Scenario( Outline)?:|Example:", s):
            current = [s.split(":", 1)[1].strip(), (pending | rule_tags | feature_tags) & {"@postgres", "@sqlite"}, []]
            out.append(current)
            pending, phase = set(), None
        elif current is not None:
            m = re.match(r"(Given|When|Then|And|But) (.*)", s)
            if m:
                word, step = m.groups()
                if word in ("Given", "When", "Then"):
                    phase = word
                if phase == "Then":
                    current[2].append(scope_of(step))
                elif scope_of(step):
                    current[2].append(scope_of(step))
    return [tuple(c) for c in out]


def listed(name):
    """feature, scenario, reason[, covered by] for each line of a list."""
    rows = []
    for n, line in enumerate(open(os.path.join(here, name), encoding="utf-8"), 1):
        if line.startswith("#") or not line.strip():
            continue
        cells = line.rstrip("\n").split("\t")
        rows.append((n, cells))
    return rows


def sibling(tag, title):
    """The title of a scenario's sibling for the other provider: Reqnroll
    refuses two scenarios of one title in a feature, so SQLite's is
    PostgreSQL's followed by " on SQLite"."""
    if tag == "@postgres":
        return title + SUFFIX
    return title[: -len(SUFFIX)] if title.endswith(SUFFIX) else None


failures = []
features = {os.path.relpath(p, root): scenarios(p) for p in sorted(glob.glob(os.path.join(root, "features", "**", "*.feature"), recursive=True))}
by = {tag: {(f, t) for f, ss in features.items() for t, tags, _ in ss if tag in tags} for tag in ("@postgres", "@sqlite")}
titles = {(f, t) for f, ss in features.items() for t, _, _ in ss}

for name, tag, other, need_cover in (("postgres-only.txt", "@postgres", "@sqlite", True), ("sqlite-only.txt", "@sqlite", "@postgres", False)):
    seen = set()
    for n, cells in listed(name):
        if len(cells) < (4 if need_cover else 3) or not all(c.strip() for c in cells):
            failures.append(f"{name}:{n}: needs feature, scenario, reason{', covered by' if need_cover else ''}, tab-separated")
            continue
        key = (cells[0], cells[1])
        if key in seen:
            failures.append(f"{name}:{n}: {cells[1]} is listed twice")
        seen.add(key)
        if key not in by[tag]:
            failures.append(f"{name}:{n}: {cells[0]}: no {tag} scenario \"{cells[1]}\"")
        if need_cover and cells[3] == "-":
            # Only a test dialect's scenario has nothing to cover on SQLite:
            # it is a claim about what the core asks of any dialect.
            if not cells[2].startswith(TEST_DIALECT):
                failures.append(f"{name}:{n}: \"-\" covers only a scenario whose reason starts \"{TEST_DIALECT}\"")
        elif need_cover and not any(t == cells[3] for _, t in titles):
            failures.append(f"{name}:{n}: the scenario covering it, \"{cells[3]}\", is in no feature")
    for f, t in sorted(by[tag] - seen):
        if (f, sibling(tag, t)) not in by[other]:
            failures.append(f"{f}: {tag} \"{t}\" has no {other} sibling (\"{sibling(tag, t)}\") and is not in {name}")

counts = {"both": 0, "postgres": 0, "sqlite": 0}
scoped = {"postgres": 0, "sqlite": 0}
for f, ss in features.items():
    for t, tags, thens in ss:
        if len(tags) == 2:
            failures.append(f"{f}: \"{t}\" is tagged both @postgres and @sqlite")
        elif tags:
            counts[next(iter(tags))[1:]] += 1
        else:
            counts["both"] += 1
            if None not in thens:
                failures.append(f"{f}: \"{t}\" is untagged, but no Then of it holds on both databases: tag it for its provider, or assert what holds on both")
        for s in thens:
            if s:
                scoped[s] += 1

# docs/sqlite.md's table of differences is differences.txt's, row for row.
doc = os.path.join(root, "docs", "sqlite.md")
if os.path.exists(doc):
    rows = [l.rstrip("\n").split("\t") for l in open(os.path.join(here, "differences.txt")) if l.strip() and not l.startswith("#")]
    table = ["| " + " | ".join(x.replace("|", "\\|") for x in r[:3]) + " |" for r in rows]
    text = open(doc).read()
    if "\n".join(table) not in text:
        failures.append("docs/sqlite.md's table of differences is not differences.txt's: make it again from it")

for line in failures:
    print("tags: " + line)
print(f"scenarios: {counts['both']} on both, {counts['postgres']} on PostgreSQL only, {counts['sqlite']} on SQLite only; "
      f"steps scoped to PostgreSQL {scoped['postgres']}, to SQLite {scoped['sqlite']}")
sys.exit(1 if failures else 0)
