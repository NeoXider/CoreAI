#!/usr/bin/env python3
"""Bump every CoreAI UPM package to one version, in lockstep.

The repo ships six packages under Assets/*/ that release together. This sets the
`version` field AND every internal `com.neoxider.*` dependency pin in each
package.json to the target version, then verifies the result against the same
rule the CI "Package graph (lockstep + deps)" gate enforces (one shared version,
internal pins equal to it). Formatting and line endings are preserved (targeted
regex, not a JSON re-dump).

The one version that also lives in C# — McpServerInfo.Version, advertised to every
MCP client during `initialize` — is rewritten from the same input, so it can no
longer drift behind the manifest (McpPackageVersionEditModeTests guards it).

The two documents that state the current release in prose - the ROADMAP package map and the
Developer Guide footer - are rewritten from the same input as well, with today's date. The
`check_positive_module_opt_in.py` gate reads both back and fails the build when either drifts.

Usage:
    python tools/bump_version.py 6.2.1     # write the bump to every package.json
    python tools/bump_version.py --check   # verify lockstep only, no writes
"""
import datetime
import glob
import json
import os
import re
import sys

SEMVER = re.compile(r"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?$")
VERSION_FIELD = re.compile(r'("version"\s*:\s*")[^"]*(")')
INTERNAL_PIN = re.compile(r'("com\.neoxider\.[a-z0-9.]+"\s*:\s*")[^"]*(")')

MCP_VERSION_FILE = "Assets/CoreAIMcp/Runtime/Protocol/McpMethods.cs"
MCP_VERSION_CONST = re.compile(r'(public const string Version = ")[^"]*(";)')

# Prose that names the current release. check_positive_module_opt_in.py reads both back and fails the
# build when either drifts, so a bump has to rewrite them or the next run of that gate goes red.
VERSIONED_DOCS = (
    ("Docs/ROADMAP.md",
     re.compile(r"(Six UPM packages, released in lockstep \(all currently )[^)]*(\):)")),
    ("Assets/CoreAiUnity/Docs/DEVELOPER_GUIDE.md",
     re.compile(r"(\*\*Version of this guide:\*\* )\d+\.\d+\.\d+ \(\d{4}-\d{2}-\d{2}(\))")),
)


def package_files():
    return sorted(glob.glob("Assets/*/package.json"))


def bump_file(path, version):
    with open(path, encoding="utf-8", newline="") as f:
        text = f.read()
    text, n_version = VERSION_FIELD.subn(r"\g<1>" + version + r"\g<2>", text, count=1)
    text, n_pins = INTERNAL_PIN.subn(r"\g<1>" + version + r"\g<2>", text)
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text)
    return n_version, n_pins


def bump_mcp_const(version):
    """Rewrite the advertised MCP server version. Returns the number of substitutions."""
    if not os.path.exists(MCP_VERSION_FILE):
        return 0
    with open(MCP_VERSION_FILE, encoding="utf-8", newline="") as f:
        text = f.read()
    text, n = MCP_VERSION_CONST.subn(r"\g<1>" + version + r"\g<2>", text, count=1)
    if n:
        with open(MCP_VERSION_FILE, "w", encoding="utf-8", newline="") as f:
            f.write(text)
    return n


def bump_versioned_docs(version, release_date):
    """Rewrite the release line in each doc the opt-in gate checks. Returns (path, count) pairs."""
    results = []
    for path, pattern in VERSIONED_DOCS:
        if not os.path.exists(path):
            results.append((path, 0))
            continue
        with open(path, encoding="utf-8", newline="") as f:
            text = f.read()
        if "Version of this guide" in pattern.pattern:
            replacement = r"\g<1>" + f"{version} ({release_date}" + r"\g<2>"
        else:
            replacement = r"\g<1>" + version + r"\g<2>"
        text, n = pattern.subn(replacement, text, count=1)
        if n:
            with open(path, "w", encoding="utf-8", newline="") as f:
                f.write(text)
        results.append((path, n))
    return results


def mcp_const_version():
    """The version string currently baked into McpServerInfo, or None when unreadable."""
    if not os.path.exists(MCP_VERSION_FILE):
        return None
    with open(MCP_VERSION_FILE, encoding="utf-8") as f:
        match = MCP_VERSION_CONST.search(f.read())
    return match.group(0).split('"')[1] if match else None


def check():
    """Return True when every package shares one version and every internal pin matches it."""
    pkgs = {}
    for path in package_files():
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
        pkgs[data["name"]] = (data.get("version", ""), data.get("dependencies", {}))

    ok = True
    versions = {v[0] for v in pkgs.values()}
    if len(versions) != 1:
        print(f"ERROR: package versions are not in lockstep: {sorted(versions)}")
        ok = False
    for name, (_ver, deps) in pkgs.items():
        for dep, dver in deps.items():
            if dep.startswith("com.neoxider.") and dep in pkgs and dver != pkgs[dep][0]:
                print(f"ERROR: {name} pins {dep}@{dver} but that package is {pkgs[dep][0]}")
                ok = False
    mcp = mcp_const_version()
    if mcp is not None and len(versions) == 1 and mcp not in versions:
        print(f"ERROR: McpServerInfo.Version is {mcp} but the packages are {sorted(versions)[0]}")
        ok = False

    print("packages:", {n: v[0] for n, v in sorted(pkgs.items())}, "| mcp const:", mcp)
    return ok


def main(argv):
    # Run from the repo root regardless of the caller's cwd.
    os.chdir(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

    if not argv or argv[0] in ("-h", "--help"):
        print(__doc__)
        return 0

    if argv[0] == "--check":
        return 0 if check() else 1

    version = argv[0].lstrip("v")
    if not SEMVER.match(version):
        print(f"ERROR: '{version}' is not a valid semver (expected e.g. 6.2.1)")
        return 2

    files = package_files()
    if not files:
        print("ERROR: no Assets/*/package.json found")
        return 2

    total_pins = 0
    for path in files:
        n_version, n_pins = bump_file(path, version)
        total_pins += n_pins
        print(f"  {path}: version x{n_version}, internal pins x{n_pins}")
    print(f"bumped {len(files)} packages to {version} ({total_pins} internal pins)")
    print(f"  {MCP_VERSION_FILE}: McpServerInfo.Version x{bump_mcp_const(version)}")
    release_date = datetime.date.today().isoformat()
    for path, n in bump_versioned_docs(version, release_date):
        print(f"  {path}: release line x{n}")

    if not check():
        print("LOCKSTEP CHECK FAILED after bump")
        return 1
    print(f"LOCKSTEP CHECK PASSED for {version}")
    print("Next: add a CHANGELOG entry, commit, tag, and release.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
