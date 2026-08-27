# syntax=docker/dockerfile:1.7
# REST service fronting activity-statement lodgement for partner platforms.
#
# Unlike PracticeManager.Api this carries no browser: it talks to Postgres and, from phase 3c, to
# PracticeManager.Api over gRPC. A plain runtime image is all it needs.
#
# Build context is the repository root:
#   docker build -f src/Api/Dockerfile -t bas-api .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Restore against csproj files only, so a source-only change doesn't re-download packages.
COPY nuget.config                               .
COPY src/Contracts/Contracts.csproj             Contracts/
COPY src/ServiceDefaults/ServiceDefaults.csproj ServiceDefaults/
COPY src/Api/Api.csproj                         Api/

# PracticeManager.Api.Contracts comes from GitHub Packages, which needs credentials even for a
# public package. Mounted as a BuildKit secret rather than passed as a build arg: an ARG is
# recorded in the image history, and a token in image history is a token that has leaked.
RUN --mount=type=secret,id=gh_token \
    dotnet nuget update source GitHubPackages \
      --username ci \
      --password "$(cat /run/secrets/gh_token)" \
      --store-password-in-clear-text \
      --configfile nuget.config \
 && dotnet restore Api/Api.csproj

COPY src/Contracts/       Contracts/
COPY src/ServiceDefaults/ ServiceDefaults/
COPY src/Api/             Api/

RUN dotnet publish Api/Api.csproj \
    --configuration $BUILD_CONFIGURATION \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# Non-root. There is no persistent state in this container - the database holds everything,
# including the signing key - so nothing needs write access to the filesystem.
USER $APP_UID

ENTRYPOINT ["dotnet", "Bas.Api.dll"]
