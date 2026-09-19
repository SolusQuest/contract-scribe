#!/usr/bin/env python3
"""fake_provider.py — credential-free campaign provider fake for Action tests.

Port of ProposalLoopbackServer (CampaignCliProcessTests.cs) to stdlib-only
Python. Scenarios:
  proposal         emit a terminal proposal derived from the request's
                   target-evidence message (the real admission path)
  skip             emit the insufficient-evidence terminal skip
  hang             accept the connection and never respond (cancellation legs)

Writes <ready-file> {"endpoint": "http://127.0.0.1:P/v1/chat/completions"}
and appends one line per request to <log-file>.
"""

import json
import socket
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


def terminal_response(terminal_json):
    body = {"choices": [{"index": 0, "message": {
        "role": "assistant",
        "tool_calls": [{"id": "call.terminal", "type": "function",
                        "function": {"name": "cs_terminal",
                                     "arguments": terminal_json}}]},
        "finish_reason": "tool_calls"}]}
    return json.dumps(body).encode()


SKIP = terminal_response(
    '{"kind":"skip","reason":"scribe.skip.insufficient-evidence",'
    '"evidenceReferenceIds":[]}')


def proposal_response(wire):
    evidence = None
    for message in wire.get("messages", []):
        content = message.get("content")
        if not content:
            continue
        try:
            parsed = json.loads(content)
        except json.JSONDecodeError:
            continue
        if parsed.get("authority") == "target-evidence":
            evidence = parsed
            break
    if evidence is None:
        raise ValueError("target-evidence missing")
    reference = next(r["evidenceReferenceId"] for r in
                     evidence["evidenceReferences"]
                     if "symbolRef" in r.get("subject", {}))
    if evidence.get("applicableComponents"):
        return SKIP
    unit = {"kind": "content.summary",
            "lines": ["Documents the selected contract."],
            "claimCategoryId": "claim.behavior",
            "evidenceReferenceIds": [reference]}
    terminal = {"kind": "proposal", "target": evidence["terminalTarget"],
                "contentUnits": [unit]}
    return terminal_response(json.dumps(terminal))


def serve(scenario, ready_file, log_file):
    lock = threading.Lock()

    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *args):
            pass

        def do_POST(self):
            if self.path != "/v1/chat/completions":
                self.send_error(404)
                return
            size = int(self.headers.get("Content-Length") or 0)
            if not 0 < size <= 1048576:
                self.send_error(400)
                return
            body = self.rfile.read(size)
            with lock:
                with open(log_file, "a", encoding="utf-8") as fh:
                    fh.write(f"POST {self.path} bytes={size}\n")
            if scenario == "hang":
                while True:
                    time.sleep(3600)
            try:
                wire = json.loads(body.decode("utf-8"))
                response = SKIP if scenario == "skip" \
                    else proposal_response(wire)
            except (ValueError, KeyError, StopIteration, TypeError):
                self.send_error(400, "Unsupported synthetic request")
                return
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(response)))
            self.end_headers()
            self.wfile.write(response)

    sock = socket.socket()
    sock.bind(("127.0.0.1", 0))
    port = sock.getsockname()[1]
    sock.close()
    server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    server.daemon_threads = True
    threading.Thread(target=server.serve_forever, daemon=True).start()
    with open(ready_file, "w", encoding="utf-8") as fh:
        json.dump({"endpoint": f"http://127.0.0.1:{port}/v1/chat/completions"},
                  fh)
    server.serve_forever()


if __name__ == "__main__":
    serve(sys.argv[1], sys.argv[2], sys.argv[3])
