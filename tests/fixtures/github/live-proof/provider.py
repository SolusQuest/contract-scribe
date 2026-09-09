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
            evidence = next(v for v in (json.loads(m['content']) for m in wire['messages']) if v.get('authority') == 'target-evidence')
            reference = next(r['evidenceReferenceId'] for r in evidence['evidenceReferences'] if 'symbolRef' in r['subject'])
            terminal = {'kind': 'proposal', 'target': evidence['terminalTarget'], 'contentUnits': [
                {'kind': 'content.summary', 'lines': ['Runs the synthetic operation.'],
                 'claimCategoryId': 'claim.behavior', 'evidenceReferenceIds': [reference]}]}
            response = json.dumps({'choices': [{'index': 0, 'message': {'role': 'assistant', 'tool_calls': [
                {'id': 'call.terminal', 'type': 'function', 'function': {'name': 'cs_terminal', 'arguments': json.dumps(terminal)}}]},
                'finish_reason': 'tool_calls'}]}).encode()
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
