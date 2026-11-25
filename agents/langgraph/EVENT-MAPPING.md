# Pemetaan event: LangGraph → katalog event

Dokumen ini menjelaskan cara service LangGraph mengubah keluaran graph menjadi
event di `contracts/events.schema.json`, lalu menyajikannya lewat
`contracts/agent-service-api.md`. Semua pemetaan terjadi di service, sehingga
`LangGraphAgentClient` di aplikasi hanya meneruskan event apa adanya.

Data runtime (`service.db`, `checkpoints.db`) disimpan di `agents/langgraph/data`, atau di `LANGGRAPH_DATA_DIR` jika diisi. Nama variable sengaja spesifik: `DATA_DIR` sudah dipakai aplikasi lain di mesin pengembang.

Versi yang dipakai: `langgraph` 1.2.24, `langchain` 1.5.2 (`langchain.mcp`),
`langchain-openai` 1.8.5, `langgraph-checkpoint-sqlite` 3.1.3. Model
`qwen/qwen3-30b-a3b-instruct-2507` lewat OpenRouter.

## Susunan graph

```text
START → agent ─┬─ tool call MCP ──→ tools ──┬─→ agent
               │                            └─→ review (jika ada submit_memo)
               ├─ submit_memo ─────────────→ review ──┬─→ agent (memo tidak sah, atau revisi)
               │                                      └─→ END (setuju, tolak, atau error)
               ├─ hanya teks ──────────────→ nudge → agent ("Lanjutkan…")
               └─ 3 giliran teks berturut ─→ no_memo → END
```

- `agent`: `ChatOpenAI` dengan `base_url` OpenRouter. Tool MCP dan skema `submit_memo` diikat lewat `bind_tools`, dengan instruksi sistem dari ASD.
- `tools`: menjalankan tool MCP secara paralel, menghitung batas langkah, dan menambahkan `[toolCallId: ...]` di awal setiap hasil.
- `review`: memvalidasi memo, lalu memanggil `interrupt()` untuk keputusan analis.
- Checkpointer: `AsyncSqliteSaver` di `data/checkpoints.db`, dengan `durability="sync"`. Setiap langkah tersimpan sebelum langkah berikutnya, termasuk jeda approval.
- `thread_id` sama dengan `runId`.

## Pemetaan

Service membaca `graph.astream(..., stream_mode=["messages", "custom"], version="v2")`.

| Keluaran LangGraph | Event katalog | Catatan |
|---|---|---|
| Run dibuat (`POST /runs`) | `run_started` | Dibuat service sebelum graph dijalankan |
| `messages`: `AIMessageChunk` dari node `agent` | `token` | Digabung sekitar 60 karakter, atau sampai ada event lain |
| `custom` `{"kind": "tool_started"}` dari node `tools` atau `review` | `tool_call_started` | `toolCallId` = id tool call dari model |
| `custom` `{"kind": "tool_finished"}` | `tool_call_finished` | `summary` dibuat `summarizer.py` dari envelope idx. Envelope `error` atau `ToolMessage(status="error")` menjadi `status: error` |
| `interrupt()` di `review` (dibaca dari `aget_state().tasks[].interrupts`) | `approval_required` | Run berstatus `paused` |
| State akhir `outcome` (`approved` / `rejected`) | `done` | Termasuk `memo` dan `toolCallsUsed` |
| State akhir `error` | `error` | `step_limit_exceeded`, `revision_limit_exceeded`, atau `model_error` |
| `openai.APIError` | `error` (`model_error`, `retryable: true`) | |
| MCP tidak bisa dihubungi saat run mulai | `error` (`mcp_unavailable`, `retryable: true`) | |
| `POST /runs/{id}/cancel` | `tool_call_finished` (`cancelled`) untuk tool yang terbuka, lalu `done` (`cancelled`) | Task asyncio dibatalkan; jika run sedang menunggu, service langsung menutupnya |

## Tahap dan bukti (CHANGE-001)

| Event atau field | Asal di service |
|---|---|
| `step_started` | `RunManager._emit` menanyakan `steps.next_step` untuk setiap event. Tahap baru dipancarkan sebelum `tool_call_started` dan `approval_required`, atau sesudah `run_started`. Tahap terakhir disimpan di `RunInfo.step` (`service.db`), sehingga tetap benar setelah service dinyalakan ulang di tengah jeda approval. |
| `tool_call_finished.source` | `summarizer.summarize` membaca amplop idx dari `ToolMessage` dan memanggil `contract.tool_source`. |
| `tool_call_finished.resultPreview` | Teks amplop paling dalam dari konten `ToolMessage`, dipotong dengan `contract.result_preview`. |

Node `tools` dan `review` di graph tidak berubah. Pemetaan terjadi di stream
`custom` yang sudah ada.

Aturan tahap tidak diambil dari framework. LangGraph tidak punya konsep tahap
alur kerja, dan model tidak diminta melaporkannya. Tahap ditentukan dari nama
tool, sama persis dengan ketiga adapter lain. Detail dan alasannya ada di
`contracts/CHANGE-001-step-and-evidence.md`.

## Perbedaan dengan kontrak dan cara menyerapnya

1. **`sequence` dan penyimpanan event.** LangGraph tidak memberi nomor urut event. Service memberi nomor per run dan menyimpan setiap event di `data/service.db`, sehingga event bisa diputar ulang dan nomornya tetap berlanjut setelah restart.
2. **Node diulang saat dilanjutkan.** Dokumentasi LangGraph menyebut node yang berisi `interrupt()` dijalankan ulang dari awal saat dilanjutkan. Akibatnya `tool_call_started` untuk `submit_memo` bisa terkirim dua kali. Service membuang duplikat berdasarkan `toolCallId`. Validasi memo di depan `interrupt()` tidak punya efek samping, jadi aman diulang.
3. **Revisi.** Komentar analis dikirim ke model sebagai `ToolMessage` untuk `submit_memo` dengan awalan "Analis meminta revisi: ". Penolakan akhir tidak dikirim ke model.
4. **Batas langkah.** `recursion_limit` LangGraph menghitung super-step, bukan tool call, jadi tidak dipakai sebagai batas ASD (diset 200 hanya sebagai pengaman). Batas ASD dihitung di node `tools` dengan aturan yang sama seperti MAF: tool call paralel dalam satu giliran yang melewati batas hanya diberi peringatan, dan `step_limit_exceeded` baru dipancarkan jika model meminta tool lagi di giliran berikutnya.
5. **Model lewat `ChatOpenAI`.** Dokumentasi LangChain menyarankan paket khusus `langchain-openrouter`, karena `ChatOpenAI` tidak membaca field non-standar seperti `reasoning`. Kami tetap memakai `ChatOpenAI(base_url=...)` karena CLAUDE.md meminta endpoint kompatibel OpenAI lewat `MODEL_BASE_URL`, dan model yang dipakai adalah varian instruct tanpa mode reasoning.
6. **MCP lewat `langchain.mcp`.** `langchain-mcp-adapters` sudah tidak dirawat. Service memakai `MCPAdapter` (beta) di atas `fastmcp` `StreamableHttpTransport` dengan header `Authorization: Bearer`.
7. **Run yang terputus.** Jika service mati saat run sedang berjalan (bukan saat jeda), service menutup run itu saat menyala lagi dengan `error` (`internal`, `retryable: true`). Run yang sedang menunggu approval tetap bisa dilanjutkan dari checkpoint.
8. **Narasi di giliran terpisah.** Saat uji live, Qwen menulis narasi di satu giliran (tanpa tool call, `finish_reason=stop`) lalu memanggil tool di giliran berikutnya. Giliran yang hanya berisi teks dianggap narasi: node `nudge` mengirim "Lanjutkan: panggil tool berikutnya, atau panggil submit_memo jika data sudah cukup." Run baru gagal (`model_error`) setelah 3 giliran teks berturut-turut. MAF memakai aturan yang sama.
9. **Setelah memo disetujui.** Node `review` langsung mengakhiri graph tanpa memanggil model lagi, sama seperti MAF.

## Hal yang tidak terdokumentasi dan sudah dipastikan lewat test

- Pembatalan task asyncio di tengah `astream` tidak terdokumentasi. `test_cancel_during_model_call_ends_cancelled` memastikan pembatalan menutup run dengan `done(cancelled)`.
- Melanjutkan run setelah checkpointer dan service dibuka ulang dipastikan oleh `test_paused_run_survives_a_service_restart`.

## Run terputus karena restart (CHANGE-003)

Run yang sedang berjalan saat proses berhenti ditutup dengan `error` kode
`run_interrupted` dan `retryable: true`, bukan lagi `internal`. Lihat
`docs/RESTART.md`.
`RunManager.recover` menulisnya untuk run berstatus `running` saat service start.

## Use case 2: deteksi pola (Tahap 14, CHANGE-002)

Run dengan `useCase: pattern_detection` memakai graph kedua,
`build_pattern_graph` (`app/pattern_graph.py`), dengan checkpointer yang sama:

```text
START -> workers -> report_agent -> review -> report_agent | END   (interrupt untuk analis)
                                 -> nudge  -> report_agent
                                 -> no_report -> END
```

| Bagian | LangGraph |
|---|---|
| Orchestrator | Node `workers` menjalankan `PatternOrchestrator` (`app/pattern_orchestrator.py`, kode biasa; salinan sama di ADK, padanan `PatternOrchestrator.cs` di MAF). Paling banyak 3 worker sekaligus (`asyncio.Semaphore`). |
| Worker | Satu `StateGraph` kecil per worker (`worker_agent` ↔ `worker_tools`, berakhir di `finish`). Dikompilasi tanpa checkpointer dan dipanggil dengan `ainvoke` dari node `workers`. Tool hanya yang sesuai perannya, ditambah `finish_worker`. |
| Batas | Langkah per worker dan `JobBudget` dicek di `worker_tools`. Panggilan ke tool yang tidak ada juga dihitung sebagai langkah. |
| Penyusun laporan | Node `report_agent` (model dengan `submit_report` saja) dan `review` dengan `interrupt()`. Jeda dan lanjut sama seperti `submit_memo`. |

| Event | Asal |
|---|---|
| `worker_started`, `worker_finished` | Stream `custom` (`kind` dengan nama yang sama) dari node `workers` |
| `tool_call_*` worker | Stream `custom` dengan field `agent`. `RunInfo.call_agents` menyimpan agent setiap tool call. |
| `token` | Narasi orchestrator (`kind: narration`) dan teks node `report_agent` dari stream `messages`. Teks `worker_agent` dibuang. |
| `agentName` | `RunManager._emit`: id worker untuk event worker, `orchestrator` untuk event lain. Tahap UMA (`step_started`) tidak dipakai. |

**Ketahanan.**
- Jeda approval laporan bertahan setelah service dinyalakan ulang, karena checkpointer menyimpan graph setelah node `workers`.
- Bila service mati saat worker berjalan, `recover` menutup worker yang terbuka, lalu run berakhir dengan `run_interrupted`.

**Uji live, 25 November 2026, 3 kandidat:**

| Model | Hasil |
|---|---|
| `qwen3-30b-a3b-instruct-2507` | Jalur penuh terlewati: worker saham, worker broker, klaster, lalu worker kepemilikan. Masalahnya: model memanggil tool yang tidak ada (`worker_broker`) berulang kali dan menulis nama broker, bukan kodenya. Kedua hal itu kini ditangani di kode dan diperjelas di instruksi ASD. Run berakhir `model_error` karena koneksi ke OpenRouter putus (WinError 1236, jaringan lokal). |
| `qwen3.8-27b` (model aktif) | Ketiga worker saham selesai `ok` tanpa panggilan tool yang salah. Laporan diterima setelah satu perbaikan format, lalu disetujui (30 panggilan data). Dengan batas job 4 tool call, ketiga worker `limit_reached` dan laporan parsial mencatat batasnya. |
