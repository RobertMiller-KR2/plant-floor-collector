# Plant Floor Collector

Cross-platform collector for Plant Floor Monitor for Odoo.

## Docker run

```powershell
docker compose -f docker-compose.updatable.yml up -d --build
```

Open:

```text
http://localhost:8080
```

## GitHub release pipeline

This package includes a GitHub Actions release workflow.

Read:

```text
docs/GITHUB_RELEASE_PIPELINE.md
```

The workflow builds the .NET app, publishes Docker images to GHCR, creates a GitHub Release, and generates the `version.json` manifest used by the in-app updater.

## Version Display Fix

Version 1.4.8 adds runtime build metadata. The collector now shows the actual running version from Docker/assembly metadata instead of a hard-coded SQLite setting.

Verify after an update:

```text
http://localhost:8080/about
http://localhost:8080/api/about
```

For local Docker builds, copy `.env.example` to `.env` and set `APP_VERSION` before running `docker compose up -d --build`.
