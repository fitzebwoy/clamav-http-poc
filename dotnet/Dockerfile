FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

COPY ClamAvApi.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o /out

FROM clamav/clamav:stable

RUN apk add --no-cache \
    dotnet8-runtime \
    aspnetcore8-runtime \
    bash

WORKDIR /app

COPY --from=build /out /app/
COPY start.sh /app/start.sh

RUN chmod +x /app/start.sh

EXPOSE 8000

ENV ASPNETCORE_URLS=http://0.0.0.0:8000
ENV MAX_BYTES=30000000

CMD ["sh", "/app/start.sh"]