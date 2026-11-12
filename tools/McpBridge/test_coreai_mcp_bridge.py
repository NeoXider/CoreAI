#!/usr/bin/env python3
"""Stdlib unittest suite for coreai_mcp_bridge.

Starts the bridge on an ephemeral port, drives the game side with a small fake game (attach, long poll, reply)
and the client side with http.client, and checks the round trip, the framing, the refusals and the security
checks.

Run:  python tools/McpBridge/test_coreai_mcp_bridge.py
"""

from __future__ import annotations

import http.client
import json
import os
import socket
import sys
import threading
import time
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import coreai_mcp_bridge as bridge  # noqa: E402

TOKEN = "client-token-0123456789"
KEY = "game-key-0123456789"


class FakeGame:
    """Attaches, polls and answers every request with the given function, in a thread."""

    def __init__(self, port, answer, key=KEY, instance="game-a", origin="http://localhost:8777"):
        self.port = port
        self.answer = answer
        self.key = key
        self.instance = instance
        self.origin = origin
        self.seen = []
        self._stop = threading.Event()
        self._thread = None

    def post(self, route, payload, timeout=15):
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=timeout)
        headers = {"Content-Type": "application/json", "Authorization": "Bearer " + self.key}
        if self.origin:
            headers["Origin"] = self.origin
        connection.request("POST", route, body=json.dumps(payload), headers=headers)
        response = connection.getresponse()
        body = response.read().decode("utf-8")
        connection.close()
        return response.status, body, dict(response.getheaders())

    def attach(self):
        return self.post("/bridge/attach", {"instance": self.instance, "name": "Block World", "platform": "WebGLPlayer"})

    def start(self):
        status, body, _ = self.attach()
        if status != 200:
            raise AssertionError("attach failed: %d %s" % (status, body))
        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._thread.start()

    def stop(self):
        self._stop.set()
        if self._thread:
            self._thread.join(15)

    def _loop(self):
        while not self._stop.is_set():
            status, body, _ = self.post("/bridge/next", {"instance": self.instance})
            if status == 204:
                continue
            if status != 200:
                return
            envelope = json.loads(body)
            self.seen.append(envelope)
            reply = self.answer(envelope)
            if reply is None:
                continue
            reply["instance"] = self.instance
            reply["id"] = envelope["id"]
            self.post("/bridge/reply", reply)


def echo_initialize(envelope):
    request = json.loads(envelope["body"])
    if "id" not in request:
        return {"status": 202}
    result = {"jsonrpc": "2.0", "id": request["id"], "result": {"echo": request.get("params")}}
    return {"status": 200, "content_type": "application/json", "body": json.dumps(result), "session": "s-1"}


class BridgeTests(unittest.TestCase):
    def start_bridge(self, **options):
        options.setdefault("quiet", True)
        options.setdefault("poll_seconds", 0.5)
        options.setdefault("attach_wait", 0.3)
        server = bridge.create_server(0, client_token=TOKEN, game_key=KEY, **options)
        thread = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.05}, daemon=True)
        thread.start()
        self.addCleanup(lambda: (server.shutdown(), server.server_close()))
        return server.server_port, server

    def client(self, port, method="POST", body=None, headers=None, path="/mcp"):
        connection = http.client.HTTPConnection("127.0.0.1", port, timeout=15)
        sent = {"Authorization": "Bearer " + TOKEN, "Content-Type": "application/json"}
        sent.update(headers or {})
        sent = {key: value for key, value in sent.items() if value is not None}
        connection.request(method, path, body=body, headers=sent)
        response = connection.getresponse()
        data = response.read().decode("utf-8")
        result = (response.status, data, {key.lower(): value for key, value in response.getheaders()})
        connection.close()
        return result

    def game(self, port, answer=echo_initialize, **options):
        game = FakeGame(port, answer, **options)
        game.start()
        self.addCleanup(game.stop)
        return game

    def test_round_trip_keeps_the_body_exact_and_relays_the_session(self):
        port, _ = self.start_bridge()
        game = self.game(port)
        body = '{"jsonrpc":"2.0","id":"2026-11-12T10:00:00Z","method":"initialize","params":{"t":"é"}}'

        status, data, headers = self.client(port, body=body.encode("utf-8"),
                                            headers={"MCP-Protocol-Version": "2025-06-18"})

        self.assertEqual(200, status, data)
        self.assertEqual("s-1", headers.get("mcp-session-id"))
        self.assertEqual("2026-11-12T10:00:00Z", json.loads(data)["id"])
        self.assertEqual(body, game.seen[0]["body"], "the bridge must not reformat the JSON-RPC text")
        self.assertEqual("2025-06-18", game.seen[0]["protocol"])
        self.assertIsNone(game.seen[0]["session"])

    def test_event_stream_framing_when_the_client_asks(self):
        port, _ = self.start_bridge()
        self.game(port)

        status, data, headers = self.client(port, body=b'{"jsonrpc":"2.0","id":1,"method":"ping"}',
                                            headers={"Accept": "application/json, text/event-stream"})

        self.assertEqual(200, status)
        self.assertEqual("text/event-stream", headers.get("content-type"))
        self.assertTrue(data.startswith("event: message\ndata: {"), data)

    def test_notification_is_202_and_delete_is_forwarded(self):
        port, _ = self.start_bridge()
        game = self.game(port, answer=lambda env: {"status": 204} if env["method"] == "DELETE" else echo_initialize(env))

        status, _, _ = self.client(port, body=b'{"jsonrpc":"2.0","method":"notifications/initialized"}')
        deleted, _, _ = self.client(port, method="DELETE", headers={"Mcp-Session-Id": "s-1", "Content-Type": None})

        self.assertEqual(202, status)
        self.assertEqual(204, deleted)
        self.assertEqual("DELETE", game.seen[-1]["method"])
        self.assertEqual("s-1", game.seen[-1]["session"])

    def test_game_text_rejection_keeps_its_status(self):
        port, _ = self.start_bridge()
        self.game(port, answer=lambda env: {"status": 404, "content_type": "text/plain; charset=utf-8",
                                            "body": "The MCP session expired"})

        status, data, _ = self.client(port, body=b'{"jsonrpc":"2.0","id":1,"method":"ping"}',
                                      headers={"Mcp-Session-Id": "old"})

        self.assertEqual(404, status)
        self.assertIn("expired", data)

    def test_client_needs_the_token_and_no_origin(self):
        port, _ = self.start_bridge()
        self.game(port)

        self.assertEqual(401, self.client(port, body=b"{}", headers={"Authorization": "Bearer wrong"})[0])
        self.assertEqual(403, self.client(port, body=b"{}", headers={"Origin": "http://localhost:8777"})[0])
        self.assertEqual(415, self.client(port, body=b"{}", headers={"Content-Type": "text/plain"})[0])
        self.assertEqual(405, self.client(port, method="GET", headers={"Content-Type": None})[0])

    def test_rebound_host_is_refused(self):
        port, _ = self.start_bridge()
        status, data, _ = self.client(port, method="GET", path="/health",
                                      headers={"Host": "evil.example:%d" % port})
        self.assertEqual(403, status)
        self.assertIn("DNS-rebinding", data)

    def test_no_game_attached_answers_503_with_the_fix(self):
        port, _ = self.start_bridge()
        status, data, _ = self.client(port, body=b'{"jsonrpc":"2.0","id":1,"method":"ping"}')
        self.assertEqual(503, status)
        self.assertIn("coreaiMcp", data)

    def test_game_needs_the_key(self):
        port, _ = self.start_bridge()
        status, _, _ = FakeGame(port, echo_initialize, key="wrong").attach()
        self.assertEqual(401, status)

    def test_second_game_is_refused_and_named(self):
        port, _ = self.start_bridge()
        self.game(port, instance="game-a")

        status, body, _ = FakeGame(port, echo_initialize, instance="game-b").attach()

        self.assertEqual(409, status)
        self.assertEqual("game-a", json.loads(body)["attached"]["instance"])
        self.assertEqual("Block World", json.loads(body)["attached"]["name"])

    def test_takeover_lets_the_newest_game_in(self):
        port, _ = self.start_bridge(takeover=True)
        self.game(port, instance="game-a")
        status, _, _ = FakeGame(port, echo_initialize, instance="game-b").attach()
        self.assertEqual(200, status)

    def test_abandoned_poll_frees_the_bridge_for_a_reloaded_page(self):
        port, _ = self.start_bridge(poll_seconds=5.0)
        first = FakeGame(port, echo_initialize, instance="old-page")
        self.assertEqual(200, first.attach()[0])
        raw = socket.create_connection(("127.0.0.1", port))
        payload = json.dumps({"instance": "old-page"}).encode("utf-8")
        raw.sendall(b"POST /bridge/next HTTP/1.1\r\nHost: 127.0.0.1:%d\r\nAuthorization: Bearer %s\r\n"
                    b"Content-Type: application/json\r\nContent-Length: %d\r\n\r\n"
                    % (port, KEY.encode("ascii"), len(payload)) + payload)
        time.sleep(0.3)
        raw.close()
        time.sleep(1.0)

        status, _, _ = FakeGame(port, echo_initialize, instance="new-page").attach()

        self.assertEqual(200, status, "a page that went away must not hold the bridge for the stale window")

    def test_cors_only_for_loopback_pages(self):
        port, _ = self.start_bridge()
        status, _, headers = self.client(port, method="OPTIONS", path="/bridge/next",
                                         headers={"Origin": "http://localhost:8777", "Authorization": None,
                                                  "Content-Type": None,
                                                  "Access-Control-Request-Headers": "authorization, content-type"})
        self.assertEqual(204, status)
        self.assertEqual("http://localhost:8777", headers.get("access-control-allow-origin"))
        self.assertIn("Authorization", headers.get("access-control-allow-headers"))

        status, _, headers = self.client(port, method="OPTIONS", path="/bridge/next",
                                         headers={"Origin": "http://evil.example", "Authorization": None,
                                                  "Content-Type": None})
        self.assertEqual(403, status)
        self.assertIsNone(headers.get("access-control-allow-origin"))

    def test_slow_game_times_out_with_504(self):
        port, _ = self.start_bridge(request_timeout=0.5)
        self.game(port, answer=lambda env: None)
        status, data, _ = self.client(port, body=b'{"jsonrpc":"2.0","id":1,"method":"ping"}')
        self.assertEqual(504, status)
        self.assertIn("did not answer", data)

    def test_status_needs_the_client_token_and_names_the_game(self):
        port, _ = self.start_bridge()
        self.game(port, instance="game-a")
        self.assertEqual(401, self.client(port, method="GET", path="/bridge/status",
                                          headers={"Authorization": None, "Content-Type": None})[0])
        status, data, _ = self.client(port, method="GET", path="/bridge/status", headers={"Content-Type": None})
        self.assertEqual(200, status)
        self.assertEqual("game-a", json.loads(data)["attached"]["instance"])

    def start_lan(self, server, address="127.0.0.2"):
        lan = bridge.create_lan_server(server, address)
        thread = threading.Thread(target=lan.serve_forever, kwargs={"poll_interval": 0.05}, daemon=True)
        thread.start()
        self.addCleanup(lambda: (lan.shutdown(), lan.server_close()))
        return lan

    def test_lan_listener_serves_the_game_and_refuses_the_client(self):
        port, server = self.start_bridge()
        self.start_lan(server)
        game = FakeGame(port, echo_initialize, origin=None)
        game_host = "127.0.0.2"

        def post(route, payload):
            connection = http.client.HTTPConnection(game_host, port, timeout=15)
            connection.request("POST", route, body=json.dumps(payload),
                               headers={"Content-Type": "application/json", "Authorization": "Bearer " + KEY})
            response = connection.getresponse()
            body = response.read().decode("utf-8")
            connection.close()
            return response.status, body, {}

        game.post = post
        game.start()
        self.addCleanup(game.stop)

        status, data, _ = self.client(port, body=b'{"jsonrpc":"2.0","id":1,"method":"ping"}')
        self.assertEqual(200, status, "the client on loopback reaches the game attached over the LAN listener")

        connection = http.client.HTTPConnection(game_host, port, timeout=15)
        connection.request("POST", "/mcp", body=b"{}", headers={"Authorization": "Bearer " + TOKEN,
                                                                 "Content-Type": "application/json"})
        refused = connection.getresponse()
        refused.read()
        connection.close()
        self.assertEqual(403, refused.status, "the MCP endpoint never listens on the LAN")

    def test_lan_listener_needs_a_long_key_and_a_real_lan_address(self):
        server = bridge.create_server(0, client_token=TOKEN, game_key="short", quiet=True)
        self.addCleanup(server.server_close)
        with self.assertRaises(ValueError):
            bridge.create_lan_server(server, "127.0.0.2")
        for bad in ("127.0.0.1", "0.0.0.0", "phone", "10.0.0"):
            with self.assertRaises(ValueError):
                bridge.validate_lan_address(bad)
        self.assertEqual("192.168.1.20", bridge.validate_lan_address("192.168.1.20"))

    def test_loopback_origin_parsing(self):
        self.assertTrue(bridge.is_loopback_origin("http://localhost:8777"))
        self.assertTrue(bridge.is_loopback_origin("http://127.0.0.1"))
        self.assertTrue(bridge.is_loopback_origin("http://[::1]:9000"))
        self.assertFalse(bridge.is_loopback_origin("https://localhost:8777"))
        self.assertFalse(bridge.is_loopback_origin("http://localhost.evil.example"))
        self.assertFalse(bridge.is_loopback_origin("null"))
        self.assertFalse(bridge.is_loopback_origin("http://127.0.0.1:80/page"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
