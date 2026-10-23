# Gemini MCP - Docker self-hosting helper (Windows PowerShell 5.1+ / PowerShell 7)
#
#   .\docker\docker.ps1 setup [-Bind 127.0.0.1] [-Port 5100] [-Domain "localhost, 127.0.0.1"]
#                                   [-TlsCert fullchain.pem -TlsKey privkey.pem] [-BundledS3] [-Force]
#                                   -TlsCert/-TlsKey: use certificate files prepared by the server admin instead of MCP_TLS
#                                   -BundledS3: also run the bundled S3 container (docker-compose.s3.yml) for generated media.
#                                   Without it, set S3_* in .env for an external S3 (see .env.example).
#   .\docker\docker.ps1 up          # build + start (runs setup first if .env is missing), waits until healthy
#   .\docker\docker.ps1 down        # stop and remove the containers
#   .\docker\docker.ps1 restart
#   .\docker\docker.ps1 rebuild     # rebuild the images without cache and restart
#   .\docker\docker.ps1 logs        # follow logs
#   .\docker\docker.ps1 status      # container state, health and connection info
#   .\docker\docker.ps1 cert        # export the Caddy local CA root certificate (MCP_TLS=internal) to docker\caddy-root.crt
#   .\docker\docker.ps1 reset-admin # forgot the dashboard password: delete the admin account; a new setup code is logged
param(
    [Parameter(Position = 0)][ValidateSet("setup", "up", "down", "restart", "rebuild", "logs", "status", "cert", "reset-admin")][string]$Command = "up",
    [string]$Bind,
    [int]$Port,
    [string]$Domain,
    [string]$TlsCert,
    [string]$TlsKey,
    [switch]$BundledS3,
    [switch]$Force
)

# Not "Stop": Windows PowerShell 5.1 turns docker's progress output on stderr into terminating errors.
# Native command failures are checked through $LASTEXITCODE instead.
$ErrorActionPreference = "Continue"
$root = $PSScriptRoot  # docker/ folder: compose file, .env, .env.example
$envFile = Join-Path $root ".env"
$healthChecked = @("gemini-mcp", "gemini-mcp-etcd", "gemini-mcp-apisix", "gemini-mcp-dashboard")
$dashboard = "gemini-mcp-dashboard"
$proxy = "gemini-mcp-caddy"
$caFile = Join-Path $root "caddy-root.crt"

function Read-DotEnv {
    $values = @{}
    if (Test-Path $envFile) {
        foreach ($line in [IO.File]::ReadAllLines($envFile)) {
            if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$') { $values[$Matches[1]] = $Matches[2].Trim().Trim('"') }
        }
    }
    $values
}

function Test-Loopback([string]$address) { (-not $address) -or $address -in @("127.0.0.1", "localhost", "::1", "[::1]") }

function New-Secret {
    $bytes = New-Object byte[] 16
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    -join ($bytes | ForEach-Object { $_.ToString("x2") })
}

# The bundled S3 moved from the "s3" compose profile to docker-compose.s3.yml: .env files that enabled the profile
# get COMPOSE_FILE instead, so the same store keeps running.
function Update-MediaStoreSettings {
    if (-not (Test-Path $envFile)) { return }
    $cfg = Read-DotEnv
    $profiles = @(($cfg["COMPOSE_PROFILES"] -split ",") | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($profiles -notcontains "s3" -or $cfg["COMPOSE_FILE"]) { return }
    $lines = [Collections.Generic.List[string]]::new([IO.File]::ReadAllLines($envFile))
    $index = $lines.FindIndex({ param($l) $l -match '^\s*COMPOSE_PROFILES\s*=' })
    $lines[$index] = "COMPOSE_PROFILES=" + (@($profiles | Where-Object { $_ -ne "s3" }) -join ",")
    $lines.InsertRange($index + 1, [string[]]@("COMPOSE_PATH_SEPARATOR=:", "COMPOSE_FILE=docker-compose.yml:docker-compose.s3.yml"))
    [IO.File]::WriteAllLines($envFile, $lines, (New-Object Text.UTF8Encoding $false))
    Write-Host "Updated $envFile : the bundled S3 now comes from docker-compose.s3.yml (COMPOSE_FILE)."
}

function Invoke-Setup {
    if ((Test-Path $envFile) -and -not $Force) {
        Write-Host ".env already exists (use -Force to regenerate): $envFile"
        return
    }
    $bindAddress = if ($Bind) { $Bind } else { "127.0.0.1" }
    $portNumber = if ($Port) { $Port } else { 5100 }
    $domainNames = if ($Domain) { $Domain } else { "localhost, 127.0.0.1" }
    if ([bool]$TlsCert -ne [bool]$TlsKey) { throw "-TlsCert and -TlsKey must be given together." }
    $certFile = if ($TlsCert) { (Resolve-Path $TlsCert -ErrorAction Stop).Path } else { "" }
    $keyFile = if ($TlsKey) { (Resolve-Path $TlsKey -ErrorAction Stop).Path } else { "" }

    $template = [IO.File]::ReadAllText((Join-Path $root ".env.example"))
    $content = $template `
        -replace '(?m)^MCP_BIND=.*$', "MCP_BIND=$bindAddress" `
        -replace '(?m)^MCP_PORT=.*$', "MCP_PORT=$portNumber" `
        -replace '(?m)^MCP_DOMAIN=.*$', "MCP_DOMAIN=$domainNames" `
        -replace '(?m)^MCP_TLS_CERT=.*$', "MCP_TLS_CERT=$certFile" `
        -replace '(?m)^MCP_TLS_KEY=.*$', "MCP_TLS_KEY=$keyFile"
    if ($BundledS3) {
        $content = $content `
            -replace '(?m)^#(COMPOSE_PATH_SEPARATOR|COMPOSE_FILE)=', '$1=' `
            -replace '(?m)^S3_SECRET_KEY=.*$', "S3_SECRET_KEY=$(New-Secret)"
    }
    [IO.File]::WriteAllText($envFile, $content, (New-Object Text.UTF8Encoding $false))

    Write-Host "Created $envFile"
    Write-Host "  listen        : https://${bindAddress}:$portNumber  (TLS names: $domainNames)"
    if ($certFile) { Write-Host "  certificate   : $certFile (key: $keyFile)" }
    if ($BundledS3) {
        Write-Host "  media store   : bundled S3 (docker-compose.s3.yml), bucket gemini-media"
    } else {
        Write-Host "  media store   : none - set S3_* in .env for image/speech/video tools (or rerun setup -Force -BundledS3)"
    }
    if (-not (Test-Loopback $bindAddress) -and -not $Domain) {
        Write-Host "  note          : add this host's name/IP to MCP_DOMAIN in .env (or pass -Domain) so remote clients get a matching certificate"
    }
}

function Assert-Docker {
    docker info --format "{{.ServerVersion}}" *> $null
    if ($LASTEXITCODE -ne 0) { throw "Docker engine is not running. Start Docker Desktop and retry." }
}

# docker compose validates ${GEMINI_API_KEY:?} for every command, so commands that do not start the
# server (down, logs) get a placeholder when no key is available.
function Set-ApiKey($cfg, [switch]$Optional) {
    if ($cfg["GEMINI_API_KEY"]) { return }
    if (-not $env:GEMINI_API_KEY) { $env:GEMINI_API_KEY = [Environment]::GetEnvironmentVariable("GEMINI_API_KEY", "User") }
    if ($env:GEMINI_API_KEY) { return }
    if ($Optional) { $env:GEMINI_API_KEY = "unused" } else { throw "GEMINI_API_KEY is not set (set it in the environment or in .env)." }
}

function Wait-Healthy {
    foreach ($name in $healthChecked) {
        Write-Host -NoNewline "Waiting for $name"
        $state = ""
        for ($i = 0; $i -lt 90; $i++) {
            $state = docker inspect -f "{{.State.Health.Status}}" $name 2>$null
            if ($state -eq "healthy" -or $state -eq "unhealthy") { break }
            Write-Host -NoNewline "."
            Start-Sleep -Seconds 1
        }
        if ($state -ne "healthy") {
            Write-Host ""
            docker logs --tail 50 $name
            throw "$name did not become healthy (state: $state)."
        }
        Write-Host " -> healthy"
    }
}

function Wait-Proxy {
    for ($i = 0; $i -lt 30; $i++) {
        $state = docker inspect -f "{{.State.Status}}" $proxy 2>$null
        if ($state -eq "running") { return }
        Start-Sleep -Seconds 1
    }
    docker logs --tail 50 $proxy
    throw "Caddy proxy is not running (state: $state)."
}

# MCP_TLS=internal: clients must trust Caddy's local CA. It lives in the caddy-data volume, so it survives restarts.
function Export-Cert {
    docker cp "${proxy}:/data/caddy/pki/authorities/local/root.crt" $caFile *> $null
    if ($LASTEXITCODE -ne 0) { throw "Could not export the root certificate (is the proxy running with MCP_TLS=internal?)." }
    Write-Host "Exported Caddy root CA: $caFile"
    Write-Host "Trust it for Claude Code (Node.js) : `$env:NODE_EXTRA_CA_CERTS = `"$caFile`"  (or set it as a user environment variable)"
    Write-Host "Trust it in the Windows user store : certutil -user -addstore Root `"$caFile`""
}

# Certificate files prepared by the server admin (MCP_TLS_CERT/MCP_TLS_KEY). Checked before compose so a missing
# file is reported here instead of Docker creating an empty directory in its place.
function Assert-TlsFiles {
    $certFile = Get-Setting "MCP_TLS_CERT" ""
    $keyFile = Get-Setting "MCP_TLS_KEY" ""
    if (-not $certFile -and -not $keyFile) { return }
    if (-not $certFile -or -not $keyFile) { throw "Set both MCP_TLS_CERT and MCP_TLS_KEY in .env (or neither)." }
    Push-Location $root  # relative paths resolve against docker/, like docker compose
    try {
        foreach ($file in @($certFile, $keyFile)) {
            if (-not (Test-Path $file -PathType Leaf)) { throw "TLS certificate file not found: $file" }
        }
    } finally { Pop-Location }
}

# Same precedence as docker compose: shell environment first, then .env, then default.
function Get-Setting([string]$name, [string]$default) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if (-not $value) { $value = (Read-DotEnv)[$name] }
    if ($value) { $value } else { $default }
}

# The dashboard prints a one-time setup code while no admin exists (no shell in the image, so ask it with deno eval).
function Show-SetupCode {
    $state = docker exec $dashboard deno eval "const r = await fetch('http://127.0.0.1:3000/api/admin/auth/state'); console.log((await r.json()).setupRequired)" 2>$null
    if ($state -ne "true") { return }
    $code = docker logs $dashboard 2>&1 | Select-String -Pattern "Dashboard setup code: (\S+)" | Select-Object -Last 1
    if ($code) {
        Write-Host "Setup code   : $($code.Matches[0].Groups[1].Value)  (create the dashboard admin password with it)"
    }
}

function Show-Connection {
    $bindAddress = Get-Setting "MCP_BIND" "127.0.0.1"
    $host_ = if ($bindAddress -eq "0.0.0.0") { "<this-host>" } else { $bindAddress }
    $portNumber = Get-Setting "MCP_PORT" "5100"
    $base = "https://${host_}:$portNumber"
    Write-Host ""
    Write-Host "Dashboard    : $base/   (logs, monitoring, API keys)"
    Show-SetupCode
    Write-Host "MCP endpoint : $base/mcp   (Authorization: Bearer <API key issued in the dashboard>)"
    Write-Host "Skill        : $base/skill"
    $certFile = Get-Setting "MCP_TLS_CERT" ""
    $keyFile = Get-Setting "MCP_TLS_KEY" ""
    if ($certFile) {
        Write-Host "TLS          : certificate files $certFile, $keyFile  (restart after renewing them)"
    } elseif ((Get-Setting "MCP_TLS" "internal") -eq "internal") {
        Write-Host "TLS          : Caddy local CA (run '.\docker\docker.ps1 cert' to export the root certificate for clients)"
    }
    Write-Host "Register in Claude Code:"
    Write-Host "  claude mcp add --transport http gemini $base/mcp --header `"Authorization: Bearer <API key>`""
}

function Invoke-Compose([string[]]$composeArgs) {
    Push-Location $root
    try {
        & docker compose @composeArgs
        if ($LASTEXITCODE -ne 0) { throw "docker compose $($composeArgs -join ' ') failed ($LASTEXITCODE)" }
    } finally { Pop-Location }
}

function Invoke-Up([string[]]$buildArgs) {
    Assert-Docker
    if (-not (Test-Path $envFile)) { Invoke-Setup }
    Update-MediaStoreSettings
    Set-ApiKey (Read-DotEnv)
    Assert-TlsFiles
    if ($buildArgs) { Invoke-Compose (@("build") + $buildArgs) }
    Invoke-Compose @("up", "-d", "--build")
    Wait-Healthy
    Wait-Proxy
    Show-Connection
}

function Invoke-ResetAdmin {
    Assert-Docker
    docker exec $dashboard deno eval "await Deno.remove('/data/admin.json').catch(() => {}); console.log('Dashboard admin account deleted.')"
    if ($LASTEXITCODE -ne 0) { throw "reset-admin failed (is $dashboard running?)" }
    Show-SetupCode
}

switch ($Command) {
    "setup"       { Invoke-Setup }
    "up"          { Invoke-Up @() }
    "rebuild"     { Invoke-Up @("--no-cache", "--pull") }
    "down"        { Assert-Docker; Update-MediaStoreSettings; Set-ApiKey (Read-DotEnv) -Optional; Invoke-Compose @("down") }
    "restart"     { Assert-Docker; Update-MediaStoreSettings; Set-ApiKey (Read-DotEnv); Assert-TlsFiles; Invoke-Compose @("restart"); Wait-Healthy; Wait-Proxy; Show-Connection }
    "cert"        { Assert-Docker; Export-Cert }
    "logs"        { Assert-Docker; Update-MediaStoreSettings; Set-ApiKey (Read-DotEnv) -Optional; Invoke-Compose @("logs", "-f", "--tail", "200") }
    "reset-admin" { Invoke-ResetAdmin }
    "status"      {
        Assert-Docker
        docker ps -a --filter "name=^gemini-mcp" --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}\t{{.Image}}"
        Show-Connection
    }
}
