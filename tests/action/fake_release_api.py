#!/usr/bin/env python3
"""fake_release_api.py — bounded Releases API + asset CDN fake.

Two loopback listeners on different ports = two origins, so the Action's
"Authorization never crosses origins" rule is genuinely exercised.

Config file (reloaded per request — legs mutate it without restarts):
{
  "repository": "SolusQuest/contract-scribe",
  "expected_token": "contract-scribe-synthetic-acquisition-only",
  "releases": [
    {"id": 9001, "tag": "payload-1.2.3", "draft": false,
     "assets": [{"id": 1, "name": "a.tar.gz", "file": "<path>"}]}
  ],
  "overrides": {
    "by_tag_status": null,        // e.g. 404/403 to force statuses
    "asset_200": false,           // serve bytes directly instead of 302
    "asset_location": null,       // custom redirect Location (policy tests)
    "truncate_bytes": 0,          // send fewer bytes than Content-Length
    "asset_delay_seconds": 0      // slow-drip / timeout tests
  }
}
Auth rule: enumeration + asset endpoints require Authorization == expected_token
only when the config sets "require_auth": true (draft-lookup legs); the public
by-tag route never requires it but accepts it.

Writes <ready-file> {"api": "http://127.0.0.1:P/", "cdn": "http://127.0.0.1:Q/"}
and appends "METHOD path auth=present|absent" lines to <log-file>.
"""

import json
import os
import socket
import sys
import threading
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


_last_good = {"value": None}


def load_config(path):
    # Legs rewrite the config between requests; on platforms where the
    # replace is not atomic the torn/absent window is real, so serve the
    # last fully-parsed document rather than failing the request.
    for attempt in range(40):
        try:
            with open(path, encoding="utf-8") as fh:
                config = json.load(fh)
            _last_good["value"] = config
            return config
        except (json.JSONDecodeError, OSError):
            if attempt == 39:
                if _last_good["value"] is not None:
                    return _last_good["value"]
                raise
            time.sleep(0.05)
    return _last_good["value"]


def free_port():
    sock = socket.socket()
    sock.bind(("127.0.0.1", 0))
    port = sock.getsockname()[1]
    sock.close()
    return port


class Server:
    def __init__(self, config_path, log_file, cdn_origin):
        self.config_path = config_path
        self.log_file = log_file
        self.cdn_origin = cdn_origin
        self.lock = threading.Lock()

    def log(self, request):
        auth = "present" if request.headers.get("Authorization") else "absent"
        with self.lock:
            with open(self.log_file, "a", encoding="utf-8") as fh:
                fh.write(f"{request.command} {request.path} auth={auth}\n")

    def check_auth(self, request, config):
        if not config.get("require_auth"):
            return True
        return request.headers.get("Authorization") == \
            "Bearer " + config.get("expected_token", "")


def make_api_handler(server):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *args):
            pass

        def _json(self, status, body):
            data = json.dumps(body).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def do_GET(self):
            server.log(self)
            config = load_config(server.config_path)
            overrides = config.get("overrides", {})
            repo = config["repository"]
            base = f"/repos/{repo}"
            path = urllib.parse.urlsplit(self.path).path
            if path.startswith(base + "/releases/tags/"):
                tag = urllib.parse.unquote(path.rsplit("/", 1)[-1])
                if overrides.get("by_tag_status"):
                    return self._json(overrides["by_tag_status"],
                                      {"message": "forced"})
                for release in config["releases"]:
                    if release["tag"] == tag and not release.get("draft"):
                        return self._json(200, release_doc(release))
                return self._json(404, {"message": "Not Found"})
            if path == base + "/releases":
                if not server.check_auth(self, config):
                    return self._json(401, {"message": "Requires authentication"})
                page = int(urllib.parse.parse_qs(
                    urllib.parse.urlsplit(self.path).query
                ).get("page", ["1"])[0])
                per = int(urllib.parse.parse_qs(
                    urllib.parse.urlsplit(self.path).query
                ).get("per_page", ["30"])[0])
                rows = [release_doc(r) for r in config["releases"]]
                start = (page - 1) * per
                return self._json(200, rows[start:start + per])
            if path.startswith(base + "/releases/assets/"):
                if not server.check_auth(self, config):
                    return self._json(401, {"message": "Requires authentication"})
                asset_id = int(path.rsplit("/", 1)[-1])
                for release in config["releases"]:
                    for asset in release["assets"]:
                        if asset["id"] == asset_id:
                            return serve_asset(self, release, asset,
                                               overrides, server.cdn_origin)
                return self._json(404, {"message": "Not Found"})
            return self._json(404, {"message": "Not Found"})

    return Handler


def release_doc(release):
    return {"id": release["id"], "tag_name": release["tag"],
            "draft": bool(release.get("draft")),
            "assets": [{"id": a["id"], "name": a["name"],
                        "size": os.path.getsize(a["file"])}
                       for a in release["assets"]]}


def serve_asset(handler, release, asset, overrides, cdn_origin):
    if overrides.get("asset_location"):
        handler.send_response(302)
        handler.send_header("Location", overrides["asset_location"])
        handler.send_header("Content-Length", "0")
        handler.end_headers()
        return
    if overrides.get("asset_200", False):
        return send_bytes(handler, asset["file"], overrides)
    handler.send_response(302)
    handler.send_header("Location", f"{cdn_origin}/cdn/{asset['id']}")
    handler.send_header("Content-Length", "0")
    handler.end_headers()
    return None


def send_bytes(handler, path, overrides):
    size = os.path.getsize(path)
    delay = float(overrides.get("asset_delay_seconds", 0))
    truncate = int(overrides.get("truncate_bytes", 0))
    body_size = size if not truncate else max(0, size - truncate)
    handler.send_response(200)
    handler.send_header("Content-Type", "application/octet-stream")
    handler.send_header("Content-Length", str(size))
    handler.end_headers()
    sent = 0
    with open(path, "rb") as fh:
        while True:
            chunk = fh.read(1 << 16)
            if not chunk or sent >= body_size:
                break
            chunk = chunk[:body_size - sent]
            handler.wfile.write(chunk)
            sent += len(chunk)
            if delay:
                time.sleep(delay)


def make_cdn_handler(server):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *args):
            pass

        def do_GET(self):
            server.log(self)
            config = load_config(server.config_path)
            overrides = config.get("overrides", {})
            asset_id = int(self.path.rsplit("/", 1)[-1])
            for release in config["releases"]:
                for asset in release["assets"]:
                    if asset["id"] == asset_id:
                        return send_bytes(self, asset["file"], overrides)
            self.send_response(404)
            self.send_header("Content-Length", "2")
            self.end_headers()
            self.wfile.write(b"{}")

    return Handler


def main(config_path, ready_file, log_file):
    api_port, cdn_port = free_port(), free_port()
    cdn_origin = f"http://127.0.0.1:{cdn_port}"
    server = Server(config_path, log_file, cdn_origin)
    api = ThreadingHTTPServer(("127.0.0.1", api_port), make_api_handler(server))
    cdn = ThreadingHTTPServer(("127.0.0.1", cdn_port), make_cdn_handler(server))
    api.daemon_threads = cdn.daemon_threads = True
    threading.Thread(target=cdn.serve_forever, daemon=True).start()
    with open(ready_file, "w", encoding="utf-8") as fh:
        json.dump({"api": f"http://127.0.0.1:{api_port}", "cdn": cdn_origin}, fh)
    api.serve_forever()


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3])
