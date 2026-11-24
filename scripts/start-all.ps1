# Starts the idx MCP server and both Python agent services, each in its own window,
# waits until they answer their health checks, and optionally starts the web app.
#
#   .\scripts\start-all.ps1                 # MCP + LangGraph (8101) + ADK (8102)
#   .\scripts\start-all.ps1 -Provider adk   # ...and the web app with Agent:Provider=adk
#
# Close a service by closing its window.

[CmdletBinding()]
param(
    [ValidateSet('', 'fake', 'maf', 'langgraph', 'adk')]
    [string]$Provider = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($env:IDX_MCP_TOKEN)) {
    $env:IDX_MCP_TOKEN = [Environment]::GetEnvironmentVariable('IDX_MCP_TOKEN', 'User')
}
if ([string]::IsNullOrWhiteSpace($env:IDX_MCP_TOKEN)) {
    Write-Error 'IDX_MCP_TOKEN belum diisi di environment User. Lihat scripts/start-mcp.ps1.'
    exit 1
}

$services = @(
    @{ Name = 'MCP idx';   Script = 'scripts\start-mcp.ps1'; Health = 'http://127.0.0.1:8765/healthz'; Env = $null },
    @{ Name = 'LangGraph'; Script = 'agents\langgraph\run.ps1'; Health = 'http://127.0.0.1:8101/health'; Env = 'agents\langgraph\.env' },
    @{ Name = 'ADK';       Script = 'agents\adk\run.ps1'; Health = 'http://127.0.0.1:8102/health'; Env = 'agents\adk\.env' }
)

function Test-Health([string]$url) {
    try { return (Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 } catch { return $false }
}

foreach ($s in $services) {
    if (Test-Health $s.Health) {
        $s.Status = 'sudah berjalan'
        continue
    }
    if ($s.Env -and -not (Test-Path (Join-Path $root $s.Env))) {
        $s.Status = "dilewati: $($s.Env) belum ada"
        continue
    }
    # The child window inherits IDX_MCP_TOKEN from this process.
    Start-Process powershell -ArgumentList '-NoExit', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root $s.Script) `
        -WorkingDirectory $root | Out-Null
    $s.Status = 'dimulai'
}

$deadline = (Get-Date).AddSeconds(90)
foreach ($s in $services | Where-Object { $_.Status -eq 'dimulai' }) {
    while (-not (Test-Health $s.Health) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    $s.Status = if (Test-Health $s.Health) { 'siap' } else { 'belum siap, lihat jendelanya' }
}

$services | ForEach-Object { [pscustomobject]@{ Service = $_.Name; Status = $_.Status; Health = $_.Health } } |
    Format-Table -AutoSize

if ($Provider) {
    $launchProfile = if ($Provider -eq 'fake') { 'http' } else { $Provider }
    Start-Process powershell -ArgumentList '-NoExit', '-Command',
        "dotnet run --project `"$(Join-Path $root 'app\Surveillance.Web')`" --launch-profile $launchProfile" -WorkingDirectory $root | Out-Null
    Write-Host "Aplikasi web dimulai dengan provider '$Provider': http://localhost:5272"
}
