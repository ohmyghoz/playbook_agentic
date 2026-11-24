# Starts the LangGraph agent service on http://127.0.0.1:8101 (contracts/agent-service-api.md).
# Needs agents/langgraph/.env (see .env.example) and IDX_MCP_TOKEN in the environment.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Test-Path '.env')) {
    Write-Error '.env belum ada. Salin .env.example menjadi .env lalu isi OPENROUTER_API_KEY.'
    exit 1
}

if ([string]::IsNullOrWhiteSpace($env:IDX_MCP_TOKEN)) {
    $env:IDX_MCP_TOKEN = [Environment]::GetEnvironmentVariable('IDX_MCP_TOKEN', 'User')
}

& .\.venv\Scripts\python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8101
exit $LASTEXITCODE
