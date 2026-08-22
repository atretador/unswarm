# ── Build ─────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Unswarm.slnx ./
COPY src/ ./src/
COPY tests/ ./tests/
RUN dotnet restore Unswarm.slnx

COPY src/ ./src/
RUN dotnet publish src/Unswarm.Api -c Release -o /app --no-restore

# ── Runtime ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app

# Program.cs resolves the SQLite path as $HOME/.config/unswarm/unswarm.db
# (SpecialFolder.UserProfile), so pointing HOME at the mounted volume puts
# the database (and DataProtection keys) on persistent storage.
ENV ASPNETCORE_URLS=http://0.0.0.0:5014 \
    HOME=/data

COPY --from=build /app ./

RUN useradd --uid 1000 --create-home --home-dir /data unswarm \
    && mkdir -p /data/.config/unswarm \
    && chown -R unswarm:unswarm /data

USER unswarm

EXPOSE 5014
