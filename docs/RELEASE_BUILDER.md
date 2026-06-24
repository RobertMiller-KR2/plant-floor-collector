# Plant Floor Collector Release Builder

The release builder creates the Docker image, release ZIP, and `version.json` update manifest used by the in-app updater.

## One-time GHCR login

```powershell
docker login ghcr.io
```

Use a GitHub Personal Access Token with package write permission.

## Build and push a release from Windows

```powershell
.\tools\release\build-release.ps1 `
  -Version 1.4.6 `
  -Registry ghcr.io/robertmiller-kr2 `
  -ManifestBaseUrl https://yourdomain.com/plant-floor-collector `
  -ReleaseBaseUrl https://yourdomain.com/plant-floor-collector/releases `
  -Push
```

This creates:

- `releases/plant_floor_collector_v1_4_6_docker.zip`
- `releases/version.json`
- Docker image `ghcr.io/robertmiller-kr2/plant-floor-collector:1.4.6`
- Docker image `ghcr.io/robertmiller-kr2/plant-floor-collector:latest`

## Upload to GoDaddy

Upload these files:

```text
public_html/plant-floor-collector/version.json
public_html/plant-floor-collector/releases/plant_floor_collector_v1_4_6_docker.zip
```

You can use the included SFTP helper:

```powershell
.\tools\release\publish-godaddy-sftp.ps1 `
  -HostName yourdomain.com `
  -UserName your_godaddy_user `
  -RemoteRoot /public_html/plant-floor-collector `
  -Version 1.4.6
```

## Required manifest fields

The collector updater requires a lowercase Docker image reference and a `version` field:

```json
{
  "version": "1.4.6",
  "image": "ghcr.io/robertmiller-kr2/plant-floor-collector:1.4.6"
}
```

## GitHub Actions

The included workflow `.github/workflows/docker-release.yml` can build and push the Docker image to GitHub Container Registry, then attach the release ZIP and manifest to a GitHub release.
