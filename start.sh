#!/usr/bin/env sh
set -eu

# Best-effort signature updates (don’t block startup forever)
( freshclam --foreground & ) || true

# Start clamd
clamd &

# Wait up to ~60s for clamd socket so scans work
i=0
while [ $i -lt 60 ]; do
  [ -S /tmp/clamd.sock ] && break
  i=$((i+1))
  sleep 1
done

# Start HTTP API (this is what makes App Service warmup succeed)
exec uvicorn app:app --host 0.0.0.0 --port 8000