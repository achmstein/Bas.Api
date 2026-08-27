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
COPY src/Contracts/Contracts.csproj             Contracts/
COPY src/ServiceDefaults/ServiceDefaults.csproj ServiceDefaults/
COPY src/Api/Api.csproj                         Api/
RUN dotnet restore Api/Api.csproj

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

# Non-root. There is no persistent state in this container - the database holds everything, and the
# signing key lives there encrypted - so nothing needs write access to the filesystem.
USER $APP_UID

ENTRYPOINT ["dotnet", "Bas.Api.dll"]
