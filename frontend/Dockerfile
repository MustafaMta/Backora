# syntax=docker/dockerfile:1.7
# Backora V2 frontend (Blazor Web App, BFF) — runs beside the current frontend, against the same API.
# Build context MUST be the repository root (the app references backend/src/Backora.Contracts):
#   docker build -f frontend/Dockerfile -t backora-frontend .
ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-alpine AS build
WORKDIR /src
COPY backend/Directory.Build.props backend/
COPY backend/src/Backora.Contracts/Backora.Contracts.csproj backend/src/Backora.Contracts/
COPY frontend/src/Backora.DesignSystem/Backora.DesignSystem.csproj frontend/src/Backora.DesignSystem/
COPY frontend/src/Backora.Components/Backora.Components.csproj frontend/src/Backora.Components/
COPY frontend/src/Backora.Web/Backora.Web.csproj frontend/src/Backora.Web/
RUN dotnet restore frontend/src/Backora.Web/Backora.Web.csproj
COPY backend/src/Backora.Contracts/ backend/src/Backora.Contracts/
# wwwroot first: src/Backora.DesignSystem/wwwroot/{styles,icons,illustrations} are relative symlinks into it.
COPY frontend/wwwroot/ frontend/wwwroot/
COPY frontend/src/ frontend/src/
RUN dotnet publish frontend/src/Backora.Web/Backora.Web.csproj -c Release -o /app /p:UseAppHost=false \
 && test -f /app/wwwroot/_framework/blazor.web.js \
 && test -f /app/wwwroot/_content/Backora.DesignSystem/styles/foundation.css

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-alpine AS runtime
RUN apk add --no-cache icu-libs tzdata \
 && mkdir -p /app /data/keys \
 && chgrp -R 0 /data && chmod -R g=u /data
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DataProtection__KeysPath=/data/keys \
    DOTNET_gcServer=0 \
    TMPDIR=/tmp
WORKDIR /app
COPY --from=build --chown=1001:0 /app .
USER 1001
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=20s --retries=3 \
  CMD wget -q -O /dev/null http://127.0.0.1:8080/health || exit 1
ENTRYPOINT ["dotnet", "Backora.Web.dll"]
