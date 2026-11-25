# Kompatibilitas adapter terhadap kontrak

Dokumen ini mencatat hasil contract test `IAgentClient` terhadap keempat adapter:
fake, MAF, LangGraph, dan Google ADK, untuk kedua use case. Semua adapter diuji
dengan skenario dan pemeriksaan yang sama, sehingga hasilnya bisa dibandingkan
langsung.

Ringkasnya:

- **Use case 1, offline (model tiruan): 40 dari 40 lulus.** Sepuluh skenario untuk keempat adapter, termasuk tahap dan bukti dari CHANGE-001.
- **Use case 2, offline: 28 dari 28 lulus.** Tujuh skenario deteksi pola untuk keempat adapter (CHANGE-002). Lihat [Use case 2: deteksi pola](#use-case-2-deteksi-pola).
- **Use case 2, live:** ketiga solusi menjalankan job dari penyaringan sampai laporan disetujui, dan batas 4 panggilan data menghasilkan laporan parsial. Uji ini dilakukan dari browser, bukan test otomatis.
- **Live (model sungguhan): 12 dari 12 lulus pada uji terakhir** (Tahap 8, setelah CHANGE-001). Pada uji Tahap 6a, satu test LangGraph gagal sekali dan tidak muncul lagi dalam tiga uji ulang. Penjelasannya ada di bagian [Kegagalan yang tercatat](#kegagalan-yang-tercatat).
- Tidak ada adapter yang melanggar `contracts/events.schema.json` atau aturan urutan event di ASD.

Tanggal uji: 25 November 2026.

## Cara menjalankan

Contract test ada di `app/Surveillance.Web.Tests/Contract/`.

| Bagian | Isi |
|---|---|
| `AgentContractTests.cs` | Sepuluh skenario offline use case 1, ditulis sekali untuk semua adapter |
| `AgentContractTests.PatternDetection.cs` | Tujuh skenario offline use case 2 (deteksi pola), ditulis sekali untuk semua adapter |
| `AdapterContractTests.cs` | Turunan per adapter, ditambah test `Live` |
| `scenarios.json` | Giliran model tiruan, dipakai bersama oleh MAF, LangGraph, dan ADK |
| `PythonService.cs` | Menyalakan service LangGraph atau ADK sebagai proses uvicorn sungguhan di port bebas |
| `ScenarioChatClient.cs` | Model tiruan untuk MAF yang membaca `scenarios.json` |

Test offline jalan bersama test lain:

```powershell
dotnet test app/Surveillance.Web.Tests
```

Test `Live` butuh server MCP idx yang menyala (`scripts/start-mcp.ps1`) serta
API key di user-secrets (MAF), `agents/langgraph/.env`, dan `agents/adk/.env`.
Setiap test `Live` menyalakan service Python-nya sendiri.

```powershell
$env:RUN_LIVE_TESTS = '1'
dotnet test app/Surveillance.Web.Tests --filter "Category=Live"
```

### Cara kerja model tiruan

- **Pemilihan skenario.** Model tiruan membaca `scenario=<nama>` dari catatan analis. Catatan ini ikut masuk ke instruksi sistem.
- **Pemilihan skenario use case 2.** Worker dan penyusun laporan tidak menerima catatan analis. Model tiruan mengenali perannya dari instruksi ("Anda worker saham", "Anda penyusun laporan", dan seterusnya), lalu memakai skenario `pattern_<peran>`. Bila ada `pattern_<peran>_<target>`, skenario itu yang dipakai. `{target}` di giliran diganti target worker, supaya ID tool call unik antar-worker yang berjalan bersamaan.
- **Pemilihan giliran.** Model memakai giliran pertama yang ID tool call-nya belum ada di percakapan. Karena tidak menyimpan state, cara ini tetap benar setelah service dinyalakan ulang, dan tidak terpengaruh pesan internal yang ditambahkan framework.
- **Agent fake** memakai skrip sendiri, tapi memilih skenarionya dari catatan yang sama (`scenario=slow`, `scenario=step_limit`).
- **Service Python** masuk ke mode ini lewat env `AGENT_SCRIPT=<path scenarios.json>` (`app/scripted.py`). Dalam mode ini, tool MCP diganti tool tiruan dan service tidak butuh API key.

## Hasil offline

Setiap skenario diperiksa dengan `EventAssertions.AssertWellFormedRun`:
- semua event valid terhadap skema
- `sequence` mulai dari 1 tanpa lompatan
- `run_started` di awal
- setiap tool call berpasangan
- run diakhiri tepat satu `done` atau `error`, tanpa tool call yang masih terbuka
- `step_started` tepat setelah `run_started` dengan `measure`, tahap hanya maju, `assess` sebelum `submit_memo`, `memo` sebelum `approval_required`
- setiap hasil tool MCP punya `source`, dan `resultPreview` tidak lebih dari 8192 byte UTF-8

| Skenario | Fake | MAF | LangGraph | ADK |
|---|---|---|---|---|
| Approval menjeda, lalu `done` (`approved`) | ✅ | ✅ | ✅ | ✅ |
| Tolak, lalu `done` (`rejected`) | ✅ | ✅ | ✅ | ✅ |
| Revisi menghasilkan draf `revision: 1`, lalu disetujui | ✅ | ✅ | ✅ | ✅ |
| Revisi ketiga, lalu `error` (`revision_limit_exceeded`) | ✅ | ✅ | ✅ | ✅ |
| Batal saat menunggu approval: `submit_memo` ditutup `cancelled`, lalu `done` (`cancelled`) | ✅ | ✅ | ✅ | ✅ |
| Batal saat agent berjalan, lalu `done` (`cancelled`) | ✅ | ✅ | ✅ | ✅ |
| Batas langkah: tepat 15 tool dijalankan, lalu `error` (`step_limit_exceeded`) | ✅ | ✅ | ✅ | ✅ |
| `approvalId` usang ditolak (`InvalidOperationException`) | ✅ | ✅ | ✅ | ✅ |
| Stream ulang saat menunggu tidak menghasilkan event ganda | ✅ | ✅ | ✅ | ✅ |
| Tahap maju berurutan; hasil MCP membawa `source` dan `resultPreview`; pratinjau `idx_history` dipotong di 8192 byte (CHANGE-001) | ✅ | ✅ | ✅ | ✅ |

LangGraph dan ADK diuji lewat jalur yang sama seperti di produksi: klien C#
`HttpSseAgentClient` → HTTP dan SSE → service Python sungguhan →
framework sungguhan (graph, checkpointer, Tool Confirmation, session service).
Hanya model dan tool MCP yang diganti.

## Hasil live

Model sungguhan dan server MCP idx sungguhan, saham BBCA tanggal 24 November 2026.
Sejak Tahap 8, alur penuh juga memeriksa bahwa tahap `assess` muncul dan setiap
hasil MCP membawa `resultPreview`; semua pemeriksaan skema dan urutan tahap
berlaku juga untuk run live.

| Skenario | MAF (gpt-5-mini) | LangGraph (qwen3-30b-a3b-instruct-2507; diulang dengan qwen3.8-27b) | ADK (gemini-3.8-flash) |
|---|---|---|---|
| Alur penuh sampai approval, lalu disetujui | ✅ | ✅ | ✅ |
| Tolak | ✅ | ✅ (gagal 1 dari 4 uji, lihat di bawah) | ✅ |
| Batal saat menunggu approval | ✅ | ✅ | ✅ |
| Batas langkah 3 tidak pernah terlampaui | ✅ | ✅ | ✅ |

Durasi satu test live 20 detik sampai 1,5 menit, tergantung model.

Pada 25 November 2026 model LangGraph diganti ke `qwen/qwen3.8-27b`. Keempat test live
LangGraph diulang dan lulus 4 dari 4 (3 menit 39 detik).

Skenario revisi, revisi ketiga, `approvalId` usang, dan stream ulang hanya diuji
offline. Pada model sungguhan, alur revisi diuji manual dari browser saat Tahap
3, 4, dan 5.

Test batas langkah live menerima dua akhir yang sama-sama benar. Dengan batas 3,
model sungguhan bisa meminta tool lagi setelah diberi peringatan (`error`
`step_limit_exceeded`), atau mematuhi peringatan lalu menyusun memo dari data
yang ada (`approval_required`). Pada kedua akhir, jumlah tool yang benar-benar
dijalankan tidak pernah lebih dari 3.

## Kegagalan yang tercatat

### LangGraph `Live_reject_flow` gagal sekali

- **Gejala.** Pada uji live pertama, run LangGraph berakhir sebelum sampai ke `approval_required`, padahal seharusnya berhenti untuk menunggu keputusan analis.
- **Frekuensi.** 1 kali dari 4. Dua uji ulang test itu sendirian lulus, dan uji ulang seluruh suite live secara paralel juga lulus.
- **Penyebab pasti tidak tertangkap.** Pesan assertion saat itu hanya "Expected False, Actual True", dan folder data service uji langsung dihapus setelah test, sehingga event terakhirnya hilang.
- **Kemungkinan penyebab**, diurutkan dari yang paling mungkin menurut perilaku yang sudah terlihat pada uji lain:
  1. Qwen membalas teks tanpa tool call tiga kali berturut-turut, sehingga run ditutup `model_error`. Qwen memang terlihat sering memisahkan narasi dan tool call ke giliran berbeda (lihat EVENT-MAPPING LangGraph).
  2. Qwen menyusun memo yang tidak sah tiga kali berturut-turut, juga berakhir `model_error`.
  3. Gangguan sesaat di OpenRouter (timeout atau 5xx) setelah dua kali coba ulang.
- **Bukan pelanggaran kontrak.** Apa pun penyebabnya, akhir run seperti itu tetap sah menurut kontrak: run ditutup satu event `error` dengan semua tool call tertutup. Yang gagal adalah harapan test bahwa model sampai ke memo.
- **Tindak lanjut.** Test live sekarang mencetak event terakhir saat run tidak sampai ke approval (`AssertPaused` di `AdapterContractTests.cs`), jadi kalau terulang, penyebabnya langsung terlihat.

### MAF `Third_revision_ends_with_revision_limit` gagal sekali (offline)

- **Gejala.** Pada Tahap 15, test offline use case 1 ini gagal satu kali dalam satu run penuh suite, tepat setelah build.
- **Frekuensi.** 1 kali dari 11 run. Tiga uji ulang test MAF sendirian, tiga run penuh suite, dan empat run seluruh contract test lulus semua.
- **Penyebab pasti tidak tertangkap.** Pesan kegagalan tidak tersimpan pada run itu.
- **Dugaan.** Batas waktu 60 detik per run hampir pasti tidak tercapai, karena test ini selesai dalam beberapa ratus milidetik. Dugaan yang tersisa adalah urutan waktu saat banyak test berjalan bersamaan tepat setelah build.
- **Bukan pelanggaran kontrak.** Run yang sama lulus di semua uji lain. Bila terulang, pesan kegagalannya perlu dicatat di sini.

## Use case 2: deteksi pola

### Hasil offline

Skenario model tiruan, sama untuk MAF, LangGraph, dan ADK (`scenarios.json`):
- **Worker saham** memanggil `idx_broker`, lalu melaporkan YP sebagai broker dominan (45%).
- **Worker broker** untuk YP menemukan jejak ITMG.
- **Orchestrator** menjalankan worker saham untuk ITMG pada kedalaman 1, membentuk satu klaster (ADRO, ITMG, PTBA), lalu menjalankan worker kepemilikan.
- **Penyusun laporan** mencoba laporan dengan klaster dan tanpa klaster secara bergantian. Validasi di kode menolak yang tidak cocok dengan klaster orchestrator.

Agent fake memakai skripnya sendiri dengan aturan yang sama. Sejak Tahap 15 agent fake juga menegakkan anggaran tool call per job.

Selain pemeriksaan di use case 1, setiap skenario diperiksa dengan `EventAssertions.AssertWorkers`:
- setiap event membawa `agentName`
- setiap `worker_started` punya tepat satu `worker_finished` dengan peran yang sama
- tool call worker berada di antara keduanya
- tidak ada worker yang masih terbuka saat `approval_required`, `done`, atau `error`

| Skenario | Fake | MAF | LangGraph | ADK |
|---|---|---|---|---|
| Worker saham, broker, saham jejak baru (kedalaman 1), dan kepemilikan berjalan; tool MCP hanya dipanggil worker; berhenti di approval dengan `PatternReport` satu klaster | ✅ | ✅ | ✅ | ✅ |
| Setujui, lalu `done` (`approved`) dengan laporan | ✅ | ✅ | ✅ | ✅ |
| Tolak, lalu `done` (`rejected`) | ✅ | ✅ | ✅ | ✅ |
| Revisi menghasilkan laporan `revision: 1` tanpa menjalankan ulang worker, lalu disetujui | ✅ | ✅ | ✅ | ✅ |
| Anggaran 1 tool call per job: tepat 1 tool MCP dijalankan, laporan parsial mencatat batasnya, job tidak gagal | ✅ | ✅ | ✅ | ✅ |
| Batal saat worker berjalan: semua worker yang terbuka ditutup `cancelled`, lalu `done` (`cancelled`) | ✅ | ✅ | ✅ | ✅ |
| Batal saat menunggu approval laporan, lalu `done` (`cancelled`) | ✅ | ✅ | ✅ | ✅ |

Test anggaran menerima dua jalur yang sama-sama benar. Di LangGraph, ADK, dan agent fake, worker saham kedua sudah berjalan saat anggaran habis, jadi worker itu berakhir `limit_reached`. Di MAF dengan model tiruan, worker pertama selesai sebelum worker kedua dimulai, jadi worker kedua tidak dijalankan dan dicatat di "jejak yang tidak diikuti". Pada kedua jalur, laporan mencatat batas yang tercapai.

**Cacat yang ditemukan contract test ini, sudah diperbaiki:**
- **MAF.** `finish_worker` menolak panggilan yang tidak menyertakan `leads` atau `findings`, karena `AIFunctionFactory` menganggap parameter tanpa nilai bawaan sebagai wajib. Worker lalu berakhir `error` tanpa temuan. Pada uji live tidak terlihat karena gpt-5-mini selalu mengisi semua argumen. Sekarang ketiga daftar diberi nilai bawaan `null`.
- **ADK.** Saat dihentikan, worker yang sedang berjalan ditutup `error`, bukan `cancelled`, dan orchestrator masih memulai gelombang worker berikutnya. Sekarang worker yang berhenti karena pembatalan ditutup `cancelled`, dan orchestrator tidak memulai worker baru setelah pembatalan.

### Hasil live

Diuji dari browser (`/jobs`) dengan penyaringan awal dari server MCP idx sungguhan pada 25 November 2026, kandidat dibatasi 3 saham.

| Pemeriksaan | MAF (gpt-5-mini) | LangGraph (qwen3.8-27b) | ADK (gemini-3.8-flash) |
|---|---|---|---|
| Penyaringan sampai laporan, lalu disetujui | ✅ | ✅ (30 panggilan data) | ✅ (28 panggilan data) |
| Panel setiap worker terisi tool call sungguhan | ✅ | ✅ | ✅ |
| Batas 4 panggilan data per job: worker `limit_reached`, laporan parsial, bukan error | ✅ | ✅ | ✅ |
| Revisi laporan | ✅ | – (hanya offline) | – (hanya offline) |
| Laporan ditolak validasi sebelum diterima | tidak dicatat | 1 kali | 2 kali (klaster dan rujukan bukti) |

Uji live use case 2 belum dijadikan test otomatis kategori `Live`. Satu job memakai 20 sampai 30 panggilan data dan beberapa menit model berbayar. Hasilnya juga bergantung pada data pasar hari itu, sehingga klaster yang terbentuk tidak bisa dipastikan sebelumnya.

### Perbedaan antar-solusi (use case 2)

| Aspek | Fake | MAF | LangGraph | ADK |
|---|---|---|---|---|
| Orchestrator | Skrip | `PatternOrchestrator.cs` (kode biasa) | `pattern_orchestrator.py` (kode biasa) | Salinan sama dari LangGraph |
| Worker | Skrip berselang | `ChatClientAgent` per worker, paralel sampai 3 | `StateGraph` kecil per worker di node `workers` | `LlmAgent` per worker dengan `Runner` dan session di memori |
| Tool per peran | – | `IToolSource.GetToolsAsync(nama)` | Tool dipilih dari daftar penuh | Satu `McpToolset` per peran dengan `tool_filter` |
| Batas langkah dan anggaran job | Anggaran job | Function middleware dan `JobBudget` | Node alat worker dan `JobBudget` | `before_tool_callback` dan `JobBudget` |
| Jeda approval laporan | Skrip | `ApprovalRequiredAIFunction` `submit_report` | `interrupt()` di node `review` | Tool Confirmation di `SubmitReportTool` |
| Approval laporan bertahan setelah restart | Tidak | Ya (file JSON per run) | Ya (checkpointer) | Ya (`DatabaseSessionService`, app `pattern_report`) |
| Restart saat worker berjalan | Run hilang (`run_interrupted`) | `run_interrupted` | Worker terbuka ditutup, lalu `run_interrupted` | Sama dengan LangGraph |

Orchestrator sengaja ditulis sebagai kode di ketiga solusi, bukan agent model. Batas kedalaman, aturan sebaran broker, dan anggaran job harus ditegakkan di kode. Dengan begitu, perbandingan tiga solusi hanya membedakan cara framework menjalankan worker dan jeda approval, bukan keputusan penelusurannya.

### Perilaku model pada use case 2

- **gpt-5-mini (MAF)** pernah mengirim laporan dengan daftar kosong berupa `null` dan klaster satu saham yang tidak dibentuk orchestrator. Keduanya ditemukan saat uji live Tahap 14, lalu ditangani di validasi laporan (`PatternRunner.ValidateReport`).
- **Qwen.** `qwen3-30b-a3b-instruct-2507` memanggil tool yang tidak ada (`worker_broker`) dan menulis nama broker, bukan kodenya. Penggantinya, `qwen3.8-27b`, tidak mengulangi kesalahan itu. Panggilan ke tool yang tidak ada kini dihitung sebagai langkah, dan kode broker disaring di kode.
- **gemini-3.8-flash (ADK)** dua kali menyusun laporan dengan klaster yang tidak cocok atau rujukan bukti yang tidak dikenal, lalu memperbaikinya dari pesan validasi.

## Perbedaan antar-adapter

Semua perbedaan berikut diserap di adapter atau service, sehingga controller dan
view tidak perlu tahu solusi mana yang bekerja. Detailnya ada di
`agents/*/EVENT-MAPPING.md`.

| Aspek | Fake | MAF | LangGraph | ADK |
|---|---|---|---|---|
| Lokasi agent | In-process | In-process | Service Python, port 8101 | Service Python, port 8102 |
| Pemberi `sequence` | Adapter | Adapter | Service (disimpan di `service.db`) | Service (disimpan di `service.db`) |
| Potongan `token` | Sekitar 3 kata | Digabung sekitar 60 karakter | Digabung sekitar 60 karakter | Digabung sekitar 60 karakter; teks utuh dari SSE dibuang supaya tidak dobel |
| Mekanisme jeda approval | Skrip | `ApprovalRequiredAIFunction` | `interrupt()` di node `review` | Tool Confirmation (`request_confirmation`) |
| Cara mengirim revisi ke model | Skrip | Approval ditolak dengan alasan berisi komentar | `ToolMessage` berisi komentar | `payload` konfirmasi berisi keputusan dan komentar |
| Pembatalan | Token `StreamAsync` | Token `StreamAsync` | `POST /runs/{id}/cancel`, lalu task asyncio dibatalkan | `POST /runs/{id}/cancel`, lalu `abort_signal` |
| Penghitung batas langkah | Skrip | Function middleware | Node `tools` | `before_tool_callback` |
| Approval bertahan setelah restart | Tidak (run di memori) | Ya (file JSON per run) | Ya (`AsyncSqliteSaver`) | Ya (`DatabaseSessionService`) |
| Stream setelah aplikasi restart | Run hilang, ditutup `run_interrupted` (CHANGE-003) | Run yang sedang berjalan ditutup `run_interrupted`; jeda approval dilanjutkan | Diputar ulang dari `sequence` 1; duplikat dibuang `RunPump` | Sama dengan LangGraph |
| MCP tidak tersedia terdeteksi | – | Saat memuat tool | Saat memuat tool | Pemeriksaan `get_tools()` di awal run, karena ADK sendiri tetap berjalan tanpa tool |
| Coba ulang model | – | Klien OpenAI bawaan | `max_retries=2` | `retry_options` (2 kali untuk 429 dan 5xx) |

### Perilaku model yang memengaruhi tampilan

Perbedaan ini bukan pelanggaran kontrak, tapi terlihat oleh analis:

- **gpt-5-mini (MAF)** tidak menulis narasi sama sekali sampai instruksi sistem memintanya, dan menggabungkan narasi dengan tool call dalam satu giliran.
- **Qwen (LangGraph)** menulis narasi di giliran terpisah dari tool call. Pernah salah menyebut satuan ("juta lot" padahal datanya dalam lembar).
- **Gemini (ADK)** menulis narasi per langkah dan memakai satuan dengan benar. `gemini-2.5-flash` ditolak untuk akun baru, dan tier gratis sering membalas 503 saat permintaan tinggi.

### Keterbatasan yang sama di semua adapter

- **Bandar tidak tersedia.** `idx_bandar` hanya membaca data dari IPOT, yang membalas HTTP 502 sejak 11 November 2026. Ketiga agent mencatatnya di "Data tidak tersedia".
- **Uji offline vs uji live.** Skenario revisi, revisi ketiga, dan `approvalId` usang tidak diuji dengan model sungguhan secara otomatis. Use case 2 diuji live dari browser, belum lewat test otomatis.

## Berpindah solusi

Ada dua cara mengganti solusi, dan keduanya tidak menyentuh controller atau view
investigasi:

- **Saat aplikasi berjalan:** halaman **Setting** (`/settings`). Pilihan disimpan di database, berlaku untuk investigasi baru, dan halaman itu menampilkan status setiap solusi (API key ada, service Python menjawab health check). Investigasi yang sedang berjalan atau menunggu approval tetap memakai solusi yang memulainya, karena state-nya hanya dikenal adapter itu.
- **Nilai awal:** `Agent:Provider` (`fake`, `maf`, `langgraph`, `adk`), lewat `appsettings.json`, environment variable `Agent__Provider`, atau profil di Visual Studio.

Contract test di atas
menunjukkan bahwa keempat adapter memenuhi kontrak yang sama. Controller dan
view tidak berubah sejak Tahap 2; ini juga dijaga oleh test arsitektur
(`ArchitectureTests.cs`).
