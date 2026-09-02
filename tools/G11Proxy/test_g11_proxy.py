#!/usr/bin/env python3
"""Stdlib unittest suite for g11_proxy.

Spins up a fake OpenAI-compatible upstream (non-streaming JSON plus a delayed SSE
stream) and a proxy on an ephemeral port, then asserts passthrough, real-time
streaming, deterministic fault injection, CORS and the control counters.

Run:  python tools\\G11Proxy\\test_g11_proxy.py
"""

from __future__ import annotations

import contextlib
import http.client
import io
import json
import os
import socket
import sys
import tempfile
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import g11_proxy  # noqa: E402

SSE_FRAMES = ("alpha", "beta", "gamma")
SSE_DELAY = 0.3
CLIENT_TIMEOUT = 15


def _read_request_body(handler):
    encoding = (handler.headers.get("Transfer-Encoding") or "").lower()
    if "chunked" in encoding:
        out = bytearray()
        while True:
            line = handler.rfile.readline(65536)
            if not line:
                break
            try:
                size = int(line.strip().split(b";", 1)[0], 16)
            except ValueError:
                break
            if size == 0:
                handler.rfile.readline(65536)
                break
            out += handler.rfile.read(size)
            handler.rfile.readline(65536)
        return bytes(out)
    length = int(handler.headers.get("Content-Length") or 0)
    return handler.rfile.read(length) if length > 0 else b""


class FakeUpstreamHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        return

    def do_GET(self):
        self._note()
        if self.path.startswith("/v1/models"):
            self._json(200, {"object": "list", "data": [{"id": "fake-model"}]})
            return
        self._json(404, {"error": "not found"})

    def do_POST(self):
        body = _read_request_body(self)
        self._note(len(body))
        if not self.path.startswith("/v1/chat/completions"):
            self._json(404, {"error": "not found"})
            return
        try:
            payload = json.loads(body.decode("utf-8")) if body else {}
        except ValueError:
            payload = {}
        if payload.get("stream"):
            self._sse()
        else:
            self._json(
                200,
                {
                    "id": "cmpl-fake",
                    "object": "chat.completion",
                    "model": payload.get("model", ""),
                    "received_bytes": len(body),
                    "choices": [
                        {
                            "index": 0,
                            "message": {"role": "assistant", "content": "NONCE-OK"},
                            "finish_reason": "stop",
                        }
                    ],
                },
            )

    def _note(self, body_bytes=0):
        with self.server.lock:
            self.server.requests.append((self.command, self.path, body_bytes))

    def _json(self, status, payload):
        raw = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        # The proxy must replace this, not duplicate it.
        self.send_header("Access-Control-Allow-Origin", "https://upstream.example")
        self.send_header("X-Upstream-Marker", "fake")
        self.end_headers()
        self.wfile.write(raw)
        self.wfile.flush()

    def _sse(self):
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Transfer-Encoding", "chunked")
        self.end_headers()
        for index, text in enumerate(SSE_FRAMES):
            frame = (
                "data: "
                + json.dumps({"choices": [{"index": 0, "delta": {"content": text}}]})
                + "\n\n"
            ).encode("utf-8")
            self._chunk(frame)
            if index < len(SSE_FRAMES) - 1:
                time.sleep(SSE_DELAY)
        self._chunk(b"data: [DONE]\n\n")
        with self.server.lock:
            self.server.last_sent_at = time.monotonic()
        self.wfile.write(b"0\r\n\r\n")
        self.wfile.flush()

    def _chunk(self, payload):
        self.wfile.write(b"%x\r\n" % len(payload) + payload + b"\r\n")
        self.wfile.flush()


class FakeUpstreamServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, address):
        super().__init__(address, FakeUpstreamHandler)
        self.lock = threading.Lock()
        self.requests = []
        self.last_sent_at = None


class RecordingLogger(g11_proxy.Logger):
    """Keeps the request log out of the unittest output but still assertable."""

    def __init__(self):
        super().__init__(None)
        self.lines = []

    def line(self, text):
        self.lines.append(text)


class ProxyTestBase(unittest.TestCase):
    def setUp(self):
        self.upstream = FakeUpstreamServer(("127.0.0.1", 0))
        self.upstream_thread = threading.Thread(
            target=self.upstream.serve_forever, kwargs={"poll_interval": 0.05}, daemon=True
        )
        self.upstream_thread.start()
        self.upstream_url = "http://127.0.0.1:%d/v1" % self.upstream.server_port
        self.proxy = self._start_proxy(self.upstream_url)
        self.proxy_port = self.proxy.server_port

    def tearDown(self):
        for server, thread in getattr(self, "_started", []):
            server.shutdown()
            server.server_close()
            server.logger.close()
            thread.join(timeout=5)
        self.upstream.shutdown()
        self.upstream.server_close()
        self.upstream_thread.join(timeout=5)

    def _start_proxy(self, upstream_url):
        server = g11_proxy.create_server(port=0, upstream=upstream_url, upstream_timeout=10)
        server.logger.close()
        server.logger = RecordingLogger()
        thread = threading.Thread(
            target=server.serve_forever, kwargs={"poll_interval": 0.05}, daemon=True
        )
        thread.start()
        if not hasattr(self, "_started"):
            self._started = []
        self._started.append((server, thread))
        return server

    # ------------------------------------------------------------- HTTP helpers

    def _connect(self, port=None):
        return http.client.HTTPConnection(
            "127.0.0.1", port if port else self.proxy_port, timeout=CLIENT_TIMEOUT
        )

    def _request(self, method, path, body=None, headers=None, port=None):
        conn = self._connect(port)
        try:
            conn.request(method, path, body=body, headers=headers or {})
            response = conn.getresponse()
            data = response.read()
            return response.status, response.getheaders(), data
        finally:
            conn.close()

    def _post_json(self, path, payload, headers=None, port=None):
        merged = {"Content-Type": "application/json"}
        merged.update(headers or {})
        return self._request(
            "POST", path, body=json.dumps(payload).encode("utf-8"), headers=merged, port=port
        )

    def _control(self, path, payload=None):
        status, _, data = self._post_json(path, payload if payload is not None else {})
        self.assertEqual(200, status, data)
        return json.loads(data.decode("utf-8"))

    def _state(self):
        status, _, data = self._request("GET", "/control/state")
        self.assertEqual(200, status, data)
        return json.loads(data.decode("utf-8"))

    @staticmethod
    def _values(headers, name):
        return [value for key, value in headers if key.lower() == name.lower()]

    def _assert_cors(self, headers, expect_allow_headers="*"):
        origins = self._values(headers, "Access-Control-Allow-Origin")
        self.assertEqual(["*"], origins, "exactly one wildcard ACAO expected")
        self.assertEqual([expect_allow_headers], self._values(headers, "Access-Control-Allow-Headers"))
        self.assertEqual(["GET, POST, OPTIONS"], self._values(headers, "Access-Control-Allow-Methods"))
        self.assertEqual(["*"], self._values(headers, "Access-Control-Expose-Headers"))


class HealthAndPassthroughTests(ProxyTestBase):
    def test_health_reports_upstream(self):
        status, headers, data = self._request("GET", "/health")
        self.assertEqual(200, status)
        payload = json.loads(data.decode("utf-8"))
        self.assertTrue(payload["ok"])
        self.assertEqual(self.upstream_url, payload["upstream"])
        self._assert_cors(headers)

    def test_non_streaming_post_passes_through(self):
        status, headers, data = self._post_json(
            "/v1/chat/completions", {"model": "fake-model", "stream": False}
        )
        self.assertEqual(200, status)
        payload = json.loads(data.decode("utf-8"))
        self.assertEqual("cmpl-fake", payload["id"])
        self.assertEqual("fake-model", payload["model"])
        self.assertEqual("NONCE-OK", payload["choices"][0]["message"]["content"])
        self.assertEqual(["fake"], self._values(headers, "X-Upstream-Marker"))
        # Upstream's own CORS header must be replaced, never duplicated.
        self._assert_cors(headers)
        with self.upstream.lock:
            self.assertEqual(1, len(self.upstream.requests))
            self.assertEqual(("POST", "/v1/chat/completions"), self.upstream.requests[0][:2])

    def test_get_with_query_passes_through(self):
        status, headers, data = self._request("GET", "/v1/models?limit=1")
        self.assertEqual(200, status)
        self.assertEqual("fake-model", json.loads(data.decode("utf-8"))["data"][0]["id"])
        self._assert_cors(headers)
        with self.upstream.lock:
            self.assertEqual("/v1/models?limit=1", self.upstream.requests[0][1])

    def test_chunked_request_body_is_dechunked(self):
        parts = [b'{"model": "fake-model",', b' "stream": false,', b' "pad": "0123456789"}']
        expected = sum(len(part) for part in parts)
        conn = self._connect()
        try:
            conn.request(
                "POST",
                "/v1/chat/completions",
                body=iter(parts),
                headers={"Content-Type": "application/json", "Transfer-Encoding": "chunked"},
                encode_chunked=True,
            )
            response = conn.getresponse()
            data = response.read()
            self.assertEqual(200, response.status)
        finally:
            conn.close()
        payload = json.loads(data.decode("utf-8"))
        self.assertEqual(expected, payload["received_bytes"])
        self.assertEqual("fake-model", payload["model"])


class StreamingTests(ProxyTestBase):
    def test_sse_chunks_arrive_incrementally(self):
        conn = self._connect()
        try:
            conn.request(
                "POST",
                "/v1/chat/completions",
                body=json.dumps({"model": "fake-model", "stream": True}).encode("utf-8"),
                headers={"Content-Type": "application/json", "Accept": "text/event-stream"},
            )
            response = conn.getresponse()
            self.assertEqual(200, response.status)
            self.assertIn("text/event-stream", response.getheader("Content-Type"))
            self.assertEqual("chunked", (response.getheader("Transfer-Encoding") or "").lower())
            self._assert_cors(response.getheaders())

            first_at = None
            received = b""
            while True:
                chunk = response.read1(4096)
                if not chunk:
                    break
                if first_at is None:
                    first_at = time.monotonic()
                received += chunk
        finally:
            conn.close()

        with self.upstream.lock:
            last_sent_at = self.upstream.last_sent_at
        self.assertIsNotNone(first_at, "no streamed chunk was received")
        self.assertIsNotNone(last_sent_at, "upstream never finished the stream")
        # Real-time proof: the client had frame 1 before upstream produced the last frame.
        self.assertLess(
            first_at,
            last_sent_at,
            "first chunk must reach the client before the upstream sends the last one",
        )
        for text in SSE_FRAMES:
            self.assertIn(text.encode("utf-8"), received)
        self.assertIn(b"data: [DONE]", received)
        self.assertEqual(len(SSE_FRAMES) + 1, received.count(b"data:"))

    def test_stream_is_logged_as_streamed(self):
        self._post_json("/v1/chat/completions", {"stream": True})
        lines = [line for line in self.proxy.logger.lines if "/v1/chat/completions" in line]
        self.assertTrue(lines)
        self.assertIn("streamed=yes", lines[-1])


class FaultInjectionTests(ProxyTestBase):
    def test_fail_next_two_then_pass(self):
        state = self._control("/control/fail-next", {"count": 2})
        self.assertEqual(2, state["settings"]["fail_next_remaining"])

        for _ in range(2):
            status, headers, data = self._post_json("/v1/chat/completions", {"stream": False})
            self.assertEqual(503, status)
            self._assert_cors(headers)
            self.assertEqual(
                "g11_proxy_injected", json.loads(data.decode("utf-8"))["error"]["type"]
            )

        status, _, data = self._post_json("/v1/chat/completions", {"stream": False})
        self.assertEqual(200, status)
        self.assertEqual("cmpl-fake", json.loads(data.decode("utf-8"))["id"])

        with self.upstream.lock:
            self.assertEqual(1, len(self.upstream.requests), "injected turns must not hit upstream")

    def test_fail_next_custom_status_and_body(self):
        self._control(
            "/control/fail-next", {"count": 1, "status": 500, "body": "upstream exploded"}
        )
        status, headers, data = self._post_json("/v1/chat/completions", {})
        self.assertEqual(500, status)
        self.assertEqual(b"upstream exploded", data)
        self._assert_cors(headers)

    def test_fail_next_path_filter_spares_readiness_probe(self):
        self._control("/control/fail-next", {"count": 1, "path": "chat/completions"})
        status, _, _ = self._request("GET", "/v1/models")
        self.assertEqual(200, status, "the models probe must not consume the injected failure")
        status, _, _ = self._post_json("/v1/chat/completions", {})
        self.assertEqual(503, status)
        status, _, _ = self._post_json("/v1/chat/completions", {})
        self.assertEqual(200, status)

    def test_block_until_reset(self):
        self._control("/control/block", {"enabled": True})
        for _ in range(3):
            status, headers, _ = self._post_json("/v1/chat/completions", {})
            self.assertEqual(503, status)
            self._assert_cors(headers)
        with self.upstream.lock:
            self.assertEqual(0, len(self.upstream.requests))

        state = self._control("/control/reset")
        self.assertFalse(state["settings"]["blocked"])
        status, _, data = self._post_json("/v1/chat/completions", {})
        self.assertEqual(200, status)
        self.assertEqual("cmpl-fake", json.loads(data.decode("utf-8"))["id"])

    def test_block_disable_flag(self):
        self._control("/control/block", {"enabled": True})
        self.assertEqual(503, self._post_json("/v1/chat/completions", {})[0])
        self._control("/control/block", {"enabled": False})
        self.assertEqual(200, self._post_json("/v1/chat/completions", {})[0])

    def test_hang_holds_then_fails(self):
        self._control("/control/hang", {"seconds": 0.5})
        started = time.monotonic()
        status, headers, _ = self._post_json("/v1/chat/completions", {})
        elapsed = time.monotonic() - started
        self.assertEqual(503, status)
        self.assertGreaterEqual(elapsed, 0.5)
        self._assert_cors(headers)
        # One-shot: the following request is served normally.
        self.assertEqual(200, self._post_json("/v1/chat/completions", {})[0])

    def test_upstream_error_becomes_502(self):
        dead = socket.socket()
        dead.bind(("127.0.0.1", 0))
        dead_port = dead.getsockname()[1]
        dead.close()
        broken = self._start_proxy("http://127.0.0.1:%d/v1" % dead_port)

        status, headers, data = self._post_json(
            "/v1/chat/completions", {}, port=broken.server_port
        )
        self.assertEqual(502, status)
        self._assert_cors(headers)
        self.assertEqual(
            "g11_proxy_upstream_error", json.loads(data.decode("utf-8"))["error"]["type"]
        )
        self.assertEqual(1, broken.state.snapshot()["counters"]["upstream_errors"])


class CorsTests(ProxyTestBase):
    def test_preflight_returns_204_and_echoes_requested_headers(self):
        status, headers, data = self._request(
            "OPTIONS",
            "/v1/chat/completions",
            headers={
                "Origin": "http://localhost:8080",
                "Access-Control-Request-Method": "POST",
                "Access-Control-Request-Headers": "content-type, authorization",
            },
        )
        self.assertEqual(204, status)
        self.assertEqual(b"", data)
        self._assert_cors(headers, expect_allow_headers="content-type, authorization")
        self.assertEqual([], self._values(headers, "Content-Length"))
        with self.upstream.lock:
            self.assertEqual(0, len(self.upstream.requests), "preflight must not reach upstream")

    def test_preflight_without_requested_headers_falls_back_to_wildcard(self):
        status, headers, _ = self._request("OPTIONS", "/v1/chat/completions")
        self.assertEqual(204, status)
        self._assert_cors(headers)

    def test_cors_present_on_injected_failure(self):
        self._control("/control/block", {"enabled": True})
        status, headers, _ = self._post_json(
            "/v1/chat/completions",
            {},
            headers={
                "Origin": "http://localhost:8080",
                "Access-Control-Request-Headers": "content-type",
            },
        )
        self.assertEqual(503, status)
        self._assert_cors(headers, expect_allow_headers="content-type")


class ControlStateTests(ProxyTestBase):
    def test_counters_track_injected_and_proxied(self):
        empty = self._state()
        self.assertEqual(
            {
                "total_requests": 0,
                "total_proxied": 0,
                "injected_failures": 0,
                "scripted_replies": 0,
                "upstream_errors": 0,
                "in_flight": 0,
            },
            empty["counters"],
        )
        self.assertIsNone(empty["last"])
        self.assertEqual(self.upstream_url, empty["settings"]["upstream"])

        self._control("/control/fail-next", {"count": 2})
        self._post_json("/v1/chat/completions", {})  # injected 503
        self._post_json("/v1/chat/completions", {})  # injected 503
        self._post_json("/v1/chat/completions", {})  # proxied 200
        self._request("GET", "/v1/models")  # proxied 200

        state = self._state()
        self.assertEqual(4, state["counters"]["total_requests"])
        self.assertEqual(2, state["counters"]["total_proxied"])
        self.assertEqual(2, state["counters"]["injected_failures"])
        self.assertEqual(0, state["counters"]["upstream_errors"])
        self.assertEqual(0, state["counters"]["in_flight"])
        self.assertEqual("/v1/models", state["last"]["path"])
        self.assertEqual("GET", state["last"]["method"])
        self.assertEqual(200, state["last"]["status"])
        self.assertIsNone(state["last"]["injected"])
        self.assertEqual(0, state["settings"]["fail_next_remaining"])

    def test_reset_keeps_counters_unless_asked(self):
        self._control("/control/block", {"enabled": True})
        self._post_json("/v1/chat/completions", {})
        self.assertEqual(1, self._state()["counters"]["injected_failures"])

        self._control("/control/reset")
        self.assertEqual(1, self._state()["counters"]["injected_failures"])

        self._control("/control/reset", {"counters": True})
        self.assertEqual(0, self._state()["counters"]["injected_failures"])
        self.assertIsNone(self._state()["last"])

    def test_control_rejects_bad_json_and_wrong_method(self):
        status, _, _ = self._request(
            "POST",
            "/control/fail-next",
            body=b"not json",
            headers={"Content-Type": "application/json"},
        )
        self.assertEqual(400, status)
        status, _, _ = self._post_json("/control/state", {})
        self.assertEqual(405, status)
        status, _, _ = self._request("GET", "/nope")
        self.assertEqual(404, status)


class ScriptedReplyTests(ProxyTestBase):
    def _script(self, replies):
        return self._control("/control/script", {"replies": replies})

    def _sse_frames(self, raw):
        return [
            json.loads(line[len("data: "):])
            for line in raw.decode("utf-8").split("\n")
            if line.startswith("data: ") and line != "data: [DONE]"
        ]

    def test_scripted_tool_call_streams_one_native_tool_call_then_done(self):
        state = self._script(
            [{"tool_call": {"name": "execute_lua", "arguments": {"code": "return 2 + 2"}}}]
        )
        self.assertEqual(1, state["settings"]["script_remaining"])
        with self.upstream.lock:
            before = len(self.upstream.requests)
        status, headers, data = self._post_json(
            "/v1/chat/completions", {"model": "fake-model", "stream": True, "messages": []}
        )
        self.assertEqual(200, status, data)
        self.assertIn("text/event-stream", self._values(headers, "Content-Type")[0])
        self._assert_cors(headers)
        frames = self._sse_frames(data)
        self.assertEqual(2, len(frames), data)
        call = frames[0]["choices"][0]["delta"]["tool_calls"][0]
        self.assertEqual("execute_lua", call["function"]["name"])
        self.assertEqual({"code": "return 2 + 2"}, json.loads(call["function"]["arguments"]))
        self.assertEqual(0, call["index"])
        self.assertTrue(call["id"])
        self.assertEqual("tool_calls", frames[1]["choices"][0]["finish_reason"])
        self.assertTrue(data.rstrip().endswith(b"data: [DONE]"))
        with self.upstream.lock:
            self.assertEqual(before, len(self.upstream.requests), "scripted replies never reach upstream")
        self.assertEqual(0, self._state()["settings"]["script_remaining"])
        self.assertTrue(any("injected=script" in line for line in self.proxy.logger.lines), self.proxy.logger.lines)

    def test_scripted_text_reply_is_a_plain_completion_when_not_streaming(self):
        self._script([{"text": "DONE"}])
        status, headers, data = self._post_json(
            "/v1/chat/completions", {"model": "fake-model", "stream": False, "messages": []}
        )
        self.assertEqual(200, status, data)
        self.assertIn("application/json", self._values(headers, "Content-Type")[0])
        payload = json.loads(data.decode("utf-8"))
        self.assertEqual("chat.completion", payload["object"])
        self.assertEqual("fake-model", payload["model"])
        self.assertEqual("DONE", payload["choices"][0]["message"]["content"])
        self.assertEqual("stop", payload["choices"][0]["finish_reason"])

    def test_script_is_consumed_in_order_then_requests_forward_again(self):
        self._script([{"tool_call": {"name": "a", "arguments": {}}}, {"text": "second"}])
        _, _, first = self._post_json("/v1/chat/completions", {"stream": False})
        _, _, second = self._post_json("/v1/chat/completions", {"stream": False})
        self.assertEqual("a", json.loads(first)["choices"][0]["message"]["tool_calls"][0]["function"]["name"])
        self.assertEqual("second", json.loads(second)["choices"][0]["message"]["content"])
        with self.upstream.lock:
            before = len(self.upstream.requests)
        status, _, third = self._post_json("/v1/chat/completions", {"stream": False})
        self.assertEqual(200, status, third)
        with self.upstream.lock:
            self.assertEqual(before + 1, len(self.upstream.requests), "an exhausted script forwards upstream")
        self.assertEqual(2, self._state()["counters"]["scripted_replies"])

    def test_script_only_answers_chat_completions(self):
        self._script([{"text": "never"}])
        with self.upstream.lock:
            before = len(self.upstream.requests)
        self._request("GET", "/v1/models")
        with self.upstream.lock:
            self.assertEqual(before + 1, len(self.upstream.requests))
        self.assertEqual(1, self._state()["settings"]["script_remaining"])

    def test_script_rejects_malformed_entries(self):
        for bad in ([{"nope": 1}], [{"tool_call": {"arguments": {}}}], "text", [{"text": 5}]):
            status, _, data = self._post_json("/control/script", {"replies": bad})
            self.assertEqual(400, status, data)
        self.assertEqual(0, self._state()["settings"]["script_remaining"])

    def test_control_requests_returns_captured_bodies(self):
        self._post_json("/v1/chat/completions", {"stream": False, "messages": [{"role": "tool", "content": "HTTP_PROBE"}]})
        status, _, data = self._request("GET", "/control/requests")
        self.assertEqual(200, status, data)
        captured = json.loads(data.decode("utf-8"))["requests"]
        self.assertTrue(captured)
        self.assertEqual("/v1/chat/completions", captured[-1]["path"])
        self.assertIn("HTTP_PROBE", captured[-1]["body"])

    def test_reset_clears_the_script(self):
        self._script([{"text": "x"}])
        self._control("/control/reset", {})
        self.assertEqual(0, self._state()["settings"]["script_remaining"])


class LoggingTests(ProxyTestBase):
    def test_request_line_has_every_required_field(self):
        self._post_json("/v1/chat/completions", {"stream": False})
        self._control("/control/block", {"enabled": True})
        self._post_json("/v1/chat/completions", {})

        proxied = [line for line in self.proxy.logger.lines if "/v1/chat/completions" in line]
        self.assertEqual(2, len(proxied))

        ok_line = proxied[0]
        self.assertIn("POST /v1/chat/completions", ok_line)
        self.assertIn("status=200", ok_line)
        self.assertIn("injected=no", ok_line)
        self.assertIn("streamed=no", ok_line)
        self.assertIn("bytes=", ok_line)
        self.assertIn("dur_ms=", ok_line)
        self.assertTrue(ok_line.startswith("20") and ok_line.split(" ", 1)[0].endswith("Z"))

        blocked_line = proxied[1]
        self.assertIn("status=503", blocked_line)
        self.assertIn("injected=block", blocked_line)

    def test_log_file_receives_the_same_lines(self):
        handle, path = tempfile.mkstemp(suffix=".log")
        os.close(handle)
        logger = g11_proxy.Logger(path)
        try:
            # The same lines also go to stdout; keep them out of the test report.
            with contextlib.redirect_stdout(io.StringIO()) as captured:
                logger.line("line-one")
                logger.line("line-two")
        finally:
            logger.close()
        self.assertEqual(["line-one", "line-two"], captured.getvalue().split())
        with open(path, "r", encoding="utf-8") as stream:
            self.assertEqual(["line-one", "line-two"], stream.read().split())
        os.remove(path)


if __name__ == "__main__":
    unittest.main(verbosity=2)
