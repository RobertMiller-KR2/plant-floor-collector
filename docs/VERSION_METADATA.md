# Version Metadata

The collector no longer displays a hard-coded version from the local SQLite settings table.

Runtime version is resolved in this order:

1. `APP_VERSION` environment variable from the Docker image.
2. .NET assembly informational version.
3. .NET assembly version.
4. `dev` fallback.

The following runtime endpoints verify the actual running container/build:

- `/about` - human-readable About page
- `/api/about` - JSON metadata

GitHub Actions passes these Docker build args:

- `APP_VERSION`
- `APP_COMMIT`
- `APP_BUILD_DATE`
- `APP_IMAGE`

This prevents successful Docker updates from still showing an old UI version.
