# KiComLib Server

Backend and web UI for the [KiComLib](https://github.com/heronitec-solutions/kicomlib) KiCad library sync system. Stores symbols, footprints, 3D models, datasheets and templates with **content-addressed blobs**, **linear commit history**, and **multiple 3D models per footprint**.

Repos:

| Path | Role |
|------|------|
| `backend/` | ASP.NET 10 Web API (`KiComLibBackend`) |
| `backend.Tests/` | xUnit integration tests (Postgres) |
| `frontend/` | Angular 22 admin UI |
| `docs/` | Code for Github Pages |
| `deployment/` | Docker Compose examples |

## Requirements

- .NET SDK 10 (project targets `net10.0`)
- PostgreSQL 14+ (tested with 16/18)
- Node 22.22+ for the frontend (24.15+ and 26 also work)
- Docker (optional) for DB / packaged deploy

## Environment variables

| Variable | Default / notes |
|----------|-----------------|
| `DB_HOST`, `DB_PORT`, `DB_NAME`, `DB_USER`, `DB_PASSWORD` | Postgres connection |
| `DB_MAINTENANCE_NAME` | `postgres` – used when creating the DB |
| `DB_LOCALE` | `en_US.UTF-8` |
| `DB_OWNER` | Role owning new objects (often same as `DB_USER`) |
| `DB_AUTO_INIT` | `true` unless set to `false` – create an empty database and apply schema version 1 on startup |
| `DB_STARTUP_TIMEOUT_SECONDS` | `120` – wait this long when PostgreSQL is still starting before schema initialization fails. `0` tries once |
| `BLOB_MAX_SIZE_MB` | `200` – max body size for `PUT /api/blob/{hash}` |
| `BACKUP_MAX_MB` | `2048` – max size of an encrypted backup upload. Kestrel allows the larger of this and `BLOB_MAX_SIZE_MB`. Keep `client_max_body_size` in `deployment/proxy/nginx.conf` at the same limit. |
| `BACKUP_RESTART_AFTER_RESTORE` | unset – the process exits after an encrypted restore so it reloads the key ring. Set `false` to keep it running; the restored keys are then not used until the next start. |

Put local overrides in `backend/.env` (gitignored). DotEnv does **not** overwrite variables already set in the process environment.

## Running locally

### Database

```shell
docker run -d --name kicomlib_db -e POSTGRES_USER=kicad -e POSTGRES_PASSWORD=test123 -e POSTGRES_DB=kicaddbl -p 15432:5432 postgres:18
```

### Backend

```shell
cd backend
$env:DB_HOST='localhost'; $env:DB_PORT='15432'; $env:DB_NAME='kicaddbl'
$env:DB_USER='kicad'; $env:DB_PASSWORD='test123'
dotnet run --project KiComLibBackend.csproj --urls http://localhost:5229
```

### Frontend

```shell
cd frontend
npm install
npx ng serve
# production build:
npx ng build --configuration production
```


### Runner

Catalog browsing works without the runner. Analysis, schematic and board images, the visual diff, and release outputs need it. Build the image once, then start it next to `dotnet run`:

```shell
docker build -t kicomlib-kicad-runner:latest -f runner/Dockerfile runner
docker run -d --name kicomlib_runner -p 8080:8080 -e RUNNER_TOKEN=<long-random-token> kicomlib-kicad-runner:latest
```

Point the backend at that container before `dotnet run` (the Compose default `http://kicad-runner:8080` only resolves inside Compose). Use the same token on both sides. Compose reads `KICOMLIB_DB_PASSWORD` and `KICAD_RUNNER_TOKEN` from `deployment/.env` (see `deployment/.env.example`).

```shell
$env:KICAD_RUNNER_URL='http://localhost:8080'
$env:KICAD_RUNNER_TOKEN='<long-random-token>'
```

`GET http://localhost:8080/health` returns the runner and KiCad versions. The token must match on both sides. Inside Compose the runner has no published port and no internet access; see `deployment/compose.yml`.

### Tests

```shell
# Backend. Default connection: Host=localhost;Port=15432;Database=kicaddbl_test;Username=kicad;Password=test123
# Override the whole connection string with TEST_DB_CONNECTION.
dotnet test backend.Tests/KiComLibBackend.Tests.csproj

# Frontend build check
cd frontend
npx ng build --configuration production
```

## Running local Docker system

Build to the local Docker registry and run from localhost:

```shell
# build to local Docker registry
./build_local_registry.sh
# go to deployment dir and optionally change the passwords
cd deployment
cp .env.example .env
# start local Docker containers
docker compose -f compose_local.yml up -d
```

## API v2 overview

- `GET /api/info` – product version, HTTP library `v1`, sync API version, projects API version, schema version, and head commit. Requires read permission.
- `PUT/GET /api/blob/{hash}`, `POST /api/blob/exists`
- `POST /api/commit` – atomic create/update/rename/delete with `expectedHash` → 409 on conflict
- `GET /api/commit`, `/api/commit/{id}`, `/api/commit/{id}/diff` (array of wrapped diffs)
- `GET /api/{entity}/{id}/history`, `/revision/{n}`, `/diff` (flat diff object)
- `GET /api/sync/changes?sinceCommitId=`
- Lists: `/api/symbol`, `/api/footprint` (includes `models[]`), `/api/model3d`, …

Full request examples: [`backend/KiComLibBackend.http`](backend/KiComLibBackend.http).

## Planned part review workflow

The [KiComLib review implementation plan](docs/kicomlib-review-implementation-plan.md)
defines the proposed part revision fields, retained review definitions, approval
workflow, and API for a later KiBot release checker. The plan is documentation;
these review controls and release checks are not implemented yet.

## Git projects

KiComLib mirrors KiCad projects from Git read-only and compares them with the catalog. GitLab.com, a self-hosted GitLab, and GitHub.com are supported. Any other Git server is polled only. Admins add hosts, credentials, and repositories under **Git Integration**. Projects are chosen from discovered `.kicad_pro` files; nothing is created automatically.

The HTTP library writes a hidden `KiComLibUUID` field. Matching then prefers that UUID, then the symbol name when the library nickname is listed in `kicomlib_library_nicknames`, then a unique manufacturer part number.

Schematic and board rendering, the visual diff, and release outputs (PDF, Gerber, drill, position, BOM, ERC/DRC, STEP) run in the separate `kicad-runner` container. KiCad 10.0.6 is pinned in `runner/Dockerfile`.

### Provider setup

| Provider | Read access | Webhook |
|----------|-------------|---------|
| GitLab.com and self-hosted GitLab | Deploy token (`read_repository`) or a project/group access token with the Reporter role, or a read-only deploy key | `X-Gitlab-Token`. Self-hosted GitLab may need “Allow requests to the local network from webhooks and integrations”. Set the SSH host on the host entry. The port defaults to 22 (`ssh://git@gitlab.example.com:22`); put a different port in that host when SSH is not on 22. Add a PEM CA certificate if required. |
| GitHub.com | Fine-grained PAT with Contents: Read-only on the selected repositories, or a read-only deploy key. Each repository needs its own key. | `X-Hub-Signature-256`, content type `application/json`. |
| Other Git servers | HTTPS user and token, or an SSH key | None. Use poll mode. |

KiComLib shows the webhook URL and the secret once, after the repository is created or the secret is rotated. The URL is `PUBLIC_BASE_URL` plus `/api/hooks/git/{repository-uuid}`. GitLab.com and GitHub.com can deliver hooks only when that path is reachable from the internet. Otherwise leave the repository in poll mode.

Confirm the SSH host key once (`ssh-keyscan` in the host dialog). After that, SSH uses `StrictHostKeyChecking=yes`.

### Project environment

| Variable | Default | Purpose |
|----------|---------|---------|
| `PUBLIC_BASE_URL` | — | External base URL used in webhook instructions |
| `DATA_PROTECTION_KEYS_DIR` | `Data/Keys` | Live Data Protection key ring. An encrypted backup includes this directory, so a restore on another machine can decrypt stored secrets. |
| `GIT_REPOS_DIR` | `Data/Repos` | Bare mirrors. Not part of the backup; they are cloned again. |
| `PROJECT_CACHE_DIR` | `Data/ProjectCache` | Rendered pages and diff layers. Not part of the backup; they are rendered again. |
| `PROJECT_CACHE_MAX_MB` | `5120` | Cache cleanup limit |
| `PROJECT_ARCHIVE_MAX_MB` | `200` | Largest project archive sent to the runner |
| `PROJECT_WORKER_ENABLED` | `true` | Set `false` or `0` to disable the job worker and polling |
| `PROJECT_WORKER_PARALLEL` | `2` | Concurrent jobs, capped at 32 |
| `KICAD_RUNNER_URL` | `http://kicad-runner:8080` | Runner |
| `KICAD_RUNNER_TOKEN` | — | Shared secret. Required once the runner is used. Must match `RUNNER_TOKEN`. |
| `CORS_ORIGINS` | empty | Comma-separated browser origins allowed to call the API. Empty means same-origin only. Set `http://localhost:4200` for `ng serve`. |
| `TRUSTED_PROXIES` | empty | Comma-separated proxy IPs allowed to set `X-Forwarded-For` and `X-Forwarded-Proto`. Empty means those headers are ignored. `compose_ssl.yml` sets `172.28.10.10`. |
| `GIT_ALLOW_FILE_PROTOCOL` | `false` | Tests only (`file://` remotes) |

Runner: `RUNNER_TOKEN`, `RUNNER_MAX_PARALLEL` (2; Compose sets 4), `RUNNER_JOB_TIMEOUT_SECONDS` (600), `RUNNER_MAX_UPLOAD_MB` (200), `RUNNER_WORK_DIR` (`/work`), `RUNNER_HOST` (`0.0.0.0`), `RUNNER_PORT` (`8080`).

Poll intervals, the commit limit, the initial tag count, and the library nicknames are stored in **Settings** (`git_poll_interval_minutes` 5, `git_webhook_poll_interval_minutes` 60, `project_max_commits_per_sync` 20, `project_initial_tags` 50, `kicomlib_library_nicknames` `KiComLib`).

## Docker Deployment

See `deployment/compose.yml` and the root `Dockerfile` (Angular build + .NET publish). Copy `deployment/.env.example` to `deployment/.env` and set `KICOMLIB_DB_PASSWORD` and `KICAD_RUNNER_TOKEN` before the first start. The Compose file starts `kicad-runner` on an internal network with no internet access and passes that same token to both services.

The default login for a fresh install is `admin` / `kicomlib`. You have to change it after the first login.

### HTTP

```shell
cd deployment
# copy .env file an change passwords
cp .env.example .env
# create Docker containers
docker compose up -d
```

### HTTPS

The compose file for HTTPS support adds an NGINX reverse proxy in front of the KiComLib system as HTTPS endpoint.

```shell
cd deployment
# copy .env file an change passwords
cp .env.example .env
# create Docker containers
docker compose -f compose_ssl.yml up -d
```

The `./keys` volume is the live Data Protection key ring. **Backups** in the admin UI writes an encrypted `.kibkp` archive of the database and that key ring. The passphrase is chosen at creation, entered again on restore, and is not stored. Git mirrors, the render cache, the database password and the runner token stay on the host. After a restore the process exits so it can load the restored keys. Compose starts it again (`restart: unless-stopped`). `./repos` and `./cache` can be deleted; KiComLib rebuilds them.

## Version

See `version.txt`.
