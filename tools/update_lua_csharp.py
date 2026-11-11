"""Swaps the bundled Lua-CSharp VM for the one in two NuGet packages; downloads nothing.

Usage: python -I tools/update_lua_csharp.py <LuaCSharp.<v>.nupkg> <LuaCSharp.Annotations.<v>.nupkg> [--apply]
                                            [--plugins <dir>]

Reads lib/netstandard2.1/Lua.dll and Lua.Annotations.dll from the packages (untrusted data: only those two members
are read, nothing is executed) and prints version, commit, netstandard2.1 dependencies, size and SHA-256 of each.
With --apply it overwrites the two DLLs in Assets/CoreAIMods/Plugins (or --plugins) in place, leaving their .meta
files untouched, and checks the SHA-256 of what landed on disk. Verify the package signatures first
(dotnet nuget verify); EngineVersion, docs and CHANGELOG are updated by hand, see
dev-docs/DEPENDENCY_VERSIONS_2026-11-11.md, section "Lua-CSharp".
"""
import hashlib
import re
import sys
import zipfile
from pathlib import Path

LIBRARY = "lib/netstandard2.1/"


def read(package, dll):
    with zipfile.ZipFile(package) as archive:
        names = archive.namelist()
        nuspec = [name for name in names if name.endswith(".nuspec") and "/" not in name]
        if len(nuspec) != 1 or LIBRARY + dll not in names:
            raise SystemExit(f"{package}: expected one nuspec and {LIBRARY}{dll}")
        spec = archive.read(nuspec[0]).decode("utf-8")
        data = archive.read(LIBRARY + dll)
    version = re.search(r"<version>([^<]+)</version>", spec).group(1)
    commit = re.search(r'commit="([0-9a-f]+)"', spec)
    group = re.search(r'<group targetFramework="\.NETStandard2\.1"(?:\s*/>|>(.*?)</group>)', spec, re.S)
    dependencies = re.findall(r'<dependency id="([^"]+)" version="([^"]+)"', (group.group(1) or "") if group else "")
    print(f"{dll}: {Path(package).name} version {version}, commit {commit.group(1) if commit else '?'}")
    print(f"  netstandard2.1 dependencies: {', '.join(f'{n} >= {v}' for n, v in dependencies) or 'none'}")
    print(f"  {len(data)} bytes, SHA-256 {hashlib.sha256(data).hexdigest().upper()}")
    return data


def main(argv):
    apply = "--apply" in argv
    plugins = Path(__file__).resolve().parent.parent / "Assets" / "CoreAIMods" / "Plugins"
    if "--plugins" in argv:
        plugins = Path(argv[argv.index("--plugins") + 1]).resolve()
    packages = [arg for arg in argv if arg.endswith(".nupkg")]
    if len(packages) != 2:
        raise SystemExit(__doc__)
    payload = {"Lua.dll": read(packages[0], "Lua.dll"), "Lua.Annotations.dll": read(packages[1], "Lua.Annotations.dll")}
    for dll, data in payload.items():
        target = plugins / dll
        if not (target.with_name(dll + ".meta")).exists():
            raise SystemExit(f"{target}.meta is missing: refusing to create a DLL Unity would import with new GUIDs")
        before = hashlib.sha256(target.read_bytes()).hexdigest().upper() if target.exists() else "absent"
        print(f"{target}: now {before}")
        if apply:
            target.write_bytes(data)
            after = hashlib.sha256(target.read_bytes()).hexdigest().upper()
            if after != hashlib.sha256(data).hexdigest().upper():
                raise SystemExit(f"{target}: written file does not match the package")
            print(f"  replaced, {after}")
    if not apply:
        print("dry run: pass --apply to replace the files")


if __name__ == "__main__":
    main(sys.argv[1:])
