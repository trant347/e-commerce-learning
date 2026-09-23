param(
    [switch]$ConfirmReset
)

$ErrorActionPreference = "Stop"

if (-not $ConfirmReset) {
    throw "This deletes all products database data. Re-run with -ConfirmReset."
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repositoryRoot

$runningMongo = docker compose ps --status running --services mongo
if ($LASTEXITCODE -ne 0 -or $runningMongo -notcontains "mongo") {
    throw "The Docker Compose mongo service is not running."
}

docker compose stop product-service
if ($LASTEXITCODE -ne 0) {
    throw "Failed to stop product-service before resetting its database."
}

$resetScriptCopied = $false
try {
    docker compose cp mongo-init/products-reset.js mongo:/tmp/products-reset.js
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to copy the products reset script into the mongo container."
    }
    $resetScriptCopied = $true

    docker compose exec -T mongo sh -lc 'mongosh --quiet --username "$MONGO_INITDB_ROOT_USERNAME" --password "$MONGO_INITDB_ROOT_PASSWORD" --authenticationDatabase admin /tmp/products-reset.js'
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to reset the products database."
    }
} finally {
    if ($resetScriptCopied) {
        docker compose exec -T mongo rm -f /tmp/products-reset.js
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "The temporary products reset script could not be removed from the mongo container."
        }
    }
}

$runningRedis = docker compose ps --status running --services redis
if ($LASTEXITCODE -eq 0 -and $runningRedis -contains "redis") {
    docker compose exec -T redis sh -lc 'redis-cli --scan --pattern "products:*" | xargs -r redis-cli del >/dev/null'
    if ($LASTEXITCODE -ne 0) {
        throw "Products database was reset, but stale product cache entries could not be removed."
    }
}

Write-Host "Products database reset. Rebuild/restart product-service to load canonical categories and TaskMaster seed data."
