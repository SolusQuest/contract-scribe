"""Fixed credential-free numeric-loopback provider for the original synthetic method."""
import http.server
import json
import socket
import time

PORT = 48265


class Provider(http.server.BaseHTTPRequestHandler):
    requests = 0

    def log_message(self, *_):
        pass

    def do_POST(self):
        try:
            type(self).requests += 1
            size = int(self.headers['Content-Length'])
            if self.path != '/v1/chat/completions' or not 0 < size <= 1048576 or type(self).requests > 8:
                raise ValueError()
            wire = json.loads(self.rfile.read(size))
            policy = json.loads(wire['messages'][3]['content'])
            evidence = json.loads(wire['messages'][4]['content'])
            if evidence['authority'] != 'target-evidence':
                raise ValueError()
            finish = bool(evidence['progress'])
            if finish:
                payload = {'kind': 'finish', 'scribeRequestSha256': policy['artifactSha256']}
            else:
                target_id, = evidence['executableTargetIds']
                member = next(m for m in evidence['manifest'] if m['targetId'] == target_id)
                reference = next(r for r in evidence['evidenceReferences']
                                 if r['evidenceReferenceId'] in member['evidenceReferenceIds'] and 'symbolRef' in r['subject'])
                result = {'kind': 'proposal', 'target': member['proposalTarget'], 'contentUnits': [
                    {'kind': 'content.summary', 'lines': ['Runs the synthetic operation.'],
                     'claimCategoryId': reference['claimCategoryIds'][0],
                     'evidenceReferenceIds': [reference['evidenceReferenceId']]}]}
                if member['applicableComponents']:
                    result = {'kind': 'skip', 'reason': 'scribe.skip.insufficient-evidence', 'evidenceReferenceIds': []}
                payload = {'kind': 'submit', 'scribeRequestSha256': policy['artifactSha256'],
                           'sequence': evidence['nextSequence'], 'items': [{'targetId': target_id, 'result': result}]}
            response = json.dumps({'choices': [{'index': 0, 'message': {
                'role': 'assistant', 'content': None, 'reasoning_content': 'synthetic-proof-continuation', 'tool_calls': [
                    {'id': 'call.finish' if finish else 'call.submit', 'type': 'function',
                     'function': {'name': 'cs_terminal', 'arguments': json.dumps(payload)}}]},
                'finish_reason': 'tool_calls'}], 'usage': {
                    'prompt_tokens': 0 if finish else 31, 'prompt_cache_hit_tokens': 0 if finish else 29,
                    'prompt_cache_miss_tokens': 0 if finish else 2, 'completion_tokens': 0 if finish else 7}}).encode()
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Content-Length', str(len(response)))
            self.end_headers()
            self.wfile.write(response)
        except (ValueError, KeyError, StopIteration, TypeError):
            self.send_error(400, 'Unsupported synthetic request')


def wait_ready(process):
    for _ in range(50):
        if process.poll() is not None:
            raise OSError('provider-unavailable')
        try:
            with socket.create_connection(('127.0.0.1', PORT), timeout=.1):
                return
        except OSError:
            time.sleep(.1)
    raise OSError('provider-unavailable')


if __name__ == '__main__':
    http.server.ThreadingHTTPServer(('127.0.0.1', PORT), Provider).serve_forever()
