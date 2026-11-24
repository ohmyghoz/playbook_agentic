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
(`contracts/CHANGE-001-step-and-evidence.md`). Butir yang berubah merujuk
ke nama file dan method, bukan nomor baris, karena nomor baris lama sudah bergeser.

## Ringkasan

| Bab | Ya | Sebagian | Belum |
|---|---|---|---|
| 10. Transport dan streaming | 3 | 3 | 2 |
| 11. Pola UI/UX agentic | 5 | 5 | 1 |
| 12. Human-in-the-loop | 4 | 4 | 0 |
| 13. State dan durabilitas | 3 | 2 | 3 |

Kekuatan utama yang sudah ada:

- satu kontrak event untuk empat solusi agent (fake, MAF, LangGraph, ADK), dengan contract test yang membuktikannya.
- SSE dengan putar ulang dari database dan menyambung kembali lewat `Last-Event-ID`.
- kartu approval dengan tiga keputusan, jeda dan lanjut yang bertahan setelah restart.
- database aplikasi sebagai sumber kebenaran, terpisah dari state framework.

Kekurangan terbesar:

- hanya ada satu transport (SSE); polling dan SignalR belum ada.
- semua pola UI ada di satu view dan satu file JavaScript, belum dipecah per pola seperti format katalog Bab 11.
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
| Skenario: spinner panjang tanpa kabar | **Ya** | `Details.cshtml`, `investigation.js:69` (`handle`) | Halaman sudah menampilkan narasi, tool call, dan status sejak event pertama. Untuk skenario "sebelum", buku perlu versi tanpa streaming sebagai pembanding (lihat butir berikut). |
| Kenapa request-response klasik MVC tidak cukup | **Sebagian** | `InvestigationController.cs:25` (`Start`, pola POST-redirect) | Belum ada contoh endpoint klasik yang menunggu agent selesai lalu mengembalikan view, misalnya `POST /investigations/sync`. Tanpa itu, buku hanya bisa menjelaskan, belum bisa memperagakan timeout dan halaman beku. |
| SSE, SignalR, polling: konsep dan kode | **Sebagian** | SSE: `InvestigationController.cs:64-74` (`TypedResults.ServerSentEvents`), `:124-133` (`SseItem` dengan `EventId`), `investigation.js:341` (`new EventSource`). Polling: endpoint `events.json?after=` di `InvestigationController.cs:77-83` | SSE lengkap. Polling hanya punya endpoint; belum ada klien JavaScript yang memanggilnya berkala. SignalR belum ada sama sekali (hub, `AddSignalR`, klien JS). |
| Timeout dan proxy | **Sebagian** | `AgentServiceCollectionExtensions.cs` (`HttpClient.Timeout = InfiniteTimeSpan` untuk klien SSE ke service Python), `contracts/agent-service-api.md` (service Python wajib mengirim `: ping` tiap 15 detik) | Endpoint SSE milik aplikasi (`InvestigationService.StreamEventsAsync`, baris 186) belum mengirim ping saat menunggu, misalnya selama model berpikir lama. Di belakang proxy dengan batas idle (IIS, Azure App Service sekitar 230 detik, nginx) koneksi bisa diputus. EventSource akan menyambung ulang tanpa kehilangan event, tapi indikator koneksi berkedip. Belum ada field `retry:`, header `X-Accel-Buffering: no`, atau contoh konfigurasi proxy. |
| Pembatalan dari sisi pengguna | **Ya** | Tombol: `Details.cshtml:32`, `investigation.js:315`. Endpoint: `InvestigationController.cs:105`. Alur: `InvestigationService.StopAsync` → `RunPump.Stop` (`RunPump.cs:37`). Kontrak: `IAgentClient.cs` (token pembatalan), `agent-service-api.md` (`POST /runs/{id}/cancel`) | Sudah lengkap untuk keempat adapter. Untuk buku, perlu ditegaskan bahwa menutup tab tidak membatalkan run (sengaja); hanya tombol Hentikan. |
| Menyambung kembali tanpa kehilangan event | **Ya** | `InvestigationController.cs:68` (`Last-Event-ID`), `InvestigationService.cs:186-222` (berlangganan dulu, baru baca DB), `investigation.js:70` (buang duplikat berdasarkan `sequence`), `HttpSseAgentClient.cs` (menyambung ulang ke service Python dari `sequence` terakhir) | Sudah teruji (test `Sse_replays_from_after...` dan contract test `Streaming_again_while_paused_yields_nothing_new`). |
| Memilih transport | **Belum** | – | Tidak ada perbandingan atau pilihan transport di aplikasi. Butuh polling dan SignalR dulu, lalu tabel pertimbangan (arah komunikasi, proxy, skala, dukungan browser). |
| Demo 10-1: SSE vs polling berdampingan | **Belum** | – | Butuh halaman demo dengan dua panel untuk kasus yang sama: kiri `EventSource`, kanan polling `events.json?after=` tiap N detik, ditambah penghitung jumlah request dan jeda. Endpoint yang dibutuhkan sudah ada. |

Catatan teknis untuk Bab 10: `RunPump.PersistAsync` menyimpan setiap event ke
Azure SQL sebelum event dikirim ke browser. Sejak Tahap 7, satu event butuh
satu perjalanan ke database, bukan tiga. Waktu per event turun dari sekitar
120 ms menjadi sekitar 75 ms. Pengukuran dan alasannya ada di
`docs/NOTES-PERSIST.md`. Ini contoh nyata pertukaran "simpan dulu, baru
siarkan" yang layak dibahas.

## Bab 11: Pola UI/UX Agentic di View

Format katalog Bab 11 meminta partial view dan JavaScript per pola. Saat ini
semua pola ada di satu view (`Details.cshtml`) dan satu file
(`investigation.js`). Isinya sudah per pola (fungsi terpisah per jenis event),
jadi pemecahannya bersifat mekanis.

| Butir | Status | File relevan | Yang kurang |
|---|---|---|---|
| Skenario: agent diam 30 detik terasa rusak | **Sebagian** | Indikator koneksi `Details.cshtml:29`, `investigation.js:345-349`; titik berdenyut untuk tool berjalan (`site.css`, kelas `.tool-call.running .tool-icon`) | Saat model berpikir sebelum token pertama, atau di antara tool call, layar tidak menunjukkan apa-apa. Belum ada indikator "agent sedang berpikir" atau pengatur waktu "tidak ada kabar selama N detik". |
| Teks mengalir + indikator berpikir (`token`) | **Sebagian** | `investigation.js:82-89` (narasi, baris baru per giliran model), penggabungan token sekitar 60 karakter di MAF dan service Python | Teks mengalir sudah ada. Indikator berpikir belum. Bisa dibuat di UI saja (muncul jika lebih dari N detik tanpa event), atau 🔶 dengan event baru seperti `model_call_started`. |
| Visualisasi tool call (`tool_call_started`/`finished`) | **Ya** | `investigation.js:130-170` (`addToolCall`, `finishToolCall`), `Details.cshtml:69-72`, ringkasan bahasa Indonesia dari `ToolResultSummarizer` (MAF) dan `summarizer.py` (Python) | Yang tampil masih nama teknis (`idx_broker`), belum kalimat seperti "Sedang memeriksa broker summary…". Bisa dibuat dengan peta nama tool → label di JavaScript, tanpa mengubah kontrak. |
| Progres multi-langkah | **Sebagian** | Kontrak: event `step_started` dengan lima tahap ASD (CHANGE-001), dipancarkan keempat adapter menurut `InvestigationSteps.cs` / `steps.py`; penghitung `x / 15` di `investigation.js` | Kontrak dan adapter sudah siap sejak Tahap 8. Tampilan daftar tahap di halaman investigasi menyusul di Tahap 10d. |
| Tampilan bukti dan sumber data | **Sebagian** | `renderMemo` (`investigation.js`): temuan bertaut ke kartu tool call; kontrak: `source` dan `resultPreview` di `tool_call_finished` (CHANGE-001); aplikasi menyimpan pratinjau di tabel `ToolResultPreviews` (`ResultPreviewSplitter.cs`) dan menyajikannya lewat `GET /investigations/{id}/tools/{toolCallId}/preview` | Data sudah tersedia di backend sejak Tahap 8. Panel bukti yang menampilkan sumber, waktu data, dan pratinjau menyusul di Tahap 10e. |
| Tombol stop | **Ya** | `Details.cshtml:32`, `investigation.js:315-326` | Belum ada konfirmasi sebelum menghentikan (sengaja tanpa dialog), dan belum ada pesan bahwa event yang sudah masuk tetap tersimpan sebelum tombol ditekan. Pesan itu baru muncul setelah run berhenti. |
| Error yang manusiawi (`error`) | **Ya** | Banner `Details.cshtml:52-56`, `investigation.js:115-121`; pesan bahasa Indonesia dari adapter; tombol "Mulai ulang dengan data yang sama" untuk `retryable: true` (`InvestigationController.cs:39`); kegagalan per tool tampil merah dengan alasan | Error non-retryable belum memberi langkah berikutnya. Error HTTP aplikasi (404, 403, 500) sudah memakai halaman berbahasa Indonesia yang menjelaskan apa yang terjadi dan langkah berikutnya (`HomeController.Error`, `ErrorViewModel.ForStatus`). Respons JSON dari endpoint approval tidak diubah. |
| Menyatukan: halaman investigasi UMA utuh | **Ya** | `Details.cshtml`, `investigation.js`, `History` (`InvestigationController.cs:56`) untuk putar ulang | Sudah diuji di browser untuk keempat provider. |
| Menegosiasikan event tambahan dengan pembuat agent | **Ya** | `contracts/CHANGE-001-step-and-evidence.md` (kebutuhan, alternatif tanpa perubahan kontrak, usulan, dampak per solusi, kompatibilitas ke belakang), `events.schema.json`, `EVENT-MAPPING.md` per solusi, contract test `Steps_move_forward_and_mcp_results_carry_source_and_preview` | Contoh negosiasi tuntas dari kebutuhan sampai contract test lulus untuk keempat adapter. |
| Format katalog: partial view + JavaScript per pola | **Belum** | – | Butuh pemecahan menjadi partial view (misalnya `_Narrative.cshtml`, `_ToolCalls.cshtml`, `_ApprovalCard.cshtml`, `_ErrorBanner.cshtml`) dan modul JavaScript per pola. |
| Demo 11-1 sampai 11-6 (satu per pola) | **Sebagian** | Skenario fake: `FakeAgentOptions` (`Happy`, `ModelError`, `StepLimit`) dan skenario lewat catatan (`scenario=slow`, `scenario=step_limit`) di `FakeAgentClient.cs` | Semua pola bisa diperagakan dengan provider `fake`, tapi di satu halaman. Belum ada halaman atau skenario per pola. Skenario "diam 30 detik" belum ada di fake. |

## Bab 12: Human-in-the-Loop dan Approval Analis

| Butir | Status | File relevan | Yang kurang |
|---|---|---|---|
| Skenario: memo UMA terkirim tanpa persetujuan | **Ya** | `asd-investigator-uma.md` (tool `submit_memo` wajib approval), event `approval_required` | Keempat adapter memaksa approval: MAF lewat `ApprovalRequiredAIFunction`, LangGraph lewat `interrupt()`, ADK lewat Tool Confirmation, fake lewat skrip. |
| Titik henti agent dan kenapa eskalasi butuh manusia | **Ya** | `asd-investigator-uma.md` (bagian Keputusan analis), `ApprovalRequiredPayload` di `Payloads.cs` | – |
| Kartu approval, tiga keputusan | **Ya** | Template `Details.cshtml:95-121`, `addApproval`/`submitDecision` (`investigation.js:172`, `:281`), endpoint `InvestigationController.cs:85-103`, antiforgery | Komentar wajib untuk revisi dan tolak sudah divalidasi di browser dan server. |
| Jeda dan lanjut ujung ke ujung lewat `IAgentClient` | **Ya** | `IAgentClient.ResumeAsync`, `InvestigationService.DecideAsync` (`:87-146`), `RunPump.Start`; contract test `Approve_pauses...`, `Revise_produces...` untuk keempat adapter | Terbukti juga melewati restart service LangGraph di browser. |
| Antrean approval untuk analis | **Sebagian** | Daftar kasus `Index.cshtml:69-100` dengan status "Menunggu keputusan analis" | Belum ada halaman antrean khusus (filter status, urut paling lama menunggu, jumlah), penugasan ke analis, atau penanda kasus yang sedang dibuka orang lain. |
| Kasus tepi: approval tak kunjung datang, analis lain melanjutkan | **Sebagian** | Pengecekan `approvalId` usang (`DecisionResult.StaleApproval`). Dua keputusan bersamaan: `InvestigationService.DecideAsync` merebut kasus dengan update bersyarat (`AwaitingApproval` → `Running`) dalam satu transaksi; hanya satu yang menang, yang lain mendapat `DecisionResult.AlreadyDecided` dan HTTP 409 "Kasus ini sudah diputuskan oleh …", tanpa menyentuh agent. Test: `DecisionConcurrencyTests.cs` | Belum ada batas waktu, pengingat, atau kedaluwarsa approval (ASD sengaja tanpa batas waktu). Kedaluwarsa yang diputuskan agent butuh 🔶. |
| Jejak audit: "siapa menyetujui ini?" | **Sebagian** | Tabel `ApprovalDecisionRecord` (`Entities.cs:56-65`: keputusan, komentar, `DecidedBy`, `DecidedAt`, `ApprovalId`), daftar "Keputusan analis" di `Details.cshtml:76`, event tersimpan di `AgentEventRecord` | `DecidedBy` diambil dari pengguna yang login (`InvestigationController.Approval`), bukan dari isian formulir. Autentikasi masih untuk demo: tiga akun dari konfigurasi `DemoUsers`, masuk tanpa kata sandi (`Auth/DemoUsers.cs`). Belum ada halaman atau ekspor audit, dan keputusan belum masuk aliran event (lihat 🔶 `approval_decided`). |
| Demo 12-1: dua panel saling terhubung | **Sebagian** | `EventBroadcaster.cs` menyiarkan event ke semua koneksi kasus yang sama | Dua tab browser untuk kasus yang sama sudah saling sinkron. State machine kasus sudah ada di satu file (`CaseStateMachine.cs`, dengan test per pasangan status). Belum ada halaman dua panel (sisi agent menunggu, sisi analis memutuskan) dan diagram Mermaid-nya (Tahap 11). |

## Bab 13: State, Sesi, Durabilitas, dan Interaksi Asinkron

| Butir | Status | File relevan | Yang kurang |
|---|---|---|---|
| Skenario: analis menutup browser di tengah penelusuran | **Ya** | `RunPump.cs` (membaca agent di latar belakang, tidak bergantung koneksi browser), putar ulang dari DB saat dibuka lagi | – |
| Pembagian peran: SQL Server = sumber kebenaran, checkpoint = hanya untuk melanjutkan | **Ya** | `CLAUDE.md` (Arsitektur, Catatan keputusan), `RunPump.PersistAsync`; state framework: MAF di `App_Data/maf-runs`, LangGraph `checkpoints.db`, ADK `sessions.db` (di luar `app/`) | – |
| Skema SQL Server: kasus, riwayat event, keputusan approval | **Ya** | `Entities.cs`, `SurveillanceDbContext.cs` (indeks unik `(RunId, Sequence)`, konversi UTC), `Data/Migrations/20261124150620_Initial.cs` | Skrip DDL dalam bentuk `.sql` belum ada; bisa dibuat dengan `dotnet ef migrations script`. ERD belum ada. |
| Model dan repository C# | **Sebagian** | Entitas di `Entities.cs`; akses data langsung lewat `IDbContextFactory` di `InvestigationService` dan `RunPump` | Belum ada pola repository (misalnya `ICaseRepository`, `IEventRepository`). Kalau buku ingin menampilkan repository, perlu dipisahkan dari service. |
| Use case 2: daftar job, status, notifikasi selesai, membuka kembali hasil | **Belum** | – (Tahap 6b) | Belum ada spesifikasi `asd-pattern-detection.md`, background service job, halaman daftar job, halaman detail per worker, atau notifikasi. Progres per worker butuh 🔶 `agentName`. |
| Resume setelah restart | **Sebagian** | `StartupRecovery.cs`, `InvestigationService.RecoverAsync` (`:225`) | Run yang menunggu approval tetap bisa dilanjutkan untuk MAF, LangGraph, dan ADK. Run yang sedang streaming saat aplikasi atau service mati ditutup dengan `error internal`, tidak dilanjutkan. Agent fake kehilangan semua run saat restart. Pembedaan ini perlu dijelaskan sebagai keputusan desain di buku. |
| Retensi dan pembersihan | **Belum** | – | Tidak ada kebijakan umur data maupun pembersihan untuk tabel event, file `App_Data/maf-runs`, atau database service Python (`service.db`, `checkpoints.db`, `sessions.db`). |
| Demo 13-1: daftar job dengan status berjalan | **Belum** | – | Bergantung pada use case 2. |

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
polling, SignalR, partial view per pola, halaman antrean approval, repository,
retensi, dan halaman demo. Perbaikan konkurensi keputusan dan autentikasi
sudah dikerjakan di Tahap 7 tanpa perubahan kontrak.
