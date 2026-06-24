#!/usr/bin/env bash
set -euo pipefail

VERSION=""
REGISTRY="ghcr.io/robertmiller-kr2"
IMAGE_NAME="plant-floor-collector"
MANIFEST_BASE_URL="https://yourdomain.com/plant-floor-collector"
RELEASE_BASE_URL="https://yourdomain.com/plant-floor-collector/releases"
PUSH="false"
NO_DOCKER_BUILD="false"
OUTPUT_DIR="releases"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="$2"; shift 2 ;;
    --registry) REGISTRY="$2"; shift 2 ;;
    --image-name) IMAGE_NAME="$2"; shift 2 ;;
    --manifest-base-url) MANIFEST_BASE_URL="$2"; shift 2 ;;
    --release-base-url) RELEASE_BASE_URL="$2"; shift 2 ;;
    --push) PUSH="true"; shift ;;
    --no-docker-build) NO_DOCKER_BUILD="true"; shift ;;
    --output-dir) OUTPUT_DIR="$2"; shift 2 ;;
    *) echo "Unknown argument: $1"; exit 1 ;;
  esac
done

if [[ -z "$VERSION" ]]; then
  echo "Usage: $0 --version 1.4.6 [--registry ghcr.io/owner] [--push]"
  exit 1
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
mkdir -p "$OUTPUT_DIR"

IMAGE_REF="$(echo "$REGISTRY/$IMAGE_NAME:$VERSION" | tr '[:upper:]' '[:lower:]')"
LATEST_REF="$(echo "$REGISTRY/$IMAGE_NAME:latest" | tr '[:upper:]' '[:lower:]')"
ZIP_NAME="plant_floor_collector_v${VERSION//./_}_docker.zip"
ZIP_PATH="$OUTPUT_DIR/$ZIP_NAME"
MANIFEST_PATH="$OUTPUT_DIR/version.json"

echo "==> Building .NET project"
dotnet restore PlantFloorCollector.sln
dotnet build PlantFloorCollector.sln -c Release --no-restore

if [[ "$NO_DOCKER_BUILD" != "true" ]]; then
  echo "==> Building Docker image $IMAGE_REF"
  docker build -t "$IMAGE_REF" -t "$LATEST_REF" .
  if [[ "$PUSH" == "true" ]]; then
    echo "==> Pushing Docker image"
    docker push "$IMAGE_REF"
    docker push "$LATEST_REF"
  else
    echo "Skipping docker push. Add --push after logging in to your registry."
  fi
fi

echo "==> Generating update manifest"
cat > "$MANIFEST_PATH" <<JSON
{
  "product": "Plant Floor Collector",
  "version": "$VERSION",
  "image": "$IMAGE_REF",
  "latestImage": "$LATEST_REF",
  "releaseDate": "$(date +%F)",
  "downloadUrl": "$RELEASE_BASE_URL/$ZIP_NAME",
  "notes": [
    "Automated release package",
    "Docker image: $IMAGE_REF",
    "Persistent Docker volumes are preserved during update"
  ]
}
JSON
cp "$MANIFEST_PATH" version.json

echo "==> Creating release ZIP"
rm -f "$ZIP_PATH"
zip -rq "$ZIP_PATH" . -x "./.git/*" "*/bin/*" "*/obj/*" "./$OUTPUT_DIR/*"

echo "==> Release complete"
echo "Image:    $IMAGE_REF"
echo "ZIP:      $ZIP_PATH"
echo "Manifest: $MANIFEST_PATH"
echo "Upload $MANIFEST_PATH to: $MANIFEST_BASE_URL/version.json"
echo "Upload $ZIP_PATH to: $RELEASE_BASE_URL/$ZIP_NAME"
