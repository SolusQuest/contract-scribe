#!/usr/bin/env python3
"""fake_release_github.py — loopback Releases/Git-refs/Actions fake (M6-R1).

Three loopback listeners = three origins (api / uploads / cdn), so the
release script's origin-separation rules are genuinely exercised:
  - api     : /repos/{repo}/... (releases, git refs, actions runs/jobs)
  - uploads : /uploads/repos/{repo}/releases/{id}/assets?name=...
  - cdn     : /cdn/{asset_id} (asset bytes after the api redirect)

State file (reloaded per request, atomically rewritten after mutations):
{
  "repository": {"id": 12345, "full_name": "SolusQuest/contract-scribe"},
  "expected_release_token": "contract-scribe-synthetic-release-only",
  "expected_read_token": "contract-scribe-synthetic-read-only",
  "releases": [{"id":9001,"tag_name":"payload-x","target_commitish":"<sha>",
                "name":"n","body":"b","draft":true,"prerelease":false,
                "assets":[{"id":1,"name":"a.tar.gz","file":"<path>",
                           "size":3,"digest":"sha256:..."}]}],
  "refs": {"tags/payload-x": "<40hex>"},
  "runs": [{"id":555,"event":"workflow_dispatch",
            "path":".github/workflows/release.yml","status":"completed",
            "conclusion":"success","head_branch":"main","head_sha":"<sha>",
            "run_attempt":1,"repository":{"id":12345,"full_name":"..."},
            "head_repository":{"id":12345}}],
  "artifacts": [{"run_id":555,"id":777,"name":"release-candidate",
                 "expired":false,"digest":"sha256:...",
                 "workflow_run":{"id":555,"head_sha":"<sha>",
                                 "head_repository_id":12345}}],
  "ci_runs": {"<head_sha>": [{"id":888,"conclusion":"success",
              "run_attempt":1,
              "jobs":[{"name":"action_packaged","status":"completed",
                       "conclusion":"success"}]}]},
  "overrides": {"drop_after_write": ["release-create","ref-create",
                 "asset-upload","release-publish"],
                "force_status": {"release-create": 500},
                "emit_digest_field": true}
}

Draft visibility rule (platform-faithful): /releases/tags/{t} never shows
drafts; authenticated enumeration does. Auth: release token for
release/ref/asset routes, read token for /repos + /actions routes.

Writes <ready-file> {"api","uploads","cdn"} and appends
"METHOD path auth=release|read|absent" lines to <log-file> — never the
credential value.
"""
import hashlib
import json
import os
import re
import socket
import sys
import threading
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

_last_good = {"value": None}


def load_state(path):
    for attempt in range(40):
        try:
            with open(path, encoding="utf-8") as fh:
                state = json.load(fh)
            _last_good["value"] = state
            return state
        except (json.JSONDecodeError, OSError):
            if attempt == 39:
                if _last_good["value"] is not None:
                    return _last_good["value"]
                raise
            time.sleep(0.05)
    return _last_good["value"]


def save_state(path, state):
    tmp = path + ".srv-tmp"
    with open(tmp, "w", encoding="utf-8") as fh:
        json.dump(state, fh)
    for attempt in range(50):
        try:
            os.replace(tmp, path)
            return
        except OSError:
            if attempt == 49:
                raise
            time.sleep(0.05)


def free_port():
    sock = socket.socket()
    sock.bind(("127.0.0.1", 0))
    port = sock.getsockname()[1]
    sock.close()
    return port


class Server:
    def __init__(self, state_path, log_file, uploads_origin, cdn_origin):
        self.state_path = state_path
        self.log_file = log_file
        self.uploads_origin = uploads_origin
        self.cdn_origin = cdn_origin
        self.api_origin = None
        self.lock = threading.Lock()

    def log(self, request):
        auth = request.headers.get("Authorization") or ""
        state = load_state(self.state_path)
        if auth == "Bearer " + state.get("expected_release_token", ""):
            channel = "release"
        elif auth == "Bearer " + state.get("expected_read_token", ""):
            channel = "read"
        elif auth:
            channel = "unknown"
        else:
            channel = "absent"
        with self.lock:
            with open(self.log_file, "a", encoding="utf-8") as fh:
                fh.write(f"{request.command} {request.path} "
                         f"auth={channel}\n")

    def auth(self, request, state, channel):
        expected = state.get("expected_release_token") \
            if channel == "release" else state.get("expected_read_token")
        return request.headers.get("Authorization") == "Bearer " \
            + (expected or "")


def release_doc(state, release, server):
    return {"id": release["id"], "tag_name": release["tag_name"],
            "target_commitish": release["target_commitish"],
            "name": release["name"], "body": release["body"],
            "draft": bool(release.get("draft")),
            "prerelease": bool(release.get("prerelease")),
            "url": None, "assets_url": None,
            "upload_url": f"{server.uploads_origin}/uploads/"
            f"{state['repository']['full_name']}/releases/"
            f"{release['id']}/assets{{?name,label}}",
            "assets": [asset_doc(state, server, a)
                       for a in release["assets"]]}


def asset_doc(state, server, asset):
    doc = {"id": asset["id"], "name": asset["name"],
           "size": asset.get("size", 0), "state": "uploaded",
           "url": f"{server.api_origin}/repos/"
           f"{state['repository']['full_name']}/releases/assets/"
           f"{asset['id']}",
           "digest": asset.get("digest")
           if state.get("overrides", {}).get("emit_digest_field", True)
           else None}
    return doc


def find_release(state, release_id):
    for release in state["releases"]:
        if release["id"] == release_id:
            return release
    return None


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

        def _body(self):
            length = int(self.headers.get("Content-Length") or 0)
            return self.rfile.read(min(length, 300 * 1024 * 1024))

        def _drop_after_write(self, state, route):
            return route in state.get("overrides", {}) \
                .get("drop_after_write", [])

        def _forced(self, state, route):
            forced = state.get("overrides", {}).get("force_status", {})
            return forced.get(route)

        def _deny(self, state, route):
            status = self._forced(state, route)
            if status:
                self._json(status, {"message": "forced"})
                return True
            return False

        # ----------------------------------------------------------- GET
        def do_GET(self):
            server.log(self)
            state = load_state(server.state_path)
            repo = state["repository"]["full_name"]
            base = f"/repos/{repo}"
            parsed = urllib.parse.urlsplit(self.path)
            path = parsed.path
            query = urllib.parse.parse_qs(parsed.query)

            if path == base:
                if not server.auth(self, state, "read"):
                    return self._json(401, {"message": "auth"})
                return self._json(200, state["repository"])

            if path == base + "/releases":
                if not server.auth(self, state, "release"):
                    return self._json(401, {"message": "auth"})
                page = int(query.get("page", ["1"])[0])
                per = int(query.get("per_page", ["30"])[0])
                rows = [release_doc(state, r, server)
                        for r in state["releases"]]
                start = (page - 1) * per
                return self._json(200, rows[start:start + per])

            if path.startswith(base + "/releases/tags/"):
                # Platform rule: the by-tag route exposes only published
                # releases; drafts are never visible here.
                tag = urllib.parse.unquote(path.rsplit("/", 1)[-1])
                if self._deny(state, "release-by-tag"):
                    return
                for release in state["releases"]:
                    if release["tag_name"] == tag \
                            and not release.get("draft"):
                        return self._json(
                            200, release_doc(state, release, server))
                return self._json(404, {"message": "Not Found"})

            match = re.fullmatch(base + r"/releases/(\d+)/assets", path)
            if match:
                if not server.auth(self, state, "release"):
                    return self._json(401, {"message": "auth"})
                release = find_release(state, int(match.group(1)))
                if release is None:
                    return self._json(404, {"message": "Not Found"})
                return self._json(
                    200, [asset_doc(state, server, a) for a in release["assets"]])

            match = re.fullmatch(base + r"/releases/(\d+)", path)
            if match:
                if not server.auth(self, state, "release"):
                    return self._json(401, {"message": "auth"})
                release = find_release(state, int(match.group(1)))
                if release is None:
                    return self._json(404, {"message": "Not Found"})
                return self._json(
                    200, release_doc(state, release, server))

            match = re.fullmatch(base + r"/releases/assets/(\d+)", path)
            if match:
                if not server.auth(self, state, "release"):
                    return self._json(401, {"message": "auth"})
                asset_id = int(match.group(1))
                for release in state["releases"]:
                    for asset in release["assets"]:
                        if asset["id"] == asset_id:
                            if self._deny(state, "asset-get"):
                                return
                            self.send_response(302)
                            self.send_header(
                                "Location",
                                f"{server.cdn_origin}/cdn/{asset_id}")
                            self.send_header("Content-Length", "0")
                            self.end_headers()
                            return None
                return self._json(404, {"message": "Not Found"})

            match = re.fullmatch(base + r"/git/ref/(.+)", path)
            if match:
                if not server.auth(self, state, "release"):
                    return self._json(401, {"message": "auth"})
                ref = urllib.parse.unquote(match.group(1))
                sha = state["refs"].get(ref)
                if sha is None:
                    return self._json(404, {"message": "Not Found"})
                return self._json(200, {"ref": f"refs/{ref}",
                                        "object": {"type": "commit",
                                                   "sha": sha}})

            match = re.fullmatch(base + r"/actions/runs/(\d+)/artifacts",
                                 path)
            if match:
                if not server.auth(self, state, "read"):
                    return self._json(401, {"message": "auth"})
                run_id = int(match.group(1))
                rows = [a for a in state["artifacts"]
                        if a["run_id"] == run_id]
                return self._json(200, {"total_count": len(rows),
                                        "artifacts": rows})

            match = re.fullmatch(base + r"/actions/runs/(\d+)/jobs", path)
            if match:
                if not server.auth(self, state, "read"):
                    return self._json(401, {"message": "auth"})
                run_id = int(match.group(1))
                jobs = []
                for runs in state["ci_runs"].values():
                    for run in runs:
                        if run["id"] == run_id:
                            jobs = run.get("jobs", [])
                return self._json(200, {"total_count": len(jobs),
                                        "jobs": jobs})

            match = re.fullmatch(base + r"/actions/runs/(\d+)", path)
            if match:
                if not server.auth(self, state, "read"):
                    return self._json(401, {"message": "auth"})
                for run in state["runs"]:
                    if run["id"] == int(match.group(1)):
                        return self._json(200, run)
                return self._json(404, {"message": "Not Found"})

            match = re.fullmatch(base + r"/actions/workflows/(.+)/runs",
                                 path)
            if match:
                if not server.auth(self, state, "read"):
                    return self._json(401, {"message": "auth"})
                workflow = urllib.parse.unquote(match.group(1))
                head_sha = query.get("head_sha", [""])[0]
                if workflow != ".github/workflows/ci.yml":
                    return self._json(404, {"message": "Not Found"})
                rows = state["ci_runs"].get(head_sha, [])
                return self._json(200, {"total_count": len(rows),
                                        "workflow_runs": rows})

            return self._json(404, {"message": "Not Found"})

        # ---------------------------------------------------------- POST
        def do_POST(self):
            server.log(self)
            state = load_state(server.state_path)
            repo = state["repository"]["full_name"]
            base = f"/repos/{repo}"
            parsed = urllib.parse.urlsplit(self.path)
            path = parsed.path
            query = urllib.parse.parse_qs(parsed.query)

            if path == base + "/releases":
                if not server.auth(self, state, "release"):
                    return self._json(401, {"message": "auth"})
                if self._deny(state, "release-create"):
                    return
                body = json.loads(self._body() or b"{}")
                for release in state["releases"]:
                    if release["tag_name"] == body.get("tag_name"):
                        return self._json(422, {"message": "tag conflict"})
                release = {
                    "id": max([r["id"] for r in state["releases"]] + [9000])
                    + 1,
                    "tag_name": body.get("tag_name"),
                    "target_commitish": body.get("target_commitish"),
                    "name": body.get("name"),
                    "body": body.get("body", ""),
                    "draft": bool(body.get("draft")),
                    "prerelease": bool(body.get("prerelease")),
                    "assets": [],
                }
                state["releases"].append(release)
                save_state(server.state_path, state)
                if self._drop_after_write(state, "release-create"):
                    self.close_connection = True
                    return
                return self._json(
                    201, release_doc(state, release, server))

            if path == base + "/git/refs":
                if not server.auth(self, state, "release"):
                    return self._json(401, {"message": "auth"})
                if self._deny(state, "ref-create"):
                    return
                body = json.loads(self._body() or b"{}")
                ref = body.get("ref", "")
                if not ref.startswith("refs/"):
                    return self._json(422, {"message": "bad ref"})
                key = ref[len("refs/"):]
                if key in state["refs"]:
                    return self._json(422, {"message": "ref exists"})
                state["refs"][key] = body.get("sha")
                save_state(server.state_path, state)
                if self._drop_after_write(state, "ref-create"):
                    self.close_connection = True
                    return
                return self._json(201, {"ref": ref, "object": {
                    "type": "commit", "sha": body.get("sha")}})

            return self._json(404, {"message": "Not Found"})

        # --------------------------------------------------------- PATCH
        def do_PATCH(self):
            server.log(self)
            state = load_state(server.state_path)
            repo = state["repository"]["full_name"]
            base = f"/repos/{repo}"
            path = urllib.parse.urlsplit(self.path).path

            match = re.fullmatch(base + r"/releases/(\d+)", path)
            if match:
                if not server.auth(self, state, "release"):
                    return self._json(401, {"message": "auth"})
                if self._deny(state, "release-publish"):
                    return
                release = find_release(state, int(match.group(1)))
                if release is None:
                    return self._json(404, {"message": "Not Found"})
                body = json.loads(self._body() or b"{}")
                if "draft" in body:
                    release["draft"] = bool(body["draft"])
                save_state(server.state_path, state)
                if self._drop_after_write(state, "release-publish"):
                    self.close_connection = True
                    return
                return self._json(
                    200, release_doc(state, release, server))

            return self._json(404, {"message": "Not Found"})

    return Handler


def make_uploads_handler(server):
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

        def do_POST(self):
            server.log(self)
            state = load_state(server.state_path)
            repo = state["repository"]["full_name"]
            parsed = urllib.parse.urlsplit(self.path)
            query = urllib.parse.parse_qs(parsed.query)
            match = re.fullmatch(
                rf"/uploads/{re.escape(repo)}/releases/(\d+)/assets",
                parsed.path)
            if not match or "name" not in query:
                return self._json(404, {"message": "Not Found"})
            if not server.auth(self, state, "release"):
                return self._json(401, {"message": "auth"})
            if "asset-upload" in state.get("overrides", {}) \
                    .get("force_status", {}):
                return self._json(
                    state["overrides"]["force_status"]["asset-upload"],
                    {"message": "forced"})
            release = find_release(state, int(match.group(1)))
            if release is None:
                return self._json(404, {"message": "Not Found"})
            name = query["name"][0]
            for asset in release["assets"]:
                if asset["name"] == name:
                    return self._json(422, {"message": "asset exists"})
            length = int(self.headers.get("Content-Length") or 0)
            data = self.rfile.read(length)
            asset_id = max(
                [a["id"] for r in state["releases"] for a in r["assets"]]
                + [0]) + 1
            store = os.path.join(os.path.dirname(server.state_path),
                                 f"asset-{asset_id}.bin")
            with open(store, "wb") as fh:
                fh.write(data)
            asset = {"id": asset_id, "name": name, "file": store,
                     "size": len(data),
                     "digest": "sha256:" + hashlib.sha256(data).hexdigest()}
            release["assets"].append(asset)
            save_state(server.state_path, state)
            if "asset-upload" in state.get("overrides", {}) \
                    .get("drop_after_write", []):
                self.close_connection = True
                return
            return self._json(201, asset_doc(state, server, asset))

    return Handler


def make_cdn_handler(server):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *args):
            pass

        def do_GET(self):
            server.log(self)
            state = load_state(server.state_path)
            match = re.fullmatch(r"/cdn/(\d+)", self.path)
            if match:
                asset_id = int(match.group(1))
                for release in state["releases"]:
                    for asset in release["assets"]:
                        if asset["id"] == asset_id:
                            with open(asset["file"], "rb") as fh:
                                data = fh.read()
                            self.send_response(200)
                            self.send_header(
                                "Content-Type", "application/octet-stream")
                            self.send_header("Content-Length",
                                             str(len(data)))
                            self.end_headers()
                            self.wfile.write(data)
                            return None
            self.send_response(404)
            self.send_header("Content-Length", "2")
            self.end_headers()
            self.wfile.write(b"{}")

    return Handler


def main(state_path, ready_file, log_file):
    ports = {key: free_port() for key in ("api", "uploads", "cdn")}
    uploads_origin = f"http://127.0.0.1:{ports['uploads']}"
    cdn_origin = f"http://127.0.0.1:{ports['cdn']}"
    server = Server(state_path, log_file, uploads_origin, cdn_origin)
    api = ThreadingHTTPServer(("127.0.0.1", ports["api"]),
                              make_api_handler(server))
    uploads = ThreadingHTTPServer(("127.0.0.1", ports["uploads"]),
                                  make_uploads_handler(server))
    cdn = ThreadingHTTPServer(("127.0.0.1", ports["cdn"]),
                              make_cdn_handler(server))
    server.api_origin = f"http://127.0.0.1:{ports['api']}"
    for listener in (api, uploads, cdn):
        listener.daemon_threads = True
    threading.Thread(target=uploads.serve_forever, daemon=True).start()
    threading.Thread(target=cdn.serve_forever, daemon=True).start()
    with open(ready_file, "w", encoding="utf-8") as fh:
        json.dump({"api": f"http://127.0.0.1:{ports['api']}",
                   "uploads": uploads_origin, "cdn": cdn_origin}, fh)
    api.serve_forever()


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3])
