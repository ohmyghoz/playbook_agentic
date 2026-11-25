# Kompatibilitas adapter terhadap kontrak

Dokumen ini mencatat hasil contract test `IAgentClient` terhadap keempat adapter:
fake, MAF, LangGraph, dan Google ADK. Semua adapter diuji dengan skenario dan
pemeriksaan yang sama, sehingga hasilnya bisa dibandingkan langsung.

Ringkasnya:

- **Offline (model tiruan): 40 dari 40 lulus.** Sepuluh skenario untuk keempat adapter, termasuk tahap dan bukti dari CHANGE-001.
- **Live (model sungguhan): 12 dari 12 lulus pada uji terakhir** (Tahap 8, setelah CHANGE-001). Pada uji Tahap 6a, satu test LangGraph gagal sekali dan tidak muncul lagi dalam tiga uji ulang. Penjelasannya ada di bagian [Kegagalan yang tercatat](#kegagalan-yang-tercatat).
- Tidak ada adapter yang melanggar `contracts/events.schema.json` atau aturan urutan event di ASD.

Tanggal uji: 25 November 2026.

## Cara menjalankan

Contract test ada di `app/Surveillance.Web.Tests/Contract/`.

| Bagian | Isi |
|---|---|
| `AgentContractTests.cs` | Sepuluh skenario offline, ditulis sekali untuk semua adapter |
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
- **Uji offline vs uji live.** Skenario revisi, revisi ketiga, dan `approvalId` usang tidak diuji dengan model sungguhan secara otomatis.

## Berpindah solusi

Ada dua cara mengganti solusi, dan keduanya tidak menyentuh controller atau view
investigasi:

- **Saat aplikasi berjalan:** halaman **Setting** (`/settings`). Pilihan disimpan di database, berlaku untuk investigasi baru, dan halaman itu menampilkan status setiap solusi (API key ada, service Python menjawab health check). Investigasi yang sedang berjalan atau menunggu approval tetap memakai solusi yang memulainya, karena state-nya hanya dikenal adapter itu.
- **Nilai awal:** `Agent:Provider` (`fake`, `maf`, `langgraph`, `adk`), lewat `appsettings.json`, environment variable `Agent__Provider`, atau profil di Visual Studio.

Contract test di atas
menunjukkan bahwa keempat adapter memenuhi kontrak yang sama. Controller dan
view tidak berubah sejak Tahap 2; ini juga dijaga oleh test arsitektur
(`ArchitectureTests.cs`).
