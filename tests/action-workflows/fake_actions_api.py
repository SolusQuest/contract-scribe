#!/usr/bin/env python3
"""fake_actions_api.py — loopback substitute for the GitHub Actions REST
metadata surface that handoff.py authenticates (tests/action-workflows).

Serves, from a per-request-reloaded JSON config:

  GET /repos/{owner}/{repo}                              -> repository record
  GET /repos/{o}/{r}/actions/runs/{id}                   -> run record
  GET /repos/{o}/{r}/actions/runs/{id}/artifacts?name=X  -> name-filtered list
  GET /repos/{o}/{r}/actions/artifacts/{id}              -> artifact detail

Every request must carry `Authorization: Bearer <expected_token>` and
`X-GitHub-Api-Version: 2026-03-10`; anything else is rejected and logged.

Config shape:
{"repository": {"id": 7001, "full_name": "Owner/repo",
                "default_branch": "main"},
 "expected_token": "contract-scribe-synthetic-actions-read",
 "runs": {"<id>": {<run record fields>}, ...},
 "artifacts": {"<id>": {<artifact record fields>}, ...},
 "run_artifacts": {"<run id>": [<artifact ids>], ...}}

Usage: python3 fake_actions_api.py <config.json> <ready-file> <log-file>
Writes {"endpoint": "http://127.0.0.1:P"} into <ready-file> once bound and
appends one line per request to <log-file>.
"""

import json
import socket
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs


def serve(config_path, ready_path, log_path):
    lock = threading.Lock()

    def config():
        with open(config_path, encoding="utf-8") as fh:
            return json.load(fh)

    def log(line):
        with lock, open(log_path, "a", encoding="utf-8") as fh:
            fh.write(line + "\n")

    class Handler(BaseHTTPRequestHandler):
        def _reject(self, code, reason):
            log(f"{self.command} {self.path} -> {code} {reason}")
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(b"{}")

        def _json(self, obj):
            body = json.dumps(obj).encode()
            log(f"{self.command} {self.path} -> 200")
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            cfg = config()
            auth = self.headers.get("Authorization")
            if auth != "Bearer " + cfg.get("expected_token", ""):
                return self._reject(401, "auth")
            if self.headers.get("X-GitHub-Api-Version") != "2026-03-10":
                return self._reject(400, "api-version")

            repo = cfg["repository"]
            prefix = "/repos/" + repo["full_name"]
            url = urlparse(self.path)
            path = url.path

            if path == prefix:
                return self._json(repo)

            if path.startswith(prefix + "/actions/runs/"):
                rest = path[len(prefix + "/actions/runs/"):]
                if rest.endswith("/artifacts"):
                    run_id = rest[: -len("/artifacts")]
                    query = parse_qs(url.query)
                    name = (query.get("name") or [None])[0]
                    ids = cfg.get("run_artifacts", {}).get(run_id, [])
                    found = [cfg["artifacts"][str(i)] for i in ids
                             if str(i) in cfg.get("artifacts", {})
                             and (name is None
                                  or cfg["artifacts"][str(i)].get("name")
                                  == name)]
                    return self._json({"total_count": len(found),
                                       "artifacts": found})
                record = cfg.get("runs", {}).get(rest)
                if record is None:
                    return self._reject(404, "run-not-found")
                return self._json(record)

            if path.startswith(prefix + "/actions/artifacts/"):
                artifact_id = path[len(prefix + "/actions/artifacts/"):]
                record = cfg.get("artifacts", {}).get(artifact_id)
                if record is None:
                    return self._reject(404, "artifact-not-found")
                return self._json(record)

            self._reject(404, "unmatched")

        def log_message(self, *_args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    port = server.server_address[1]
    with open(ready_path, "w", encoding="utf-8") as fh:
        json.dump({"endpoint": f"http://127.0.0.1:{port}"}, fh)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    while True:
        time.sleep(3600)


if __name__ == "__main__":
    serve(*sys.argv[1:4])
