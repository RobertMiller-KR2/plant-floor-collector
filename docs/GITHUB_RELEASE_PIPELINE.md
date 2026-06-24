# GitHub Release Pipeline

This project includes a GitHub Actions workflow that builds and publishes the Plant Floor Collector release artifacts.

## What the workflow does

When you run the workflow, it will:

1. Build the .NET 8 collector.
2. Build a Docker image.
3. Push the image to GitHub Container Registry.
4. Tag the image with the release version and `latest`.
5. Generate `releases/version.json` for the in-app updater.
6. Create a Docker/source release ZIP.
7. Publish a GitHub Release.
8. Optionally upload the manifest and ZIP to GoDaddy by SFTP.

## One-time GitHub setup

Create a GitHub repository and push this project to it.

The workflow uses the built-in `GITHUB_TOKEN` for GitHub Releases and GHCR package publishing. In the repository settings, verify:

- **Settings → Actions → General → Workflow permissions** is set to **Read and write permissions**.
- **Settings → Actions → General → Allow GitHub Actions to create and approve pull requests** can remain disabled.

## Optional GoDaddy SFTP secrets

Add these repository secrets only if you want GitHub to upload the update manifest and release ZIP to GoDaddy:

| Secret | Example |
|---|---|
| `GODADDY_SFTP_HOST` | `yourdomain.com` |
| `GODADDY_SFTP_USER` | your hosting username |
| `GODADDY_SFTP_PASSWORD` | your hosting password |
| `GODADDY_SFTP_PORT` | `22` |
| `GODADDY_REMOTE_PATH` | `/public_html/plant-floor-collector` |

The workflow uploads the files in `releases/` to that remote folder.

Recommended GoDaddy layout:

```text
public_html/
└── plant-floor-collector/
    ├── version.json
    └── plant_floor_collector_v1_4_7_docker.zip
```

## Running a release manually

In GitHub:

1. Go to **Actions**.
2. Select **Plant Floor Collector Release**.
3. Click **Run workflow**.
4. Enter:
   - `version`: `1.4.7`
   - `manifest_base_url`: `https://yourdomain.com/plant-floor-collector`
   - `release_base_url`: `https://yourdomain.com/plant-floor-collector`
   - `upload_to_godaddy`: `true` or `false`
5. Click **Run workflow**.

## Triggering by Git tag

You can also create and push a tag:

```powershell
git tag collector-v1.4.7
git push origin collector-v1.4.7
```

For tag-triggered builds, the workflow uses default placeholder URLs unless you edit the defaults in `.github/workflows/release.yml`.

## Collector update manifest

The workflow generates a valid manifest like this:

```json
{
  "product": "Plant Floor Collector",
  "version": "1.4.7",
  "image": "ghcr.io/robertmiller-kr2/plant-floor-collector:1.4.7",
  "releaseDate": "2026-06-24",
  "downloadUrl": "https://yourdomain.com/plant-floor-collector/plant_floor_collector_v1_4_7_docker.zip",
  "notesUrl": "https://github.com/your-org/your-repo/releases/tag/collector-v1.4.7",
  "notes": [
    "Docker image published to GHCR",
    "Release ZIP and update manifest generated automatically"
  ]
}
```

Set the collector update manifest URL to:

```text
https://yourdomain.com/plant-floor-collector/version.json
```

## First Docker update requirement

The update image must be a real registry image, for example:

```text
ghcr.io/robertmiller-kr2/plant-floor-collector:1.4.7
```

Do not use local-only names such as:

```text
plant-floor-collector:1.4.7
```

Docker will try to pull local-only names from Docker Hub and fail.
