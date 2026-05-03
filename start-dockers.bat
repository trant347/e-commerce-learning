@echo off
setlocal EnableDelayedExpansion

where docker >nul 2>&1
if errorlevel 1 (
	echo Docker CLI is not installed or not in PATH.
	exit /b 1
)

docker info >nul 2>&1
if errorlevel 1 (
	echo Docker is not running. Starting Docker Desktop...

	if exist "%ProgramFiles%\Docker\Docker\Docker Desktop.exe" (
		start "" "%ProgramFiles%\Docker\Docker\Docker Desktop.exe"
	) else if exist "%LocalAppData%\Docker\Docker Desktop.exe" (
		start "" "%LocalAppData%\Docker\Docker Desktop.exe"
	) else (
		echo Docker Desktop executable was not found.
		exit /b 1
	)

	set /a retries=0
	:wait_for_docker
	docker info >nul 2>&1
	if not errorlevel 1 goto docker_ready

	set /a retries+=1
	if !retries! geq 90 (
		echo Timed out waiting for Docker to start.
		exit /b 1
	)

	timeout /t 2 /nobreak >nul
	goto wait_for_docker
)

:docker_ready
echo Docker is ready.
docker compose up -d --build

endlocal