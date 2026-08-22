# Relay image.
#
# Built from the repository root so the shared project is in context:
#
#   docker build -f deploy/Dockerfile -t gamelauncher-relay .
#
# The image serves plain HTTP on 8080 and is meant to sit behind a proxy that
# terminates TLS. It does not publish that port itself; see compose.yml.

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Project files first, so restore is cached and only re-runs when a dependency
# actually changes rather than on every source edit.
COPY GameLauncher.Shared/GameLauncher.Shared.csproj GameLauncher.Shared/
COPY GameLauncher.Relay/GameLauncher.Relay.csproj  GameLauncher.Relay/

# Directory.Build.props carries ImplicitUsings, Nullable and LangVersion for
# every project in the solution. Without it the container compiles against
# different language settings than any developer machine does, and the failure
# names types rather than the cause: DateTimeOffset, Task and IReadOnlyList all
# "could not be found", because the implicit usings that import them were never
# switched on.
COPY global.json Directory.Build.props ./

RUN dotnet restore GameLauncher.Relay/GameLauncher.Relay.csproj

COPY GameLauncher.Shared/ GameLauncher.Shared/
COPY GameLauncher.Relay/  GameLauncher.Relay/

RUN dotnet publish GameLauncher.Relay/GameLauncher.Relay.csproj \
        -c Release \
        -o /app \
        --no-restore \
        /p:UseAppHost=false

# ---------------------------------------------------------------------------
# Runtime
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# curl is here for the health check and nothing else. The alternative is a
# health check that cannot tell "starting" from "broken".
RUN apt-get update \
 && apt-get install --no-install-recommends -y curl \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./

# The database lives on a volume, not in the image layer. Without this the
# relay would lose every account each time the image was rebuilt.
RUN mkdir -p /data && chown -R app:app /data
VOLUME ["/data"]

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Relay__Database__Provider=Sqlite \
    Relay__Database__ConnectionString="Data Source=/data/gamelauncher-relay.db"

EXPOSE 8080

# Runs as the non-root user the base image already provides. A relay that is
# reachable from the internet has no business running as root.
USER app

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail --silent http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "GameLauncher.Relay.dll"]
