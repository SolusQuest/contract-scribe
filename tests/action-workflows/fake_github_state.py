#!/usr/bin/env python3
"""fake_github_state.py — A3-owned wire-level GitHub substitute for the
action-workflows CI jobs (tests/action-workflows). NOT the A2 fake: it adds
what the fresh-runner handoff proof needs and keeps ownership inside #186's
declared paths.

Same publication wire contract as tests/action/fake_github_api.py:
  Authorization: Bearer contract-scribe-synthetic-transport-only and
  X-GitHub-Api-Version: 2026-03-10 on every request; Git object identities
  use the public git object format; updateRefs is CAS-only and rejects
  non-campaign refs and force pushes; pull requests must be drafts.

A3 additions:
  seed_repo   import every git object reachable from a real repository's
              HEAD so refs/heads/main == that checkout's base commit and
              request.expectedBaseCommitOid == git rev-parse HEAD holds on
              the production request code path.
  state_file  dump the full object/ref/PR state after every mutation so the
              producer's published coordination/proposal/PR state can cross
              job boundaries inside the diagnostics artifact.
  load_state  hydrate a previously dumped state instead of seeding — the
              consumer runner sees the same remote the producer published to.

Config JSON:
  {"repository": "Owner/repo",
   "seed_repo":  "<path to real git repo>",   # first start
   "state_file": "<path>",                    # dump after each mutation
   "load_state": "<path>"}                    # hydrate at startup
Exactly one of seed_repo / load_state is required.

Writes <ready-file> JSON {endpoint, baseOid}; appends "METHOD path" lines to
<log-file>; protocol violations append "FAIL <detail>" to <fail-file>.
"""

import base64
import hashlib
import json
import os
import socket
import subprocess
import sys
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

EXPECTED_AUTH = "Bearer contract-scribe-synthetic-transport-only"
EXPECTED_VERSION = "2026-03-10"
MAX_BODY = 32 * 1024 * 1024


def object_oid(kind, data):
    return hashlib.sha1(kind.encode() + b" " + str(len(data)).encode()
                        + b"\0" + data).hexdigest()


def _actor_parts(line):
    """'Name <mail> 1700000000 +0000' -> (name, email, iso date)."""
    name, rest = line.rsplit(" <", 1)
    email, rest = rest.split("> ", 1)
    import datetime
    stamp = int(rest.split()[0])
    date = datetime.datetime.fromtimestamp(
        stamp, datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    return {"name": name, "email": email, "date": date}


class State:
    def __init__(self):
        self.blobs = {}          # oid -> bytes
        self.trees = {}          # oid -> [(path, mode, oid)]
        self.commits = {}        # oid -> dict
        self.refs = {}
        self.pull_requests = []
        self.base_oid = None
        self.lock = threading.Lock()
        self.failures = []

    @staticmethod
    def actor():
        return {"name": "Synthetic", "email": "synthetic@example.invalid",
                "date": "2000-01-01T00:00:00Z"}

    # -- git-object import -------------------------------------------------
    def seed_from_repo(self, repo):
        head = subprocess.run(["git", "-C", repo, "rev-parse", "HEAD"],
                              capture_output=True, text=True, check=True
                              ).stdout.strip()
        listing = subprocess.run(
            ["git", "-C", repo, "rev-list", "--objects", "HEAD"],
            capture_output=True, text=True, check=True).stdout.splitlines()
        oids = [line.split(" ", 1)[0] for line in listing if line.strip()]
        for oid in oids:
            kind = subprocess.run(["git", "-C", repo, "cat-file", "-t", oid],
                                  capture_output=True, text=True,
                                  check=True).stdout.strip()
            raw = subprocess.run(["git", "-C", repo, "cat-file", kind, oid],
                                 capture_output=True, check=True).stdout
            if kind == "blob":
                self.blobs[oid] = raw
            elif kind == "tree":
                self.trees[oid] = self._parse_tree(raw)
            elif kind == "commit":
                self.commits[oid] = self._parse_commit(oid, raw)
        self.base_oid = head
        self.refs["refs/heads/main"] = head

    @staticmethod
    def _parse_tree(raw):
        entries = []
        while raw:
            mode, _, rest = raw.partition(b" ")
            name, _, rest = rest.partition(b"\0")
            oid = rest[:20].hex()
            raw = rest[20:]
            entries.append((name.decode(), mode.decode(), oid))
        return entries

    @staticmethod
    def _parse_commit(oid, raw):
        text = raw.decode()
        head, _, message = text.partition("\n\n")
        tree, parents = None, []
        author = committer = State.actor()
        for line in head.splitlines():
            if line.startswith("tree "):
                tree = line[5:]
            elif line.startswith("parent "):
                parents.append(line[7:])
            elif line.startswith("author "):
                author = _actor_parts(line[7:])
            elif line.startswith("committer "):
                committer = _actor_parts(line[10:])
        return {"sha": oid, "tree": {"sha": tree},
                "parents": [{"sha": p} for p in parents],
                "message": message, "author": author, "committer": committer}

    # -- persistence -------------------------------------------------------
    def dump(self, path):
        snapshot = {
            "blobs": {o: base64.b64encode(d).decode()
                      for o, d in self.blobs.items()},
            "trees": self.trees,
            "commits": self.commits,
            "refs": self.refs,
            "pull_requests": self.pull_requests,
            "base_oid": self.base_oid}
        fd, tmp = tempfile.mkstemp(dir=os.path.dirname(path) or ".")
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as fh:
                json.dump(snapshot, fh)
            os.replace(tmp, path)
        finally:
            if os.path.exists(tmp):
                os.unlink(tmp)

    def load(self, path):
        with open(path, encoding="utf-8") as fh:
            snapshot = json.load(fh)
        self.blobs = {o: base64.b64decode(d)
                      for o, d in snapshot["blobs"].items()}
        self.trees = {o: [tuple(e) for e in entries]
                      for o, entries in snapshot["trees"].items()}
        self.commits = snapshot["commits"]
        self.refs = snapshot["refs"]
        self.pull_requests = snapshot["pull_requests"]
        self.base_oid = snapshot["base_oid"]

    # -- object model (same wire shapes as the A2 fake) --------------------
    def add_blob(self, data):
        oid = object_oid("blob", data)
        self.blobs[oid] = data
        return oid

    def add_tree(self, entries):
        stream = bytearray()
        for path, mode, oid in sorted(
                entries, key=lambda e: e[0] + ("/" if e[1] in
                                             ("040000", "40000") else "\0")):
            stream += (b"40000" if mode == "040000" else mode.encode())
            stream += b" " + path.encode() + b"\0"
            stream += bytes.fromhex(oid)
        oid = object_oid("tree", bytes(stream))
        self.trees[oid] = entries
        return oid

    def add_commit(self, tree, parents, message, author, committer):
        def actor_line(role, actor):
            import datetime
            ts = int(datetime.datetime.fromisoformat(
                actor["date"].replace("Z", "+00:00")).timestamp())
            return (f"{role} {actor['name']} <{actor['email']}> {ts} +0000\n")
        text = "tree " + tree + "\n"
        text += "".join(f"parent {p}\n" for p in parents)
        text += actor_line("author", author) + actor_line("committer", committer)
        text += "\n" + message
        oid = object_oid("commit", text.encode())
        self.commits[oid] = {
            "sha": oid, "tree": {"sha": tree},
            "parents": [{"sha": p} for p in parents],
            "message": message, "author": author, "committer": committer}
        return oid

    def tree_response(self, oid):
        # Git objects store the directory mode unpadded (40000); the REST
        # API surface pads to six chars (040000), which the CLI requires.
        return {"sha": oid, "truncated": False, "tree": [
            {"path": p, "mode": m.zfill(6), "sha": o,
             "type": "tree" if m in ("040000", "40000")
             else "commit" if m == "160000" else "blob"}
            for p, m, o in self.trees[oid]]}

    @staticmethod
    def repository():
        return {"id": 1, "node_id": "REPO_node", "name": "repo",
                "full_name": "Owner/repo", "private": False,
                "archived": False, "disabled": False,
                "owner": {"id": 1, "node_id": "OWNER_node",
                          "login": "Owner", "type": "Organization"}}

    def commit_response(self, raw):
        response = dict(raw)
        if response["message"].endswith("\n"):
            response["message"] = response["message"][:-1]
        return response


def serve(config_path, ready_file, log_file, fail_file):
    with open(config_path, encoding="utf-8") as fh:
        config = json.load(fh)
    state = State()
    if config.get("load_state"):
        state.load(config["load_state"])
    elif config.get("seed_repo"):
        state.seed_from_repo(config["seed_repo"])
    else:
        raise SystemExit("fake_github_state: seed_repo or load_state required")
    state_path = config.get("state_file")
    log_lock = threading.Lock()

    def log_line(line):
        with log_lock:
            with open(log_file, "a", encoding="utf-8") as fh:
                fh.write(line + "\n")

    def fail(detail):
        with log_lock:
            with open(fail_file, "a", encoding="utf-8") as fh:
                fh.write("FAIL " + detail + "\n")

    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *args):
            pass

        def _send(self, status, body):
            data = json.dumps(body).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def _check(self):
            if self.headers.get("X-GitHub-Api-Version") != EXPECTED_VERSION:
                fail("api-version"); return False
            if self.headers.get("Authorization") != EXPECTED_AUTH:
                fail("authorization"); return False
            return True

        def _body(self):
            length = int(self.headers.get("Content-Length") or 0)
            if length > MAX_BODY:
                fail("body-bound")
                return None
            return self.rfile.read(length) if length else b"{}"

        def do_GET(self):
            self._handle("GET")

        def do_POST(self):
            self._handle("POST")

        def _handle(self, method):
            try:
                path = self.path.split("?")[0]
                log_line(f"{method} {path}")
                if not self._check():
                    return self._send(500, {"message": "wire contract"})
                with state.lock:
                    if method == "GET":
                        status, body = self._read(path)
                    else:
                        status, body = self._write(path, self._body())
                        if state_path and 200 <= status < 300:
                            state.dump(state_path)
                self._send(status, body)
            except Exception as error:  # noqa: BLE001 — record, never hang
                fail(repr(error))
                try:
                    self._send(500, {"message": "fake failure"})
                except OSError:
                    pass  # connection already broken; the log has the detail
            return None

        def _read(self, path):
            if path == "/repos/Owner/repo":
                return 200, state.repository()
            if path.startswith("/repos/Owner/repo/pulls"):
                for pr in state.pull_requests:
                    pr["head"]["sha"] = state.refs.get(
                        "refs/heads/" + pr["head"]["ref"])
                    if pr["state"] == "open":
                        pr["base"]["sha"] = state.refs["refs/heads/main"]
                if path == "/repos/Owner/repo/pulls":
                    return 200, state.pull_requests
                number = path.rsplit("/", 1)[-1]
                for pr in state.pull_requests:
                    if str(pr["number"]) == number:
                        return 200, pr
                return 404, {"message": "Not Found"}
            prefix = "/repos/Owner/repo/git/ref/"
            if path.startswith(prefix):
                name = "refs/" + path[len(prefix):]
                if name in state.refs:
                    return 200, {"ref": name, "node_id": "REF_node",
                                 "object": {"type": "commit",
                                            "sha": state.refs[name]}}
                return 404, {"message": "Not Found"}
            key = path.rsplit("/", 1)[-1]
            if "/git/blobs/" in path and key in state.blobs:
                data = state.blobs[key]
                return 200, {"sha": key, "encoding": "base64",
                             "size": len(data),
                             "content": base64.b64encode(data).decode()}
            if "/git/trees/" in path and key in state.trees:
                return 200, state.tree_response(key)
            if "/git/commits/" in path and key in state.commits:
                return 200, state.commit_response(state.commits[key])
            return 404, {"message": "Not Found"}

        def _write(self, path, raw):
            body = json.loads(raw.decode() or "{}")
            if path == "/repos/Owner/repo/git/blobs":
                if body.get("encoding") != "base64":
                    fail("blob-encoding")
                    return 500, {"message": "encoding"}
                return 201, {"sha": state.add_blob(
                    base64.b64decode(body["content"]))}
            if path == "/repos/Owner/repo/git/trees":
                entries = [(e["path"], e["mode"], e["sha"])
                           for e in body["tree"]]
                oid = state.add_tree(entries)
                return 201, state.tree_response(oid)
            if path == "/repos/Owner/repo/git/commits":
                oid = state.add_commit(
                    tree=body["tree"], parents=body["parents"],
                    message=body["message"], author=body["author"],
                    committer=body["committer"])
                return 201, state.commit_response(state.commits[oid])
            if path == "/graphql":
                query = body.get("query", "")
                if query != ("mutation($input:UpdateRefsInput!)"
                             "{updateRefs(input:$input){clientMutationId}}"):
                    fail("graphql-query")
                    return 500, {"message": "query"}
                update = body["variables"]["input"]["refUpdates"][0]
                name = update["name"]
                if body["variables"]["input"]["repositoryId"] != "REPO_node" \
                        or not name.startswith("refs/heads/contract-scribe/") \
                        or update.get("force"):
                    fail("cas-shape")
                    return 500, {"message": "cas"}
                after = update["afterOid"]
                if after == "0" * 40 or after not in state.commits:
                    fail("cas-after")
                    return 500, {"message": "after"}
                if state.refs.get(name, "0" * 40) != update["beforeOid"]:
                    return 200, {"data": None, "errors": [
                        {"message": "conflict", "type": "CONFLICT"}]}
                state.refs[name] = after
                return 200, {"data": {"updateRefs": {
                    "clientMutationId": body["variables"]["input"]
                    ["clientMutationId"]}}}
            if path == "/repos/Owner/repo/pulls":
                if not body.get("draft") or body.get("maintainer_can_modify"):
                    fail("pr-shape")
                    return 500, {"message": "pr"}
                if any(pr["state"] == "open" for pr in state.pull_requests):
                    return 422, {"message": "duplicate"}
                head = body["head"]
                number = len(state.pull_requests) + 1
                pr = {
                    "id": number, "node_id": f"PR_{number}", "number": number,
                    "state": "open", "draft": True, "merged": False,
                    "merged_at": None, "closed_at": None,
                    "created_at": "2026-01-01T00:00:00Z",
                    "title": body["title"], "body": body["body"],
                    "user": {"id": 41898282, "node_id": "MDM6Qm90NDE4OTgyODI=",
                             "login": "github-actions[bot]", "type": "Bot"},
                    "head": {"ref": head,
                             "sha": state.refs["refs/heads/" + head],
                             "repo": state.repository()},
                    "base": {"ref": body["base"],
                             "sha": state.refs["refs/heads/main"],
                             "repo": state.repository()},
                    "maintainer_can_modify": False}
                state.pull_requests.append(pr)
                return 201, pr
            return 404, {"message": "Not Found"}

    sock = socket.socket()
    sock.bind(("127.0.0.1", 0))
    port = sock.getsockname()[1]
    sock.close()
    server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    server.daemon_threads = True
    with open(ready_file, "w", encoding="utf-8") as fh:
        json.dump({"endpoint": f"http://127.0.0.1:{port}/",
                   "baseOid": state.base_oid}, fh)
    server.serve_forever()


if __name__ == "__main__":
    serve(sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4])
