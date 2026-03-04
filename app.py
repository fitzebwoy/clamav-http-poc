import os
import uuid
import subprocess
from fastapi import FastAPI, UploadFile, File, HTTPException

app = FastAPI()

MAX_BYTES = int(os.getenv("MAX_BYTES", "30000000"))  # 30MB
TMP_DIR = "/tmp"

def clamd_scan(path: str) -> dict:
    # Exit codes: 0=clean, 1=infected, 2=error
    p = subprocess.run(
        ["clamdscan", "--fdpass", "--no-summary", path],
        capture_output=True,
        text=True,
    )
    out = (p.stdout or "").strip()
    err = (p.stderr or "").strip()

    if p.returncode == 0:
        return {"status": "clean", "raw": out}
    if p.returncode == 1:
        return {"status": "infected", "raw": out}
    return {"status": "error", "raw": out, "stderr": err}

@app.get("/health")
def health():
    return {"ok": True}

@app.post("/scan")
async def scan(file: UploadFile = File(...)):
    target = os.path.join(TMP_DIR, f"{uuid.uuid4()}_{file.filename}")
    total = 0
    try:
        with open(target, "wb") as f:
            while True:
                chunk = await file.read(1024 * 1024)
                if not chunk:
                    break
                total += len(chunk)
                if total > MAX_BYTES:
                    raise HTTPException(status_code=413, detail="File exceeds MAX_BYTES")
                f.write(chunk)

        result = clamd_scan(target)
        result["bytes_scanned"] = total
        return result
    finally:
        try:
            if os.path.exists(target):
                os.remove(target)
        except Exception:
            pass