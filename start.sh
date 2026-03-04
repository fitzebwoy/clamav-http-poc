#!/usr/bin/env sh 
set -eu

( freshclam --foreground & ) || true
clamd &

# wait for clamd socket (up to 60s)
i=0
while [ $i -lt 60 ]; do
  [ -S /tmp/clamd.sock ] && break
  i=$((i+1))
  sleep 1
done

exec /venv/bin/uvicorn app:app --host 0.0.0.0 --port 8000