#!/usr/bin/env bash
# Enforces the database-per-service rule documented in database-refactor.md.
#
# Each service may ONLY reference its own MongoDB host. A service reaching into
# another service's mongo container is a shared-database anti-pattern and is
# rejected here.
#
# How it works:
#   - For each service directory, grep its source for any forbidden mongo host
#     (a host owned by a different service).
#   - Tracking files (this script, database-refactor.md, the session plan) and
#     the docker-compose.yml are excluded — they legitimately mention every host.
#
# Run locally:  bash scripts/check-db-ownership.sh
# Run in CI:    invoked from .github/workflows/test.yml

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

# service-dir : own-mongo-host
declare -A OWN_HOST=(
  ["authorization-service"]="mongo-auth"
  ["product-service"]="mongo-products"
  ["calendar-service"]="mongo-bookings"
  ["notification-service"]="mongo-notifications"
)

ALL_HOSTS=(mongo-auth mongo-products mongo-bookings mongo-notifications)
violations=0

for svc in "${!OWN_HOST[@]}"; do
  own="${OWN_HOST[$svc]}"
  if [[ ! -d "$svc" ]]; then
    echo "skip: $svc (directory not found)"
    continue
  fi
  for host in "${ALL_HOSTS[@]}"; do
    [[ "$host" == "$own" ]] && continue
    matches="$(grep -RIn --exclude-dir=bin --exclude-dir=obj --exclude-dir=target \
                       --exclude-dir=node_modules --exclude='*.csproj.user' \
                       --exclude='*.Backup.tmp' \
                       -e "$host" "$svc" 2>/dev/null || true)"
    if [[ -n "$matches" ]]; then
      echo "VIOLATION: '$svc' references '$host' (owned by another service):"
      echo "$matches" | sed 's/^/  /'
      violations=$((violations + 1))
    fi
  done
done

# Also block the legacy shared 'mongodb' hostname anywhere in service code.
for svc in "${!OWN_HOST[@]}"; do
  [[ ! -d "$svc" ]] && continue
  matches="$(grep -RIn --exclude-dir=bin --exclude-dir=obj --exclude-dir=target \
                     --exclude-dir=node_modules --exclude='*.csproj.user' \
                     --exclude='*.Backup.tmp' \
                     -E '(mongodb://mongodb[:/])|(host:\s*mongodb\b)' "$svc" 2>/dev/null || true)"
  if [[ -n "$matches" ]]; then
    echo "VIOLATION: '$svc' references the legacy shared 'mongodb' host:"
    echo "$matches" | sed 's/^/  /'
    violations=$((violations + 1))
  fi
done

if (( violations > 0 )); then
  echo ""
  echo "Database ownership check FAILED with $violations violation(s)."
  echo "Each service must use only its own mongo host. See database-refactor.md."
  exit 1
fi

echo "Database ownership check passed: each service references only its own mongo host."
