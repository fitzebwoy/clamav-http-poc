import os
import uuid
import time
import socket
import subprocess
import asyncio
from typing import Optional, Dict

from fastapi import FastAPI, UploadFile, File, HTTPException, Request

app = FastAPI()

MAX_BYTES = int(os.getenv("MAX_BYTES", "30000000"))  # 30MB
TMP_DIR = "/tmp"

# Concurrency guard (per instance)
MAX_CONCURRENT_SCANS = int(os.getenv("MAX_CONCURRENT_SCANS", "2"))
scan_sem = asyncio.Semaphore(MAX_CONCURRENT_SCANS)

# Multipart overhead varies; allow a bit of headroom for Content-Length
CONTENT_LENGTH_HEADROOM = int(os.getenv("CONTENT_LENGTH_HEADROOM", str(1024 * 1024)))  # 1MB


def _clamd_ping(host: str = "127.0.0.1", port: int = 3310, timeout_s: float = 1.0) -> bool:
    """Lightweight readiness check: connect to clamd TCP and PING."""
    try:
        with socket.create_connection((host, port), timeout=timeout_s) as s:
            s.sendall(b"PING\n")
            resp = s.recv(16)
            return b"PONG" in resp
    except Exception:
        return False


def _parse_clamdscan_output(raw: str) -> Dict[str, Optional[str]]:
    """
    clamdscan output typically looks like:
      /tmp/<file>: OK
      /tmp/<file>: Eicar-Test-Signature FOUND
      /tmp/<file>: <something> ERROR
    """
    # Default
    signature = None

    # Split on first colon
    parts = raw.split(":", 1)
    tail = parts[1].strip() if len(parts) == 2 else raw.strip()

    if tail.endswith("FOUND"):
        signature = tail[:-len("FOUND")].strip()
    return {"detail": tail, "signature": signature}


import time

def clamd_scan(path: str) -> dict:
    scan_start = time.monotonic()

    p = subprocess.run(
        ["clamdscan", "--fdpass", "--no-summary", path],
        capture_output=True,
        text=True,
    )

    scan_duration_ms = int((time.monotonic() - scan_start) * 1000)

    out = (p.stdout or "").strip()
    err = (p.stderr or "").strip()

    if p.returncode == 0:
        return {
            "status": "clean",
            "engine": "clamav",
            "engine_detail": out,
            "scan_duration_ms": scan_duration_ms
        }

    if p.returncode == 1:
        return {
            "status": "infected",
            "engine": "clamav",
            "engine_detail": out,
            "scan_duration_ms": scan_duration_ms
        }

    return {
        "status": "error",
        "engine": "clamav",
        "engine_detail": out,
        "stderr": err,
        "scan_duration_ms": scan_duration_ms
    }

@app.get("/health")
def health():
    # Liveness: API is up
    return {"ok": True}

@app.get("/ready")
def ready():
    # Readiness: clamd is reachable
    if not _clamd_ping():
        raise HTTPException(status_code=503, detail="clamd not ready")
    return {"ready": True}

@app.get("/")
def root():
    return {"service": "clamav-http-poc", "liveness": "/health", "readiness": "/ready", "scan": "/scan"}


@app.post("/scan")
async def scan(request: Request, file: UploadFile = File(...)):
    if not _clamd_ping():
        raise HTTPException(status_code=503, detail="clamd not ready")
    # Early reject on request size, if Content-Length is present.
    # Note: multipart adds overhead, so we allow a bit of headroom.
    cl = request.headers.get("content-length")
    if cl:
        try:
            if int(cl) > (MAX_BYTES + CONTENT_LENGTH_HEADROOM):
                raise HTTPException(status_code=413, detail="Request too large")
        except ValueError:
            # Ignore invalid header; we'll still enforce MAX_BYTES while streaming
            pass

    # Immediate 503 if scanner is busy
    try:
        await asyncio.wait_for(scan_sem.acquire(), timeout=0.1)
    except asyncio.TimeoutError:
        raise HTTPException(status_code=503, detail="Scanner busy, retry later")

    target = os.path.join(TMP_DIR, f"{uuid.uuid4()}_{file.filename}")
    total = 0
    start = time.monotonic()

    try:
        # Stream upload to disk with hard cap
        with open(target, "wb") as f:
            while True:
                chunk = await file.read(1024 * 1024)  # 1MB
                if not chunk:
                    break
                total += len(chunk)
                if total > MAX_BYTES:
                    raise HTTPException(status_code=413, detail="File exceeds MAX_BYTES")
                f.write(chunk)

        # Run blocking scan off the event loop thread
        scan_result = await asyncio.to_thread(clamd_scan, target)

        duration_ms = int((time.monotonic() - start) * 1000)

        # Cleaner response JSON
        response = {
            "result": scan_result.get("result"),          # clean | infected | error
            "file_name": file.filename,
            "bytes_scanned": total,
            "signature": scan_result.get("signature"),    # e.g. Eicar-Test-Signature
            "engine": "clamav",
            "engine_detail": scan_result.get("engine_detail"),  # e.g. OK / "<sig> FOUND"
            "scan_duration_ms": scan_result.get["scan_duration_ms"]
        }

        # Include stderr only on error
        if scan_result.get("result") == "error":
            response["stderr"] = scan_result.get("stderr")

        return response

    finally:
        scan_sem.release()
        try:
            if os.path.exists(target):
                os.remove(target)
        except Exception:
            pass