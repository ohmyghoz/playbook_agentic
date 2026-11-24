# Menjalankan server MCP idx lewat HTTP (streamable HTTP) di loopback.
#
# Endpoint : http://127.0.0.1:8765/mcp   (wajib header Authorization: Bearer <token>)
# Health   : http://127.0.0.1:8765/healthz
#
# Token dibaca idx langsung dari environment variable IDX_MCP_TOKEN
# (setara dengan --token). Token tidak diteruskan sebagai argumen supaya
# tidak terlihat di daftar proses.
# Rujukan: https://github.com/fzn0x/idx/blob/main/docs/mcp.md

[CmdletBinding()]
param(
    [string]$Address = '127.0.0.1:8765'
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:IDX_MCP_TOKEN)) {
    Write-Error 'IDX_MCP_TOKEN belum diisi. Contoh: $env:IDX_MCP_TOKEN = -join ((1..32) | ForEach-Object { ''{0:x}'' -f (Get-Random -Max 16) })'
    exit 1
}

if (-not (Get-Command idx -ErrorAction SilentlyContinue)) {
    Write-Error 'Perintah idx tidak ditemukan di PATH. Pasang idx dulu (lihat README repo fzn0x/idx), atau pakai: npx -y ihsg-mcp --http <alamat>'
    exit 1
}

Write-Host "Server MCP idx: http://$Address/mcp (Ctrl+C untuk berhenti)"
& idx mcp --http $Address
exit $LASTEXITCODE
