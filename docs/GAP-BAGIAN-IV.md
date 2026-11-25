# Gap Bagian IV: Front End Menangani Interaksi Agent

Dokumen ini memetakan outline Bab 10 sampai 13 ke kode yang sudah ada di
`app/` dan `contracts/`. Untuk setiap butir ada status, file yang relevan, dan
apa yang masih kurang supaya aplikasi bisa menjadi contoh di buku.

Status:

- **Ya**: aplikasi sudah bisa dipakai sebagai contoh apa adanya.
- **Sebagian**: ada dasarnya, tapi perlu ditambah supaya contohnya utuh.
- **Belum**: belum ada di aplikasi.

Kondisi kode yang dipetakan: commit `e1ef9c2` (Tahap 5), ditambah contract
test Tahap 6a yang belum di-commit. Nomor baris merujuk ke kondisi itu.

Diperbarui setelah Tahap 7 (25 November 2026): autentikasi demo, keputusan
approval yang aman dari konkurensi, state machine kasus, penyimpanan event yang
lebih cepat, dan halaman error berbahasa Indonesia.

Diperbarui setelah Tahap 8: kontrak event untuk tahap alur kerja dan bukti data
(`contracts/CHANGE-001-step-and-evidence.md`).

Diperbarui setelah Tahap 9: tiga transport, ping SSE dan konfigurasi proxy, halaman
`/lab/transport` dan `/lab/sync`.

Diperbarui setelah Tahap 10: pola UI dipecah per partial dan modul, indikator
berpikir, label tool, progres tahap, panel bukti, dan `/lab/patterns`.

Diperbarui setelah Tahap 11: antrean approval, pengambilan dan pengalihan kasus,
kedaluwarsa approval, garis waktu audit, `/lab/approval`, dan `docs/APPROVAL-STATES.md`.

Diperbarui setelah Tahap 12: repository, `schema.sql` dan ERD, `run_interrupted`
setelah restart (CHANGE-003), dan retensi data.

Diperbarui setelah Tahap 13: use case 2 di aplikasi dengan agent tiruan (CHANGE-002). Butir yang berubah merujuk
ke nama file dan method, bukan nomor baris, karena nomor baris lama sudah bergeser.

## Ringkasan

| Bab | Ya | Sebagian | Belum |
|---|---|---|---|
| 10. Transport dan streaming | 8 | 0 | 0 |
| 11. Pola UI/UX agentic | 11 | 0 | 0 |
| 12. Human-in-the-loop | 8 | 0 | 0 |
| 13. State dan durabilitas | 8 | 0 | 0 |

Kekuatan utama yang sudah ada:

- satu kontrak event untuk empat solusi agent (fake, MAF, LangGraph, ADK), dengan contract test yang membuktikannya.
- SSE dengan putar ulang dari database dan menyambung kembali lewat `Last-Event-ID`.
- kartu approval dengan tiga keputusan, jeda dan lanjut yang bertahan setelah restart.
- database aplikasi sebagai sumber kebenaran, terpisah dari state framework.

Kekurangan terbesar:

- (Tahap 9) tiga transport (SSE, polling, SignalR) sudah ada; lihat `docs/TRANSPORTS.md`.
- use case 2, retensi data, dan notifikasi belum ada.

Butir yang **butuh perubahan kontrak event** ditandai 🔶 dan dirangkum di bagian akhir.

## Peta file

File yang paling sering dirujuk:

| Peran | File |
|---|---|
| Kontrak event | `contracts/events.schema.json` |
| Perilaku agent | `contracts/asd-investigator-uma.md` |
| Kontrak HTTP service Python | `contracts/agent-service-api.md` |
| Antarmuka agent | `app/Surveillance.Agents.Abstractions/IAgentClient.cs`, `AgentEvent.cs`, `Payloads.cs` |
| Controller | `app/Surveillance.Web/Controllers/InvestigationController.cs` |
| View | `app/Surveillance.Web/Views/Investigation/Index.cshtml`, `Details.cshtml` |
| JavaScript | `app/Surveillance.Web/wwwroot/js/investigation.js` |
| Pembaca event di latar belakang | `app/Surveillance.Web/Investigations/RunPump.cs` |
| Logika kasus dan approval | `app/Surveillance.Web/Investigations/InvestigationService.cs` |
| Status kasus dan transisi yang sah | `app/Surveillance.Web/Investigations/CaseStateMachine.cs` |
| Autentikasi demo | `app/Surveillance.Web/Auth/DemoUsers.cs`, `Controllers/AccountController.cs`, `Views/Account/Login.cshtml` |
| Halaman error | `app/Surveillance.Web/Controllers/HomeController.cs` (`Error`), `Models/ErrorViewModel.cs`, `Views/Shared/Error.cshtml` |
| Siaran event ke browser | `app/Surveillance.Web/Investigations/EventBroadcaster.cs` |
| Pemulihan saat start | `app/Surveillance.Web/Investigations/StartupRecovery.cs` |
| Entitas dan DbContext | `app/Surveillance.Web/Data/Entities.cs`, `SurveillanceDbContext.cs`, `Data/Migrations/` |
| Pemilihan adapter | `app/Surveillance.Web/Agents/AgentServiceCollectionExtensions.cs` |
| Agent tiruan | `app/Surveillance.Web/Agents/Fake/FakeAgentClient.cs` |
| Klien HTTP + SSE | `app/Surveillance.Web/Agents/Http/HttpSseAgentClient.cs` |

## Bab 10: Transport dan Streaming ke View

| Butir | Status | File relevan | Yang kurang |
|---|---|---|---|
| Skenario: spinner panjang tanpa kabar | **Ya** | `Details.cshtml`, `investigation.js` (`handle`); pembanding "sebelum": `/lab/sync` | Halaman menampilkan narasi, tool call, dan status sejak event pertama. `/lab/sync` memperagakan versi tanpa streaming. |
| Kenapa request-response klasik MVC tidak cukup | **Ya** | `LabController.Sync` (`POST /lab/sync`, `[RequestTimeout]` dengan kebijakan `lab-sync`), `Views/Lab/Sync.cshtml` | Halaman membeku sampai agent menunggu approval. Skenario lambat melewati batas 20 detik dan berakhir HTTP 504 berbahasa Indonesia, sementara run tetap berjalan di server. Test: `Lab_sync_answers_after_the_run_pauses_and_times_out_when_slow`. |
| SSE, SignalR, polling: konsep dan kode | **Ya** | Antarmuka: `wwwroot/js/transports/transport.js`; implementasi: `transport-sse.js`, `transport-polling.js`, `transport-signalr.js`; server: `InvestigationController.Events` dan `EventsJson`, `Hubs/InvestigationHub.cs` (grup per kasus, `JoinCase` dari `sequence` terakhir), `Hubs/InvestigationHubRelay.cs` (sumbernya `EventBroadcaster`) | Dipilih lewat `Transport:Default` atau `?transport=`. Diuji di browser: satu kasus sampai `done` lewat ketiganya tanpa event hilang atau ganda. Test: `SignalRTransportTests`, `SseAndLabTransportTests`. |
| Timeout dan proxy | **Ya** | `SseKeepAlive.cs` (ping langsung saat terhubung dan setiap 15 detik tanpa event, `retry:` 3000 ms), header `X-Accel-Buffering: no` dan `Cache-Control: no-cache` di `InvestigationController.Events`, `docs/PROXY.md` (IIS, nginx, Azure App Service) | Contoh IIS/ARR di `PROXY.md` belum diuji di server IIS dan ditandai demikian. |
| Pembatalan dari sisi pengguna | **Ya** | Tombol dan catatan "tetap berjalan walau tab ditutup" di `Details.cshtml`; `InvestigationService.StopAsync` → `RunPump.Stop`; kontrak: `IAgentClient` (token pembatalan), `agent-service-api.md` (`POST /runs/{id}/cancel`) | Antarmuka menegaskan bahwa menutup tab tidak menghentikan investigasi; hanya tombol Hentikan. |
| Menyambung kembali tanpa kehilangan event | **Ya** | SSE: `Last-Event-ID`; polling: `after=`; SignalR: `JoinCase(caseId, after)` setelah `onreconnected`; `InvestigationService.StreamEventsAsync` (berlangganan dulu, baru baca DB); `HttpSseAgentClient.cs` | Teruji untuk ketiga transport, termasuk tombol "Putuskan dan sambung ulang" di `/lab/transport`. |
| Memilih transport | **Ya** | `docs/TRANSPORTS.md` (tabel pertimbangan: arah, koneksi, jeda terukur, jumlah request, proxy, library), `TransportOptions.cs` | – |
| Demo 10-1: SSE vs polling berdampingan | **Ya** | `/lab/transport` (`Views/Lab/Transport.cshtml`, `wwwroot/js/lab-transport.js`) | Dua panel untuk kasus yang sama dengan penghitung request, event, duplikat, dan jeda rata-rata, serta pilihan interval polling. Contoh hasil: SSE 1 request dengan jeda 81 ms, polling 14 request dengan jeda 1.083 ms. |

Catatan teknis untuk Bab 10: `RunPump.PersistAsync` menyimpan setiap event ke
Azure SQL sebelum event dikirim ke browser. Sejak Tahap 7, satu event butuh
satu perjalanan ke database, bukan tiga. Waktu per event turun dari sekitar
120 ms menjadi sekitar 75 ms. Pengukuran dan alasannya ada di
`docs/NOTES-PERSIST.md`. Ini contoh nyata pertukaran "simpan dulu, baru
siarkan" yang layak dibahas.

## Bab 11: Pola UI/UX Agentic di View

Sejak Tahap 10, setiap pola punya satu partial view dan satu modul JavaScript
(`docs/PATTERNS.md`), dan setiap pola bisa diperagakan sendiri di `/lab/patterns`.

| Butir | Status | File relevan | Yang kurang |
|---|---|---|---|
| Skenario: agent diam 30 detik terasa rusak | **Ya** | Indikator berpikir (`_ThinkingIndicator.cshtml`, `thinking-indicator.js`); skenario fake `scenario=silent_30s`; `/lab/patterns` bagian Indikator berpikir | Diuji di browser: indikator muncul setelah 4 detik, lalu teksnya berubah setelah 15 detik dan menegaskan bahwa investigasi tetap berjalan bila halaman ditutup. |
| Teks mengalir + indikator berpikir (`token`) | **Ya** | `_Narrative.cshtml` + `narrative.js`; `_ThinkingIndicator.cshtml` + `thinking-indicator.js`; penggabungan token sekitar 60 karakter di MAF dan service Python | Indikator berpikir tanpa perubahan kontrak, sesuai Keputusan kontrak. |
| Visualisasi tool call (`tool_call_started`/`finished`) | **Ya** | `_ToolCalls.cshtml` + `tool-calls.js` (label ramah seperti "Sedang memeriksa broker summary…", nama teknis tetap terlihat), ringkasan dari `ToolResultSummarizer` dan `summarizer.py` | – |
| Progres multi-langkah | **Ya** | `_StepProgress.cshtml` + `step-progress.js`, digerakkan `step_started` (CHANGE-001) | Tahap yang dilompati ditandai "dilewati", dan akhir run menandai tahap terakhir. |
| Tampilan bukti dan sumber data | **Ya** | `_Evidence.cshtml` + `evidence.js`; tautan `data-evidence-call` dari `approval-card.js`; endpoint `GET /investigations/{id}/tools/{toolCallId}/preview` | Panel menampilkan tool, sumber data, waktu data dalam WIB, ringkasan, dan data mentah yang diambil saat panel dibuka. Diuji di browser. |
| Tombol stop | **Ya** | `_StopButton.cshtml` + `stop-button.js` | Teks bantuan "Data yang sudah masuk tetap tersimpan" terlihat sebelum tombol ditekan. Tombol hilang setelah run berakhir. |
| Error yang manusiawi (`error`) | **Ya** | `_ErrorBanner.cshtml` + `error-banner.js`; halaman error HTTP berbahasa Indonesia (Tahap 7) | Galat yang tidak bisa diulang memberi langkah berikutnya per `code`. Galat yang bisa diulang diberi tombol mulai ulang. |
| Menyatukan: halaman investigasi UMA utuh | **Ya** | `Views/Investigation/Details.cshtml` merangkai delapan partial; `agent-ui.js` sebagai host; `History` untuk putar ulang | Diuji di browser setelah dipecah: alur langsung sampai approval dan `done`, bukti, serta putar ulang. |
| Menegosiasikan event tambahan dengan pembuat agent | **Ya** | `contracts/CHANGE-001-step-and-evidence.md` (kebutuhan, alternatif tanpa perubahan kontrak, usulan, dampak per solusi, kompatibilitas ke belakang), `events.schema.json`, `EVENT-MAPPING.md` per solusi, contract test `Steps_move_forward_and_mcp_results_carry_source_and_preview` | Contoh negosiasi tuntas dari kebutuhan sampai contract test lulus untuk keempat adapter. |
| Format katalog: partial view + JavaScript per pola | **Ya** | `Views/Shared/Patterns/_*.cshtml`, `wwwroot/js/patterns/*.js`, `docs/PATTERNS.md` (tabel pola, event, partial, modul, implikasi backend) | – |
| Demo 11-1 sampai 11-6 (satu per pola) | **Ya** | `/lab/patterns` (`Views/Lab/Patterns.cshtml`, `wwwroot/js/lab-patterns.js`): delapan bagian, masing-masing memutar skenario fake untuk pola itu saja | Memakai polling tiap 1 detik karena batas enam koneksi HTTP/1.1 (`docs/TRANSPORTS.md`). |

## Bab 12: Human-in-the-Loop dan Approval Analis

| Butir | Status | File relevan | Yang kurang |
|---|---|---|---|
| Skenario: memo UMA terkirim tanpa persetujuan | **Ya** | `asd-investigator-uma.md` (tool `submit_memo` wajib approval), event `approval_required` | Keempat adapter memaksa approval: MAF lewat `ApprovalRequiredAIFunction`, LangGraph lewat `interrupt()`, ADK lewat Tool Confirmation, fake lewat skrip. |
| Titik henti agent dan kenapa eskalasi butuh manusia | **Ya** | `asd-investigator-uma.md` (bagian Keputusan analis), `ApprovalRequiredPayload` di `Payloads.cs` | – |
| Kartu approval, tiga keputusan | **Ya** | `_ApprovalCard.cshtml` + `approval-card.js`, `InvestigationController.Approval`, antiforgery | Komentar wajib untuk revisi dan tolak divalidasi di browser dan server. Draf yang diganti tetap terlihat bersama keputusan dan komentar yang menggantinya (riwayat versi memo). |
| Jeda dan lanjut ujung ke ujung lewat `IAgentClient` | **Ya** | `IAgentClient.ResumeAsync`, `InvestigationService.DecideAsync` (`:87-146`), `RunPump.Start`; contract test `Approve_pauses...`, `Revise_produces...` untuk keempat adapter | Terbukti juga melewati restart service LangGraph di browser. |
| Antrean approval untuk analis | **Ya** | `/approvals` (`ApprovalsController`, `Views/Approvals/Index.cshtml`): jumlah draf, urut dari yang paling lama menunggu, lama menunggu, batas waktu, penanda "Hampir kedaluwarsa", pemegang kasus, dan alihkan untuk supervisor; `CaseClaims.cs`, `_CaseClaim.cshtml` + `case-claim.js` | Pengambilan kasus lepas sendiri setelah 15 menit tanpa aktivitas. |
| Kasus tepi: approval tak kunjung datang, analis lain melanjutkan | **Ya** | Kedaluwarsa oleh aplikasi: `ApprovalExpiryService.cs` (batas dari `Approval:ExpiryHours`, status `Expired`, alasan tercatat, run dihentikan lewat jalur tombol Hentikan); analis lain: pengambilan dan pengalihan kasus (`CaseClaims.cs`); konkurensi: `DecideAsync` (Tahap 7) | Tanpa perubahan kontrak, sesuai Keputusan kontrak. Diagram dan aturan menang-kalah ada di `docs/APPROVAL-STATES.md`. |
| Jejak audit: "siapa menyetujui ini?" | **Ya** | `/investigations/{id}/audit` dan ekspor `audit.json` (`AuditTimeline.cs`, `Views/Investigation/Audit.cshtml`): event agent, keputusan analis, serta tindakan aplikasi (ambil, lepas, alihkan, kedaluwarsa) dalam satu urutan waktu; tabel `CaseAuditRecords` | Autentikasi masih untuk demo (Tahap 7). |
| Demo 12-1: dua panel saling terhubung | **Ya** | `/lab/approval` (`Views/Lab/Approval.cshtml`): sisi agent (tahap, narasi, panggilan data) dan sisi analis (ambil kasus, kartu approval) untuk kasus yang sama; kasus Lab kedaluwarsa setelah 2 menit; `docs/APPROVAL-STATES.md` (diagram Mermaid yang cocok dengan `CaseStateMachine.cs`) | Diuji di browser: revisi, persetujuan, dan kedaluwarsa. |

## Bab 13: State, Sesi, Durabilitas, dan Interaksi Asinkron

| Butir | Status | File relevan | Yang kurang |
|---|---|---|---|
| Skenario: analis menutup browser di tengah penelusuran | **Ya** | `RunPump.cs` (membaca agent di latar belakang, tidak bergantung koneksi browser), putar ulang dari DB saat dibuka lagi | – |
| Pembagian peran: SQL Server = sumber kebenaran, checkpoint = hanya untuk melanjutkan | **Ya** | `CLAUDE.md` (Arsitektur, Catatan keputusan), `RunPump.PersistAsync`; state framework: MAF di `App_Data/maf-runs`, LangGraph `checkpoints.db`, ADK `sessions.db` (di luar `app/`) | – |
| Skema SQL Server: kasus, riwayat event, keputusan approval | **Ya** | `Entities.cs`, `SurveillanceDbContext.cs`, `Data/Migrations/`; `docs/schema.sql` (dibuat dengan `dotnet ef migrations script --idempotent`); `docs/ERD.md` (Mermaid) | – |
| Model dan repository C# | **Ya** | `Data/Repositories/` (`ICaseRepository`, `IEventRepository`, `IApprovalRepository`, `ISettingsRepository`, implementasi EF di `EfRepositories.cs`); dipakai `InvestigationService`, `RunPump`, `CaseClaims`, `ApprovalExpiryService`, `AuditTimeline`, `AgentSelection`, `RetentionService` | `RepositoryBoundaryTests` memastikan tidak ada `DbContext` di luar folder `Data` dan `Program.cs`. |
| Use case 2: daftar job, status, notifikasi selesai, membuka kembali hasil | **Ya** | `contracts/asd-pattern-detection.md`, `contracts/CHANGE-002-multi-agent.md`; `Patterns/CandidateScreening.cs` (penyaringan awal sebagai kode biasa), `Patterns/PatternJobRunner.cs` (job di background service), `Patterns/JobNotifier.cs` (SignalR ke pengguna yang memulai job); `/jobs` dan `/jobs/{id}` (`JobsController`, `_WorkerPanels.cshtml` + `worker-panels.js`, laporan di `approval-card.js`); penanda di navigasi (`job-notifications.js`) | Tahap 13 dengan agent tiruan; Tahap 14 di MAF (`agents/maf/PatternRunner.cs`), LangGraph (`agents/langgraph/app/pattern_graph.py`), dan ADK (`agents/adk/app/pattern_agents.py`). Ketiganya diuji live dari penyaringan sampai laporan disetujui, dan dengan batas 4 panggilan data menghasilkan laporan parsial. |
| Resume setelah restart | **Ya** | `StartupRecovery.cs`, `InvestigationService.RecoverAsync`, `RunPump` (`afterRestart`), kode error `run_interrupted` (`contracts/CHANGE-003-run-interrupted.md`), `docs/RESTART.md` (tabel per solusi dan alasan desain) | Run yang menunggu approval dilanjutkan. Run yang terputus di tengah langkah ditutup dengan `run_interrupted` yang bisa diulang, dan halaman menawarkan mulai ulang. |
| Retensi dan pembersihan | **Ya** | `RetentionService.cs` (pratinjau 30 hari, penggabungan token setelah run selesai, event 90 hari, file `maf-runs` 30 hari), `docs/RETENTION.md` (termasuk pembersihan database service Python) | Pembersihan database service Python didokumentasikan, belum dijadwalkan otomatis. |
| Demo 13-1: daftar job dengan status berjalan | **Ya** | `/jobs` (daftar, status, "Buka hasil"), `/jobs/{id}` (satu panel per worker, laporan untuk disetujui, tombol hentikan) | Diuji di browser: penyaringan dengan data MCP sungguhan (10 kandidat), lima worker termasuk satu jejak baru pada kedalaman 1, pemberitahuan di halaman lain, lalu approval. |

## Butir yang butuh perubahan kontrak event 🔶

Perubahan `contracts/events.schema.json` atau `contracts/agent-service-api.md`
harus disetujui dulu (aturan di `CLAUDE.md`). Butir berikut diurutkan dari yang
paling dibutuhkan buku.

| Kebutuhan | Bab | Perubahan yang diusulkan | Bisa tanpa perubahan kontrak? |
|---|---|---|---|
| Progres per worker di use case 2 | 13 | Field opsional `agentName` di semua event (sudah direncanakan di Tahap 6) | Tidak |
| Indikator agent sedang berpikir | 11 | Event baru, misalnya `model_call_started` (payload kosong atau nomor giliran) | Ya, dengan pendekatan UI: tampilkan indikator jika lebih dari N detik tanpa event. Tidak setepat event dari agent. |
| Progres multi-langkah | 11 | ✅ Diterapkan di Tahap 8: event `step_started` (CHANGE-001) | – |
| Bukti dan sumber data mentah | 11 | ✅ Diterapkan di Tahap 8: `source` dan `resultPreview` di `tool_call_finished`; data mentah lewat endpoint aplikasi, bukan endpoint service agent (CHANGE-001) | – |
| Keputusan analis di aliran event | 12 | Event `approval_decided` (keputusan, `decidedBy`, komentar) supaya putar ulang dan audit lengkap dari satu aliran | Ya, aplikasi bisa menggabungkan event dengan tabel `ApprovalDecisionRecord` saat menampilkan. Dengan event baru, urutan waktunya lebih jelas. |
| Approval kedaluwarsa yang diputuskan agent | 12 | Kode error baru, misalnya `approval_expired`, atau `outcome: expired` di `done` | Ya, jika kedaluwarsa diputuskan aplikasi: aplikasi cukup memanggil pembatalan, dan hasilnya `done(cancelled)`. |
| Label tool yang ramah | 11 | Field opsional `displayName` di `tool_call_started` | Ya, dengan peta nama tool ke label di JavaScript. Disarankan tanpa perubahan kontrak. |

Hal yang **tidak** butuh perubahan kontrak: ping keep-alive di SSE aplikasi,
partial view per pola, halaman antrean approval, repository,
retensi, dan halaman demo. Perbaikan konkurensi keputusan dan autentikasi
sudah dikerjakan di Tahap 7 tanpa perubahan kontrak.
