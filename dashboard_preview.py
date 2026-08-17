#!/usr/bin/env python3
"""Local preview server for the ESP light-sensor dashboard."""

from __future__ import annotations

import argparse
import json
import math
import threading
import time
import urllib.parse
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


HERE = Path(__file__).resolve().parent
DASHBOARD_SOURCE = HERE / "DashboardPage.h"
MIN_ISO = 100
MAX_ISO = 25600
SUPPORTED_ISOS = {100, 200, 400, 800, 1600, 3200, 6400, 12800, 25600}
MAX_BANDS = 8


def load_dashboard() -> bytes:
    source = DASHBOARD_SOURCE.read_text(encoding="utf-8")
    start_marker = 'R"DASHBOARD(\n'
    end_marker = "\n)DASHBOARD"
    start = source.index(start_marker) + len(start_marker)
    end = source.index(end_marker, start)
    return source[start:end].encode("utf-8")


class DemoState:
    def __init__(self) -> None:
        self.lock = threading.Lock()
        self.started_at = time.monotonic()
        self.bands = [
            {"max": 25, "iso": 3200},
            {"max": 50, "iso": 1600},
            {"max": 75, "iso": 800},
            {"max": 100, "iso": 400},
        ]

    def snapshot(self) -> dict[str, object]:
        with self.lock:
            elapsed = time.monotonic() - self.started_at
            light = round((math.sin(elapsed / 3.2) + 1) * 50)
            current_iso = next(
                band["iso"] for band in self.bands if light <= band["max"]
            )
            return {
                "ok": True,
                "light": light,
                "rawLight": round(light / 100 * 4095),
                "currentIso": current_iso,
                "bandCount": len(self.bands),
                "bands": [dict(band) for band in self.bands],
                "apActive": True,
                "apSsid": "JvdP-LightSensor",
                "apIp": "192.168.9.1",
                "uptimeMs": round(elapsed * 1000),
                "demo": True,
            }

    def apply(self, values: dict[str, list[str]]) -> None:
        try:
            count = int(values["bandCount"][-1])
            bounds = [int(value) for value in values["bounds"][-1].split(",")]
            isos = [int(value) for value in values["isos"][-1].split(",")]
        except (KeyError, ValueError, IndexError) as error:
            raise ValueError("Incomplete ISO mapping") from error

        if not 1 <= count <= MAX_BANDS or len(bounds) != count or len(isos) != count:
            raise ValueError("Invalid range count")
        if bounds[-1] != 100:
            raise ValueError("Ranges must end at 100")

        previous = -1
        for bound, iso in zip(bounds, isos, strict=True):
            if bound <= previous or not 0 <= bound <= 100:
                raise ValueError("Ranges must be strictly increasing")
            if iso not in SUPPORTED_ISOS:
                raise ValueError("ISO value is outside the supported range")
            previous = bound

        with self.lock:
            self.bands = [
                {"max": bound, "iso": iso}
                for bound, iso in zip(bounds, isos, strict=True)
            ]


DASHBOARD_HTML = load_dashboard()
STATE = DemoState()


class PreviewHandler(BaseHTTPRequestHandler):
    server_version = "JvdP-LightSensor-Preview/1.0"

    def do_GET(self) -> None:
        path = urllib.parse.urlsplit(self.path).path
        if path == "/":
            self.send_bytes(200, "text/html; charset=utf-8", DASHBOARD_HTML)
        elif path == "/api/state":
            self.send_json(200, STATE.snapshot())
        elif path == "/api/health":
            self.send_json(200, {"ok": True, "demo": True})
        elif path == "/ping":
            self.send_bytes(200, "text/plain; charset=utf-8", b"ok")
        elif path == "/favicon.ico":
            self.send_bytes(204, "image/x-icon", b"")
        else:
            self.send_json(404, {"ok": False, "error": "Not found"})

    def do_POST(self) -> None:
        path = urllib.parse.urlsplit(self.path).path
        if path != "/api/action":
            self.send_json(404, {"ok": False, "error": "Not found"})
            return

        try:
            length = int(self.headers.get("Content-Length", "0"))
            body = self.rfile.read(length).decode("utf-8")
            STATE.apply(urllib.parse.parse_qs(body, keep_blank_values=True))
            self.send_json(
                200,
                {
                    "ok": True,
                    "message": "ISO mapping saved",
                    "state": STATE.snapshot(),
                },
            )
        except ValueError as error:
            self.send_json(400, {"ok": False, "error": str(error)})

    def send_json(self, status: int, payload: dict[str, object]) -> None:
        body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
        self.send_bytes(status, "application/json; charset=utf-8", body)

    def send_bytes(self, status: int, content_type: str, body: bytes) -> None:
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store, no-cache, must-revalidate")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.end_headers()
        if body:
            self.wfile.write(body)

    def log_message(self, _format: str, *_args: object) -> None:
        return


def main() -> None:
    parser = argparse.ArgumentParser(description="Preview the light-sensor dashboard.")
    parser.add_argument("--port", type=int, default=8766)
    parser.add_argument("--no-browser", action="store_true")
    args = parser.parse_args()

    try:
        server = ThreadingHTTPServer(("127.0.0.1", args.port), PreviewHandler)
    except OSError:
        server = ThreadingHTTPServer(("127.0.0.1", 0), PreviewHandler)

    url = f"http://127.0.0.1:{server.server_address[1]}/"
    print()
    print("Light Sensor dashboard preview is running.")
    print(f"Open: {url}")
    print("Close this window to stop the preview.")
    print()

    if not args.no_browser:
        threading.Timer(0.4, webbrowser.open, args=(url,)).start()

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
