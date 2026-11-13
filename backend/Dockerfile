# syntax=docker/dockerfile:1.7
# Backora backend — one multi-stage Dockerfile, two runtime targets:
#   docker build --target api      -t backora-api      backend/   (API + durable background execution)
#   docker build --target migrator -t backora-migrator backend/   (one-shot schema migration job)
# There is no separate worker image: backup/restore commands run on the configured execution host over SSH.
# All images run as non-root UID 1001 with group 0 (OpenShift arbitrary-UID compatible) and contain no secrets.

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-alpine AS build
WORKDIR /src
COPY Directory.Build.props Backora.sln ./
COPY src/Backora.Domain/Backora.Domain.csproj src/Backora.Domain/
COPY src/Backora.Contracts/Backora.Contracts.csproj src/Backora.Contracts/
COPY src/Backora.Application/Backora.Application.csproj src/Backora.Application/
COPY src/Backora.Infrastructure/Backora.Infrastructure.csproj src/Backora.Infrastructure/
COPY src/Backora.Api/Backora.Api.csproj src/Backora.Api/
COPY src/Backora.Tools/Backora.Tools.csproj src/Backora.Tools/
RUN dotnet restore src/Backora.Api/Backora.Api.csproj \
 && dotnet restore src/Backora.Tools/Backora.Tools.csproj
COPY src/ src/
# Built-in backup templates are embedded in Backora.Infrastructure (see BuiltInTemplates).
COPY database/templates/ database/templates/
RUN dotnet publish src/Backora.Api/Backora.Api.csproj -c Release -o /out/api --no-restore /p:UseAppHost=false \
 && dotnet publish src/Backora.Tools/Backora.Tools.csproj -c Release -o /out/tools --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-alpine AS base
# ICU + tzdata: culture-aware formatting and IANA time zones for cron schedules.
RUN apk add --no-cache icu-libs tzdata \
 && mkdir -p /app /data/keys \
 && chgrp -R 0 /data && chmod -R g=u /data
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DataProtection__KeysPath=/data/keys \
    TMPDIR=/tmp
WORKDIR /app
USER 1001
EXPOSE 8080

FROM base AS api
COPY --from=build --chown=1001:0 /out/api .
# Graceful drain: in-flight execution steps finish and leases are released (remote processes keep running on the execution host).
STOPSIGNAL SIGTERM
HEALTHCHECK --interval=30s --timeout=3s --start-period=20s --retries=3 CMD wget -q -O /dev/null http://127.0.0.1:8080/health/ready || exit 1
ENTRYPOINT ["dotnet", "Backora.Api.dll"]

FROM base AS migrator
COPY --from=build --chown=1001:0 /out/tools .
ENTRYPOINT ["dotnet", "Backora.Tools.dll"]
CMD ["migrate"]
