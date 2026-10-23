# Gemini MCP - Docker self-hosting helper (Windows PowerShell 5.1+ / PowerShell 7)
#
#   .\docker\docker.ps1 setup [-ShareDir C:\Users\me] [-Bind 127.0.0.1] [-Port 5100] [-Domain "localhost, 127.0.0.1"] [-Force]
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
    [string]$ShareDir,
    [string]$Bind,
    [int]$Port,
    [string]$Domain,
    [switch]$Force
)

# Not "Stop": Windows PowerShell 5.1 turns docker's progress output on stderr into terminating errors.
# Native command failures are checked through $LASTEXITCODE instead.
$ErrorActionPreference = "Continue"
$root = $PSScriptRoot  # docker/ folder: compose file, .env, .env.example
$envFile = Join-Path $root ".env"
$healthChecked = @("gemini-mcp", "gemini-mcp-dashboard", "gemini-mcp-gateway")
$gateway = "gemini-mcp-gateway"
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

function Invoke-Setup {
    if ((Test-Path $envFile) -and -not $Force) {
        Write-Host ".env already exists (use -Force to regenerate): $envFile"
        return
    }
    $share = if ($ShareDir) { (Resolve-Path $ShareDir -ErrorAction Stop).Path } else { [Environment]::GetFolderPath("UserProfile") }
    $bindAddress = if ($Bind) { $Bind } else { "127.0.0.1" }
    $portNumber = if ($Port) { $Port } else { 5100 }
    $domainNames = if ($Domain) { $Domain } else { "localhost, 127.0.0.1" }

    $template = [IO.File]::ReadAllText((Join-Path $root ".env.example"))
    $content = $template `
        -replace '(?m)^HOST_SHARE_DIR=.*$', "HOST_SHARE_DIR=$share" `
        -replace '(?m)^MCP_BIND=.*$', "MCP_BIND=$bindAddress" `
        -replace '(?m)^MCP_PORT=.*$', "MCP_PORT=$portNumber" `
        -replace '(?m)^MCP_DOMAIN=.*$', "MCP_DOMAIN=$domainNames"
    [IO.File]::WriteAllText($envFile, $content, (New-Object Text.UTF8Encoding $false))

    Write-Host "Created $envFile"
    Write-Host "  shared folder : $share  ->  /share (outputs: $share\gemini-mcp-output)"
    Write-Host "  listen        : https://${bindAddress}:$portNumber  (TLS names: $domainNames)"
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

# Same precedence as docker compose: shell environment first, then .env, then default.
function Get-Setting([string]$name, [string]$default) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if (-not $value) { $value = (Read-DotEnv)[$name] }
    if ($value) { $value } else { $default }
}

# The gateway prints a one-time setup code while no dashboard admin exists (no shell in the image, so ask it with deno eval).
function Show-SetupCode {
    $state = docker exec $gateway deno eval "const r = await fetch('http://127.0.0.1:8080/api/admin/auth/state'); console.log((await r.json()).setupRequired)" 2>$null
    if ($state -ne "true") { return }
    $code = docker logs $gateway 2>&1 | Select-String -Pattern "Dashboard setup code: (\S+)" | Select-Object -Last 1
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
    if ((Get-Setting "MCP_TLS" "internal") -eq "internal") {
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
    Set-ApiKey (Read-DotEnv)
    if ($buildArgs) { Invoke-Compose (@("build") + $buildArgs) }
    Invoke-Compose @("up", "-d", "--build")
    Wait-Healthy
    Wait-Proxy
    Show-Connection
}

function Invoke-ResetAdmin {
    Assert-Docker
    docker exec $gateway deno run --allow-env --allow-read=/data --allow-write=/data main.ts --reset-admin
    if ($LASTEXITCODE -ne 0) { throw "reset-admin failed (is $gateway running?)" }
    Show-SetupCode
}

switch ($Command) {
    "setup"       { Invoke-Setup }
    "up"          { Invoke-Up @() }
    "rebuild"     { Invoke-Up @("--no-cache", "--pull") }
    "down"        { Assert-Docker; Set-ApiKey (Read-DotEnv) -Optional; Invoke-Compose @("down") }
    "restart"     { Assert-Docker; Set-ApiKey (Read-DotEnv); Invoke-Compose @("restart"); Wait-Healthy; Wait-Proxy; Show-Connection }
    "cert"        { Assert-Docker; Export-Cert }
    "logs"        { Assert-Docker; Set-ApiKey (Read-DotEnv) -Optional; Invoke-Compose @("logs", "-f", "--tail", "200") }
    "reset-admin" { Invoke-ResetAdmin }
    "status"      {
        Assert-Docker
        docker ps -a --filter "name=^gemini-mcp" --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}\t{{.Image}}"
        Show-Connection
    }
}
