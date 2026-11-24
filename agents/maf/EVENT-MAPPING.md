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
