# Changelog

## 1.4.7

- Added production GitHub Actions release workflow.
- Publishes Docker images to GHCR using lowercase owner/repository references.
- Generates valid update `version.json` with required `version` and registry `image` fields.
- Creates release ZIP and GitHub Release automatically.
- Adds optional GoDaddy SFTP upload support.
- Adds documentation for repository secrets, manual workflow runs, and tag-triggered releases.
