# CHANGE-002: event untuk agent multi-worker

Status: diterima (Keputusan kontrak di CLAUDE.md) dan diterapkan di Tahap 13
untuk agent tiruan, lalu di MAF, LangGraph, dan ADK pada Tahap 14.

## Kebutuhan front end

Use case 2 (`asd-pattern-detection.md`) dijalankan oleh satu orchestrator dan
beberapa worker. Halaman detail job harus bisa menampilkan:

- satu panel per worker, dengan tool call dan temuannya;
- kapan worker mulai dan selesai, serta jejak baru yang diusulkannya;
- laporan klaster untuk disetujui analis, yang bentuknya berbeda dari memo UMA.

## Alternatif tanpa perubahan kontrak, dan kenapa tidak cukup

| Alternatif | Kenapa tidak cukup |
|---|---|
| Menebak worker dari argumen tool (misalnya `symbol`) | Worker broker dan worker kepemilikan memanggil tool untuk banyak saham. Narasi orchestrator tidak punya argumen sama sekali. |
| Menjalankan setiap worker sebagai run terpisah | `IAgentClient` dan aplikasi menganggap satu kasus = satu run. Approval laporan butuh satu run yang menunggu. Urutan event antar-worker juga hilang. |
| Memasukkan laporan ke `Memo` | `Memo` mewajibkan satu `symbol` dan `anomalyDate`. Laporan klaster mencakup banyak saham dan satu periode. |

## Perubahan

Semua perubahan bersifat menambah. `IAgentClient` tidak berubah.

| Bagian | Perubahan |
|---|---|
| Event | Field opsional `agentName` di tingkat atas setiap event |
| Katalog | Event baru `worker_started` (`workerId`, `role`, `target`, `depth`, `reason`) dan `worker_finished` (`workerId`, `role`, `status`, `summary`, `leads`) |
| `run_started` | Field opsional `useCase`. `symbol` hanya wajib bila `useCase` bukan `pattern_detection`. |
| `approval_required`, `done` | Membawa `memo` atau `report` (`$defs/PatternReport`) |
| `InvestigationRequest` | Field opsional `useCase` dan `job` (`$defs/PatternJob`). `symbol`, `anomalyDate`, dan `trigger` hanya wajib untuk UMA. |
| `agent-service-api.md` | `POST /runs` menerima `useCase` dan `job`. Service yang belum mendukung `pattern_detection` membalas `400 invalid_input`. |

`$defs/Evidence` dipisah dari `Finding` supaya dipakai bersama oleh memo dan
laporan. Bentuknya tidak berubah.

## Dampak ke tiap solusi

| Solusi | Tahap 13 | Tahap 14 |
|---|---|---|
| Fake | Skrip job lengkap: orchestrator, worker saham, broker, dan kepemilikan berselang, satu jejak baru, approval laporan | – |
| MAF | Belum mendukung. Aplikasi tidak menawarkan job untuk solusi ini (`AgentProvider.SupportsPatternDetection`). | Orchestrator sebagai kode (`PatternOrchestrator.cs`), worker `ChatClientAgent` paralel, `submit_report` dengan `ApprovalRequiredAIFunction` |
| LangGraph | Sama dengan MAF. Service membalas `400` untuk `pattern_detection`. | Graph kedua (`pattern_graph.py`): node `workers` menjalankan orchestrator dan satu `StateGraph` per worker, lalu `report_agent` dan `review` dengan `interrupt()` |
| ADK | Sama dengan LangGraph | Orchestrator sebagai kode, satu `LlmAgent` per worker dengan `Runner` sendiri, `submit_report` dengan Tool Confirmation (`pattern_agents.py`) |

## Kompatibilitas ke belakang

- Event UMA tidak berubah dan tidak membawa `agentName`. Investigator UMA di keempat adapter tetap lulus contract test lama.
- Klien yang tidak mengenal `worker_started` atau `worker_finished` mengabaikannya, seperti `step_started` di CHANGE-001. Mengabaikan berarti kehilangan, bukan menunda. `EventSource` hanya memanggil listener untuk tipe yang didaftarkan, sementara `Last-Event-ID` tetap maju melewati event itu. Event baru tidak akan diputar ulang setelah daftar tipe diperbarui; klien harus memuat ulang dari `sequence` 0. Ini ditemukan saat uji browser Tahap 13: panel worker kosong sampai `transport-sse.js` mendaftarkan kedua tipe baru. Polling dan SignalR tidak terkena masalah ini, karena keduanya meneruskan semua event.
- Event lama di database tetap sah, karena semua field baru opsional.
- Kolom baru `AgentName` di tabel `Events` boleh kosong.

## Cara memeriksa

Tahap 13:
- test aplikasi (`PatternJobTests`) memvalidasi setiap event job dari agent tiruan terhadap skema;
- test yang sama memeriksa pasangan `worker_started`/`worker_finished`;
- test yang sama memeriksa `agentName` di setiap event.

Tahap 15 menjadikannya contract test untuk keempat adapter.
