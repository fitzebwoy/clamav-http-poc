FROM clamav/clamav:stable

# install python + pip (Alpine uses apk not apt)
RUN apk add --no-cache python3 py3-pip

RUN pip3 install --no-cache-dir fastapi uvicorn

WORKDIR /app

COPY app.py /app/app.py
COPY start.sh /app/start.sh

RUN chmod +x /app/start.sh

EXPOSE 8000

CMD ["/app/start.sh"]