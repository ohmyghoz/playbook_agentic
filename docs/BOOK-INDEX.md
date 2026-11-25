# Indeks buku: Bagian IV, Bab 10 sampai 13

Dokumen ini memetakan setiap butir outline Bab 10 sampai 13 ke file yang menjadi
contohnya dan halaman yang memperagakannya. Status dan rincian setiap butir ada di
`docs/GAP-BAGIAN-IV.md`; dokumen ini hanya menjawab "contohnya di mana, dan bisa
dilihat di mana".

## Cara memperagakan

Semua peragaan berjalan dengan provider `fake`, tanpa memanggil model:

1. Jalankan aplikasi dengan `Agent:Provider` = `fake`, atau pilih "fake" di `/settings`.
2. Masuk di `/account/login` dengan salah satu akun contoh: dua analis, satu supervisor.
3. Buka halaman di kolom "Halaman peragaan".

Halaman di bawah `/lab` hanya aktif saat solusi aktif adalah `fake`.

Jalur file di tabel relatif terhadap `app/Surveillance.Web/`, kecuali yang diawali
`contracts/`, `docs/`, atau `agents/`.

## Bab 10: transport dan streaming ke view

| Butir | File contoh | Halaman peragaan |
|---|---|---|
| Skenario: spinner panjang tanpa kabar | `Controllers/LabController.cs` (`Sync`), `Views/Lab/Sync.cshtml` | `/lab/sync` |
| Kenapa request-response klasik MVC tidak cukup | `LabController.Sync` dengan kebijakan `[RequestTimeout]` `lab-sync` | `/lab/sync`: halaman beku sampai agent selesai, lalu timeout |
| SSE, SignalR, polling: konsep dan kode | `wwwroot/js/transports/transport.js` (antarmuka), `transport-sse.js`, `transport-polling.js`, `transport-signalr.js`; server: `InvestigationController.Events` dan `EventsJson`, `Hubs/InvestigationHub.cs`, `Hubs/InvestigationHubRelay.cs` | `/investigations/{id}?transport=sse`, `?transport=polling`, `?transport=signalr` |
| Timeout dan proxy | `Investigations/SseKeepAlive.cs` (ping, `retry:`), header di `InvestigationController.Events`, `docs/PROXY.md` | `/investigations/{id}`: ping terlihat di tab Network browser |
| Pembatalan dari sisi pengguna | `Views/Shared/Patterns/_StopButton.cshtml`, `wwwroot/js/patterns/stop-button.js`, `InvestigationService.StopAsync`, `RunPump.Stop`; `contracts/agent-service-api.md` (`POST /runs/{id}/cancel`) | `/investigations/{id}`, tombol Hentikan; `/lab/patterns` bagian tombol stop |
| Menutup tab tidak menghentikan investigasi | `Investigations/RunPump.cs`, catatan di `Views/Investigation/Details.cshtml` | `/investigations/{id}`: tutup tab, buka lagi |
| Menyambung kembali tanpa kehilangan event | `InvestigationService.StreamEventsAsync` (berlangganan dulu, baru baca DB); `Last-Event-ID` (SSE), `after=` (polling), `JoinCase(caseId, after)` (SignalR) | `/investigations/{id}` dengan ketiga transport: muat ulang halaman di tengah run |
| Memilih transport | `docs/TRANSPORTS.md`, `Investigations/TransportOptions.cs` | `/lab/transport` |
| Demo 10-1: SSE vs polling berdampingan | `Views/Lab/Transport.cshtml`, `wwwroot/js/lab-transport.js` | `/lab/transport` |

## Bab 11: pola UI/UX agentic di view

Setiap pola punya satu partial view di `Views/Shared/Patterns/` dan satu modul
JavaScript di `wwwroot/js/patterns/`. Tabel lengkapnya ada di `docs/PATTERNS.md`.

| Butir | File contoh | Halaman peragaan |
|---|---|---|
| Skenario: agent diam 30 detik terasa rusak | `_ThinkingIndicator.cshtml`, `thinking-indicator.js`; skenario fake `scenario=silent_30s` | `/lab/patterns`, bagian indikator berpikir |
| Teks mengalir dan indikator berpikir (`token`) | `_Narrative.cshtml` + `narrative.js`; `_ThinkingIndicator.cshtml` + `thinking-indicator.js` | `/lab/patterns`, bagian narasi |
| Visualisasi tool call | `_ToolCalls.cshtml` + `tool-calls.js` (label ramah dan nama teknis); ringkasan dari `agents/maf/ToolResultSummarizer.cs` dan `agents/*/app/summarizer.py` | `/lab/patterns`, bagian tool call |
| Progres multi-langkah | `_StepProgress.cshtml` + `step-progress.js`; `contracts/CHANGE-001-step-and-evidence.md` | `/lab/patterns`, bagian progres langkah |
| Tampilan bukti dan sumber data | `_Evidence.cshtml` + `evidence.js`; endpoint `GET /investigations/{id}/tools/{toolCallId}/preview`; `app/Surveillance.Agents.Abstractions/ToolEvidence.cs` | `/lab/patterns`, bagian bukti; dari temuan memo di `/investigations/{id}` |
| Tombol stop | `_StopButton.cshtml` + `stop-button.js` | `/lab/patterns`, bagian tombol stop |
| Error yang manusiawi (`error`) | `_ErrorBanner.cshtml` + `error-banner.js`; `Views/Shared/Error.cshtml` untuk error HTTP | `/lab/patterns`, bagian banner error; `/error/404` |
| Progres per worker (use case 2) | `_WorkerPanels.cshtml` + `worker-panels.js`; `contracts/CHANGE-002-multi-agent.md` | `/jobs/{id}` |
| Menyatukan: halaman investigasi UMA utuh | `Views/Investigation/Details.cshtml` merangkai partial; `wwwroot/js/patterns/agent-ui.js` sebagai host | `/investigations/{id}`; putar ulang di `/investigations/{id}/history` |
| Menegosiasikan event tambahan dengan pembuat agent | `contracts/CHANGE-001-step-and-evidence.md`, `CHANGE-002-multi-agent.md`, `CHANGE-003-run-interrupted.md`; `contracts/events.schema.json`; `agents/*/EVENT-MAPPING.md`; contract test di `app/Surveillance.Web.Tests/Contract/` | – (dokumen); hasilnya terlihat di `/lab/patterns` |
| Format katalog: partial view dan JavaScript per pola | `Views/Shared/Patterns/_*.cshtml`, `wwwroot/js/patterns/*.js`, `docs/PATTERNS.md` | `/lab/patterns` |
| Demo 11-1 sampai 11-6 (satu per pola) | `Views/Lab/Patterns.cshtml`, `wwwroot/js/lab-patterns.js` | `/lab/patterns` |

## Bab 12: human-in-the-loop dan approval analis

| Butir | File contoh | Halaman peragaan |
|---|---|---|
| Skenario: memo UMA terkirim tanpa persetujuan | `contracts/asd-investigator-uma.md` (`submit_memo` wajib approval), event `approval_required` | `/investigations/{id}`: run berhenti di kartu approval |
| Titik henti agent dan kenapa eskalasi butuh manusia | `contracts/asd-investigator-uma.md` (Keputusan analis), `contracts/asd-pattern-detection.md` (approval laporan), `ApprovalRequiredPayload` di `app/Surveillance.Agents.Abstractions/Payloads.cs` | `/investigations/{id}`, `/jobs/{id}` |
| Kartu approval, tiga keputusan | `_ApprovalCard.cshtml` + `approval-card.js`, `InvestigationController.Approval` | `/lab/patterns`, bagian kartu approval; `/investigations/{id}` |
| Putaran revisi dan riwayat versi memo | `InvestigationService.DecideAsync`, `approval-card.js` (setiap draf tampil sebagai kartu sendiri dengan nomor revisinya) | `/investigations/{id}`: pilih Minta revisi |
| Jeda dan lanjut ujung ke ujung lewat `IAgentClient` | `app/Surveillance.Agents.Abstractions/IAgentClient.cs` (`ResumeAsync`), `InvestigationService.DecideAsync`, `RunPump.Start`; contract test `Approve_pauses...`, `Revise_produces...` | `/lab/approval` |
| Konkurensi keputusan | `InvestigationService.DecideAsync` (update bersyarat); `app/Surveillance.Web.Tests/DecisionConcurrencyTests.cs` | Dua analis di dua browser memutuskan kasus yang sama |
| Status kasus sebagai state machine | `Investigations/CaseStateMachine.cs`, `docs/APPROVAL-STATES.md` (Mermaid) | – (dokumen); status terlihat di `/investigations` |
| Antrean approval untuk analis | `Controllers/ApprovalsController.cs`, `Views/Approvals/Index.cshtml` | `/approvals` |
| Pengambilan kasus dan pengalihan oleh supervisor | `Investigations/CaseClaims.cs`, `_CaseClaim.cshtml` + `case-claim.js` | `/approvals` dan `/investigations/{id}` dengan dua akun berbeda |
| Kasus tepi: approval tak kunjung datang | `Investigations/ApprovalExpiryService.cs`, `ApprovalOptions.cs` | `/lab/approval` (kasus Lab kedaluwarsa setelah 2 menit); penanda "Hampir kedaluwarsa" di `/approvals` |
| Jejak audit: "siapa menyetujui ini?" | `Investigations/AuditTimeline.cs`, `Views/Investigation/Audit.cshtml`, tabel `CaseAuditRecords` | `/investigations/{id}/audit`, ekspor `/investigations/{id}/audit.json` |
| Demo 12-1: dua panel saling terhubung | `Views/Lab/Approval.cshtml` | `/lab/approval` |

## Bab 13: state, sesi, durabilitas, dan interaksi asinkron

| Butir | File contoh | Halaman peragaan |
|---|---|---|
| Skenario: analis menutup browser di tengah penelusuran | `Investigations/RunPump.cs`, `Patterns/PatternJobRunner.cs` | `/jobs/{id}` atau `/investigations/{id}`: tutup browser, buka lagi |
| Pembagian peran: SQL Server sebagai sumber kebenaran, checkpoint hanya untuk melanjutkan | `RunPump.PersistAsync`; state framework: `App_Data/maf-runs` (MAF), `agents/langgraph/data/checkpoints.db`, `agents/adk/data/sessions.db`; `CLAUDE.md` (Arsitektur) | – (arsitektur) |
| Skema SQL Server: kasus, riwayat event, keputusan approval | `Data/Entities.cs`, `Data/SurveillanceDbContext.cs`, `Data/Migrations/`, `docs/schema.sql`, `docs/ERD.md` | – (skema) |
| Model dan repository C# | `Data/Repositories/` (`ICaseRepository`, `IEventRepository`, `IApprovalRepository`, `ISettingsRepository`, `EfRepositories.cs`); `RepositoryBoundaryTests` di `app/Surveillance.Web.Tests/ArchitectureTests.cs` | – (kode) |
| Biaya menyimpan setiap event | `RunPump.PersistAsync`, `docs/NOTES-PERSIST.md` (ukuran sebelum dan sesudah) | – (catatan pengukuran) |
| Resume setelah restart | `Investigations/StartupRecovery.cs`, `InvestigationService.RecoverAsync`, `RunPump` (`afterRestart`), `contracts/CHANGE-003-run-interrupted.md`, `docs/RESTART.md` | `/investigations/{id}`: matikan aplikasi di tengah run, nyalakan lagi, lalu pilih Jalankan ulang |
| Retensi dan pembersihan | `Investigations/RetentionService.cs`, `docs/RETENTION.md` | – (layanan latar belakang); diuji di `DurabilityTests.cs` |
| Use case 2: job asinkron, status, notifikasi selesai, membuka kembali hasil | `contracts/asd-pattern-detection.md`; `Patterns/CandidateScreening.cs`, `Patterns/PatternJobRunner.cs`, `Patterns/JobNotifier.cs`; `Controllers/JobsController.cs`; `wwwroot/js/job-notifications.js` | `/jobs`, `/jobs/{id}`; penanda di navigasi saat berada di halaman lain |
| Orchestrator dan worker di tiga solusi | `agents/maf/PatternOrchestrator.cs` dan `PatternRunner.cs`; `agents/langgraph/app/pattern_orchestrator.py` dan `pattern_graph.py`; `agents/adk/app/pattern_agents.py` dan `runs.py` (`_execute_pattern`) | `/jobs` setelah memilih solusi di `/settings` (butuh model sungguhan) |
| Demo 13-1: daftar job dengan status berjalan | `Views/Jobs/Index.cshtml`, `Views/Jobs/Details.cshtml` | `/jobs`, `/jobs/{id}` |

## Bukti bahwa kontrak berlaku untuk semua solusi

| Hal | File |
|---|---|
| Contract test use case 1 | `app/Surveillance.Web.Tests/Contract/AgentContractTests.cs` |
| Contract test use case 2 | `app/Surveillance.Web.Tests/Contract/AgentContractTests.PatternDetection.cs` |
| Model tiruan bersama | `app/Surveillance.Web.Tests/Contract/scenarios.json`, `ScenarioChatClient.cs`, `agents/*/app/scripted.py` |
| Hasil dan perbandingan tiga solusi | `contracts/COMPATIBILITY.md` |
| Controller dan view tidak merujuk framework agent | `app/Surveillance.Web.Tests/ArchitectureTests.cs` |
