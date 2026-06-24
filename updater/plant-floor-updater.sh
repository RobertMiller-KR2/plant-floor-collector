#!/bin/sh
set -eu

REQUEST_FILE="${REQUEST_FILE:-/data/update-request.json}"
POLL_SECONDS="${POLL_SECONDS:-10}"
COLLECTOR_CONTAINER="${COLLECTOR_CONTAINER:-plant-floor-collector}"
COLLECTOR_PORT="${COLLECTOR_PORT:-8080}"
DATA_VOLUME="${DATA_VOLUME:-pfc_data}"
DATABASE_PATH="${DATABASE_PATH:-/app/data/plant_floor_collector.db}"

log() { echo "[$(date -Iseconds)] $*"; }

extract_json_value() {
  key="$1"
  sed -n "s/.*\"$key\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" "$REQUEST_FILE" | head -n 1
}

log "Plant Floor Updater started. Watching $REQUEST_FILE"
while true; do
  if [ -f "$REQUEST_FILE" ]; then
    TARGET_IMAGE="$(extract_json_value TargetImage)"
    TARGET_VERSION="$(extract_json_value TargetVersion)"
    if [ -z "$TARGET_IMAGE" ]; then
      log "Update request found but TargetImage was blank. Leaving request in place."
      sleep "$POLL_SECONDS"
      continue
    fi

    log "Update requested: image=$TARGET_IMAGE version=$TARGET_VERSION"
    log "Pulling $TARGET_IMAGE"
    docker pull "$TARGET_IMAGE"

    log "Stopping existing container $COLLECTOR_CONTAINER"
    docker rm -f "$COLLECTOR_CONTAINER" >/dev/null 2>&1 || true

    log "Starting $COLLECTOR_CONTAINER from $TARGET_IMAGE"
    docker run -d \
      --name "$COLLECTOR_CONTAINER" \
      --restart unless-stopped \
      -p "$COLLECTOR_PORT:8080" \
      -v "$DATA_VOLUME:/app/data" \
      -e "Collector__DatabasePath=$DATABASE_PATH" \
      "$TARGET_IMAGE"

    mv "$REQUEST_FILE" "$REQUEST_FILE.applied.$(date +%Y%m%d%H%M%S)"
    log "Update applied."
  fi
  sleep "$POLL_SECONDS"
done
