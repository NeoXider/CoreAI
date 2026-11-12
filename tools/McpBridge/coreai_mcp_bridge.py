#!/usr/bin/env python3
"""CoreAI MCP bridge: an MCP endpoint for a game that cannot listen (a WebGL player).

The game links OUT to this process: it attaches, long-polls ``/bridge/next`` for the next MCP request and posts
the answer to ``/bridge/reply``. An MCP client (Claude Code and the like) talks to ``/mcp`` here as to any
streamable-HTTP MCP server. The bridge holds no game state: every request is answered by the game's own
dispatcher, sessions included.

Python 3 standard library only. Binds 127.0.0.1 only.

Endpoints
    POST/DELETE /mcp      the MCP client (Authorization: Bearer <client token>; no browser Origin allowed)
    GET /mcp              405: this bridge offers no SSE stream (tools/list_changed is not relayed)
    POST /bridge/attach   the game ({"instance","name","platform",...}; Authorization: Bearer <game key>)
    POST /bridge/next     the game's long poll ({"instance"}); 200 with a request envelope or 204
    POST /bridge/reply    the game's answer ({"instance","id","status","content_type","body","session"})
    POST /bridge/detach   the game leaves ({"instance"})
    (with --lan ADDRESS a second listener on that address serves /bridge/* only, for a phone without adb)
    GET /bridge/status    the attached game and counters (Authorization: Bearer <client token>)
    GET /health           {"ok": true, "attached": bool}

Security (see Assets/CoreAIMcp/README.md, "Connect to a WebGL build"):
    * loopback bind; the Host header must name this loopback port (DNS rebinding);
    * /mcp refuses any request that carries an Origin (a web page must never drive the client endpoint) and
      requires the client token;
    * /bridge/* answers CORS only for http://localhost:*, http://127.0.0.1:* and http://[::1]:* pages and
      requires the game key, which the page reads from its URL fragment (never sent to the page server);
    * a second game is refused (409, naming the attached one) while the first is alive, unless --takeover;
    * --lan ADDRESS adds a listener on one LAN address for the game routes only (never /mcp); anyone on that
      network who holds the game key can attach as the game, so the key must be at least 16 characters.
"""

from __future__ import annotations

import argparse
import json
import os
import secrets
import select
import socket
import sys
import threading
import time
import uuid
from collections import deque
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 8591
DEFAULT_PAGE_URL = "http://localhost:8777/"
POLL_SECONDS = 8.0
STALE_SECONDS = 10.0
ATTACH_WAIT_SECONDS = 20.0
REQUEST_TIMEOUT_SECONDS = 120.0
MAX_CLIENT_BODY = 4 * 1024 * 1024
MAX_GAME_BODY = 32 * 1024 * 1024
MIN_LAN_KEY_LENGTH = 16
TOKEN_VARIABLE = "COREAI_MCP_TOKEN"
KEY_VARIABLE = "COREAI_MCP_BRIDGE_KEY"
LOOPBACK_HOSTS = ("127.0.0.1", "localhost", "[::1]", "::1")


def _utc_now():
    return datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def _split_host(value):
    """Return (host, port) of a Host header or an origin authority; port None when absent or invalid."""
    value = (value or "").strip()
    if value.startswith("["):
        end = value.find("]")
        if end < 0:
            return value, None
        host, rest = value[: end + 1], value[end + 1 :]
        port = rest[1:] if rest.startswith(":") else None
    else:
        host, _, port = value.partition(":")
        port = port or None
    try:
        return host.lower(), int(port) if port is not None else None
    except ValueError:
        return host.lower(), -1


def is_loopback_origin(origin, extra_hosts=()):
    """True for http://localhost:*, http://127.0.0.1:* and http://[::1]:* (any port, or none), and for the
    hosts in ``extra_hosts`` (the --lan address, for a WebGL page a phone opened from this machine)."""
    if not origin:
        return False
    parts = urlsplit(origin.strip())
    if parts.scheme != "http" or parts.path not in ("", "/") or parts.query or parts.fragment:
        return False
    host, port = _split_host(parts.netloc)
    return (host in LOOPBACK_HOSTS or host in extra_hosts) and port != -1


class PendingRequest:
    """One client request waiting for the game's answer."""

    def __init__(self, method, session, protocol, body):
        self.id = uuid.uuid4().hex
        self.method = method
        self.session = session
        self.protocol = protocol
        self.body = body
        self.created = time.monotonic()
        self.done = threading.Event()
        self.reply = None

    def envelope(self):
        return {
            "id": self.id,
            "method": self.method,
            "session": self.session,
            "protocol": self.protocol,
            "body": self.body,
        }


class BridgeState:
    """The attached game, the queue of client requests and the answers in flight. One lock for all of it."""

    def __init__(self, takeover=False, stale_seconds=STALE_SECONDS):
        self.cond = threading.Condition()
        self.takeover = takeover
        self.stale_seconds = stale_seconds
        self.attached = None
        self.polling = 0
        self.queue = deque()
        self.in_flight = {}
        self.counters = {"client_requests": 0, "answered": 0, "timed_out": 0, "refused_games": 0, "attaches": 0}

    def _alive(self, now):
        game = self.attached
        if game is None:
            return False
        return self.polling > 0 or now - game["last_seen"] < self.stale_seconds

    def describe_attached(self, now=None):
        now = time.monotonic() if now is None else now
        game = self.attached
        if game is None:
            return None
        info = {key: game[key] for key in ("instance", "name", "platform", "unity", "coreai", "origin", "attached_at")}
        info["idle_seconds"] = 0.0 if self.polling > 0 else round(now - game["last_seen"], 1)
        return info

    def attach(self, info, origin):
        """Returns (status, payload)."""
        now = time.monotonic()
        instance = str(info.get("instance") or "")
        if not instance:
            return 400, {"ok": False, "error": "attach needs an instance id"}
        with self.cond:
            current = self.attached
            if current is not None and current["instance"] != instance and self._alive(now) and not self.takeover:
                self.counters["refused_games"] += 1
                return 409, {
                    "ok": False,
                    "error": "another game is attached to this bridge",
                    "attached": self.describe_attached(now),
                }
            if current is not None and current["instance"] != instance:
                self._fail_in_flight_locked("the game was replaced by another page before it answered")
            self.attached = {
                "instance": instance,
                "name": str(info.get("name") or ""),
                "platform": str(info.get("platform") or ""),
                "unity": str(info.get("unity") or ""),
                "coreai": str(info.get("coreai") or ""),
                "origin": origin or "",
                "attached_at": _utc_now(),
                "last_seen": now,
            }
            self.counters["attaches"] += 1
            self.cond.notify_all()
            return 200, {"ok": True, "attached": self.describe_attached(now)}

    def detach(self, instance):
        with self.cond:
            if self.attached is not None and self.attached["instance"] == instance:
                self.attached = None
                self._fail_in_flight_locked("the game detached before it answered")
                self.cond.notify_all()
                return True
            return False

    def is_attached(self, instance):
        with self.cond:
            return self.attached is not None and self.attached["instance"] == instance

    def touch(self, instance):
        with self.cond:
            if self.attached is not None and self.attached["instance"] == instance:
                self.attached["last_seen"] = time.monotonic()

    def next_request(self, instance, timeout, client_gone):
        """Long poll: (status, envelope). 409 when this instance is no longer the attached game."""
        deadline = time.monotonic() + timeout
        with self.cond:
            if self.attached is None or self.attached["instance"] != instance:
                return 409, None
            self.polling += 1
            try:
                while True:
                    if self.attached is None or self.attached["instance"] != instance:
                        return 409, None
                    if self.queue:
                        request = self.queue.popleft()
                        self.in_flight[request.id] = request
                        return 200, request.envelope()
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        return 204, None
                    self.cond.wait(min(remaining, 0.5))
                    # WHY: a reloaded page abandons its poll; noticing it here frees the bridge for the new page
                    # at once instead of after the stale window.
                    if client_gone():
                        if self.attached is not None and self.attached["instance"] == instance:
                            self.attached["last_seen"] = 0.0
                        return 0, None
            finally:
                self.polling -= 1
                if self.attached is not None and self.attached["instance"] == instance and self.attached["last_seen"]:
                    self.attached["last_seen"] = time.monotonic()

    def complete(self, instance, reply):
        with self.cond:
            if self.attached is None or self.attached["instance"] != instance:
                return 409
            request = self.in_flight.pop(str(reply.get("id") or ""), None)
            if request is None:
                return 404
            request.reply = reply
            request.done.set()
            self.counters["answered"] += 1
            self.attached["last_seen"] = time.monotonic()
            return 200

    def submit(self, request, attach_wait, timeout):
        """Queue a client request and wait for its answer: (status, reply or message)."""
        with self.cond:
            self.counters["client_requests"] += 1
            if self.attached is None:
                self.cond.wait_for(lambda: self.attached is not None, attach_wait)
            if self.attached is None:
                return 503, (
                    "No game is attached to the CoreAI MCP bridge. Open the WebGL page with ?coreaiMcp=<port> and "
                    "#coreaiMcpKey=<key> as the bridge printed, or start a desktop player with -coreaiMcpBridge."
                )
            self.queue.append(request)
            self.cond.notify_all()
        if request.done.wait(timeout):
            if request.reply.get("failed"):
                return 503, request.reply["failed"]
            return 200, request.reply
        with self.cond:
            if request in self.queue:
                self.queue.remove(request)
            self.in_flight.pop(request.id, None)
            self.counters["timed_out"] += 1
        return 504, "The game did not answer within %d s (paused, hidden tab, or a long tool call)." % timeout

    def _fail_in_flight_locked(self, reason):
        for request in list(self.in_flight.values()):
            request.reply = {"failed": "The request was lost: " + reason + ". Retry it."}
            request.done.set()
        self.in_flight.clear()

    def snapshot(self):
        with self.cond:
            return {
                "ok": True,
                "attached": self.describe_attached(),
                "queued": len(self.queue),
                "in_flight": len(self.in_flight),
                "counters": dict(self.counters),
            }


class BridgeHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "CoreAiMcpBridge/1.0"
    sys_version = ""

    def log_message(self, fmt, *args):
        return

    # ------------------------------------------------------------------ routing

    def do_GET(self):
        self._route()

    def do_POST(self):
        self._route()

    def do_DELETE(self):
        self._route()

    def do_OPTIONS(self):
        self._route()

    def do_PUT(self):
        self._route()

    def _route(self):
        route = urlsplit(self.path).path.rstrip("/") or "/"
        if not self._host_allowed():
            self._drain()
            self._text(403, "Forbidden: unexpected Host header (DNS-rebinding protection). Connect to "
                            "http://127.0.0.1:%d directly." % self.server.server_port)
            return
        if route == "/health":
            self._drain()
            with self.server.state.cond:
                attached = self.server.state.attached is not None
            self._json(200, {"ok": True, "attached": attached})
            return
        if route.startswith("/bridge/"):
            self._game_route(route)
            return
        if route in ("/mcp", "/"):
            self._client_route()
            return
        self._drain()
        self._text(404, "Unknown route " + route)

    # ------------------------------------------------------------------ the game side

    def _game_route(self, route):
        origin = self.headers.get("Origin")
        if origin is not None and not is_loopback_origin(origin, self.server.lan_hosts):
            self._drain()
            self._text(403, "Forbidden: Origin '%s' is not a loopback page." % origin, cors=False)
            return
        if self.command == "OPTIONS":
            self._drain()
            self.send_response(204)
            self._cors()
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        if route == "/bridge/status":
            self._drain()
            if self.server.game_only:
                self._text(403, "Forbidden: the status endpoint is loopback-only.")
                return
            if not self._bearer(self.server.client_token):
                self._text(401, "Unauthorized: send the client token.")
                return
            self._json(200, self.server.state.snapshot())
            return
        if self.command != "POST":
            self._drain()
            self._text(405, "Use POST " + route)
            return
        if not self._bearer(self.server.game_key):
            self._drain()
            self._text(401, "Unauthorized: the page must pass the bridge key (#coreaiMcpKey=...).")
            return
        if not self._json_content_type():
            self._drain()
            self._text(415, "Unsupported Media Type: POST application/json.")
            return
        payload = self._read_json(MAX_GAME_BODY)
        if payload is None:
            return
        state = self.server.state
        instance = str(payload.get("instance") or "")
        if route == "/bridge/attach":
            status, answer = state.attach(payload, origin)
            if status == 200:
                self.server.log("attached %s (%s, instance %s)" % (answer["attached"]["name"] or "game",
                                                                     answer["attached"]["platform"], instance))
            elif status == 409:
                self.server.log("refused a second game (instance %s); attached: %s" % (
                    instance, answer["attached"]["instance"]))
            self._json(status, answer)
        elif route == "/bridge/next":
            status, envelope = state.next_request(instance, self.server.poll_seconds, self._client_gone)
            if status == 0:
                self.close_connection = True
                return
            if status == 200:
                self._json(200, envelope)
            elif status == 204:
                self.send_response(204)
                self._cors()
                self.send_header("Content-Length", "0")
                self.end_headers()
            else:
                self._json(409, {"ok": False, "error": "this game is not the attached one; attach again",
                                 "attached": state.snapshot()["attached"]})
        elif route == "/bridge/reply":
            status = state.complete(instance, payload)
            self._json(status, {"ok": status == 200})
        elif route == "/bridge/detach":
            state.detach(instance)
            self.server.log("detached instance %s" % instance)
            self._json(200, {"ok": True})
        else:
            self._text(404, "Unknown route " + route)

    # ------------------------------------------------------------------ the client side

    def _client_route(self):
        if self.server.game_only:
            self._drain()
            self._text(403, "Forbidden: the MCP endpoint is loopback-only; connect the client to "
                            "http://127.0.0.1:%d/mcp on this machine." % self.server.server_port, cors=False)
            return
        if self.headers.get("Origin") is not None:
            self._drain()
            self._text(403, "Forbidden: the MCP endpoint does not serve browser pages.", cors=False)
            return
        if not self._bearer(self.server.client_token):
            self._drain()
            self.send_response(401)
            self.send_header("WWW-Authenticate", 'Bearer realm="coreai-mcp-bridge"')
            body = b"Unauthorized: send 'Authorization: Bearer <token>' (printed when the bridge started)."
            self.send_header("Content-Type", "text/plain; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        if self.command == "GET":
            self._drain()
            self.send_response(405)
            self.send_header("Allow", "POST, DELETE")
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        if self.command not in ("POST", "DELETE"):
            self._drain()
            self._text(405, "Use POST or DELETE /mcp.")
            return
        body = ""
        if self.command == "POST":
            if not self._json_content_type(allow_absent=True):
                self._drain()
                self._text(415, "Unsupported Media Type: POST JSON-RPC as application/json.")
                return
            raw = self._read_body(MAX_CLIENT_BODY)
            if raw is None:
                return
            try:
                body = raw.decode("utf-8")
            except UnicodeDecodeError:
                self._text(400, "The request body is not UTF-8.")
                return
        else:
            self._drain()
        request = PendingRequest(self.command, self.headers.get("Mcp-Session-Id"),
                                 self.headers.get("MCP-Protocol-Version"), body)
        status, reply = self.server.state.submit(request, self.server.attach_wait, self.server.request_timeout)
        if status != 200:
            self._text(status, reply)
            return
        self._relay(reply)

    def _relay(self, reply):
        status = int(reply.get("status") or 500)
        if status in (202, 204):
            self.send_response(status)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        content_type = str(reply.get("content_type") or "application/json")
        payload = str(reply.get("body") or "")
        session = reply.get("session")
        if status == 200 and content_type.startswith("application/json"):
            accept = self.headers.get("Accept") or ""
            if "text/event-stream" in accept.lower():
                data = ("event: message\ndata: " + payload + "\n\n").encode("utf-8")
                content_type = "text/event-stream"
            else:
                data = payload.encode("utf-8")
        else:
            data = payload.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        if session:
            self.send_header("Mcp-Session-Id", str(session))
        if content_type == "text/event-stream":
            self.send_header("Cache-Control", "no-cache")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    # ------------------------------------------------------------------ helpers

    def _host_allowed(self):
        host, port = _split_host(self.headers.get("Host"))
        return host in self.server.allowed_hosts and port == self.server.server_port

    def _bearer(self, expected):
        header = (self.headers.get("Authorization") or "").strip()
        if header.lower().startswith("bearer "):
            header = header[7:].strip()
        return bool(expected) and secrets.compare_digest(header.encode("utf-8"), expected.encode("utf-8"))

    def _json_content_type(self, allow_absent=False):
        value = self.headers.get("Content-Type")
        if value is None:
            return allow_absent
        media = value.split(";", 1)[0].strip().lower()
        return media == "application/json" or media.endswith("+json")

    def _client_gone(self):
        try:
            readable, _, _ = select.select([self.connection], [], [], 0)
            if not readable:
                return False
            return self.connection.recv(1, socket.MSG_PEEK) == b""
        except (OSError, ValueError):
            return True

    def _read_body(self, limit):
        raw_length = self.headers.get("Content-Length")
        if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
            self._text(411, "Send a Content-Length.")
            self.close_connection = True
            return None
        try:
            length = int(raw_length or 0)
        except ValueError:
            self._text(400, "Bad Content-Length.")
            self.close_connection = True
            return None
        if length > limit:
            self._text(413, "Payload Too Large: the body exceeds %d bytes." % limit)
            self.close_connection = True
            return None
        data = b""
        while len(data) < length:
            chunk = self.rfile.read(length - len(data))
            if not chunk:
                break
            data += chunk
        return data

    def _read_json(self, limit):
        raw = self._read_body(limit)
        if raw is None:
            return None
        try:
            payload = json.loads(raw.decode("utf-8")) if raw.strip() else {}
        except (ValueError, UnicodeDecodeError) as ex:
            self._json(400, {"ok": False, "error": "invalid JSON: " + str(ex)})
            return None
        if not isinstance(payload, dict):
            self._json(400, {"ok": False, "error": "the body must be a JSON object"})
            return None
        return payload

    def _drain(self):
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            length = 0
        if 0 < length <= 65536:
            self.rfile.read(length)
        elif length > 65536:
            self.close_connection = True

    def _cors(self):
        origin = self.headers.get("Origin")
        if origin is not None and is_loopback_origin(origin, self.server.lan_hosts):
            self.send_header("Access-Control-Allow-Origin", origin)
            self.send_header("Vary", "Origin")
            self.send_header("Access-Control-Allow-Methods", "POST, OPTIONS")
            self.send_header("Access-Control-Allow-Headers", "Authorization, Content-Type")
            self.send_header("Access-Control-Max-Age", "600")
            if (self.headers.get("Access-Control-Request-Private-Network") or "").lower() == "true":
                self.send_header("Access-Control-Allow-Private-Network", "true")

    def _json(self, status, payload):
        data = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Cache-Control", "no-store")
        self._cors()
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _text(self, status, message, cors=True):
        data = (message or "").encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "text/plain; charset=utf-8")
        if cors:
            self._cors()
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


class BridgeServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = False

    def __init__(self, address, client_token, game_key, takeover=False, poll_seconds=POLL_SECONDS,
                 attach_wait=ATTACH_WAIT_SECONDS, request_timeout=REQUEST_TIMEOUT_SECONDS,
                 stale_seconds=STALE_SECONDS, quiet=False, state=None, game_only=False, allowed_hosts=None):
        if not client_token or not game_key:
            raise ValueError("the bridge needs a client token and a game key")
        self.client_token = client_token
        self.game_key = game_key
        self.poll_seconds = poll_seconds
        self.attach_wait = attach_wait
        self.request_timeout = request_timeout
        self.quiet = quiet
        self.state = state or BridgeState(takeover=takeover, stale_seconds=stale_seconds)
        self.game_only = game_only
        self.allowed_hosts = frozenset(allowed_hosts or LOOPBACK_HOSTS)
        self.lan_hosts = frozenset(self.allowed_hosts - frozenset(LOOPBACK_HOSTS)) if game_only else frozenset()
        super().__init__(address, BridgeHandler)

    def log(self, text):
        if not self.quiet:
            print("%s %s" % (_utc_now(), text), flush=True)


def create_server(port=DEFAULT_PORT, client_token=None, game_key=None, **options):
    """A bound, not yet serving bridge on 127.0.0.1. Port 0 picks a free one (tests)."""
    token = client_token or os.environ.get(TOKEN_VARIABLE) or secrets.token_urlsafe(24)
    key = game_key or os.environ.get(KEY_VARIABLE) or secrets.token_urlsafe(18)
    return BridgeServer((DEFAULT_HOST, port), token, key, **options)


def create_lan_server(main, address):
    """A second listener of ``main`` on ``address`` (same port, same game, same key) serving the game routes
    only, for a phone that cannot reach this machine's loopback. Refuses a game key under 16 characters."""
    if len(main.game_key) < MIN_LAN_KEY_LENGTH:
        raise ValueError("--lan needs a game key of at least %d characters (COREAI_MCP_BRIDGE_KEY)" %
                         MIN_LAN_KEY_LENGTH)
    return BridgeServer((address, main.server_port), main.client_token, main.game_key,
                        poll_seconds=main.poll_seconds, attach_wait=main.attach_wait,
                        request_timeout=main.request_timeout, quiet=main.quiet, state=main.state, game_only=True,
                        allowed_hosts={address.lower()})


def validate_lan_address(address):
    """The --lan value must be one IPv4 address of this machine that is neither loopback nor a wildcard."""
    try:
        packed = socket.inet_aton(address)
    except OSError:
        raise ValueError("--lan takes an IPv4 address of this machine, for example 192.168.1.20")
    if address.count(".") != 3 or packed[0] == 127 or address == "0.0.0.0":
        raise ValueError("--lan takes the LAN address the phone reaches (not loopback, not 0.0.0.0)")
    return address


def main(argv=None):
    parser = argparse.ArgumentParser(prog="coreai_mcp_bridge",
                                     description="MCP endpoint for a CoreAI game that links out (WebGL).")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT, help="loopback port (default %(default)s)")
    parser.add_argument("--page-url", default=DEFAULT_PAGE_URL,
                        help="the served WebGL page, for the printed link (default %(default)s)")
    parser.add_argument("--takeover", action="store_true",
                        help="let a newly attaching game replace the attached one (page reloads)")
    parser.add_argument("--request-timeout", type=float, default=REQUEST_TIMEOUT_SECONDS,
                        help="seconds a client request waits for the game (default %(default)s)")
    parser.add_argument("--attach-wait", type=float, default=ATTACH_WAIT_SECONDS,
                        help="seconds a client request waits for a game to attach (default %(default)s)")
    parser.add_argument("--lan", metavar="ADDRESS", default=None,
                        help="also listen on this LAN IPv4 address for the game routes only (a phone without "
                             "adb); anyone on that network with the game key can attach as the game")
    args = parser.parse_args(argv)

    lan_address = None
    if args.lan:
        try:
            lan_address = validate_lan_address(args.lan.strip())
        except ValueError as ex:
            print("error: %s" % ex, file=sys.stderr)
            return 2
    server = create_server(args.port, takeover=args.takeover, request_timeout=args.request_timeout,
                           attach_wait=args.attach_wait)
    lan = None
    if lan_address:
        try:
            lan = create_lan_server(server, lan_address)
        except (ValueError, OSError) as ex:
            server.server_close()
            print("error: %s" % ex, file=sys.stderr)
            return 2
        threading.Thread(target=lan.serve_forever, kwargs={"poll_interval": 0.2}, daemon=True).start()
    page = args.page_url.split("#", 1)[0]
    separator = "&" if "?" in page else "?"
    print("CoreAI MCP bridge listening on http://127.0.0.1:%d (loopback only)" % server.server_port, flush=True)
    print("  game page:  %s%scoreaiMcp=%d#coreaiMcpKey=%s" % (page, separator, server.server_port, server.game_key),
          flush=True)
    print("  client:     claude mcp add --transport http coreai http://127.0.0.1:%d/mcp --header "
          "\"Authorization: Bearer %s\"" % (server.server_port, server.client_token), flush=True)
    if lan:
        print("  phone:      bridge address %s, port %d, key %s (game routes only; the client stays on "
              "127.0.0.1)" % (lan_address, server.server_port, server.game_key), flush=True)
        print("  warning:    anyone on this network who has the key can attach as the game", flush=True)
    else:
        print("  phone:      adb reverse tcp:%d tcp:%d, then bridge address 127.0.0.1, port %d, key above" %
              (server.server_port, server.server_port, server.server_port), flush=True)
    if not os.environ.get(TOKEN_VARIABLE):
        print("  set %s (and %s) to keep the token (and the page link) the same across runs" %
              (TOKEN_VARIABLE, KEY_VARIABLE), flush=True)
    try:
        server.serve_forever(poll_interval=0.2)
    except KeyboardInterrupt:
        pass
    finally:
        if lan:
            lan.shutdown()
            lan.server_close()
        server.server_close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
