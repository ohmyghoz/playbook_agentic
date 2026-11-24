# Pemetaan event: Google ADK → katalog event

Dokumen ini menjelaskan cara service ADK mengubah event ADK menjadi event di
`contracts/events.schema.json`, lalu menyajikannya lewat
`contracts/agent-service-api.md` tanpa perubahan kontrak. Semua pemetaan
terjadi di service, sehingga `AdkAgentClient` di aplikasi hanya meneruskan
event apa adanya.

Versi yang dipakai: `google-adk[mcp,db]` 2.15.0, `google-genai` 2.34.0. Model
`gemini-3.8-flash` lewat Google AI Studio (`GOOGLE_API_KEY`, bukan Vertex AI).
Data runtime (`service.db`, `sessions.db`) disimpan di `agents/adk/data`, atau
di `ADK_DATA_DIR` jika diisi.

## Susunan agent

- `LlmAgent` dengan instruksi sistem dari ASD, dibangun dari `request` di session state.
- Tool MCP lewat `McpToolset` (`StreamableHTTPConnectionParams` dengan header `Authorization: Bearer`, `tool_filter` = allowlist ASD).
- `submit_memo` sebagai `FunctionTool` yang memakai **Tool Confirmation**: fungsi memanggil `tool_context.request_confirmation(payload=...)`, ADK memancarkan function call `adk_request_confirmation`, dan keputusan analis dikirim balik sebagai `FunctionResponse` dengan `{"confirmed": true, "payload": <ApprovalDecision>}`.
- `before_model_callback` menghitung giliran model, `before_tool_callback` menghitung batas langkah, `after_tool_callback` menambahkan `[toolCallId: ...]` di awal hasil tool.
- `Runner` dengan `DatabaseSessionService(sqlite+aiosqlite)`. Session state menyimpan pembukuan run, sehingga approval yang tertunda bertahan setelah restart.
- `session_id` sama dengan `runId`.

## Pemetaan

Service membaca `runner.run_async(..., run_config=RunConfig(streaming_mode=StreamingMode.SSE), abort_signal=...)`.

| Event ADK | Event katalog | Catatan |
|---|---|---|
| Run dibuat (`POST /runs`) | `run_started` | Dibuat service; session dibuat dengan state awal |
| Teks dengan `partial=True` | `token` | Digabung sekitar 60 karakter, atau sampai ada event lain |
| Teks tanpa `partial` setelah potongan-potongan teks | (dibuang) | SSE ADK mengirim potongan lalu teks utuh. Teks utuh hanya dipakai jika tidak ada potongan sebelumnya |
| `get_function_calls()` untuk tool MCP atau `submit_memo` | `tool_call_started` | `toolCallId` = `FunctionCall.id`. Function call yang masih `partial` diabaikan |
| `get_function_responses()` untuk tool MCP | `tool_call_finished` | `summary` dari `summarizer.py`. `isError` atau `{"error": ...}` menjadi `status: error` |
| Function call `adk_request_confirmation` | `approval_required` | Isi diambil dari `pending` di session state; run berstatus `paused` |
| `FunctionResponse` `submit_memo` setelah keputusan | `tool_call_finished` | Respons `{"status": "waiting"}` dari jeda pertama tidak dipetakan |
| State `outcome` (`approved` / `rejected`) | `done` | Termasuk `memo` dan `toolCallsUsed` |
| State `error` | `error` | `step_limit_exceeded`, `revision_limit_exceeded`, atau `model_error` |
| `google.genai.errors.APIError` atau event dengan `error_code` | `error` (`model_error`, `retryable: true`) | Setelah 2 kali coba ulang (`retry_options` di model `Gemini`) untuk HTTP 429 dan 5xx |
| `McpToolset.get_tools()` gagal atau kosong saat run mulai | `error` (`mcp_unavailable`, `retryable: true`) | |
| `POST /runs/{id}/cancel` | `tool_call_finished` (`cancelled`) untuk tool yang terbuka, lalu `done` (`cancelled`) | `abort_signal.set()`; jika run sedang menunggu, service langsung menutupnya |

## Perbedaan dengan kontrak dan cara menyerapnya

1. **`sequence` dan penyimpanan event.** ADK menyimpan event-nya sendiri di session, tapi bentuknya bukan katalog dan tidak bernomor urut. Service memberi nomor per run dan menyimpan event katalog di `data/service.db`, dengan kode yang sama seperti LangGraph.
2. **Keputusan analis lewat payload konfirmasi.** Tool Confirmation hanya mengenal `confirmed` benar atau salah. Service selalu mengirim `confirmed: true` dan meletakkan keputusan (`approve`/`revise`/`reject` beserta komentar) di `payload`. `submit_memo` yang dijalankan ulang membaca payload itu. Setuju dan tolak memasang `skip_summarization` sehingga model tidak dipanggil lagi; revisi mengembalikan "Analis meminta revisi: <komentar>" ke model.
3. **Skema memo vs validasi.** Jika fungsi `submit_memo` menerima parameter bertipe `Memo`, ADK mengonversi argumen sebelum fungsi berjalan, sehingga memo yang tidak sah gagal di dalam ADK dan tidak tercatat sebagai `invalid_memo`. `SubmitMemoTool` mendeklarasikan skema `Memo` lengkap ke model, tapi fungsinya menerima dict mentah dan memvalidasinya sendiri.
4. **MCP tidak bisa dihubungi.** `McpToolset` tidak gagal saat server mati; agent berjalan tanpa tool. Service memeriksa `get_tools()` di awal run dan memancarkan `mcp_unavailable`.
5. **Batas langkah.** `RunConfig.max_llm_calls` menghitung panggilan model, bukan tool. Batas ASD dihitung di `before_tool_callback` dengan aturan yang sama seperti MAF dan LangGraph: tool paralel dalam satu giliran yang melewati batas hanya diberi peringatan, dan `step_limit_exceeded` baru dipancarkan jika model meminta tool lagi di giliran berikutnya. Saat itu `skip_summarization` mengakhiri giliran tanpa memanggil model.
6. **Narasi di giliran terpisah.** Jika `run_async` selesai tanpa tool call, service mengirim "Lanjutkan: panggil tool berikutnya, atau panggil submit_memo jika data sudah cukup." Run gagal (`model_error`) setelah 3 giliran teks berturut-turut. Aturan sama dengan MAF dan LangGraph.
7. **Run yang terputus.** Jika service mati saat run berjalan (bukan saat jeda), service menutup run itu saat menyala lagi dengan `error` (`internal`, `retryable: true`).
8. **Kode bersama.** `contract.py`, `instructions.py`, dan `summarizer.py` adalah salinan dari `agents/langgraph`. Test membandingkan instruksi dengan ASD di kedua service.

## Hal yang dokumentasinya bertentangan atau belum ada, dan sudah dipastikan lewat test

- Halaman situs ADK tentang Tool Confirmation menyebut `DatabaseSessionService` tidak didukung, sedangkan panduan di repo `adk-python` (lebih baru) menyebut keputusan bisa datang dari proses lain selama session persisten. `test_paused_run_survives_a_service_restart` memastikan jeda approval, revisi, lalu persetujuan berjalan dengan `Runner` dan `DatabaseSessionService` baru.
- `abort_signal` (sejak 2.11) hanya didokumentasikan di repo, belum di situs. `test_cancel_during_model_call_ends_cancelled` memastikan pembatalan menutup run dengan `done(cancelled)`.
