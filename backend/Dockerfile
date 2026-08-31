# ── Build ─────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching.
COPY Unswarm.slnx ./
COPY src/Unswarm.Api/Unswarm.Api.csproj src/Unswarm.Api/
COPY src/Unswarm.Core/Unswarm.Core.csproj src/Unswarm.Core/
RUN dotnet restore src/Unswarm.Api/Unswarm.Api.csproj

# Copy full source and publish.
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

RUN mkdir -p /data/.config/unswarm \
    && chown -R app:app /data

USER app
EXPOSE 5014

ENTRYPOINT ["dotnet", "Unswarm.Api.dll"]
