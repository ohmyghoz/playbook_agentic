# Starts the Google ADK agent service on http://127.0.0.1:8102 (contracts/agent-service-api.md).
# Needs agents/adk/.env with GOOGLE_API_KEY and MODEL_NAME, and IDX_MCP_TOKEN in the environment.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Test-Path '.env')) {
    Write-Error '.env belum ada. Buat agents/adk/.env berisi GOOGLE_API_KEY dan MODEL_NAME.'
    exit 1
}

if ([string]::IsNullOrWhiteSpace($env:IDX_MCP_TOKEN)) {
    $env:IDX_MCP_TOKEN = [Environment]::GetEnvironmentVariable('IDX_MCP_TOKEN', 'User')
}

& .\.venv\Scripts\python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8102
exit $LASTEXITCODE
