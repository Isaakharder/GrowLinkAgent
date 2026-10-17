#!/usr/bin/env python3
"""A local proxy in front of a climate server, for testing the agent's retry handling.

    python3 tools/fault_proxy.py <port> <target base, e.g. https://<ref>.supabase.co>

Forwards every request to the target unchanged, except while faults are queued:
    POST /__mode {"mode": "503" | "429" | "500" | "pass", "count": n, "retry_after": s}
answers the next n requests with that status instead (429 with Retry-After).
"""
import json, sys, urllib.request, urllib.error
from collections import deque
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT, TARGET = int(sys.argv[1]), sys.argv[2].rstrip('/')
faults = deque()

class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a): pass

    def _send(self, status, body=b'{}', headers=()):
        self.send_response(status)
        for k, v in headers: self.send_header(k, v)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _handle(self):
        length = int(self.headers.get('Content-Length') or 0)
        body = self.rfile.read(length) if length else None
        if self.path == '/__mode':
            m = json.loads(body or b'{}')
            for _ in range(int(m.get('count', 1))): faults.append((m.get('mode', 'pass'), m.get('retry_after')))
            return self._send(200, json.dumps({'queued': len(faults)}).encode())
        if faults:
            mode, retry_after = faults.popleft()
            if mode != 'pass':
                headers = [('Retry-After', str(retry_after))] if mode == '429' and retry_after else []
                return self._send(int(mode), json.dumps({'error': f'injected {mode}'}).encode(), headers)
        req = urllib.request.Request(TARGET + self.path, data=body, method=self.command,
                                     headers={k: v for k, v in self.headers.items() if k.lower() in ('authorization', 'content-type')})
        try:
            with urllib.request.urlopen(req, timeout=60) as r: self._send(r.status, r.read())
        except urllib.error.HTTPError as e: self._send(e.code, e.read())

    do_GET = do_POST = _handle

ThreadingHTTPServer(('127.0.0.1', PORT), Handler).serve_forever()
