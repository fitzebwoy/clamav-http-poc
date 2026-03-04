#!/usr/bin/env sh
set -eu

echo "[startup] starting clamd..."
clamd &

echo "[startup] waiting for clamd socket /tmp/clamd.sock ..."
i=0
while [ $i -lt 60 ]; do
  if [ -S /tmp/clamd.sock ]; then
    echo "[startup] clamd ready (socket present)."
    break
  fi
  i=$((i+1))
  sleep 1
done

# If clamd never became ready, fail fast so the platform restarts the container.
if [ ! -S /tmp/clamd.sock ]; then
  echo "[startup] ERROR: clamd did not become ready within 60s; exiting."
  exit 1
fi

echo "[startup] starting freshclam in background (daemon-notify)..."
( freshclam --foreground --daemon-notify & ) || true

echo "[startup] starting HTTP API on :8000 ..."
exec /venv/bin/uvicorn app:app --host 0.0.0.0 --port 8000