#!/usr/bin/env python3
"""G11 acceptance test proxy.

An OpenAI-compatible forwarding proxy used by the G11 WebGL browser acceptance in
``dev-docs/MVP2_MULTIPLAYER_PLAN.md`` section 6.5 ("Retry, terminal failure, and recovery").
It sits between the served WebGL player and LM Studio, adds browser CORS, streams
``text/event-stream`` completions through untouched, and injects deterministic faults.

Python 3 standard library only.

Control API (JSON over HTTP)::

    POST /control/fail-next  {"count": 1, "status": 503, "body": "...", "path": "/v1"}
        The next ``count`` matching /v1 requests are answered locally with ``status``.
        ``path`` is an optional substring filter (default "/v1" = every proxied request);
        use "chat/completions" so a readiness probe on /v1/models cannot consume a failure.
    POST /control/block      {"enabled": true}
        While enabled every /v1 request is answered 503 immediately, upstream untouched.
    POST /control/hang       {"seconds": 30}
        The next /v1 request is held that long, then answered 503. Exercises the client's
        outer request timeout.
    POST /control/reset      {"counters": false}
        Clears every injection. Counters are kept unless ``counters`` is true.
    GET  /control/state      Counters, last request and the current injection settings.
    GET  /health             {"ok": true, "upstream": "..."}

Injection precedence is hang (one-shot) -> block -> fail-next.
"""

from __future__ import annotations

import argparse
import json
import sys
import threading
import time
from datetime import datetime, timezone
from http.client import HTTPConnection, HTTPSConnection
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 8811
DEFAULT_UPSTREAM = "http://127.0.0.1:1234/v1"
DEFAULT_UPSTREAM_TIMEOUT = 300.0

# Relay unit. Small enough that a single SSE frame is never held back waiting for more.
STREAM_READ_SIZE = 8192

DEFAULT_FAIL_STATUS = 503
DEFAULT_FAIL_BODY = json.dumps(
    {
        "error": {
            "message": "g11-proxy injected failure",
            "type": "g11_proxy_injected",
            "code": "service_unavailable",
        }
    }
)

# Dropped in both directions: per-connection framing is re-decided by this proxy.
HOP_BY_HOP = frozenset(
    {
        "connection",
        "keep-alive",
        "proxy-authenticate",
        "proxy-authorization",
        "proxy-connection",
        "te",
        "trailer",
        "trailers",
        "transfer-encoding",
        "upgrade",
        "host",
        "content-length",
    }
)

ALLOW_METHODS = "GET, POST, OPTIONS"


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


class Logger:
    """One line per request on stdout, optionally appended to a file."""

    def __init__(self, log_file=None):
        self._lock = threading.Lock()
        self._handle = None
        if log_file:
            self._handle = open(log_file, "a", encoding="utf-8", errors="replace")

    def line(self, text):
        with self._lock:
            print(text, flush=True)
            if self._handle is not None:
                self._handle.write(text + "\n")
                self._handle.flush()

    def close(self):
        with self._lock:
            if self._handle is not None:
                self._handle.close()
                self._handle = None


class ProxyState:
    """Injection settings plus the counters served by ``GET /control/state``."""

    def __init__(self, upstream):
        self._lock = threading.Lock()
        self.upstream = upstream
        parts = urlsplit(upstream)
        self.scheme = parts.scheme or "http"
        self.host = parts.hostname or "127.0.0.1"
        self.port = parts.port or (443 if self.scheme == "https" else 80)
        # The upstream path replaces the incoming "/v1" prefix.
        self.base_path = parts.path.rstrip("/") or "/v1"
        self.reset(clear_counters=True)

    def reset(self, clear_counters=False):
        with self._lock:
            self.fail_remaining = 0
            self.fail_status = DEFAULT_FAIL_STATUS
            self.fail_body = DEFAULT_FAIL_BODY
            self.fail_path = "/v1"
            self.blocked = False
            self.block_status = DEFAULT_FAIL_STATUS
            self.hang_seconds = 0.0
            self.hang_status = DEFAULT_FAIL_STATUS
            if clear_counters:
                self.total_requests = 0
                self.total_proxied = 0
                self.injected_failures = 0
                self.upstream_errors = 0
                self.in_flight = 0
                self.last = None

    def set_fail_next(self, count, status, body, path):
        with self._lock:
            self.fail_remaining = max(0, int(count))
            self.fail_status = int(status)
            self.fail_body = body
            self.fail_path = path or "/v1"

    def set_blocked(self, enabled, status):
        with self._lock:
            self.blocked = bool(enabled)
            self.block_status = int(status)

    def set_hang(self, seconds, status):
        with self._lock:
            self.hang_seconds = max(0.0, float(seconds))
            self.hang_status = int(status)

    def take_injection(self, route):
        """Claim a fault for this request, or return None to forward upstream."""
        with self._lock:
            self.total_requests += 1
            if self.hang_seconds > 0.0:
                seconds = self.hang_seconds
                self.hang_seconds = 0.0
                self.injected_failures += 1
                return {"kind": "hang", "seconds": seconds, "status": self.hang_status, "body": None}
            if self.blocked:
                self.injected_failures += 1
                return {"kind": "block", "seconds": 0.0, "status": self.block_status, "body": None}
            if self.fail_remaining > 0 and (self.fail_path == "/v1" or self.fail_path in route):
                self.fail_remaining -= 1
                self.injected_failures += 1
                return {
                    "kind": "fail-next",
                    "seconds": 0.0,
                    "status": self.fail_status,
                    "body": self.fail_body,
                }
        return None

    def begin_forward(self):
        with self._lock:
            self.total_proxied += 1
            self.in_flight += 1

    def end_forward(self):
        with self._lock:
            self.in_flight -= 1

    def note_upstream_error(self):
        with self._lock:
            self.upstream_errors += 1

    def record_last(self, method, path, status, injected):
        with self._lock:
            self.last = {
                "method": method,
                "path": path,
                "status": status,
                "injected": injected,
                "at": _utc_now(),
            }

    def snapshot(self):
        with self._lock:
            return {
                "ok": True,
                "counters": {
                    "total_requests": self.total_requests,
                    "total_proxied": self.total_proxied,
                    "injected_failures": self.injected_failures,
                    "upstream_errors": self.upstream_errors,
                    "in_flight": self.in_flight,
                },
                "last": self.last,
                "settings": {
                    "upstream": self.upstream,
                    "fail_next_remaining": self.fail_remaining,
                    "fail_status": self.fail_status,
                    "fail_path": self.fail_path,
                    "blocked": self.blocked,
                    "block_status": self.block_status,
                    "hang_seconds": self.hang_seconds,
                },
            }


class G11ProxyHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "G11Proxy/1.0"
    sys_version = ""

    # Default stderr chatter would compete with the structured stdout log.
    def log_message(self, fmt, *args):
        return

    @property
    def state(self):
        return self.server.state

    @property
    def logger(self):
        return self.server.logger

    def do_GET(self):
        self._dispatch()

    def do_POST(self):
        self._dispatch()

    def do_OPTIONS(self):
        self._dispatch()

    def do_PUT(self):
        self._dispatch()

    def do_DELETE(self):
        self._dispatch()

    def do_PATCH(self):
        self._dispatch()

    # ---------------------------------------------------------------- routing

    def _dispatch(self):
        started = time.monotonic()
        split = urlsplit(self.path)
        route = split.path

        if self.command == "OPTIONS":
            self._read_body()
            self._send_preflight()
            self._log(started, route, 204, 0, streamed=False, injected="preflight")
            return

        if route == "/health":
            self._read_body()
            size = self._send_json(200, {"ok": True, "upstream": self.state.upstream})
            self._log(started, route, 200, size, streamed=False, injected="control")
            return

        if route.startswith("/control/"):
            self._handle_control(started, route)
            return

        if route == "/v1" or route.startswith("/v1/"):
            self._handle_proxy(started, route, split.query)
            return

        size = self._send_json(404, {"ok": False, "error": "unknown route: " + route})
        self._log(started, route, 404, size, streamed=False, injected="control")

    # ------------------------------------------------------------- control API

    def _handle_control(self, started, route):
        raw = self._read_body()

        if route == "/control/state":
            if self.command != "GET":
                size = self._send_json(405, {"ok": False, "error": "use GET /control/state"})
                self._log(started, route, 405, size, streamed=False, injected="control")
                return
            size = self._send_json(200, self.state.snapshot())
            self._log(started, route, 200, size, streamed=False, injected="control")
            return

        if self.command != "POST":
            size = self._send_json(405, {"ok": False, "error": "use POST " + route})
            self._log(started, route, 405, size, streamed=False, injected="control")
            return

        try:
            payload = json.loads(raw.decode("utf-8")) if raw.strip() else {}
            if not isinstance(payload, dict):
                raise ValueError("body must be a JSON object")
        except (ValueError, UnicodeDecodeError) as ex:
            size = self._send_json(400, {"ok": False, "error": "invalid JSON body: " + str(ex)})
            self._log(started, route, 400, size, streamed=False, injected="control")
            return

        if route == "/control/fail-next":
            self.state.set_fail_next(
                count=payload.get("count", 1),
                status=payload.get("status", DEFAULT_FAIL_STATUS),
                body=payload.get("body", DEFAULT_FAIL_BODY),
                path=payload.get("path", "/v1"),
            )
        elif route == "/control/block":
            self.state.set_blocked(
                enabled=payload.get("enabled", True),
                status=payload.get("status", DEFAULT_FAIL_STATUS),
            )
        elif route == "/control/hang":
            self.state.set_hang(
                seconds=payload.get("seconds", 5),
                status=payload.get("status", DEFAULT_FAIL_STATUS),
            )
        elif route == "/control/reset":
            self.state.reset(clear_counters=bool(payload.get("counters", False)))
        else:
            size = self._send_json(404, {"ok": False, "error": "unknown control route: " + route})
            self._log(started, route, 404, size, streamed=False, injected="control")
            return

        size = self._send_json(200, self.state.snapshot())
        self._log(started, route, 200, size, streamed=False, injected="control")

    # ----------------------------------------------------------------- proxying

    def _handle_proxy(self, started, route, query):
        # The body is drained first so an injected reply still leaves the
        # keep-alive connection correctly framed.
        body = self._read_body()
        injection = self.state.take_injection(route)

        if injection is not None:
            if injection["seconds"] > 0.0:
                time.sleep(injection["seconds"])
            status = injection["status"]
            size = self._send_injected(status, injection["body"])
            self.state.record_last(self.command, route, status, injection["kind"])
            self._log(started, route, status, size, streamed=False, injected=injection["kind"])
            return

        target = self.state.base_path + route[len("/v1"):]
        if query:
            target += "?" + query

        headers = {}
        for key, value in self.headers.items():
            if key.lower() in HOP_BY_HOP:
                continue
            headers[key] = value

        conn_cls = HTTPSConnection if self.state.scheme == "https" else HTTPConnection
        conn = conn_cls(self.state.host, self.state.port, timeout=self.server.upstream_timeout)

        self.state.begin_forward()
        try:
            try:
                conn.request(self.command, target, body=body if body else None, headers=headers)
                response = conn.getresponse()
            except OSError as ex:
                self.state.note_upstream_error()
                size = self._send_json(
                    502,
                    {
                        "ok": False,
                        "error": {
                            "message": "g11-proxy could not reach upstream: " + str(ex),
                            "type": "g11_proxy_upstream_error",
                        },
                        "upstream": self.state.upstream,
                    },
                )
                self.state.record_last(self.command, route, 502, "upstream-error")
                self._log(started, route, 502, size, streamed=False, injected="upstream-error")
                return

            streamed, size = self._relay(response)
            self.state.record_last(self.command, route, response.status, None)
            self._log(started, route, response.status, size, streamed=streamed, injected=None)
        finally:
            self.state.end_forward()
            try:
                conn.close()
            except OSError:
                pass

    def _relay(self, response):
        """Copy an upstream response to the client. Returns (streamed, bytes)."""
        content_type = (response.getheader("Content-Type") or "").lower()
        content_length = response.getheader("Content-Length")
        transfer_encoding = (response.getheader("Transfer-Encoding") or "").lower()
        streamed = (
            "text/event-stream" in content_type
            or content_length is None
            or "chunked" in transfer_encoding
        )

        passthrough = [
            (key, value)
            for key, value in response.getheaders()
            if key.lower() not in HOP_BY_HOP and not key.lower().startswith("access-control-")
        ]

        if not streamed:
            payload = response.read()
            self.send_response(response.status)
            for key, value in passthrough:
                self.send_header(key, value)
            self.send_header("Content-Length", str(len(payload)))
            self._cors_headers()
            self.end_headers()
            if self.command != "HEAD":
                self.wfile.write(payload)
                self.wfile.flush()
            return False, len(payload)

        self.send_response(response.status)
        for key, value in passthrough:
            self.send_header(key, value)
        self.send_header("Transfer-Encoding", "chunked")
        if "text/event-stream" in content_type:
            # Tell any intermediary not to sit on SSE frames.
            self.send_header("X-Accel-Buffering", "no")
        self._cors_headers()
        self.end_headers()

        # read1() returns as soon as one chunk is available; read() would keep pulling
        # further chunks until the buffer filled and destroy incremental delivery.
        read_one = getattr(response, "read1", None)
        total = 0
        try:
            while True:
                chunk = read_one(STREAM_READ_SIZE) if read_one else response.read(1)
                if not chunk:
                    break
                self.wfile.write(b"%x\r\n" % len(chunk) + chunk + b"\r\n")
                self.wfile.flush()
                total += len(chunk)
            self.wfile.write(b"0\r\n\r\n")
            self.wfile.flush()
        except OSError:
            # Client hung up (page reload / navigation) mid-stream.
            self.close_connection = True
        return True, total

    # ------------------------------------------------------------------ output

    def _cors_headers(self):
        requested = self.headers.get("Access-Control-Request-Headers")
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Headers", requested if requested else "*")
        self.send_header("Access-Control-Allow-Methods", ALLOW_METHODS)
        self.send_header("Access-Control-Expose-Headers", "*")
        self.send_header("Access-Control-Max-Age", "86400")

    def _send_preflight(self):
        # 204 must carry no Content-Length; the absent body is unambiguous.
        self.send_response(204)
        self._cors_headers()
        self.end_headers()

    def _send_json(self, status, payload):
        return self._send_bytes(status, json.dumps(payload).encode("utf-8"), "application/json")

    def _send_injected(self, status, body):
        if body is None:
            body = DEFAULT_FAIL_BODY
        if isinstance(body, (dict, list)):
            raw = json.dumps(body).encode("utf-8")
            content_type = "application/json"
        else:
            text = str(body)
            raw = text.encode("utf-8")
            stripped = text.lstrip()
            content_type = (
                "application/json"
                if stripped.startswith("{") or stripped.startswith("[")
                else "text/plain; charset=utf-8"
            )
        return self._send_bytes(status, raw, content_type)

    def _send_bytes(self, status, raw, content_type):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(raw)))
        self.send_header("Cache-Control", "no-store")
        self._cors_headers()
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(raw)
            self.wfile.flush()
        return len(raw)

    def _log(self, started, route, status, size, streamed, injected):
        elapsed_ms = (time.monotonic() - started) * 1000.0
        self.logger.line(
            "%s %s %s status=%d injected=%s bytes=%d dur_ms=%.1f streamed=%s"
            % (
                _utc_now(),
                self.command,
                route,
                status,
                injected if injected else "no",
                size,
                elapsed_ms,
                "yes" if streamed else "no",
            )
        )

    # ------------------------------------------------------------------- input

    def _read_body(self):
        encoding = (self.headers.get("Transfer-Encoding") or "").lower()
        if "chunked" in encoding:
            return self._read_chunked_body()
        raw_length = self.headers.get("Content-Length")
        if not raw_length:
            return b""
        try:
            length = int(raw_length)
        except ValueError:
            return b""
        return self._read_exact(length) if length > 0 else b""

    def _read_exact(self, length):
        out = bytearray()
        while len(out) < length:
            block = self.rfile.read(length - len(out))
            if not block:
                break
            out += block
        return bytes(out)

    def _read_chunked_body(self):
        out = bytearray()
        while True:
            line = self.rfile.readline(65536)
            if not line:
                break
            size_field = line.strip().split(b";", 1)[0]
            try:
                size = int(size_field, 16)
            except ValueError:
                break
            if size == 0:
                while True:  # optional trailers
                    trailer = self.rfile.readline(65536)
                    if not trailer or trailer in (b"\r\n", b"\n"):
                        break
                break
            out += self._read_exact(size)
            self.rfile.readline(65536)  # CRLF after the chunk
        return bytes(out)


class G11ProxyServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, address, state, logger, upstream_timeout):
        self.state = state
        self.logger = logger
        self.upstream_timeout = upstream_timeout
        super().__init__(address, G11ProxyHandler)


def create_server(
    port=DEFAULT_PORT,
    upstream=DEFAULT_UPSTREAM,
    host=DEFAULT_HOST,
    log_file=None,
    upstream_timeout=DEFAULT_UPSTREAM_TIMEOUT,
):
    """Build a bound (not yet serving) proxy. Pass port 0 for an ephemeral port."""
    return G11ProxyServer((host, port), ProxyState(upstream), Logger(log_file), upstream_timeout)


def main(argv=None):
    parser = argparse.ArgumentParser(
        prog="g11_proxy",
        description="OpenAI-compatible forwarding proxy with deterministic fault injection "
        "for the G11 WebGL browser acceptance.",
    )
    parser.add_argument("--host", default=DEFAULT_HOST, help="bind address (default %(default)s)")
    parser.add_argument(
        "--port", type=int, default=DEFAULT_PORT, help="bind port (default %(default)s)"
    )
    parser.add_argument(
        "--upstream", default=DEFAULT_UPSTREAM, help="upstream base URL (default %(default)s)"
    )
    parser.add_argument("--log-file", default=None, help="append request log lines to this file")
    parser.add_argument(
        "--upstream-timeout",
        type=float,
        default=DEFAULT_UPSTREAM_TIMEOUT,
        help="upstream socket timeout in seconds (default %(default)s)",
    )
    args = parser.parse_args(argv)

    server = create_server(
        port=args.port,
        upstream=args.upstream,
        host=args.host,
        log_file=args.log_file,
        upstream_timeout=args.upstream_timeout,
    )
    server.logger.line(
        "%s START g11_proxy listening=http://%s:%d upstream=%s timeout_s=%.0f"
        % (_utc_now(), args.host, server.server_port, args.upstream, args.upstream_timeout)
    )
    try:
        server.serve_forever(poll_interval=0.2)
    except KeyboardInterrupt:
        server.logger.line("%s STOP g11_proxy (keyboard interrupt)" % _utc_now())
    finally:
        server.shutdown()
        server.server_close()
        server.logger.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
