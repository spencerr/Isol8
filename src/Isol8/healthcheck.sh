#!/bin/bash

HEALTHCHECK_FILE="/tmp/healthcheck"
MAX_AGE_SECONDS=60  # Replace with the maximum allowed age in seconds

if [ ! -f "$HEALTHCHECK_FILE" ]; then
  echo "Healthcheck file not found"
  exit 1
fi

LAST_MODIFIED=$(stat -c %Y "$HEALTHCHECK_FILE")
CURRENT_TIME=$(date +%s)
AGE=$((CURRENT_TIME - LAST_MODIFIED))

if [ "$AGE" -gt "$MAX_AGE_SECONDS" ]; then
  echo "Healthcheck file is too old: ${AGE} seconds"
  exit 1
else
  echo "Healthcheck file is healthy: ${AGE} seconds"
  exit 0
fi