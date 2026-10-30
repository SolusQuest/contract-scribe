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
import socket
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

EXPECTED_AUTH = "Bearer contract-scribe-synthetic-transport-only"
EXPECTED_VERSION = "2026-03-10"
MAX_BODY = 32 * 1024 * 1024

# Closed public field selections supported by this independent wire model.
CURRENT_CHECKPOINT_QUERY = """
        query($owner:String!,$name:String!,$coord:String!,$target:String!,$state:String!,$checkpoint:String!,$plan:String!,$archive:String!,$event:String!){
          repository(owner:$owner,name:$name){id fullDatabaseId name owner{login}
            base:ref(qualifiedName:$target){id name prefix target{__typename oid}}
            coord:ref(qualifiedName:$coord){id name prefix target{__typename oid ... on Commit{
              message author{name email date user{__typename id databaseId login}} committer{name email date}
              parents(first:2){totalCount pageInfo{hasNextPage} nodes{oid}}
              signature{__typename isValid state payload signature wasSignedByGitHub}
              tree{oid entries{name mode oid object{__typename ... on Tree{oid entries{name mode oid}}}}}
            }}}
            state:object(expression:$state){...CheckpointBlob}
            checkpoint:object(expression:$checkpoint){...CheckpointBlob}
            plan:object(expression:$plan){...CheckpointBlob}
            archive:object(expression:$archive){...CheckpointBlob}
            event:object(expression:$event){...CheckpointBlob}
          }
        }
        fragment CheckpointBlob on GitObject{__typename oid ... on Blob{byteSize isBinary isTruncated text}}
"""
HISTORICAL_CHECKPOINT_QUERY = """
        query($owner:String!,$name:String!,$coord:String!,$anchor:String!,$state:String!){
          repository(owner:$owner,name:$name){id fullDatabaseId name owner{login}
            coord:ref(qualifiedName:$coord){id name prefix target{__typename oid}}
            document:object(expression:$anchor){__typename oid ... on Commit{
              message author{name email date user{__typename id databaseId login}} committer{name email date}
              parents(first:2){totalCount pageInfo{hasNextPage} nodes{oid}}
              signature{__typename isValid state payload signature wasSignedByGitHub}
              tree{oid entries{name mode oid object{__typename ... on Tree{oid entries{name mode oid}}}}}
            }}
            state:object(expression:$state){__typename oid ... on Blob{byteSize isBinary isTruncated text}}
          }
        }
"""


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
        self.trees[oid] = sorted(entries, key=lambda e: e[0] + ("/" if e[1] in ("040000", "40000") else "\0"))
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

    def checkpoint_snapshot(self, variables):
        historical = "anchor" in variables
        files = {"state": "coordination-state-v1.json"} if historical else {
            "state": "coordination-state-v1.json", "checkpoint": "checkpoint.json",
            "plan": "plan.json", "archive": "archive-predecessor.json", "event": "publication-event.json"}
        expected = {"owner", "name", "coord", "anchor" if historical else "target", *files}
        if set(variables) != expected or variables["owner"] != "Owner" or variables["name"] != "repo":
            raise ValueError("checkpoint-query-variables")
        coord = variables["coord"]
        if not coord.startswith("refs/heads/contract-scribe/coordination/"):
            raise ValueError("checkpoint-query-ref")
        if not historical and variables["target"] != "refs/heads/main":
            raise ValueError("checkpoint-query-target")
        anchor = variables["anchor"] if historical else coord
        for alias, path in files.items():
            if variables[alias] != anchor + ":.contract-scribe/" + path:
                raise ValueError("checkpoint-query-path")
        head = anchor if historical else self.refs.get(coord)
        repository = {"id": "REPO_node", "fullDatabaseId": "1", "name": "repo", "owner": {"login": "Owner"},
                      "coord": self.checkpoint_ref(coord, not historical)}
        if historical:
            repository["document"] = self.checkpoint_commit(head) if head in self.commits else None
        else:
            repository["base"] = self.checkpoint_ref(variables["target"], False)
        entries = {}
        if head in self.commits:
            root = self.trees[self.commits[head]["tree"]["sha"]]
            directory = next((e for e in root if e[0] == ".contract-scribe" and e[1] in ("040000", "40000")), None)
            if directory:
                entries = {e[0]: e[2] for e in self.trees[directory[2]]}
        for alias, path in files.items():
            oid = entries.get(path)
            data = self.blobs.get(oid)
            repository[alias] = None if data is None else {"__typename": "Blob", "oid": oid,
                "byteSize": len(data), "isBinary": False, "isTruncated": False, "text": data.decode("utf-8")}
        return {"data": {"repository": repository}}

    def checkpoint_ref(self, name, content):
        oid = self.refs.get(name)
        if oid is None:
            return None
        target = self.checkpoint_commit(oid) if content else {"__typename": "Commit", "oid": oid}
        return {"id": "REF_node", "name": name[11:], "prefix": "refs/heads/", "target": target}

    def checkpoint_commit(self, oid):
        raw = self.commits[oid]
        author = dict(raw["author"], user=raw.get("checkpointAuthor"))
        parents = raw["parents"]
        return {"__typename": "Commit", "oid": oid, "message": raw["message"], "author": author,
                "committer": raw["committer"], "parents": {"totalCount": len(parents),
                "pageInfo": {"hasNextPage": len(parents) > 2}, "nodes": [{"oid": p["sha"]} for p in parents[:2]]},
                "signature": raw.get("checkpointSignature"), "tree": self.checkpoint_tree(raw["tree"]["sha"], True)}

    def checkpoint_tree(self, oid, children):
        entries = []
        for path, mode, value in self.trees[oid]:
            entry = {"name": path, "mode": int(mode, 8), "oid": value}
            if children:
                entry["object"] = self.checkpoint_tree(value, False) if mode in ("040000", "40000") else {"__typename": "Blob"}
            entries.append(entry)
        return {"__typename": "Tree", "oid": oid, "entries": entries}

    def atomic_checkpoint(self, value):
        if set(value) != {"branch", "expectedHeadOid", "clientMutationId", "message", "fileChanges"} or value["branch"] != {"id": "REF_node"}:
            raise ValueError("atomic-checkpoint-input")
        reference, = [r for r in self.refs if r.startswith("refs/heads/contract-scribe/coordination/")]
        before = value["expectedHeadOid"]
        if self.refs[reference] != before:
            return {"data": None, "errors": [{"message": "conflict", "type": "CONFLICT"}]}
        if set(value["message"]) != {"headline", "body"} or set(value["fileChanges"]) != {"additions"}:
            raise ValueError("atomic-checkpoint-shape")
        root = self.trees[self.commits[before]["tree"]["sha"]]
        if len(root) != 1 or root[0][:2] != (".contract-scribe", "040000"):
            raise ValueError("atomic-checkpoint-root")
        leaf = {p: (p, m, o) for p, m, o in self.trees[root[0][2]]}
        paths = set()
        allowed = {"coordination-state-v1.json", "checkpoint.json", "plan.json", "archive-predecessor.json", "publication-event.json"}
        for file in value["fileChanges"]["additions"]:
            if set(file) != {"path", "contents"} or not file["path"].startswith(".contract-scribe/"):
                raise ValueError("atomic-checkpoint-file")
            path = file["path"][17:]
            if path not in allowed or path in paths:
                raise ValueError("atomic-checkpoint-path")
            paths.add(path)
            leaf[path] = (path, "100644", self.add_blob(base64.b64decode(file["contents"], validate=True)))
        if "coordination-state-v1.json" not in paths:
            raise ValueError("atomic-checkpoint-state")
        tree = self.add_tree([(".contract-scribe", "040000", self.add_tree(list(leaf.values())))])
        import datetime
        date = datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0)
        stamp = int(date.timestamp())
        message = value["message"]["headline"] + "\n\n" + value["message"]["body"]
        author = {"name": "github-actions[bot]", "email": "41898282+github-actions[bot]@users.noreply.github.com", "date": date.isoformat()}
        committer = {"name": "GitHub", "email": "noreply@github.com", "date": date.isoformat()}
        headers = f"tree {tree}\nparent {before}\nauthor {author['name']} <{author['email']}> {stamp} +0000\ncommitter {committer['name']} <{committer['email']}> {stamp} +0000\n"
        payload = headers + "\n" + message
        signature = "-----BEGIN PGP SIGNATURE-----\nsynthetic-action-wire-fixture\n-----END PGP SIGNATURE-----\n"
        raw = headers + "gpgsig " + signature.rstrip("\n").replace("\n", "\n ") + "\n\n" + message
        oid = object_oid("commit", raw.encode())
        self.commits[oid] = {"sha": oid, "tree": {"sha": tree}, "parents": [{"sha": before}],
            "message": message, "author": author, "committer": committer,
            "checkpointAuthor": {"__typename": "User", "id": "MDM6Qm90NDE4OTgyODI=", "databaseId": 41898282, "login": "github-actions[bot]"},
            "checkpointSignature": {"__typename": "GpgSignature", "isValid": True, "state": "VALID",
                "wasSignedByGitHub": True, "payload": payload, "signature": signature},
            "verification": {"verified": True, "reason": "valid", "payload": payload, "signature": signature}}
        self.refs[reference] = oid
        return {"data": {"createCommitOnBranch": {"clientMutationId": value["clientMutationId"]}}}

    def commit_response(self, raw):
        response = {key: raw[key] for key in ("sha", "tree", "parents", "message", "author", "committer")}
        if "verification" in raw:
            response["verification"] = raw["verification"]
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
                compact = "".join(query.split())
                if compact in ("".join(CURRENT_CHECKPOINT_QUERY.split()), "".join(HISTORICAL_CHECKPOINT_QUERY.split())):
                    return 200, state.checkpoint_snapshot(body["variables"])
                if query == "mutation($input:CreateCommitOnBranchInput!){createCommitOnBranch(input:$input){clientMutationId}}":
                    return 200, state.atomic_checkpoint(body["variables"]["input"])
                if query != ("mutation($input:UpdateRefsInput!)"
                             "{updateRefs(input:$input){clientMutationId}}"):
                    fail("graphql-query")
                    return 500, {"message": "query"}
                updates = body["variables"]["input"]["refUpdates"]
                if len(updates) not in (1, 2) or len({u["name"] for u in updates}) != len(updates):
                    fail("cas-count")
                    return 500, {"message": "cas"}
                for update in updates:
                    name = update["name"]
                    if body["variables"]["input"]["repositoryId"] != "REPO_node" or not name.startswith("refs/heads/contract-scribe/") or update.get("force"):
                        fail("cas-shape")
                        return 500, {"message": "cas"}
                    after = update["afterOid"]
                    if after == "0" * 40 or after not in state.commits:
                        fail("cas-after")
                        return 500, {"message": "after"}
                    if state.refs.get(name, "0" * 40) != update["beforeOid"]:
                        return 200, {"data": None, "errors": [{"message": "conflict", "type": "CONFLICT"}]}
                for update in updates:
                    state.refs[update["name"]] = update["afterOid"]
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
