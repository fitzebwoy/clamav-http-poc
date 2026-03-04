FROM clamav/clamav:stable

RUN apk add --no-cache python3 py3-pip

RUN python3 -m venv /venv \
  && /venv/bin/pip install --no-cache-dir --upgrade pip \
  && /venv/bin/pip install --no-cache-dir fastapi uvicorn python-multipart

WORKDIR /app

COPY app.py /app/app.py
COPY start.sh /app/start.sh

RUN chmod +x /app/start.sh

EXPOSE 8000

CMD ["sh", "/app/start.sh"]