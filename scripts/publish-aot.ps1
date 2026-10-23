# Native AOT 게시: .\scripts\publish-aot.ps1 [-Runtime win-x64]
# 결과물: .\publish\<runtime>\GeminiMcp.exe (단일 네이티브 실행 파일)
param([string]$Runtime = "win-x64")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

# ILCompiler 링크 단계가 vswhere.exe 를 PATH 에서 찾으므로 VS Installer 경로를 추가합니다.
$installer = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
if ((Test-Path $installer) -and -not ($env:PATH -split ';' -contains $installer)) {
    $env:PATH = "$env:PATH;$installer"
}

$out = Join-Path $root "publish\$Runtime"
dotnet publish (Join-Path $root "src\GeminiMcp\GeminiMcp.csproj") -c Release -r $Runtime -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Get-ChildItem $out -Filter "GeminiMcp*" | Format-Table Name, @{ n = "Size(MB)"; e = { [math]::Round($_.Length / 1MB, 2) } }
