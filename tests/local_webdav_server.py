#!/usr/bin/env python3
"""Tiny local WebDAV server for CloudRedirect compatibility smoke tests."""

from __future__ import annotations

import argparse
import base64
import html
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import quote, unquote, urlsplit


def encode_href(rel_path: str, is_dir: bool) -> str:
    pieces = [quote(p) for p in rel_path.replace("\\", "/").split("/") if p]
    href = "/dav/"
    if pieces:
        href += "/".join(pieces)
        if is_dir:
            href += "/"
    return href


class WebDavHandler(BaseHTTPRequestHandler):
    server_version = "CloudRedirectLocalWebDAV/1.0"

    def _send_empty(self, status: int) -> None:
        self.send_response(status)
        self.send_header("Content-Length", "0")
        self.end_headers()

    def _authorized(self) -> bool:
        expected = "Basic " + base64.b64encode(
            f"{self.server.username}:{self.server.password}".encode("utf-8")
        ).decode("ascii")
        if self.headers.get("Authorization") == expected:
            return True
        self.send_response(401)
        self.send_header("WWW-Authenticate", 'Basic realm="CloudRedirectLocalWebDAV"')
        self.send_header("Content-Length", "0")
        self.end_headers()
        return False

    def _target(self) -> tuple[Path | None, str]:
        path = unquote(urlsplit(self.path).path)
        if path == "/":
            rel = ""
        elif path.startswith("/dav/"):
            rel = path[5:]
        elif path == "/dav":
            rel = ""
        else:
            return None, ""

        rel = rel.strip("/")
        parts = [p for p in rel.split("/") if p]
        if any(p in (".", "..") for p in parts):
            return None, ""
        target = self.server.root.joinpath(*parts) if parts else self.server.root
        return target, "/".join(parts)

    def do_OPTIONS(self) -> None:
        self.send_response(200)
        self.send_header("DAV", "1, 2")
        self.send_header("Allow", "OPTIONS, PROPFIND, MKCOL, PUT, GET, HEAD, DELETE")
        self.send_header("Content-Length", "0")
        self.end_headers()

    def do_PROPFIND(self) -> None:
        if not self._authorized():
            return
        target, rel = self._target()
        if target is None:
            self._send_empty(400)
            return
        if not target.exists():
            self._send_empty(404)
            return

        depth = self.headers.get("Depth", "0")
        entries: list[tuple[Path, str]] = [(target, rel)]
        if depth != "0" and target.is_dir():
            for child in sorted(target.iterdir(), key=lambda p: p.name.lower()):
                child_rel = f"{rel}/{child.name}".strip("/")
                entries.append((child, child_rel))

        responses = []
        for item, item_rel in entries:
            is_dir = item.is_dir()
            href = html.escape(encode_href(item_rel, is_dir))
            resourcetype = "<D:collection/>" if is_dir else ""
            size = 0 if is_dir else item.stat().st_size
            responses.append(
                "<D:response>"
                f"<D:href>{href}</D:href>"
                "<D:propstat><D:prop>"
                f"<D:resourcetype>{resourcetype}</D:resourcetype>"
                f"<D:getcontentlength>{size}</D:getcontentlength>"
                "</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>"
                "</D:response>"
            )

        body = (
            '<?xml version="1.0" encoding="utf-8"?>'
            '<D:multistatus xmlns:D="DAV:">'
            + "".join(responses)
            + "</D:multistatus>"
        ).encode("utf-8")
        self.send_response(207)
        self.send_header("Content-Type", "application/xml; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_MKCOL(self) -> None:
        if not self._authorized():
            return
        target, _ = self._target()
        if target is None:
            self._send_empty(400)
            return
        if target.exists():
            self._send_empty(405)
            return
        if not target.parent.exists():
            self._send_empty(409)
            return
        target.mkdir()
        self._send_empty(201)

    def do_PUT(self) -> None:
        if not self._authorized():
            return
        target, _ = self._target()
        if target is None:
            self._send_empty(400)
            return
        existed = target.exists()
        target.parent.mkdir(parents=True, exist_ok=True)
        length = int(self.headers.get("Content-Length", "0") or "0")
        target.write_bytes(self.rfile.read(length))
        self._send_empty(204 if existed else 201)

    def do_GET(self) -> None:
        if not self._authorized():
            return
        target, _ = self._target()
        if target is None or not target.is_file():
            self._send_empty(404)
            return
        data = target.read_bytes()
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_HEAD(self) -> None:
        if not self._authorized():
            return
        target, _ = self._target()
        self._send_empty(200 if target is not None and target.exists() else 404)

    def do_DELETE(self) -> None:
        if not self._authorized():
            return
        target, _ = self._target()
        if target is None:
            self._send_empty(400)
            return
        if not target.exists():
            self._send_empty(404)
            return
        if target.is_file() or target.is_symlink():
            target.unlink()
            self._send_empty(204)
            return
        try:
            target.rmdir()
            self._send_empty(204)
        except OSError:
            self._send_empty(409)

    def log_message(self, fmt: str, *args: object) -> None:
        if self.server.verbose:
            super().log_message(fmt, *args)


class LocalWebDavServer(ThreadingHTTPServer):
    def __init__(self, server_address, handler_class, root: Path, username: str, password: str, verbose: bool):
        super().__init__(server_address, handler_class)
        self.root = root
        self.username = username
        self.password = password
        self.verbose = verbose


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--root", required=True)
    parser.add_argument("--username", default="cloudredirect")
    parser.add_argument("--password", default="cloudredirect")
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()

    root = Path(args.root)
    root.mkdir(parents=True, exist_ok=True)
    server = LocalWebDavServer((args.host, args.port), WebDavHandler, root, args.username, args.password, args.verbose)
    print(f"CloudRedirect local WebDAV listening on http://{args.host}:{args.port}/dav/", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
