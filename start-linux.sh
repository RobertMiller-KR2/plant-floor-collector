#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
docker compose -f docker-compose.updatable.yml up -d --build
echo "Plant Floor Collector is starting at http://localhost:8080"
