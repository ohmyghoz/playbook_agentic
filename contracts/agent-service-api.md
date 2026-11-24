# Kontrak HTTP service agent

Kontrak ini berlaku untuk semua service agent Python: LangGraph (port 8101)
dan Google ADK (port 8102). Aplikasi .NET memanggilnya lewat
`LangGraphAgentClient` dan `AdkAgentClient`. Kedua service harus mengikuti
dokumen ini persis, tanpa perbedaan.

Ringkasnya, ada empat endpoint dan satu health check:

| Metode | Path | Fungsi |
|---|---|---|
| `POST` | `/runs` | Mulai run baru, mengembalikan `runId` |
| `GET` | `/runs/{runId}/events` | Aliran event (SSE) dalam bentuk katalog |
| `POST` | `/runs/{runId}/resume` | Kirim keputusan analis untuk approval yang tertunda |
| `POST` | `/runs/{runId}/cancel` | Hentikan run |
| `GET` | `/health` | Cek service hidup |

Bentuk data mengikuti `contracts/events.schema.json`: event mengikuti skema
utama, body request mengikuti `$defs/InvestigationRequest` dan
`$defs/ApprovalDecision`. Perilaku agent mengikuti
`contracts/asd-investigator-uma.md`.

## Aturan umum

- Service hanya mendengarkan di `127.0.0.1`. Tidak ada autentikasi antara aplikasi dan service.
- Semua body memakai JSON UTF-8 dengan nama properti camelCase.
- Nilai enum memakai snake_case, sama seperti di skema, misalnya `approve` atau `tool_call_started`.
- `runId` dibuat oleh service. Isinya huruf, angka, dan tanda hubung, paling panjang 128 karakter.
- Run berjalan di service, tidak bergantung pada koneksi SSE. Menutup koneksi SSE tidak menghentikan run.
- Service menyimpan semua event setiap run, sehingga event bisa diputar ulang. `sequence` tetap berlanjut setelah service dinyalakan ulang.

### Bentuk error

Semua respons error memakai bentuk ini:

```json
{ "error": { "code": "stale_approval", "message": "Approval approval_1 sudah diganti oleh approval_2." } }
```

| HTTP | `code` | Kapan |
|---|---|---|
| 400 | `invalid_input` | Body tidak sesuai skema, termasuk komentar kosong untuk `revise` atau `reject` |
| 404 | `run_not_found` | `runId` tidak dikenal |
| 409 | `not_awaiting_approval` | `resume` dipanggil saat run tidak sedang menunggu keputusan |
| 409 | `stale_approval` | `approvalId` bukan approval yang sedang menunggu |
| 503 | `not_ready` | Service belum siap, misalnya penyimpanan state belum terbuka |

`message` untuk log dan pengembang, bukan untuk ditampilkan ke analis. Pesan
untuk analis ada di event `error`.

## `POST /runs`

Memulai run baru. Service langsung menjalankan agent di latar belakang.

Request: `InvestigationRequest`.

```json
{
  "caseId": "8a2771e2-5fe0-42bd-815e-04b8db278150",
  "symbol": "BBCA",
  "anomalyDate": "2026-11-24",
  "trigger": { "kind": "volume", "rule": "Volume di atas 1,1x rata-rata 3 bulan", "volumeRatio": 1.12 },
  "notes": null
}
```

Respons `201 Created`:

```json
{ "runId": "lg-5b0c2f1e9d8a4c7b" }
```

Error: `400 invalid_input`, `503 not_ready`.

Jika agent gagal sebelum sempat bekerja, misalnya server MCP tidak bisa
dihubungi, service tetap mengembalikan `201`. Kegagalannya muncul sebagai
event `error` di aliran event.

## `GET /runs/{runId}/events`

Aliran Server-Sent Events. `Content-Type: text/event-stream`.

Posisi awal:

- header `Last-Event-ID: <sequence>`, dikirim otomatis oleh EventSource saat menyambung ulang, atau
- query `?after=<sequence>`.

Jika keduanya ada, header yang dipakai. Jika tidak ada keduanya, aliran
dimulai dari event pertama.

Setiap event dikirim sebagai satu frame:

```text
id: 7
event: tool_call_finished
data: {"runId":"lg-5b0c2f1e9d8a4c7b","sequence":7,"timestamp":"2026-11-25T03:12:09.1234567Z","type":"tool_call_finished","payload":{...}}

```

- `id` sama dengan `sequence`.
- `event` sama dengan `type`.
- `data` adalah satu event katalog lengkap dalam satu baris JSON.

Urutan kerja service:

1. Kirim semua event tersimpan dengan `sequence` lebih besar dari posisi awal, berurutan.
2. Jika run masih berjalan, kirim event baru begitu muncul.
3. Tutup aliran setelah mengirim `approval_required`, `done`, atau `error`.
4. Jika run sedang menunggu approval atau sudah selesai, dan tidak ada event setelah posisi awal, tutup aliran langsung tanpa frame.

Selama menunggu event baru, service mengirim komentar `: ping` setiap 15 detik
supaya koneksi tidak diputus perantara.

Error sebelum aliran dimulai: `404 run_not_found`, dikirim sebagai JSON biasa.

Klien bertanggung jawab menyimpan `sequence` terakhir yang diterima dan
memakainya sebagai posisi awal saat menyambung ulang. Event yang sama boleh
diterima lebih dari sekali; klien membuang duplikat berdasarkan `sequence`.

## `POST /runs/{runId}/resume`

Mengirim keputusan analis untuk `approval_required` yang terakhir.

Request: `ApprovalDecision`.

```json
{ "approvalId": "approval_1", "decision": "revise", "comment": "Tambahkan arus asing 20 sesi.", "decidedBy": "analis-1" }
```

Respons `202 Accepted` dengan body kosong `{}`. Service melanjutkan run di
latar belakang. Event berikutnya diambil lewat `GET /runs/{runId}/events`
dengan posisi awal `sequence` terakhir.

Error: `400 invalid_input`, `404 run_not_found`, `409 not_awaiting_approval`,
`409 stale_approval`.

Keputusan ini harus tetap bisa diproses setelah service dimatikan dan
dinyalakan ulang di tengah jeda approval.

## `POST /runs/{runId}/cancel`

Menghentikan run atas permintaan pengguna. Body kosong.

Respons `202 Accepted` dengan body `{}`. Service lalu:

1. Menghentikan panggilan model dan tool yang sedang berjalan.
2. Memancarkan `tool_call_finished` dengan `status: error` dan kode `cancelled` untuk setiap tool call yang masih terbuka, termasuk `submit_memo` saat menunggu approval.
3. Memancarkan `done` dengan `outcome: cancelled`.

Endpoint ini idempoten. Memanggilnya untuk run yang sudah selesai tetap
mengembalikan `202` tanpa mengubah apa pun.

Error: `404 run_not_found`.

## `GET /health`

Respons `200 OK`:

```json
{ "status": "ok" }
```

Respons `503` dengan bentuk error di atas jika service belum siap.

## Perilaku saat service dinyalakan ulang

- Run yang sedang menunggu approval tetap menunggu dan bisa dilanjutkan dengan `resume`.
- Run yang sedang berjalan saat service mati tidak bisa dilanjutkan dengan aman. Saat service menyala, run itu ditutup dengan `tool_call_finished` (`status: error`, kode `internal`) untuk tool call yang terbuka, lalu event `error` dengan kode `internal` dan `retryable: true`.
- Run yang sudah selesai tetap bisa diputar ulang lewat `GET /runs/{runId}/events`.

## Pemetaan ke `IAgentClient`

| `IAgentClient` | HTTP |
|---|---|
| `StartAsync` | `POST /runs` |
| `StreamAsync` | `GET /runs/{runId}/events?after=<sequence terakhir>` sampai aliran ditutup |
| `ResumeAsync` | `POST /runs/{runId}/resume`. `409` menjadi `InvalidOperationException` |
| pembatalan token `StreamAsync` | `POST /runs/{runId}/cancel`, lalu terus membaca aliran sampai `done` |
