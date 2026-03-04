FROM clamav/clamav:stable

RUN apt-get update && apt-get install -y --no-install-recommends \
    python3 python3-pip \
  && rm -rf /var/lib/apt/lists/*

RUN pip3 install --no-cache-dir fastapi uvicorn

WORKDIR /app
COPY app.py /app/app.py
COPY start.sh /app/start.sh
RUN chmod +x /app/start.sh

EXPOSE 8000
CMD ["/app/start.sh"]