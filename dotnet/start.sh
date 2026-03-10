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

if [ ! -S /tmp/clamd.sock ]; then
  echo "[startup] ERROR: clamd did not become ready within 60s; exiting."
  exit 1
fi

echo "[startup] starting freshclam in background..."
( freshclam --foreground --daemon-notify & ) || true

echo "[startup] starting .NET API on :8000 ..."
exec dotnet /app/ClamAvApi.dll