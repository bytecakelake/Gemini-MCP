# Gemini MCP - Docker self-hosting helper (Windows PowerShell 5.1+ / PowerShell 7)
#
#   .\docker\docker.ps1 setup [-ShareDir C:\Users\me] [-Bind 127.0.0.1] [-Port 5100] [-WithToken] [-Force]
#   .\docker\docker.ps1 up        # build + start (runs setup first if .env is missing), waits until healthy
#   .\docker\docker.ps1 down      # stop and remove the container
#   .\docker\docker.ps1 restart
#   .\docker\docker.ps1 rebuild   # rebuild the image without cache and restart
#   .\docker\docker.ps1 logs      # follow logs
#   .\docker\docker.ps1 status    # container state, health and connection info
param(
    [Parameter(Position = 0)][ValidateSet("setup", "up", "down", "restart", "rebuild", "logs", "status")][string]$Command = "up",
    [string]$ShareDir,
    [string]$Bind,
    [int]$Port,
    [switch]$WithToken,
    [switch]$Force
)

# Not "Stop": Windows PowerShell 5.1 turns docker's progress output on stderr into terminating errors.
# Native command failures are checked through $LASTEXITCODE instead.
$ErrorActionPreference = "Continue"
$root = $PSScriptRoot  # docker/ folder: compose file, .env, .env.example
$envFile = Join-Path $root ".env"
$container = "gemini-mcp"

function Read-DotEnv {
    $values = @{}
    if (Test-Path $envFile) {
        foreach ($line in [IO.File]::ReadAllLines($envFile)) {
            if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$') { $values[$Matches[1]] = $Matches[2].Trim().Trim('"') }
        }
    }
    $values
}

function New-Token {
    $bytes = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    -join ($bytes | ForEach-Object { $_.ToString("x2") })
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
    $token = if ($WithToken -or -not (Test-Loopback $bindAddress)) { New-Token } else { "" }

    $template = [IO.File]::ReadAllText((Join-Path $root ".env.example"))
    $content = $template `
        -replace '(?m)^HOST_SHARE_DIR=.*$', "HOST_SHARE_DIR=$share" `
        -replace '(?m)^MCP_BIND=.*$', "MCP_BIND=$bindAddress" `
        -replace '(?m)^MCP_PORT=.*$', "MCP_PORT=$portNumber" `
        -replace '(?m)^MCP_AUTH_TOKEN=.*$', "MCP_AUTH_TOKEN=$token"
    [IO.File]::WriteAllText($envFile, $content, (New-Object Text.UTF8Encoding $false))

    Write-Host "Created $envFile"
    Write-Host "  shared folder : $share  ->  /share (outputs: $share\gemini-mcp-output)"
    Write-Host "  listen        : ${bindAddress}:$portNumber"
    if ($token) { Write-Host "  auth token    : generated (MCP_AUTH_TOKEN in .env)" }
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
    Write-Host -NoNewline "Waiting for container health"
    for ($i = 0; $i -lt 60; $i++) {
        $state = docker inspect -f "{{.State.Health.Status}}" $container 2>$null
        if ($state -eq "healthy") { Write-Host " -> healthy"; return }
        if ($state -eq "unhealthy") { break }
        Write-Host -NoNewline "."
        Start-Sleep -Seconds 1
    }
    Write-Host ""
    docker logs --tail 50 $container
    throw "Container did not become healthy (state: $state)."
}

# Same precedence as docker compose: shell environment first, then .env, then default.
function Get-Setting([string]$name, [string]$default) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if (-not $value) { $value = (Read-DotEnv)[$name] }
    if ($value) { $value } else { $default }
}

function Show-Connection {
    $bindAddress = Get-Setting "MCP_BIND" "127.0.0.1"
    $host_ = if ($bindAddress -eq "0.0.0.0") { "<this-host>" } else { $bindAddress }
    $portNumber = Get-Setting "MCP_PORT" "5100"
    $url = "http://${host_}:$portNumber/mcp"
    Write-Host ""
    Write-Host "MCP endpoint : $url"
    Write-Host "Skill        : http://${host_}:$portNumber/skill"
    Write-Host "Register in Claude Code:"
    if (Get-Setting "MCP_AUTH_TOKEN" "") {
        Write-Host "  claude mcp add --transport http gemini $url --header `"Authorization: Bearer <MCP_AUTH_TOKEN from .env>`""
    } else {
        Write-Host "  claude mcp add --transport http gemini $url"
    }
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
    $bindAddress = Get-Setting "MCP_BIND" ""
    if (-not (Test-Loopback $bindAddress) -and -not (Get-Setting "MCP_AUTH_TOKEN" "")) {
        throw "MCP_BIND=$bindAddress exposes the server beyond this machine; set MCP_AUTH_TOKEN in .env first."
    }
    Set-ApiKey (Read-DotEnv)
    if ($buildArgs) { Invoke-Compose (@("build") + $buildArgs) }
    Invoke-Compose @("up", "-d", "--build")
    Wait-Healthy
    Show-Connection
}

switch ($Command) {
    "setup"   { Invoke-Setup }
    "up"      { Invoke-Up @() }
    "rebuild" { Invoke-Up @("--no-cache", "--pull") }
    "down"    { Assert-Docker; Set-ApiKey (Read-DotEnv) -Optional; Invoke-Compose @("down") }
    "restart" { Assert-Docker; Set-ApiKey (Read-DotEnv); Invoke-Compose @("restart"); Wait-Healthy }
    "logs"    { Assert-Docker; Set-ApiKey (Read-DotEnv) -Optional; Invoke-Compose @("logs", "-f", "--tail", "200") }
    "status"  {
        Assert-Docker
        docker ps -a --filter "name=^$container$" --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}\t{{.Image}}"
        Show-Connection
    }
}
