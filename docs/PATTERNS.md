# Katalog pola UI agent

Halaman investigasi dirakit dari sembilan pola. Halaman job deteksi pola (`/jobs/{id}`) memakai pola yang sama, ditambah panel worker. Setiap pola punya satu partial
view dan satu modul JavaScript dengan nama yang sama, sehingga bisa dirujuk per
bab. Setiap pola juga bisa diperagakan sendiri di `/lab/patterns` dengan agent
tiruan.

## Cara kerjanya

- **Host** (`wwwroot/js/patterns/agent-ui.js`) dipasang pada satu elemen `[data-agent-ui]`. Tugasnya:
  - membuang event dengan `sequence` yang sudah tampil;
  - menyimpan status run (berjalan, menunggu analis, selesai);
  - menyambungkan transport, atau memutar ulang dari database;
  - meneruskan setiap event katalog ke semua pola di dalam elemen itu.
- **Setiap pola** mendaftar dengan `AgentUi.register(nama, ctx => ({ onEvent, onFinish, reset }))`.
  - Pola mencari elemennya lewat `ctx.part("…")`, yaitu `data-part` di dalam root, bukan `id` global. Karena itu satu halaman bisa berisi banyak root, seperti di `/lab/patterns`.
- **Pola tidak saling memanggil.** Hubungan antar-pola hanya lewat atribut di DOM. Contohnya, kartu approval memberi tautan `data-evidence-call`, lalu pola bukti menangkap klik pada tautan itu. Pola yang tidak dipasang tidak merusak pola lain.
- **Tidak ada satu pun pola yang tahu solusi agent mana yang bekerja.**

## Pola

| Pola | Event yang dibutuhkan | Partial view | Modul JavaScript | Implikasi ke backend |
|---|---|---|---|---|
| Narasi mengalir | `token` | `Views/Shared/Patterns/_Narrative.cshtml` | `wwwroot/js/patterns/narrative.js` | Adapter menggabungkan potongan teks model sekitar 60 karakter per event, supaya log tidak membengkak. Teks giliran baru dimulai di baris baru. |
| Indikator berpikir | Tidak ada event khusus; memakai waktu event terakhir dan status run | `_ThinkingIndicator.cshtml` | `thinking-indicator.js` | Tidak ada perubahan kontrak (Keputusan kontrak). Muncul setelah 4 detik tanpa event, dan teks berubah setelah 15 detik. Ambang bisa diubah lewat `data-thinking-after-ms` dan `data-thinking-long-ms`. Ping SSE tidak dihitung sebagai event. |
| Panggilan data dengan label ramah | `run_started` (`maxToolCalls`), `tool_call_started`, `tool_call_finished` | `_ToolCalls.cshtml` | `tool-calls.js` | Peta nama tool ke kalimat ada di JavaScript, tanpa perubahan kontrak. Tool baru tanpa label tampil sebagai "Sedang mengambil data (nama)…". |
| Progres tahap | `step_started`, `approval_required`, `done`, `error` | `_StepProgress.cshtml` | `step-progress.js` | Butuh event `step_started` (CHANGE-001). Adapter menentukan tahap dari nama tool, jadi keempat solusi memancarkan urutan yang sama. |
| Bukti di balik temuan | `tool_call_finished.source`, `Memo.findings[].evidence[]`; data mentah dari endpoint | `_Evidence.cshtml` | `evidence.js` | Butuh `source` dan `resultPreview` (CHANGE-001). Aplikasi menyimpan pratinjau di tabel `ToolResultPreviews` dan menyajikannya lewat `GET /investigations/{id}/tools/{toolCallId}/preview` hanya saat dibuka, tidak lewat SSE. |
| Tombol hentikan | `done`, `error` (untuk menyembunyikan tombol) | `_StopButton.cshtml` | `stop-button.js` | `POST /investigations/{id}/stop`. Adapter menutup tool call yang terbuka lalu memancarkan `done(cancelled)`. Teks bantuan menyatakan bahwa data yang sudah masuk tetap tersimpan. |
| Banner galat dan akhir run | `error` (`code`, `message`, `retryable`), `done` | `_ErrorBanner.cshtml` | `error-banner.js` | Adapter mengisi `message` dalam bahasa Indonesia dan `retryable`. Galat yang bisa diulang diberi tombol mulai ulang. Galat lain diberi langkah berikutnya per `code`. |
| Kartu approval | `approval_required`, `tool_call_started`/`finished` untuk `submit_memo`, `done`, `error` | `_ApprovalCard.cshtml` | `approval-card.js` | `POST /investigations/{id}/approval`. Keputusan tercatat atas nama pengguna yang login, dan dua keputusan bersamaan ditolak dengan 409 (Tahap 7). |
| Pengambilan kasus | `approval_required`, `tool_call_finished`, `done`, `error`; status dari `GET /investigations/{id}/claim` | `_CaseClaim.cshtml` | `case-claim.js` | Kolom pegangan di kasus dan `CaseClaims.cs`. Tombol keputusan aktif hanya untuk pemegang. Server menolak keputusan dari analis lain selama pegangan aktif. Setelah `done(cancelled)`, endpoint yang sama memberi tahu bila kasus sebenarnya kedaluwarsa (Tahap 11). |
| Panel worker | `worker_started`, `worker_finished`, `agentName` di setiap event, `tool_call_started`/`finished` milik worker | `_WorkerPanels.cshtml` | `worker-panels.js` | Butuh CHANGE-002. Satu panel per `workerId` dengan peran, target, kedalaman, tool call, ringkasan, dan jejak baru. Transport SSE harus mendaftarkan kedua tipe event baru (`SseTransportCatalogTests`). |

## Peragaan per pola

Halaman `/lab/patterns` aktif hanya saat solusi aktif adalah agent tiruan.
Setiap bagian memasang root berisi pola itu saja, lalu memulai satu run tiruan:

| Pola | Skenario fake (`scenario=` di catatan) |
|---|---|
| Narasi, panggilan data, progres tahap, bukti, kartu approval | `happy` (alur penuh sampai approval) |
| Indikator berpikir | `silent_30s` (diam 30 detik setelah mulai) |
| Tombol hentikan | `slow` (3 detik per tool) |
| Banner galat | `model_error` (bisa diulang) dan `step_limit` (tidak bisa diulang) |

Halaman ini memakai polling tiap 1 detik, bukan SSE. Delapan koneksi SSE
sekaligus menghabiskan batas enam koneksi per host pada HTTP/1.1, sehingga
tombol Hentikan tidak pernah terkirim. Masalah ini ditemukan saat pengujian
dan dijelaskan di `docs/TRANSPORTS.md`.

## Menambah pola

1. Buat partial `Views/Shared/Patterns/_NamaPola.cshtml` dengan `data-pattern="nama-pola"` dan elemen ber-`data-part`.
2. Buat `wwwroot/js/patterns/nama-pola.js` yang memanggil `AgentUi.register("nama-pola", …)`.
3. Tambahkan skrip itu ke `_AgentUiScripts.cshtml` dan partial-nya ke halaman.
4. Bila pola butuh data yang belum ada di event, tulis usulan perubahan kontrak seperti `contracts/CHANGE-001-step-and-evidence.md`.
