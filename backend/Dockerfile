# ── Build frontend ────────────────────────────────────────────────────────
FROM node:22-alpine AS frontend-build
WORKDIR /app
RUN corepack enable
COPY frontend/package.json frontend/pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile
COPY frontend/ ./
RUN pnpm build

# ── Build backend ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching.
COPY backend/Unswarm.slnx ./
COPY backend/src/Unswarm.Api/Unswarm.Api.csproj src/Unswarm.Api/
COPY backend/src/Unswarm.Core/Unswarm.Core.csproj src/Unswarm.Core/
RUN dotnet restore src/Unswarm.Api/Unswarm.Api.csproj

# Copy full source and publish.
COPY backend/src/ ./src/
RUN dotnet publish src/Unswarm.Api -c Release -o /app --no-restore

# ── Runtime ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:5014 \
    HOME=/data

COPY --from=build /app ./
COPY --from=frontend-build /app/dist ./wwwroot/

RUN mkdir -p /data/.config/unswarm \
    && chown -R app:app /data

USER app
EXPOSE 5014

ENTRYPOINT ["dotnet", "Unswarm.Api.dll"]
