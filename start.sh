#!/usr/bin/env sh
set -eu

# Start clamd first (so the socket exists for notifications)
clamd &

# Wait for clamd socket (up to 60s)
i=0
while [ $i -lt 60 ]; do
  [ -S /tmp/clamd.sock ] && break
  i=$((i+1))
  sleep 1
done

# Best-effort signature update AFTER clamd is up.
# --daemon-notify asks freshclam to tell clamd to reload when it can.
( freshclam --foreground --daemon-notify & ) || true

# Start HTTP API (this satisfies App Service warmup)
exec /venv/bin/uvicorn app:app --host 0.0.0.0 --port 8000