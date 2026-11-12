"""Prints an agent's session from the request bodies the proxy captured with --capture-dir.

Every chat request a CoreAI player sends carries the whole conversation so far (system prompt, context blocks,
the player's message, the model's answers and tool calls, the tool results), so the newest captured request of a
turn is that turn's session. A stopgap until CoreAI can export its own sessions (TODO.md, "MCP beyond parity").

Usage (from the repository root):
    python tools/G11Proxy/session_view.py <capture dir>                 the newest request, texts cut at 600 chars
    python tools/G11Proxy/session_view.py <capture dir> --chars 0       nothing cut
    python tools/G11Proxy/session_view.py <capture dir> --file <name>   one captured request
    python tools/G11Proxy/session_view.py <capture dir> --list          every captured request: time, messages, size
Standard library only.
"""

import argparse
import json
import sys
from pathlib import Path


def load_body(path):
    """The request body of one capture file (the proxy writes the body as sent)."""
    data = json.loads(path.read_text(encoding="utf-8"))
    body = data.get("body", data) if isinstance(data, dict) else data
    return json.loads(body) if isinstance(body, str) else body


def cut(text, chars):
    text = (text or "").strip()
    return text if chars <= 0 or len(text) <= chars else text[:chars] + " ...[%d more]" % (len(text) - chars)


def flatten(content):
    if isinstance(content, list):
        return " ".join(part.get("text", "[%s]" % part.get("type")) for part in content if isinstance(part, dict))
    return content or ""


def print_session(body, chars):
    tools = [t.get("function", {}).get("name", "?") for t in body.get("tools", [])]
    print("model:", body.get("model"), "| tools offered (%d):" % len(tools), ", ".join(tools))
    for index, message in enumerate(body.get("messages", [])):
        role = (message.get("role") or "?").upper()
        text = flatten(message.get("content"))
        if role == "SYSTEM":
            print("\n#%d SYSTEM (%d chars)" % (index, len(text)))
            if chars <= 0:
                print("  " + text.replace("\n", "\n  "))
            continue
        label = role if role != "TOOL" else "TOOL result for %s" % message.get("tool_call_id", "?")
        print("\n#%d %s" % (index, label))
        if message.get("reasoning_content"):
            print("  [thinking] " + cut(message["reasoning_content"], chars).replace("\n", "\n  "))
        if text.strip():
            print("  " + cut(text, chars).replace("\n", "\n  "))
        for call in message.get("tool_calls") or []:
            function = call.get("function", {})
            print("  -> %s %s" % (function.get("name"), cut(function.get("arguments"), chars).replace("\n", "\n     ")))


def main(argv):
    # WHY: a Windows console defaults to its code page (cp1251 here) and a session's text holds any character.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    parser =argparse.ArgumentParser(description="Print an agent session from captured chat requests.")
    parser.add_argument("capture_dir")
    parser.add_argument("--file", help="one capture file name instead of the newest")
    parser.add_argument("--chars", type=int, default=600, help="cut every text at this many characters; 0 = never")
    parser.add_argument("--list", action="store_true", help="list the captured chat requests")
    args = parser.parse_args(argv)

    files = sorted(Path(args.capture_dir).glob("*POST-v1_chat_completions*.json"))
    if not files:
        print("no captured chat requests in", args.capture_dir)
        return 1

    if args.list:
        for path in files:
            body = load_body(path)
            print("%s  messages=%d  %d KB" % (path.name, len(body.get("messages", [])), path.stat().st_size // 1024))
        return 0

    path = Path(args.capture_dir) / args.file if args.file else files[-1]
    print("request:", path.name, "(%d captured)" % len(files))
    print_session(load_body(path), args.chars)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
