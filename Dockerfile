# Stage 1: Build Angular app

FROM node:22 AS angular-build-env
WORKDIR /usr/local/app
# Copy Angular project files
COPY frontend/ /usr/local/app/
# Install all the dependencies
RUN npm install
# Generate the build of the application
RUN npm run publish



# Stage 2: Build .NET backend

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build-env
WORKDIR /app
# Product version (repo root). The project file publishes it next to the app.
COPY version.txt ./version.txt
# Copy backend source code
COPY backend/ ./
# Restore and publish the web project only. The solution also references
# backend.Tests via a parent-relative path, which is not part of this image.
RUN dotnet restore KiComLibBackend.csproj
# Build and publish the release
RUN dotnet publish KiComLibBackend.csproj -c Release -o out



# Stage 3: Create the final runtime image

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG BUILD_REVISION=unknown
ENV BUILD_REVISION=$BUILD_REVISION
LABEL org.opencontainers.image.revision=$BUILD_REVISION
WORKDIR /app
# Backups call pg_dump/pg_restore in this container. Ubuntu 24.04 ships the
# PostgreSQL 16 client, which cannot read a PostgreSQL 18 custom-format dump.
# Keep this major version aligned with deployment/compose.yml (postgres:18).
# The base image uses http://archive.ubuntu.com. A caching proxy in front of the
# build can return a package body whose SHA256 does not match the archive
# while size and Last-Modified stay the same, and apt then aborts. HTTPS keeps
# that cache off the package path; the apt settings retry a bad fetch.
RUN sed -i \
        -e 's|http://archive.ubuntu.com|https://archive.ubuntu.com|g' \
        -e 's|http://security.ubuntu.com|https://security.ubuntu.com|g' \
        -e 's|http://ports.ubuntu.com|https://ports.ubuntu.com|g' \
        /etc/apt/sources.list.d/ubuntu.sources \
    && printf '%s\n' \
        'Acquire::Retries "5";' \
        'Acquire::http::Pipeline-Depth "0";' \
        'Acquire::http::No-Cache "true";' \
        'Acquire::https::No-Cache "true";' \
        'Acquire::BrokenProxy "true";' \
        > /etc/apt/apt.conf.d/99-no-bad-proxy \
    && apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && install -d /usr/share/postgresql-common/pgdg \
    && curl -fsSL -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc \
        https://www.postgresql.org/media/keys/ACCC4CF8.asc \
    && echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt noble-pgdg main" \
        > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends postgresql-client-18 git git-lfs openssh-client \
    && apt-get purge -y --auto-remove curl \
    && rm -rf /var/lib/apt/lists/*
# Copy the published .NET files
COPY --from=dotnet-build-env /app/out .
# Copy the Angular build output into the wwwroot folder of the backend
COPY --from=angular-build-env /usr/local/app/dist/kicomlib-frontend/browser /app/wwwroot
RUN mkdir -p /app/Data/Repos /app/Data/ProjectCache /app/Data/Keys \
    && chmod 700 /app/Data/Keys
# Runtime configuration (override in compose / orchestrator)
ENV DB_AUTO_INIT=true \
    BLOB_MAX_SIZE_MB=200
# Set the entrypoint for your .NET application
ENTRYPOINT ["dotnet", "KiComLibBackend.dll"]
