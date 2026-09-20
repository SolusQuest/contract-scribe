#!/usr/bin/env python3
"""fake_github_api.py — bounded wire-level GitHub model for Action tests.

Port of tests/ContractScribe.IntegrationTests/GitHubProposalLoopbackServer.cs
to a stdlib-only loopback server so the packed CLI's real publication path
can run end-to-end without credentials. Object identities use Git's public
object format (sha1 "<kind> <len>\\0<body>"). Asserts the same wire contract:
Authorization: Bearer contract-scribe-synthetic-transport-only and
X-GitHub-Api-Version: 2026-03-10 on every request.

Config JSON: {"files": {"repo/path": "text"}, "repository": "Owner/repo"}
Writes <ready-file> JSON {endpoint, baseOid} once listening.
Appends one line per request to <log-file>: "METHOD path".
Protocol violations append "FAIL <detail>" to <fail-file>.
"""

import base64
import hashlib
import json
import os
import re
import socket
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

EXPECTED_AUTH = "Bearer contract-scribe-synthetic-transport-only"
EXPECTED_VERSION = "2026-03-10"
MAX_BODY = 32 * 1024 * 1024


def object_oid(kind, data):
    return hashlib.sha1(kind.encode() + b" " + str(len(data)).encode()
                        + b"\0" + data).hexdigest()


class State:
    def __init__(self, files):
        self.blobs = {}
        self.trees = {}          # oid -> [(path, mode, oid)]
        self.commits = {}        # oid -> dict
        self.refs = {}
        self.pull_requests = []
        self.lock = threading.Lock()
        self.failures = []
        tree = self.build_tree(files)
        self.base_oid = self.add_commit(
            tree=tree, parents=["1" * 40], message="Synthetic immutable base\n",
            author=self.actor(), committer=self.actor())
        self.refs["refs/heads/main"] = self.base_oid

    @staticmethod
    def actor():
        return {"name": "Synthetic", "email": "synthetic@example.invalid",
                "date": "2000-01-01T00:00:00Z"}

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

    def build_tree(self, files):
        groups = {}
        for path, content in files.items():
            groups.setdefault(path.split("/")[0], []).append((path, content))
        entries = []
        for key, items in groups.items():
            if len(items) == 1 and items[0][0] == key:
                entries.append((key, "100644",
                                self.add_blob(items[0][1].encode())))
            else:
                sub = {p[len(key) + 1:]: c for p, c in items
                       if p.startswith(key + "/")}
                entries.append((key, "040000", self.build_tree(sub)))
        return self.add_tree(entries)

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
        return {"sha": oid, "truncated": False, "tree": [
            {"path": p, "mode": m, "sha": o,
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
    state = State(config.get("files", {}))
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
                self._send(status, body)
            except Exception as error:  # noqa: BLE001 — record, never hang
                fail(repr(error))
                try:
                    self._send(500, {"message": "fake failure"})
                except OSError:
                    pass  # client already gone — recording the failure is enough
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
