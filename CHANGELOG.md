# Changelog

## 1.4.7

- Added production GitHub Actions release workflow.
- Publishes Docker images to GHCR using lowercase owner/repository references.
- Generates valid update `version.json` with required `version` and registry `image` fields.
- Creates release ZIP and GitHub Release automatically.
- Adds optional GoDaddy SFTP upload support.
- Adds documentation for repository secrets, manual workflow runs, and tag-triggered releases.

## 1.4.8
- Fixed displayed collector version after Docker updates.
- Added runtime build metadata from assembly/environment instead of hard-coded SQLite defaults.
- Added `/about` page and `/api/about` JSON endpoint.
- Docker image now receives APP_VERSION, APP_COMMIT, APP_BUILD_DATE, and APP_IMAGE build metadata from GitHub Actions.
