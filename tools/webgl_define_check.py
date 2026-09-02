"""Usage: python tools/webgl_define_check.py (run from the repo root after Unity generated the csproj files).

Compile WebGL-define variants of the generated csproj files to catch symbol errors inside
UNITY_WEBGL && !UNITY_EDITOR blocks without a full player build. Assembly names stay unchanged so
InternalsVisibleTo keeps working; only the defines and output folders differ."""
import os
import re
import subprocess

ROOT = r"D:\Git\CoreAI"
EDITOR_DEFINES = {
    "UNITY_EDITOR", "UNITY_EDITOR_64", "UNITY_EDITOR_WIN", "UNITY_STANDALONE_WIN",
    "UNITY_STANDALONE", "PLATFORM_STANDALONE_WIN", "PLATFORM_STANDALONE", "ENABLE_MONO",
}
for name in ["CoreAI.Mods", "CoreAI.Source"]:
    path = os.path.join(ROOT, name + ".csproj")
    src = open(path, encoding="utf-8").read()
    m = re.search(r"<DefineConstants>([^<]*)</DefineConstants>", src)
    defs = [d for d in m.group(1).split(";") if d and d not in EDITOR_DEFINES]
    defs += ["UNITY_WEBGL", "PLATFORM_WEBGL"]
    out = src.replace(m.group(0), "<DefineConstants>" + ";".join(defs) + "</DefineConstants>")
    base_tag = "<BaseIntermediateOutputPath>obj/WebGlCheck/" + name + "/</BaseIntermediateOutputPath>"
    out_tag = "<OutputPath>Temp/Bin/WebGlCheck/" + name + "/</OutputPath>"
    out = re.sub(r"<BaseIntermediateOutputPath>[^<]*</BaseIntermediateOutputPath>", lambda _: base_tag, out)
    out = re.sub(r"<OutputPath>[^<]*</OutputPath>", lambda _: out_tag, out)
    check = os.path.join(ROOT, name + ".WebGlCheck.csproj")
    open(check, "w", encoding="utf-8").write(out)
    subprocess.run(["dotnet", "restore", check, "-v", "q", "-nologo"], capture_output=True, text=True, cwd=ROOT)
    r = subprocess.run(["dotnet", "build", check, "--no-restore", "-v", "q", "-nologo"],
                       capture_output=True, text=True, cwd=ROOT)
    errs = sorted(set(l.strip() for l in (r.stdout + r.stderr).splitlines() if "error CS" in l))
    print(name, "webgl-defines errors:", len(errs))
    for e in errs[:10]:
        print("   ", e.replace(ROOT + "\\", "")[:220])
    os.remove(check)
