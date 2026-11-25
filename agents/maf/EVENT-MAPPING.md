# Pemetaan event: Microsoft Agent Framework → katalog event

Dokumen ini menjelaskan cara `MafAgentClient` mengubah keluaran Microsoft Agent
Framework (MAF) menjadi event di `contracts/events.schema.json`. Semua
pemetaan terjadi di adapter, sehingga controller dan view tidak mengenal MAF.

Versi yang dipakai: `Microsoft.Agents.AI.OpenAI` 1.28.0, `ModelContextProtocol`
2.4.0, model `gpt-5-mini` lewat OpenAI Chat Completions (bukan Azure OpenAI).

## Susunan agent

- `ChatClientAgent` di atas `IChatClient` dari `OpenAIClient.GetChatClient(model).AsIChatClient()`.
- Instruksi sistem dari ASD, diisi data kasus untuk setiap run.
- Tool MCP dari `McpClient.ListToolsAsync()` lewat `HttpClientTransport` (streamable HTTP, header `Authorization: Bearer $IDX_MCP_TOKEN`), lalu disaring dengan allowlist ASD.
- Tool lokal `submit_memo` dibungkus `ApprovalRequiredAIFunction`.
- Function middleware (`AsBuilder().Use(...)`) menghitung batas langkah dan menandai hasil tool dengan `[toolCallId: ...]` supaya model bisa mengutipnya sebagai bukti.

Kami memakai Chat Completions, bukan Responses API. Riwayat percakapan
tersimpan di `AgentSession` milik adapter, sehingga bisa diserialisasi dan
diuji dengan `IChatClient` tiruan.

## Pemetaan

| Keluaran MAF | Event katalog | Catatan |
|---|---|---|
| Run pertama kali di-stream | `run_started` | Dibuat adapter, bukan dari MAF |
| `AgentResponseUpdate.Text` tidak kosong | `token` | Digabung sampai sekitar 60 karakter, atau sampai ada event lain |
| `FunctionCallContent` (tool MCP) | `tool_call_started` | `toolCallId` = `CallId` dari OpenAI. Duplikat diabaikan |
| `FunctionResultContent` | `tool_call_finished` | `summary` dibuat `ToolResultSummarizer` dari envelope idx. Envelope `error` menjadi `status: error` dengan kode dari idx |
| `ToolApprovalRequestContent` untuk tool MCP | `tool_call_started` | Disetujui otomatis (lihat perbedaan 2) |
| `ToolApprovalRequestContent` untuk `submit_memo`, memo sah | `tool_call_started`, lalu `approval_required` | Run dijeda dan session diserialisasi |
| `ToolApprovalRequestContent` untuk `submit_memo`, memo tidak sah | `tool_call_started`, lalu `tool_call_finished` (`error`, kode `invalid_memo`) | Ditolak lewat `CreateResponse(false, alasan)`, lalu model memperbaiki. Maksimal 3 kali |
| `FunctionResultContent` untuk `submit_memo` setelah disetujui | `tool_call_finished`, lalu `done` (`approved`) | Stream dihentikan di sini, sehingga model tidak menulis balasan penutup |
| Keputusan `reject` | `tool_call_finished` untuk `submit_memo`, lalu `done` (`rejected`) | Model tidak dipanggil lagi |
| Keputusan `revise` | `tool_call_finished` untuk `submit_memo`, lalu langkah berikutnya | `CreateResponse(false, "Analis meminta revisi: <komentar>")` |
| Revisi ketiga | `error` (`revision_limit_exceeded`) | |
| Batas 15 panggilan tercapai, model diberi tahu | `tool_call_finished` (`error`, kode `step_limit`) | Tool tidak dijalankan |
| Model tetap meminta tool setelah diberi tahu | `error` (`step_limit_exceeded`) | Middleware memasang `FunctionInvocationContext.Terminate` |
| Token pembatalan | `tool_call_finished` (`error`, kode `cancelled`) untuk tool yang terbuka, lalu `done` (`cancelled`) | |
| `ClientResultException` dari OpenAI | `error` (`model_error`, `retryable: true`) | Klien OpenAI sudah mencoba ulang sendiri |
| MCP tidak bisa dihubungi saat run mulai | `error` (`mcp_unavailable`, `retryable: true`) | |
| Giliran model yang hanya berisi teks | Pesan "Lanjutkan…" ke model | Dianggap narasi. Setelah 3 giliran teks berturut-turut tanpa tool call, `error` (`model_error`). Aturan sama dengan LangGraph |

## Tahap dan bukti (CHANGE-001)

| Event atau field | Asal di adapter |
|---|---|
| `step_started` | `Emitter.Event` menanyakan `InvestigationSteps.Next` sebelum menulis `tool_call_started` atau `approval_required`, dan sesudah menulis `run_started`. Tahap terakhir disimpan di `RunState.Step`, jadi tidak terulang setelah jeda approval. |
| `tool_call_finished.source` | `ToolResultSummarizer` membaca `provider`, `fetched_at`, dan `stale` dari amplop idx yang sudah ia cari untuk ringkasan (`ToolEvidence.Source`). |
| `tool_call_finished.resultPreview` | Teks amplop idx paling dalam, tanpa pembungkus `{"$type":"text","text":…}` dari `McpClientTool` dan tanpa awalan `[toolCallId: …]`, dipotong dengan `ToolEvidence.Preview`. |

Panggilan yang ditutup adapter (`CloseOpenCalls`), batas langkah, dan
exception dari tool tidak membawa `source` maupun `resultPreview`, karena tidak
ada keluaran dari server MCP.

Aturan tahap tidak diambil dari framework. MAF tidak punya konsep tahap
alur kerja, dan model tidak diminta melaporkannya. Tahap ditentukan dari nama
tool, sama persis dengan ketiga adapter lain. Detail dan alasannya ada di
`contracts/CHANGE-001-step-and-evidence.md`.

## Perbedaan dengan kontrak dan cara menyerapnya

1. **`sequence` dan `timestamp`.** MAF tidak punya nomor urut event. Adapter memberi nomor per run, dan nomor itu tersimpan bersama state run supaya berlanjut setelah resume.
2. **Approval untuk semua panggilan dalam satu respons.** `FunctionInvokingChatClient` meminta approval untuk semua tool call dalam satu respons jika salah satunya butuh approval. Akibatnya, tool MCP yang dipanggil bersamaan dengan `submit_memo` juga ikut diminta approval. Adapter menyetujui tool MCP secara otomatis. Jika itu terjadi bersamaan dengan `submit_memo`, persetujuannya disimpan dan dikirim bersama keputusan analis.
3. **Penolakan dan revisi memakai jalur yang sama.** MAF hanya mengenal setuju atau tidak setuju. Revisi dikirim sebagai "tidak setuju" dengan alasan berisi komentar analis. Penolakan akhir tidak dikirim ke model sama sekali.
4. **Batas langkah.** `MaximumIterationsPerRequest` (default 40) direset setiap kali approval dilanjutkan, jadi tidak dipakai sebagai batas ASD. Batas ASD dihitung di middleware dan disimpan di state run.
5. **Bukti memo.** Model tidak otomatis tahu `CallId`. Middleware menambahkan `[toolCallId: ...]` di awal setiap hasil tool, dan validasi memo menolak rujukan `toolCallId` yang tidak ada di run.
6. **`disclaimer`.** Teksnya tetap, jadi adapter mengisinya sendiri alih-alih menolak memo yang memakai parafrase.
7. **Melanjutkan setelah restart.** Session MAF (`SerializeSessionAsync`), `ToolApprovalRequestContent` yang tertunda, dan pembukuan run disimpan sebagai satu file JSON per run di `App_Data/maf-runs`. State ini hanya untuk melanjutkan eksekusi. Sumber kebenaran tetap database aplikasi.
8. **Panggilan paralel saat batas tercapai.** Model bisa meminta beberapa tool sekaligus dalam satu giliran. Semua tool di giliran itu yang melewati batas hanya menerima peringatan. `step_limit_exceeded` baru dipancarkan jika model meminta tool lagi pada giliran berikutnya, yaitu setelah ia membaca peringatan. Giliran dibedakan dengan `FunctionInvocationContext.Iteration` dan nomor pass adapter. Ini ditemukan saat uji live.
9. **Teks setelah approval.** Setelah `submit_memo` disetujui, `FunctionInvokingChatClient` akan meminta balasan penutup dari model. Adapter menghentikan stream begitu hasil `submit_memo` diterima, karena run sudah selesai.
10. **Run yang terputus.** Jika aplikasi mati di tengah stream, session tidak konsisten. Stream berikutnya menutup run dengan `error` (`internal`, `retryable: true`).

## Hal yang tidak terdokumentasi dan sudah dipastikan lewat test

Dokumentasi Microsoft Learn tidak memuat contoh C# untuk approval saat
streaming dan untuk melanjutkan approval dari session yang diserialisasi.
Repo GitHub `microsoft/agent-framework` tidak bisa diakses saat tahap ini
dikerjakan. Perilaku berikut dipastikan oleh `MafAgentClientTests` dengan
pipeline MAF asli dan model tiruan:

- `ToolApprovalRequestContent` muncul di `AgentResponseUpdate.Contents` saat streaming.
- `ToolApprovalRequestContent` bisa diserialisasi sebagai `AIContent` dengan `AIJsonUtilities.DefaultOptions`, lalu dipakai lagi untuk `CreateResponse(...)` setelah session dideserialisasi oleh instance klien baru.
- Function middleware tetap berjalan untuk tool yang disetujui otomatis.

## Run terputus karena restart (CHANGE-003)

Run yang sedang berjalan saat proses berhenti ditutup dengan `error` kode
`run_interrupted` dan `retryable: true`, bukan lagi `internal`. Lihat
`docs/RESTART.md`.
Adapter menulisnya saat stream dibuka untuk run berstatus `Running` dari proses sebelumnya.

## Use case 2: deteksi pola (Tahap 14, CHANGE-002)

`MafAgentClient` meneruskan run dengan `useCase: pattern_detection` ke
`PatternRunner`. Susunannya mengikuti "Protokol worker dan orchestrator" di
`contracts/asd-pattern-detection.md`:

| Bagian | MAF |
|---|---|
| Orchestrator | `PatternOrchestrator` (kode biasa, tanpa model). Paling banyak 3 worker berjalan bersamaan (`SemaphoreSlim`). |
| Worker | Satu `ChatClientAgent` per worker, dengan tool MCP sesuai perannya (`IToolSource.GetToolsAsync(names)`) dan `finish_worker` (`AIFunctionFactory.Create`) |
| Batas langkah worker dan job | Function middleware (`AsBuilder().Use`). Hitungan per worker, dan `JobBudget` untuk tool call serta durasi job. |
| Penyusun laporan | `ChatClientAgent` dengan satu tool `submit_report` (`ApprovalRequiredAIFunction` berisi `PatternReport`). Sesinya diserialisasi saat menunggu analis, sama seperti `submit_memo`. |

| Event | Asal |
|---|---|
| `worker_started`, `worker_finished` | `Emitter.WorkerStarted`/`WorkerFinished`, dipanggil `PatternRunner` sebelum dan sesudah worker berjalan |
| `agentName` | `Emitter.Event(…, agentName)`. Event run dan narasi memakai `orchestrator`; tool call memakai id worker, yang dicatat di `RunState.CallAgents`. |
| `tool_call_*` worker | `FunctionCallContent` dan `FunctionResultContent` dari stream worker. `finish_worker` tidak dipancarkan. |
| `token` | Hanya narasi orchestrator (kalimat dari kode) dan teks penyusun laporan. Teks worker dibuang supaya tidak bercampur. |
| `approval_required.report` | `submit_report` yang lolos validasi |

**Validasi laporan** (`PatternRunner.ValidateReport`). Laporan yang tidak
lolos dikembalikan ke model dengan penjelasan, paling banyak 3 kali, lalu run
berakhir dengan `model_error`. Laporan ditolak bila:

- ada `toolCallId` yang tidak berasal dari worker;
- `clusters` tidak sama dengan klaster orchestrator.

Bagian yang selalu diambil dari kode, menimpa isian model: periode, `counts`,
`leadsNotFollowed`, `limitsReached`, dan `disclaimer`. Daftar yang
dikosongkan model dinormalkan menjadi daftar kosong.

**Thread safety.** Worker berjalan paralel, jadi `Emitter` memakai satu kunci
untuk nomor `sequence`, penggabungan token, dan pasangan tool call.

**Uji live** (25 November 2026, gpt-5-mini, 3 kandidat dari penyaringan MCP
sungguhan):

| Uji | Hasil |
|---|---|
| Job penuh | 3 worker saham berjalan paralel dengan tool call sungguhan, laporan diajukan, lalu disetujui. Tidak ada broker yang diikuti, karena porsi beli bersih broker teratas di bawah ambang 40%. Jalur broker dan jejak baru diuji dengan worker tiruan di `PatternOrchestratorTests`. |
| Batas job 4 tool call | Ketiga worker selesai dengan `limit_reached`. Laporan parsial mencatat "Batas 4 panggilan data per job tercapai.". Dua laporan pertama ditolak validasi dan diperbaiki model. Revisi analis menghasilkan revisi 1, lalu disetujui. |
