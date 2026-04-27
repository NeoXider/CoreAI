# -*- coding: utf-8 -*-
"""Remove // and /* */ comments from C# sources; keep /// XML docs and // TODO lines."""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOTS = [
    Path(__file__).resolve().parent.parent / "Assets" / "CoreAI",
    Path(__file__).resolve().parent.parent / "Assets" / "CoreAiUnity",
]


def is_todo_comment(after_two_slashes: str) -> bool:
    t = after_two_slashes.lstrip()
    return bool(re.match(r"(?i)todo\b", t))


def is_doc_comment_line(line: str, slash_pos: int) -> bool:
    """True if // at slash_pos is /// and only whitespace precedes it on the line."""
    if slash_pos + 2 >= len(line) or line[slash_pos + 2] != "/":
        return False
    return line[:slash_pos].strip() == ""


def strip_line_comment_and_blocks(line: str) -> str | None:
    """
    Returns processed line, or None to drop the entire line (blank after strip).
    Preserves lines that are entirely XML doc (///...) or // TODO...
    """
    original = line
    ending = ""
    if line.endswith("\r\n"):
        ending = "\r\n"
        line = line[:-2]
    elif line.endswith("\n"):
        ending = "\n"
        line = line[:-1]
    elif line.endswith("\r"):
        ending = "\r"
        line = line[:-1]

    i = 0
    n = len(line)
    out: list[str] = []
    state = "code"  # code | dq | sq | verbatim | interp_str | interp_code
    interp_depth = 0

    def peek(k: int) -> str | None:
        if i + k < n:
            return line[i + k]
        return None

    while i < n:
        c = line[i]

        if state == "code":
            if c == "@" and peek(0) == '"' and i + 1 < n and line[i + 1] == '"':
                out.append(line[i : i + 2])
                i += 2
                state = "verbatim"
                continue
            if c == "$" and i + 1 < n and line[i + 1] == '"':
                out.append('$_"')
                i += 2
                state = "interp_str"
                interp_depth = 0
                continue
            if c == "$" and i + 2 < n and line[i + 1] == "@" and line[i + 2] == '"':
                out.append('$@"')
                i += 3
                state = "verbatim_interp"
                continue
            if c == "/" and peek(0) == "/":
                rest_from_slash = line[i + 2 :]
                if peek(0) == "/":
                    if line[:i].strip() == "":
                        return original if ending else line
                    i += 2
                    while i < n and line[i] == "/":
                        i += 1
                    return (line[: i - (len(line) - i)] if False else None)  # noqa: dead
                if is_todo_comment(rest_from_slash):
                    return original if ending else line
                prefix = "".join(out) + line[:i]
                prefix = prefix.rstrip()
                return (prefix + ending) if prefix.strip() != "" or prefix == "" else prefix + ending

            if c == "/" and peek(0) == "*":
                end = line.find("*/", i + 2)
                if end == -1:
                    prefix = "".join(out) + line[:i]
                    return prefix.rstrip() + ending
                i = end + 2
                continue

            if c == '"':
                out.append(c)
                i += 1
                state = "dq"
                continue
            if c == "'":
                out.append(c)
                i += 1
                state = "sq"
                continue

            out.append(c)
            i += 1
            continue

        if state == "dq":
            out.append(c)
            if c == "\\" and i + 1 < n:
                out.append(line[i + 1])
                i += 2
                continue
            if c == '"':
                i += 1
                state = "code"
                continue
            i += 1
            continue

        if state == "sq":
            out.append(c)
            if c == "\\" and i + 1 < n:
                out.append(line[i + 1])
                i += 2
                continue
            if c == "'":
                i += 1
                state = "code"
                continue
            i += 1
            continue

        if state == "verbatim" or state == "verbatim_interp":
            out.append(c)
            if c == '"' and i + 1 < n and line[i + 1] == '"':
                out.append('"')
                i += 2
                continue
            if c == '"':
                i += 1
                if state == "verbatim_interp":
                    state = "interp_str"
                    interp_depth = 0
                else:
                    state = "code"
                continue
            i += 1
            continue

        if state == "interp_str":
            out.append(c)
            if c == '"' and interp_depth == 0:
                i += 1
                state = "code"
                continue
            if c == "{" and i + 1 < n and line[i + 1] == "{":
                out.append("{")
                i += 2
                continue
            if c == "}" and i + 1 < n and line[i + 1] == "}":
                out.append("}")
                i += 2
                continue
            if c == "{":
                interp_depth += 1
                i += 1
                state = "interp_code"
                continue
            i += 1
            continue

        if state == "interp_code":
            if c == '"' and (len(out) == 0 or out[-1] != "\\"):
                out.append(c)
                i += 1
                state = "dq_in_interp"
                continue
            if c == "'" and (len(out) == 0 or out[-1] != "\\"):
                out.append(c)
                i += 1
                state = "sq_in_interp"
                continue
            if c == "@" and peek(0) == '"' and i + 1 < n and line[i + 1] == '"':
                out.append(line[i : i + 2])
                i += 2
                state = "verbatim_in_interp"
                continue
            if c == "/" and peek(0) == "/":
                rest = line[i + 2 :]
                if peek(0) == "/":
                    i += 3
                    while i < n and line[i] == "/":
                        i += 1
                    continue
                if is_todo_comment(rest):
                    return original if ending else line
                i = n
                break
            if c == "/" and peek(0) == "*":
                end = line.find("*/", i + 2)
                if end == -1:
                    i = n
                    break
                i = end + 2
                continue
            if c == "}":
                interp_depth -= 1
                out.append(c)
                i += 1
                if interp_depth <= 0:
                    interp_depth = 0
                    state = "interp_str"
                continue
            out.append(c)
            i += 1
            continue

        if state == "dq_in_interp":
            out.append(c)
            if c == "\\" and i + 1 < n:
                out.append(line[i + 1])
                i += 2
                continue
            if c == '"':
                i += 1
                state = "interp_code"
                continue
            i += 1
            continue

        if state == "sq_in_interp":
            out.append(c)
            if c == "\\" and i + 1 < n:
                out.append(line[i + 1])
                i += 2
                continue
            if c == "'":
                i += 1
                state = "interp_code"
                continue
            i += 1
            continue

        if state == "verbatim_in_interp":
            out.append(c)
            if c == '"' and i + 1 < n and line[i + 1] == '"':
                out.append('"')
                i += 2
                continue
            if c == '"':
                i += 1
                state = "interp_code"
                continue
            i += 1
            continue

    result = ("".join(out)).rstrip()
    return result + ending if result or not ending else ending


def process_line(line: str) -> str | None:
    stripped_ws = line.lstrip(" \t")
    if stripped_ws.startswith("///"):
        return line
    if stripped_ws.startswith("//") and not stripped_ws.startswith("///"):
        if is_todo_comment(stripped_ws[2:]):
            return line
        return None
    processed = strip_line_comment_and_blocks(line)
    if processed is None:
        return None
    if isinstance(processed, str) and processed.lstrip().startswith("///"):
        return line
    p = processed.rstrip("\r\n") if processed else ""
    if not p.strip():
        return None
    return processed


def process_file(path: Path) -> bool:
    text = path.read_text(encoding="utf-8")
    lines = text.splitlines(keepends=True)
    new_lines: list[str] = []
    changed = False
    for line in lines:
        nl = process_line(line)
        if nl is None:
            if line.strip() != "":
                changed = True
            continue
        if nl != line:
            changed = True
        new_lines.append(nl)
    body = "".join(new_lines)
    if not body.endswith("\n") and text.endswith("\n"):
        body += "\n"
        changed = True
    if changed:
        path.write_text(body, encoding="utf-8", newline="")
    return changed


def main() -> int:
    changed_files: list[Path] = []
    for root in ROOTS:
        if not root.is_dir():
            continue
        for path in sorted(root.rglob("*.cs")):
            if process_file(path):
                changed_files.append(path)
    for p in changed_files:
        print(p)
    print(f"Updated {len(changed_files)} files.", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
