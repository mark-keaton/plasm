#!/usr/bin/env python3
"""The Plasm versions examples/Starter pins, against what nuget.org lists.

  starter-pins.py ci
      For CI's Starter job. Every pin listed: source=nuget.org. A pin not
      listed is allowed only as a coming version (nuget.org has the package at
      no version, or only at lower ones, and no pinned Plasm package at that
      version): source=repository, and the job packs the repository at that
      version. Any other unlisted pin fails.

  starter-pins.py release VERSION
      For a release, before anything is packed. Every pin must be VERSION or a
      version nuget.org lists: after=check. If not, a stable VERSION fails; a
      pre-release prints a notice and gives after=skip.

  starter-pins.py wait MINUTES
      After a release has published: waits until nuget.org lists every pin.

Outputs go to $GITHUB_OUTPUT, and one line saying what was decided to
$GITHUB_STEP_SUMMARY. nuget.org not answering is a failure, never a reason
to choose the other source.
"""
import json, os, re, sys, time, urllib.error, urllib.request

PROPS = "examples/Starter/Directory.Packages.props"
INDEX = "https://api.nuget.org/v3-flatcontainer/{}/index.json"


def pins():
    text = open(PROPS, encoding="utf-8").read()
    found = re.findall(r'<PackageVersion\s+Include="(Plasm(?:\.[A-Za-z0-9.]+)?)"\s+Version="([^"]+)"', text)
    if not found:
        sys.exit(f"::error::{PROPS} pins no Plasm package.")
    return found


def listed(package):
    """The versions nuget.org lists for a package; none if it has no such package."""
    url = INDEX.format(package.lower())
    for attempt in range(4):
        try:
            with urllib.request.urlopen(url, timeout=30) as response:
                return [v.lower() for v in json.load(response)["versions"]]
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return []
            problem = f"HTTP {e.code}"
        except (urllib.error.URLError, TimeoutError, ValueError) as e:
            problem = str(e)
        time.sleep(5 * (attempt + 1))
    sys.exit(f"::error::nuget.org did not answer for {package}: {problem}")


def key(version):
    """Orders versions: by number, and a pre-release before its release."""
    number, _, suffix = version.partition("-")
    parts = tuple(int(n) for n in number.split("."))
    labels = tuple((0, int(s), "") if s.isdigit() else (1, 0, s) for s in suffix.split(".")) if suffix else ()
    return (parts, 0 if suffix else 1, labels)


def output(name, value):
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a") as f:
            f.write(f"{name}={value}\n")


def say(line):
    print(line)
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a") as f:
            f.write(line + "\n")


def ci():
    coming, published = [], []
    for package, version in pins():
        versions = listed(package)
        if version.lower() in versions:
            published.append((package, version))
            continue
        higher = [v for v in versions if key(v) >= key(version.lower())]
        if higher:
            sys.exit(f"::error::{PROPS} pins {package} {version}, which nuget.org does not list, and it is not a coming "
                     f"version: nuget.org has {max(higher, key=key)}. Pin a listed version, or one above every listed one.")
        coming.append((package, version))
    if not coming:
        output("source", "nuget.org")
        say("The Starter is built from nuget.org: it lists every Plasm package the Starter pins.")
        return
    versions = {v for _, v in pins()}
    if len(versions) != 1:
        sys.exit(f"::error::{PROPS} pins Plasm packages at more than one version ({', '.join(sorted(versions))}) and one "
                 "is not on nuget.org yet. The repository is packed at one version, so the pins must agree.")
    if published:
        sys.exit(f"::error::{PROPS} pins {coming[0][0]} {coming[0][1]}, which nuget.org does not list, beside "
                 f"{published[0][0]} at the same version, which it does. A coming release has a version no Plasm "
                 "package has yet.")
    output("source", "repository")
    output("version", versions.pop())
    missing = ", ".join(f"{p} {v}" for p, v in coming)
    say(f"The Starter is built from the repository, packed: not on nuget.org yet: {missing}.")


def release(version):
    absent = [(p, v) for p, v in pins() if v.lower() != version.lower() and v.lower() not in listed(p)]
    if not absent:
        output("after", "check")
        say(f"The Starter's Plasm pins are {version} or already on nuget.org; it is tested from nuget.org after publishing.")
        return
    names = ", ".join(f"{p} {v}" for p, v in absent)
    if "-" in version:
        output("after", "skip")
        say(f"::notice::The Starter pins {names}, which is not this pre-release ({version}) and not on nuget.org. "
            "The Starter is not tested from nuget.org after this release.")
        return
    sys.exit(f"::error::The Starter pins {names}: not the version being released ({version}) and not on nuget.org. "
             f"Released like this, the Starter on main would name a version that does not exist. Change {PROPS} or the tag.")


def wait(minutes):
    deadline = time.time() + 60 * minutes
    while True:
        absent = [(p, v) for p, v in pins() if v.lower() not in listed(p)]
        if not absent:
            say("nuget.org lists every Plasm package the Starter pins.")
            return
        names = ", ".join(f"{p} {v}" for p, v in absent)
        if time.time() > deadline:
            sys.exit(f"::error::After {minutes} minutes nuget.org still does not list {names}.")
        print(f"Not listed yet: {names}. Waiting.")
        time.sleep(30)


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    if mode == "ci" and len(sys.argv) == 2:
        ci()
    elif mode == "release" and len(sys.argv) == 3:
        release(sys.argv[2])
    elif mode == "wait" and len(sys.argv) == 3:
        wait(int(sys.argv[2]))
    else:
        sys.exit(__doc__)
