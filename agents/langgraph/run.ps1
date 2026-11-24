# Starts the LangGraph agent service on http://127.0.0.1:8101 (contracts/agent-service-api.md).
# Needs agents/langgraph/.env with OPENROUTER_API_KEY, MODEL_BASE_URL, MODEL_NAME,
# and IDX_MCP_TOKEN in the environment.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Test-Path '.env')) {
    Write-Error '.env belum ada. Buat agents/langgraph/.env berisi OPENROUTER_API_KEY, MODEL_BASE_URL, dan MODEL_NAME.'
    exit 1
}

if ([string]::IsNullOrWhiteSpace($env:IDX_MCP_TOKEN)) {
    $env:IDX_MCP_TOKEN = [Environment]::GetEnvironmentVariable('IDX_MCP_TOKEN', 'User')
}

& .\.venv\Scripts\python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8101
exit $LASTEXITCODE
