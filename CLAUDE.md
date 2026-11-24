# Proyek: solusi agentic AI pengawasan pasar modal

## Tujuan

Satu aplikasi .NET MVC yang bisa memakai tiga solusi agent secara bergantian tanpa mengubah controller atau view. Proyek ini menjadi contoh berjalan untuk playbook integrasi Agentic AI, jadi kejelasan dan keterbandingan antar-solusi lebih penting daripada fitur.

Dua hal yang harus terbukti di akhir:

1. Tiga solusi agent yang berbeda bisa diintegrasikan lewat satu kontrak.
2. Front end bisa menangani interaksi agent (teks mengalir, progres tool, approval, stop, error, proses panjang) tanpa tahu solusi mana yang bekerja.

## Tujuan tambahan

Aplikasi ini menjadi acuan Bagian IV playbook (Bab 10 sampai 13). Setiap butir outline bab harus punya contoh yang bisa ditunjuk di kode dan diperagakan di browser dengan provider `fake`, tanpa memanggil model.

Aturan tambahan untuk tahap lanjutan:

- Kerjakan Tahap 7 sampai 12 dengan provider `fake`. Agent sungguhan hanya disentuh saat sebuah tahap mengubah kontrak.
- Setiap tahap ditutup dengan memperbarui `docs/GAP-BAGIAN-IV.md`: ubah status butir yang selesai dan perbarui rujukan file.
- Setiap pola atau mekanisme yang akan dikutip buku harus berada di file tersendiri dengan nama yang menjelaskan isinya, supaya bisa dirujuk per bab.
- Halaman demo berada di bawah `/Lab` dan hanya aktif saat `Agent:Provider` = `fake`.
- Contract test Tahap 6a harus tetap lulus di akhir setiap tahap.

## Cara memulai sesi

Di awal setiap sesi, baca bagian "Status tahap" di bawah, lalu sebutkan ke saya tahap mana yang sedang berjalan dan apa langkah berikutnya. Jika saya menulis "lanjut", kerjakan tahap pertama yang belum selesai.

Untuk setiap tahap:

1. Baca dokumentasi resmi yang relevan lebih dulu.
2. Tunjukkan rancangan singkat dan tunggu persetujuan saya.
3. Tulis kode, jalankan build dan test sendiri, perbaiki sampai lulus.
4. Periksa kriteria selesai satu per satu dan laporkan hasilnya.
5. Perbarui "Status tahap" dan "Catatan keputusan" di file ini.

## Arsitektur

- Aplikasi hanya mengenal `IAgentClient` dan katalog event di `contracts/`.
- Tiga adapter: `MafAgentClient` (in-process), `LangGraphAgentClient` dan `AdkAgentClient` (HTTP + SSE ke service Python). Satu `FakeAgentClient` untuk pengembangan tanpa model.
- Adapter dipilih lewat konfigurasi `Agent:Provider`: `fake`, `maf`, `langgraph`, atau `adk`. Halaman Setting (`/settings`) bisa menggantinya saat aplikasi berjalan; pilihan disimpan di database dan berlaku untuk investigasi baru.
- Semua agent memakai MCP IDX server yang sama.
- Database milik aplikasi adalah sumber kebenaran untuk kasus, event, dan approval. State internal tiap framework hanya untuk melanjutkan eksekusi.
- Pemetaan event bawaan framework ke katalog event dilakukan di sisi agent, bukan di controller atau view.

| Komponen | Lokasi | Bahasa | Model | Port |
|---|---|---|---|---|
| MCP IDX server | binary `idx` | - | - | 8765 |
| Aplikasi MVC | `app/Surveillance.Web` | C# | - | default Visual Studio |
| Solusi 1: MAF | `agents/maf` | C# | GPT via platform.openai | di dalam aplikasi |
| Solusi 2: LangGraph | `agents/langgraph` | Python | Qwen via OpenRouter | 8101 |
| Solusi 3: Google ADK | `agents/adk` | Python | Gemini via AI Studio | 8102 |

## Lingkungan

- Sistem operasi: Windows, skrip dalam PowerShell. Jika ternyata berbeda, tanyakan ke saya dan perbarui bagian ini.
- Sebelum Tahap 1, periksa versi: dotnet SDK, python, pip, node, npm, git, az, idx. Jangan memasang apa pun tanpa persetujuan saya.
- MCP IDX: `idx mcp --http 127.0.0.1:8765`, endpoint `http://127.0.0.1:8765/mcp`. Pastikan flag dan path di `docs/mcp.md` repo github.com/fzn0x/idx.
- Database: Azure SQL, connection string di user-secrets sebagai `ConnectionStrings:Default`. LocalDB boleh dipakai selama pengembangan.
- Setiap service Python punya virtual environment sendiri di foldernya.

Rahasia dan konfigurasi:

| Nama | Disimpan di | Dipakai oleh |
|---|---|---|
| `OPENAI_API_KEY` | user-secrets | Solusi 1 |
| `Agent:Maf:Model` | appsettings | Solusi 1 |
| `OPENROUTER_API_KEY`, `MODEL_BASE_URL`, `MODEL_NAME` | `agents/langgraph/.env` | Solusi 2 |
| `GOOGLE_API_KEY`, `MODEL_NAME` | `agents/adk/.env` | Solusi 3 |
| `IDX_MCP_TOKEN` | environment variable | MCP IDX dan ketiga agent |

Nama model belum ditentukan. Tanyakan ke saya saat pertama kali dibutuhkan, lalu catat di "Catatan keputusan". Model harus mendukung tool calling.

## Struktur repositori

```text
CLAUDE.md
contracts/
  events.schema.json          katalog event, sumber kebenaran kontrak
  mcp-tools.md                tool MCP IDX yang dipakai dan bentuk keluarannya
  asd-investigator-uma.md     spesifikasi agent use case 1
  asd-pattern-detection.md    spesifikasi agent use case 2
  agent-service-api.md        kontrak HTTP service agent Python
  COMPATIBILITY.md            hasil contract test per adapter
app/
  Surveillance.Agents.Abstractions/  IAgentClient, AgentEvent, tipe kontrak (class library)
  Surveillance.Web/
  Surveillance.Web.Tests/
agents/
  maf/                        class library C#, berisi EVENT-MAPPING.md
  langgraph/                  service FastAPI, berisi EVENT-MAPPING.md
  adk/                        service FastAPI, berisi EVENT-MAPPING.md
scripts/
  start-mcp.ps1
  start-all.ps1
```

## Kontrak

Katalog event: `run_started`, `token`, `tool_call_started`, `tool_call_finished`, `approval_required`, `done`, `error`. Setiap event punya `runId`, `sequence` (naik tanpa lompatan), `timestamp`, `type`, dan `payload`.

`IAgentClient` punya tiga operasi:

```csharp
Task<string> StartAsync(InvestigationRequest request, CancellationToken ct);
IAsyncEnumerable<AgentEvent> StreamAsync(string runId, CancellationToken ct);
Task ResumeAsync(string runId, ApprovalDecision decision, CancellationToken ct);
```

Kontrak HTTP service Python (sama untuk LangGraph dan ADK):

```text
POST /runs                  mulai, mengembalikan runId
GET  /runs/{id}/events      SSE, event sudah dalam bentuk katalog
POST /runs/{id}/resume      keputusan approval
```

## Keputusan kontrak

Tujuh usulan perubahan di `GAP-BAGIAN-IV.md` diputuskan sebagai berikut. Yang diterima sudah saya setujui dan dikerjakan di tahap yang disebut.

| Kebutuhan | Keputusan | Cara |
|---|---|---|
| Progres multi-langkah | Ubah kontrak (Tahap 8) | Event baru `step_started` dengan `step` (kode tahap dari ASD) dan `label` |
| Bukti dan sumber data | Ubah kontrak (Tahap 8) | Field opsional `source` dan `resultPreview` di `tool_call_finished`. `resultPreview` adalah data mentah yang dipotong maksimum 8 KB |
| Progres per worker | Ubah kontrak (Tahap 13) | Field opsional `agentName`, event `worker_started` dan `worker_finished`, field `useCase` di `POST /runs` |
| Indikator berpikir | Tanpa perubahan kontrak | UI menampilkan indikator jika tidak ada event selama N detik saat run aktif |
| Label tool yang ramah | Tanpa perubahan kontrak | Peta nama tool ke label bahasa Indonesia di JavaScript |
| Keputusan analis di aliran event | Tanpa perubahan kontrak | Aplikasi menggabungkan `AgentEventRecord` dan `ApprovalDecisionRecord` berdasarkan waktu saat menampilkan |
| Approval kedaluwarsa | Tanpa perubahan kontrak | Aplikasi yang memutuskan kedaluwarsa, lalu memanggil pembatalan |

Tidak ada endpoint baru untuk data mentah. Aplikasi menyimpan `resultPreview` di tabel tersendiri, tidak mengirimkannya lewat SSE, dan menyajikannya lewat endpoint aplikasi saat analis membuka sebuah bukti.

Semua perubahan bersifat menambah. `IAgentClient` tidak berubah.

## Use case

1. **Investigator UMA.** Satu agent menyelidiki satu saham yang harga atau volumenya menyimpang: cek berita dan aksi korporasi, gali broker dan arus asing bila perlu, nilai apakah bukti cukup, lalu susun draf memo. Agent berhenti di `approval_required` sampai analis memutuskan setuju, revisi, atau tolak. Interaksi sinkron.
2. **Deteksi akumulasi-distribusi terpola.** Orchestrator membagi kandidat ke worker saham, worker broker, dan worker kepemilikan, lalu mengikuti jejak baru sampai batas kedalaman. Berjalan sebagai job asinkron. Dikerjakan setelah use case 1 selesai di tiga solusi.

Deteksi awal (saham mana yang menyimpang) adalah kode biasa dengan ambang batas, bukan agent.

## Rincian use case 2

### Alur

1. **Penyaringan awal (kode biasa di aplikasi, bukan agent).** Menghasilkan daftar kandidat saham dari MCP IDX dengan ambang batas di konfigurasi.
2. **Orchestrator** menerima kandidat, membagi tugas ke worker, menerima temuan, lalu memutuskan jejak mana yang diikuti.
3. **Worker saham** memeriksa pola harga, volume, dan konsentrasi broker pada satu saham. Logikanya memakai ulang Investigator UMA tanpa langkah memo.
4. **Worker broker** memeriksa di saham lain mana saja sebuah broker menunjukkan pola serupa. Saham baru yang ditemukan menjadi jejak baru.
5. **Worker kepemilikan** memeriksa apakah saham-saham dalam satu klaster punya pemegang saham yang sama atau terafiliasi.
6. Orchestrator menggabungkan temuan menjadi klaster, menyusun laporan, lalu berhenti di `approval_required`.

### Tool per peran

Nama tool mengikuti `contracts/mcp-tools.md`. Jika nama di server berbeda, pakai yang di server dan perbarui tabel ini.

| Peran | Tool MCP IDX |
|---|---|
| Penyaringan awal | `idx_bandar_screen`, `idx_screen`, `idx_history` |
| Worker saham | `idx_history`, `idx_technical`, `idx_broker`, `idx_bandar`, `idx_foreign`, `idx_news`, `idx_corporate_actions` |
| Worker broker | `idx_bandar_broker`, `idx_market_brokers`, `idx_members`, `idx_broker` |
| Worker kepemilikan | `idx_ownership_ticker`, `idx_ownership_graph`, `idx_ownership_entity`, `idx_ownership_changes` |
| Orchestrator | tidak memanggil tool data, hanya mendelegasikan dan menilai |

### Ambang dan batas

Ini nilai awal untuk demo, bukan standar pengawasan. Semua dibaca dari konfigurasi `PatternDetection` dan ditegakkan di kode, bukan hanya di prompt.

| Parameter | Nilai awal |
|---|---|
| Jendela pengamatan | 10 hari bursa |
| Perubahan harga minimum dalam jendela | 15% (naik atau turun) |
| Porsi nilai beli bersih tiga broker teratas | minimal 40% |
| Kandidat maksimum per job | 10 saham |
| Kedalaman rantai (saham, broker, saham lain) | 2 |
| Saham maksimum per job, termasuk jejak baru | 15 |
| Broker yang diikuti per saham | 3 |
| Langkah maksimum per worker | 12 |
| Tool call maksimum per job | 150 |
| Durasi maksimum per job | 20 menit |

Saat batas tercapai, job tidak gagal. Orchestrator menyusun laporan dari temuan yang ada dan mencatat batas mana yang tercapai.

Orchestrator tidak mengikuti broker yang aktif di lebih dari 30% saham kandidat, kecuali porsi beli bersihnya di saham itu melewati ambang penyaringan. Setiap jejak yang tidak diikuti dicatat beserta alasannya.

### Struktur laporan

1. Ringkasan: jumlah klaster, saham, dan broker yang terlibat, serta periode pengamatan.
2. Per klaster: saham, broker dominan, kronologi akumulasi dan distribusi, serta keterkaitan kepemilikan bila ada.
3. Bukti: setiap pernyataan merujuk ke tool call yang mendasarinya.
4. Penjelasan alternatif yang wajar: berita, aksi korporasi, atau alasan lain yang ditemukan.
5. Jejak yang tidak diikuti dan batas yang tercapai.
6. Keterbatasan data: broker summary hanya sepuluh besar per sisi, harga tertunda, kepemilikan bulanan di atas 1%.
7. Usulan tindak lanjut untuk analis.

Laporan memakai bahasa indikasi. Tidak boleh ada kalimat yang menyimpulkan manipulasi atau pelanggaran.

## Tahapan

### Tahap 1: MCP IDX server

Buat `scripts/start-mcp.ps1`. Coba tool MCP untuk satu saham, lalu tulis `contracts/mcp-tools.md` berisi tool yang relevan untuk investigasi UMA, parameternya, dan contoh bentuk keluarannya.

Selesai jika: `idx stocks quote BBCA` menampilkan harga, server berjalan di port 8765, dan `mcp-tools.md` mencakup minimal quote, history, news, corporate actions, broker, bandar, dan foreign.

### Tahap 2: aplikasi MVC dengan agent tiruan

a. Tulis `contracts/events.schema.json` dan `contracts/asd-investigator-uma.md` (input, tool yang boleh dipakai, event, struktur memo, batas langkah, model error).

b. Buat solusi .NET: MVC `Surveillance.Web` dan proyek test xUnit. Isi: `IAgentClient`, `AgentEvent`, `FakeAgentClient` yang memancarkan urutan event UMA yang realistis dan berhenti di approval, `InvestigationController` dengan endpoint SSE dan endpoint approval, serta view .cshtml dengan JavaScript biasa untuk teks mengalir, daftar tool call, kartu approval, tombol stop, dan tampilan error.

c. Tambahkan EF Core: `InvestigationCase`, `AgentEventRecord`, `ApprovalDecision`. Simpan setiap event, sediakan halaman riwayat yang memutar ulang event, dan dukung menyambung kembali dari `sequence` terakhir.

Selesai jika: dengan provider `fake` investigasi berjalan sampai approval lalu `done`, memuat ulang halaman tidak menghilangkan event, data tersimpan di database, dan tidak ada rujukan framework agent di folder Controllers.

### Tahap 3: solusi MAF

Baca dokumentasi Microsoft Agent Framework di Microsoft Learn: paket NuGet, agent dengan model OpenAI (bukan Azure OpenAI), tool MCP lewat HTTP, streaming, dan persetujuan manusia. Bangun agent Investigator UMA sesuai ASD, satu tool lokal `submit_memo` yang butuh persetujuan, dan `MafAgentClient`. Tulis `agents/maf/EVENT-MAPPING.md`.

Selesai jika: provider `maf` menampilkan tool call sungguhan, approval menjeda dan melanjutkan, penolakan tercatat benar, dan agent berhenti di batas langkah.

### Tahap 4: solusi LangGraph

Baca dokumentasi LangGraph dan LangChain: StateGraph, checkpointer, interrupt, streaming, adapter MCP. Bangun graph Investigator UMA dengan interrupt sebelum memo, checkpointer SQLite, model lewat endpoint kompatibel OpenAI (default `https://openrouter.ai/api/v1`), dan service FastAPI di port 8101. Tulis `contracts/agent-service-api.md`, `LangGraphAgentClient`, dan `agents/langgraph/EVENT-MAPPING.md`.

Selesai jika: provider `langgraph` berjalan dari browser, approval menjeda dan melanjutkan, dan investigasi bertahan saat service dimatikan lalu dinyalakan di tengah jeda approval.

### Tahap 5: solusi Google ADK

Baca dokumentasi Google Agent Development Kit: agent dengan Gemini memakai API key AI Studio (bukan Vertex AI), tool MCP lewat HTTP, Runner, session service, bentuk Event, dan jeda untuk persetujuan. Bangun service FastAPI di port 8102 yang mengikuti `agent-service-api.md` tanpa perubahan, `AdkAgentClient`, dan `agents/adk/EVENT-MAPPING.md`. Pindahkan kode yang sama dengan adapter LangGraph ke base class `HttpSseAgentClient`. Buat `scripts/start-all.ps1`.

Selesai jika: provider `adk` berjalan dari browser, approval menjeda dan melanjutkan, dan `agent-service-api.md` tidak berubah sejak Tahap 4.

### Tahap 6: contract test dan use case 2

a. Buat contract test abstrak yang dijalankan terhadap keempat adapter: validasi skema, urutan `sequence`, pasangan tool call, jeda dan lanjut approval, penolakan, pembatalan, dan batas langkah. Test terhadap agent sungguhan diberi kategori `Live`. Tulis `contracts/COMPATIBILITY.md`.

b. **Dibatalkan 2026-11-25, digantikan Tahap 13 sampai 15.** Teks aslinya: Tulis `contracts/asd-pattern-detection.md`, lalu bangun use case 2 di satu solusi dulu. Tambahkan field opsional `agentName` pada event. Di aplikasi: job di background service, halaman daftar job, dan halaman detail dengan progres per worker.

Selesai jika: contract test lulus atau setiap kegagalan dijelaskan di `COMPATIBILITY.md`, berpindah solusi hanya butuh mengganti `Agent:Provider`, dan job use case 2 tetap berjalan setelah browser ditutup.

Untuk 6a, kriteria contract test dan berpindah solusi sudah terpenuhi. Kriteria job use case 2 pindah ke Tahap 13.

## Tahapan lanjutan

### Tahap 7: fondasi untuk Bagian IV

a. Autentikasi sederhana dengan cookie: halaman login dan tiga akun contoh dari konfigurasi (dua analis, satu supervisor). `DecidedBy` diambil dari identitas login, bukan dari isian bebas. Hapus isian nama dan penyimpanannya di `localStorage`. Ini autentikasi untuk demo; tulis catatan itu di kode dan di halaman login.

b. Tutup celah konkurensi di `DecideAsync` dengan concurrency token (`rowversion` pada `InvestigationCase`) atau update bersyarat. Analis kedua mendapat pesan bahwa kasus sudah diputuskan, dan run tidak boleh berakhir dengan `error internal`. Tambahkan test yang menjalankan dua keputusan bersamaan.

c. Jadikan status kasus sebuah state machine eksplisit di satu file: daftar status, transisi yang sah, dan penolakan untuk transisi lain.

d. Kurangi tiga query per event di `RunPump.PersistAsync`. Ukur waktu per event sebelum dan sesudah, lalu catat hasilnya di `docs/NOTES-PERSIST.md` sebagai bahan Bab 10.

e. Ganti halaman error bawaan template dengan halaman error berbahasa Indonesia.

Selesai jika: keputusan approval mencatat pengguna yang login, test konkurensi lulus, transisi status tidak sah ditolak, dan catatan pengukuran tertulis.

### Tahap 8: evolusi kontrak untuk progres dan bukti

Ini contoh negosiasi event untuk Bab 11 butir 9, jadi prosesnya ikut didokumentasikan.

a. Tulis `contracts/CHANGE-001-step-and-evidence.md`: kebutuhan front end, alternatif tanpa perubahan kontrak dan kenapa tidak cukup, usulan perubahan, dampak ke tiap solusi, dan cara menjaga kompatibilitas ke belakang.

b. Perbarui `events.schema.json` dan `asd-investigator-uma.md`: event `step_started` dengan lima tahap ASD, serta field `source` dan `resultPreview` di `tool_call_finished`.

c. Terapkan di `FakeAgentClient`, lalu di MAF, LangGraph, dan ADK. Perbarui `EVENT-MAPPING.md` tiap solusi.

d. Aplikasi menyimpan `resultPreview` di tabel tersendiri dan tidak meneruskannya lewat SSE. Tambahkan endpoint aplikasi untuk mengambil pratinjau satu tool call.

e. Perluas contract test: `step_started` muncul berurutan dan tidak mundur, `source` terisi untuk tool MCP, dan `resultPreview` tidak melebihi 8 KB.

Selesai jika: contract test lama dan baru lulus untuk keempat adapter, atau setiap kegagalan dijelaskan di `COMPATIBILITY.md`.

### Tahap 9: transport (Bab 10)

a. Satu antarmuka transport di JavaScript dengan tiga implementasi di file terpisah: SSE, polling ke `events.json?after=`, dan SignalR. Halaman investigasi memilih transport lewat konfigurasi dan parameter `?transport=`.

b. SignalR: hub, grup per kasus, dan menyambung kembali dari `sequence` terakhir. Sumbernya tetap `EventBroadcaster`.

c. Endpoint SSE aplikasi mengirim ping berkala saat menunggu, field `retry:`, dan header `X-Accel-Buffering: no`. Tulis `docs/PROXY.md` berisi contoh konfigurasi untuk IIS, nginx, dan Azure App Service.

d. Contoh "sebelum": endpoint klasik `POST /Lab/Sync` yang menunggu agent selesai lalu mengembalikan view, untuk memperagakan halaman beku dan timeout.

e. Halaman `/Lab/Transport`: dua panel untuk kasus yang sama, kiri SSE dan kanan polling, dengan penghitung jumlah request, jeda rata-rata, dan pilihan interval polling.

f. Tegaskan di antarmuka bahwa menutup tab tidak menghentikan investigasi.

Selesai jika: investigasi yang sama berjalan dengan ketiga transport tanpa kehilangan atau menggandakan event, menyambung kembali bekerja di ketiganya, dan halaman pembanding menunjukkan perbedaan jumlah request.

### Tahap 10: pola UI (Bab 11)

a. Pecah `Details.cshtml` dan `investigation.js` menjadi satu partial view dan satu modul JavaScript per pola: narasi, indikator berpikir, tool call, progres langkah, bukti, tombol stop, banner error, dan kartu approval. Halaman investigasi merangkai semuanya.

b. Indikator berpikir: muncul jika tidak ada event selama N detik saat run aktif, dengan teks yang berubah setelah jeda lebih panjang.

c. Label tool: peta nama tool ke kalimat seperti "Sedang memeriksa broker summary…", dengan nama teknis tetap bisa dilihat.

d. Progres langkah: daftar lima tahap ASD yang digerakkan oleh `step_started`.

e. Bukti: dari sebuah temuan di memo, analis bisa membuka tool call yang mendasarinya, melihat `source`, waktu data, dan pratinjau data mentah.

f. Error: pesan untuk error yang tidak bisa diulang menyertakan langkah berikutnya. Tombol stop menjelaskan bahwa data yang sudah masuk tetap tersimpan.

g. Halaman `/Lab/Patterns`: satu bagian per pola, masing-masing dengan tombol yang memutar skenario fake untuk pola itu saja. Tambahkan skenario fake `silent_30s` untuk agent yang diam lama.

h. Tulis `docs/PATTERNS.md`: tabel pola, event yang dibutuhkan, partial view, modul JavaScript, dan implikasi ke backend.

Selesai jika: halaman investigasi tampil dan berperilaku sama seperti sebelum dipecah, setiap pola bisa diperagakan sendiri di `/Lab/Patterns`, dan `docs/PATTERNS.md` lengkap.

### Tahap 11: approval (Bab 12)

a. Putaran revisi: pastikan keputusan revisi menghasilkan memo baru yang kembali ke approval, dan riwayat versi memo terlihat.

b. Halaman antrean approval: kasus yang menunggu keputusan, diurutkan dari yang paling lama, dengan jumlah dan lama menunggu.

c. Pengambilan kasus: analis mengambil kasus sebelum memutuskan, analis lain melihat siapa yang sedang memegangnya, dan supervisor bisa mengalihkan. Pengambilan kedaluwarsa sendiri setelah tidak aktif.

d. Kedaluwarsa approval oleh aplikasi: batas waktu dari konfigurasi, penanda "hampir kedaluwarsa" di antrean, dan saat lewat batas aplikasi memanggil pembatalan lalu mencatat alasannya.

e. Halaman audit per kasus: satu garis waktu yang menggabungkan event agent dan keputusan analis, dengan ekspor ke JSON.

f. Halaman `/Lab/Approval`: dua panel yang saling terhubung, sisi agent yang menunggu dan sisi analis yang memutuskan.

g. Tulis `docs/APPROVAL-STATES.md` berisi diagram state machine dalam Mermaid, yang cocok dengan kode di Tahap 7c.

Selesai jika: dua pengguna berbeda bisa memperagakan pengambilan dan pengalihan kasus, approval yang lewat batas waktu berakhir dengan status yang jelas, dan garis waktu audit menjawab siapa memutuskan apa dan kapan.

### Tahap 12: durabilitas (Bab 13)

a. Pisahkan akses data ke repository (`ICaseRepository`, `IEventRepository`, `IApprovalRepository`) dan pakai di `InvestigationService` dan `RunPump`.

b. Hasilkan `docs/schema.sql` dengan `dotnet ef migrations script` dan `docs/ERD.md` dalam Mermaid.

c. Resume setelah restart: pertahankan perilaku sekarang (run yang menunggu approval dilanjutkan, run yang sedang streaming ditutup), tetapi ganti `error internal` dengan kode error yang jelas dan bisa diulang, serta tawarkan jalankan ulang. Tulis `docs/RESTART.md`: tabel perilaku per provider dan alasan desainnya.

d. Retensi: kebijakan umur data dari konfigurasi, background service pembersih untuk tabel event dan pratinjau, pemadatan event `token` menjadi teks utuh setelah run selesai, dan pembersihan `App_Data/maf-runs`. Dokumentasikan pembersihan database service Python.

Selesai jika: tidak ada akses `DbContext` langsung di luar repository, skrip DDL dan ERD cocok dengan migrasi, restart di tengah streaming menghasilkan pesan yang bisa ditindaklanjuti, dan pembersih teruji dengan data berumur buatan.

### Tahap 13: use case 2 di aplikasi, dengan agent tiruan

a. Tulis `contracts/asd-pattern-detection.md` dari bagian "Rincian use case 2". Tulis `contracts/CHANGE-002-multi-agent.md`, lalu perbarui `events.schema.json` dan `agent-service-api.md`.

b. Penyaringan awal sebagai service C# biasa dengan unit test.

c. Perluas `FakeAgentClient`: orchestrator, tiga worker yang berjalan berselang, satu jejak baru, lalu approval laporan.

d. Halaman daftar job dengan status berjalan, halaman detail dengan satu panel per worker, pembatalan job, dan membuka kembali hasil. Pakai `RunPump` dan repository yang sudah ada.

e. Notifikasi selesai: penanda di navigasi dan pemberitahuan lewat SignalR untuk pengguna yang memulai job.

Selesai jika: dengan provider `fake` sebuah job berjalan sampai approval lalu `done`, job tetap berjalan setelah browser ditutup, notifikasi muncul di halaman lain, dan semua test use case 1 tetap lulus.

### Tahap 14: use case 2 di tiga solusi

Kerjakan satu solusi per sesi, berurutan: MAF, LangGraph, lalu ADK. Untuk tiap solusi, baca dokumentasi resminya tentang pola multi-agent dan eksekusi paralel, bangun orchestrator dan tiga worker sesuai ASD dengan batas ditegakkan di kode, petakan aktivitas ke `agentName`, `worker_started`, dan `worker_finished`, lalu perbarui `EVENT-MAPPING.md`.

Selesai jika, untuk tiap solusi: job berjalan dari penyaringan sampai laporan, panel tiap worker terisi tool call sungguhan, batas penelusuran menghasilkan laporan parsial dan bukan error, dan approval laporan bekerja. Untuk percobaan pertama, turunkan kandidat maksimum menjadi 3 saham.

### Tahap 15: contract test use case 2 dan penutupan

a. Contract test use case 2 untuk keempat adapter: setiap `worker_started` punya `worker_finished`, setiap event worker membawa `agentName`, batas dipatuhi, pembatalan menghentikan semua worker, dan job berakhir di approval.

b. Perbarui `COMPATIBILITY.md` dengan perbandingan tiga solusi untuk kedua use case.

c. Perbarui `docs/GAP-BAGIAN-IV.md` untuk terakhir kali. Setiap butir yang masih "Sebagian" atau "Belum" harus punya alasan.

d. Tulis `docs/BOOK-INDEX.md`: untuk setiap butir outline Bab 10 sampai 13, file yang menjadi contohnya dan halaman yang memperagakannya.

Selesai jika: contract test kedua use case lulus atau setiap kegagalan dijelaskan, dan `BOOK-INDEX.md` mencakup semua butir outline.

## Aturan

- Jangan menebak nama kelas, method, atau paket MAF, LangGraph, atau Google ADK. Baca dokumentasi resminya, karena ketiganya cepat berubah.
- Jangan mengubah `contracts/events.schema.json` atau `contracts/agent-service-api.md` tanpa persetujuan saya. Jika sebuah framework tidak cocok dengan kontrak, serap perbedaannya di adapter atau service, lalu catat di `EVENT-MAPPING.md`.
- Controller dan view tidak boleh merujuk framework agent apa pun.
- Tool MCP hanya membaca data. Agent tidak boleh punya tool yang menulis ke sistem luar.
- Setiap agent punya batas langkah maksimum sesuai ASD.
- Keluaran agent adalah indikasi untuk ditelaah analis, bukan kesimpulan pelanggaran. Gunakan bahasa itu di memo dan antarmuka.
- Jangan menulis nilai API key atau connection string ke file mana pun, dan jangan menampilkannya di terminal. Pastikan `.env` masuk `.gitignore`.
- Fitur MCP IDX yang butuh sesi Stockbit boleh dipakai (broker summary lengkap, aksi korporasi, pergerakan insider, orderbook). Login hanya dilakukan oleh saya lewat `idx stockbit login` di browser. Jangan pernah meminta, menampilkan, atau menyalin password, cookie, atau token Stockbit, dan jangan memakai sesi itu di alat lain. Periksa `idx_stockbit_status` sebelum memakai tool Stockbit. Jika sesi kedaluwarsa, minta saya login ulang.
- Commit setiap kali satu tahap memenuhi kriteria selesai, dengan pesan yang menyebut nomor tahap.
- Kode, nama file, dan komentar dalam bahasa Inggris. Teks antarmuka dan dokumen di `contracts/` dalam bahasa Indonesia.
- Jika sebuah kriteria selesai tidak bisa dipenuhi, berhenti dan jelaskan alasannya. Jangan melonggarkan kriteria sendiri.

## Status tahap

- [x] Tahap 1: MCP IDX server
- [x] Tahap 2: aplikasi MVC dengan agent tiruan
- [x] Tahap 3: solusi MAF
- [x] Tahap 4: solusi LangGraph
- [x] Tahap 5: solusi Google ADK
- [x] Tahap 6: contract test dan use case 2 (dipecah)
  - [x] Tahap 6a: contract test, commit `d69b5f7`
  - [ ] ~~Tahap 6b: use case 2 di satu solusi~~ dibatalkan 2026-11-25, digantikan Tahap 13 sampai 15
- [ ] Tahap 7: fondasi untuk Bagian IV
- [ ] Tahap 8: evolusi kontrak untuk progres dan bukti
- [ ] Tahap 9: transport (Bab 10)
- [ ] Tahap 10: pola UI (Bab 11)
- [ ] Tahap 11: approval (Bab 12)
- [ ] Tahap 12: durabilitas (Bab 13)
- [ ] Tahap 13: use case 2 di aplikasi, dengan agent tiruan
- [ ] Tahap 14: use case 2 di tiga solusi
- [ ] Tahap 15: contract test use case 2 dan penutupan

## Catatan keputusan

Tambahkan satu baris per keputusan: tanggal, keputusan, alasan singkat.

- 2026-11-24: Versi lingkungan: dotnet 10.0.301, Python 3.13.14, pip 26.1.2, node 22.21.0, npm 10.9.4, git 2.54.0, az 2.87.0, idx 0.8.4.
- 2026-11-24: Fitur MCP IDX yang butuh sesi Stockbit diizinkan (aturan diubah atas permintaan). Alasan: IPOT membalas HTTP 502, sedangkan Stockbit memberi broker summary lengkap (semua broker, angka tidak dibulatkan) dan aksi korporasi.
- 2026-11-24: Aksi korporasi memakai `idx_stockbit_actions`; `idx_corporate_actions` (IPOT) hanya cadangan. Alasan: IPOT sedang gagal, dan data Stockbit lebih lengkap.
- 2026-11-24: Contoh keluaran `idx_bandar` di `mcp-tools.md` diambil dari JSON Schema resmi (`idx schema bandar`), belum dari data nyata, karena tool ini hanya bisa memakai IPOT. Ganti dengan contoh nyata saat IPOT pulih. Jika bandar gagal, agent boleh menghitung sendiri dari `idx_broker` dengan label hitungan agent.
- 2026-11-24: Field broker `kind` pada data nyata juga bernilai `foreign`, padahal nilai itu tidak ada di enum schema resmi. Adapter jangan memvalidasi `kind` secara ketat.
- 2026-11-24: Batas langkah = jumlah panggilan tool MCP, maksimum 15 per run (termasuk selama revisi; `submit_memo` tidak dihitung). Revisi memo maksimum 2. Alasan: ketiga framework menghitung iterasi dengan cara berbeda, sedangkan tool call bisa dihitung seragam oleh adapter.
- 2026-11-24: Batas langkah tercapai → `error` kode `step_limit_exceeded`; tombol stop → `done` dengan `outcome: cancelled`. Alasan: `done` hanya untuk keputusan analis atau tindakan pengguna, `error` untuk kegagalan.
- 2026-11-24: `events.schema.json` memakai JSON Schema 2020-12. `timestamp` dan tanggal juga diberi `pattern` (UTC berakhiran `Z`), karena `format` di 2020-12 secara default hanya anotasi dan tidak semua validator memeriksanya.
- 2026-11-24: Kontrak C# (`IAgentClient`, `AgentEvent`, dan tipe input) diletakkan di class library `app/Surveillance.Agents.Abstractions`. Alasan: `agents/maf` cukup merujuk kontrak tanpa membawa MVC/EF, dan tidak terjadi referensi melingkar dengan `Surveillance.Web`.
- 2026-11-24: Entitas EF untuk keputusan analis bernama `ApprovalDecisionRecord`. Alasan: `ApprovalDecision` sudah dipakai sebagai tipe kontrak untuk `ResumeAsync`.
- 2026-11-24: Browser tidak membaca agent langsung. `RunPump` (BackgroundService) membaca `StreamAsync`, menyimpan event ke DB, lalu menyiarkannya; endpoint SSE memutar dari DB lalu mengikuti event live. `StreamAsync` berakhir saat run berhenti di `approval_required` atau selesai, dan dipanggil lagi setelah `ResumeAsync`. Alasan: DB jadi sumber kebenaran, reload dan tutup browser tidak memutus run.
- 2026-11-24: `tool_call_finished` untuk `submit_memo` dipancarkan setelah keputusan analis, bukan sebelum jeda. Alasan: tool itu memang menunggu persetujuan, dan pola ini sama dengan fungsi berpersetujuan di MAF. Tertulis di ASD.
- 2026-11-24: Stop saat run menunggu approval: pump memanggil `StreamAsync` dengan token yang sudah dibatalkan, dan adapter menutup run dengan `done(cancelled)`. Alasan: `IAgentClient` hanya punya tiga operasi. Untuk service Python (Tahap 4) perlu cara membatalkan run di sisi service; dibahas saat menulis `agent-service-api.md`.
- 2026-11-24: Jika adapter melempar exception atau run tidak bisa dimulai, aplikasi sendiri yang menambahkan event `error` (kode `internal` atau `mcp_unavailable`) dengan `sequence` berikutnya, supaya setiap run tetap diakhiri event katalog.
- 2026-11-24: Paket EF Core dan Mvc.Testing dikunci ke 10.0.15, karena versi 11 hanya untuk net11.
- 2026-11-24: LocalDB tidak bisa start di mesin ini (SQL Server 2016 dan 2025): `misaligned log IOs`, masalah ukuran sektor disk NVMe di Windows 11. Instance `(localdb)\Surveillance` (17.0) tersisa dan tidak dipakai.
- 2026-11-24: Database pengembangan memakai Azure SQL: server `ojk-agentik-sql-pthuo0`, database baru `Surveillance` (serverless GP_S_Gen5, auto-pause 60 menit). Login lewat Entra ID (`Authentication=Active Directory Default`, memakai sesi `az login`); akun pengguna dijadikan admin Entra server. Connection string tanpa password, tersimpan di user-secrets. EF Core memakai `EnableRetryOnFailure` karena database serverless perlu waktu untuk menyala.
- 2026-11-24: Model Solusi 1 (MAF): `gpt-5-mini` lewat platform.openai, Chat Completions (bukan Responses) supaya riwayat ada di `AgentSession` yang bisa diserialisasi dan diuji dengan `IChatClient` tiruan.
- 2026-11-24: Paket MAF: `Microsoft.Agents.AI.OpenAI` 1.28.0 dan `ModelContextProtocol` 2.4.0. Nama API dipastikan lewat compile karena repo GitHub MAF tidak bisa diakses; perilaku yang tidak terdokumentasi (approval saat streaming, resume dari session terserialisasi) dipastikan lewat test. Detail di `agents/maf/EVENT-MAPPING.md`.
- 2026-11-24: Batas langkah MAF dihitung di function middleware, bukan `MaximumIterationsPerRequest`, karena batas bawaan itu direset setiap kali approval dilanjutkan. Revisi dikirim ke model sebagai approval ditolak dengan alasan berisi komentar analis.
- 2026-11-24: Instruksi sistem bersama di ASD diperbarui setelah uji live MAF: menyebut `idx_stockbit_actions` secara eksplisit (model sebelumnya memilih `idx_corporate_actions` yang gagal) dan meminta satu kalimat narasi sebelum setiap tool (gpt-5-mini sebelumnya tidak menulis narasi sama sekali). LangGraph dan ADK memakai teks yang sama.
- 2026-11-24: Batas langkah: panggilan paralel dalam satu giliran model yang melewati batas hanya diberi peringatan; `step_limit_exceeded` baru jika model meminta tool lagi di giliran berikutnya. Ditemukan saat uji live; berlaku juga untuk LangGraph dan ADK.
- 2026-11-24: Setelah memo disetujui, adapter langsung memancarkan `done` tanpa meminta balasan penutup dari model. Teks model digabung sekitar 60 karakter per event `token`.
- 2026-11-25: Data bandar dibiarkan "tidak tersedia" sampai IPOT pulih (idx 0.8.4 hanya membaca bandar dari IPOT; perbaikan direncanakan di idx 0.8.5). Tidak menghitung bandar sendiri dari data broker.
- 2026-11-25: Model Solusi 2 (LangGraph): `qwen/qwen3-30b-a3b-instruct-2507` lewat OpenRouter. Varian `qwen3-30b-a3b` biasa hanya punya konteks 40K, tidak cukup untuk keluaran tool idx yang besar.
- 2026-11-25: `agent-service-api.md` punya endpoint keempat `POST /runs/{id}/cancel` (disetujui) dan `GET /health`. Tanpa cancel, tombol Hentikan tidak bisa menghentikan run Python yang sedang menunggu approval.
- 2026-11-25: LangGraph memakai `StateGraph` eksplisit (bukan `create_agent`) supaya graph terlihat untuk perbandingan; `ChatOpenAI(base_url=...)` sesuai `MODEL_BASE_URL` walau dokumentasi menyarankan `langchain-openrouter`; MCP lewat `langchain.mcp` (`MCPAdapter`, beta) karena `langchain-mcp-adapters` tidak dirawat; `AsyncSqliteSaver` dengan `durability="sync"`; event disimpan service di `data/service.db`.
- 2026-11-25: Giliran model yang hanya berisi teks dianggap narasi; agent diberi pesan "Lanjutkan…" dan gagal setelah 3 giliran teks berturut-turut. Ditemukan saat uji live (Qwen memisahkan narasi dan tool call); berlaku di MAF dan LangGraph.
- 2026-11-25: Folder data LangGraph memakai `LANGGRAPH_DATA_DIR`, bukan `DATA_DIR`, karena `DATA_DIR` di mesin ini milik open-webui. `.env` service dibaca dengan `override=True` supaya tidak kalah oleh variable sistem bernama sama.
- 2026-11-25: Test di C# dan Python membandingkan teks instruksi di kode dengan blok teks di ASD, supaya ketiga solusi tidak diam-diam berbeda.
- 2026-11-25: Model Solusi 3 (ADK): `gemini-3.8-flash` lewat AI Studio. `gemini-2.5-flash` ditolak untuk akun baru (404 "no longer available to new users").
- 2026-11-25: API key Gemini memakai tier berbayar (billing diaktifkan). Tier gratis sering 503 saat permintaan tinggi, dan prompt di tier gratis boleh dipakai Google untuk mengembangkan produk; untuk data pengawasan, tier gratis tidak dipakai.
- 2026-11-25: ADK memakai Tool Confirmation (`request_confirmation`) dengan keputusan analis di `payload`, dan `DatabaseSessionService` (sqlite+aiosqlite). Situs ADK menyebut kombinasi ini tidak didukung; panduan repo menyebut didukung; test memastikan jeda approval bertahan setelah restart.
- 2026-11-25: ADK `submit_memo` mendeklarasikan skema `Memo` tapi menerima dict mentah, supaya memo tidak sah tercatat sebagai `invalid_memo` dan tidak gagal di konversi ADK. Model Gemini diberi `retry_options` (2 kali coba ulang untuk 429/5xx), sama seperti MAF dan LangGraph.
- 2026-11-25: `contract.py`, `instructions.py`, dan `summarizer.py` disalin dari LangGraph ke ADK (bukan paket bersama) supaya tiap service berdiri sendiri; test di kedua service menjaga instruksi tetap sama dengan ASD.
- 2026-11-25: Kode klien HTTP dipindah ke base class `HttpSseAgentClient`; `LangGraphAgentClient` dan `AdkAgentClient` hanya turunan tipis. `scripts/start-all.ps1` menyalakan MCP, LangGraph, dan ADK di jendela terpisah, dengan opsi `-Provider` untuk aplikasi web.
- 2026-11-25: Contract test (Tahap 6a): satu kelas abstrak `AgentContractTests` untuk keempat adapter, model tiruan bersama dari `scenarios.json` (service Python lewat `AGENT_SCRIPT`), test `Live` di belakang `RUN_LIVE_TESTS=1`. Hasil di `contracts/COMPATIBILITY.md`.
- 2026-11-25: Halaman Setting untuk memilih solusi agent saat aplikasi berjalan (diminta). `AgentClientRegistry` menyimpan keempat adapter dan membuatnya saat pertama dipakai; pilihan aktif disimpan di tabel `Settings`; `Agent:Provider` jadi nilai awal. Kasus memakai adapter yang tercatat di `InvestigationCase.Provider`, sehingga run yang sedang berjalan tidak ikut berpindah. Nama model hanya ditampilkan; mengganti model per solusi tidak termasuk karena butuh perubahan `agent-service-api.md`.
- 2026-11-25: `CLAUDE-lanjutan.md` digabung ke file ini (diminta): bagian Tujuan tambahan, Keputusan kontrak, Rincian use case 2, dan Tahapan lanjutan (Tahap 7 sampai 15) disalin apa adanya; Tahap 6a ditandai selesai (commit `d69b5f7`); Tahap 6b dibatalkan dan digantikan Tahap 13 sampai 15. Perubahan kontrak di tabel Keputusan kontrak (`step_started`, `source`/`resultPreview`, `agentName`/`worker_started`/`worker_finished`/`useCase`) sudah disetujui untuk Tahap 8 dan 13.
- 2026-11-24: Catatan keamanan: server Azure SQL punya aturan firewall `AllowAll-Experiment` (0.0.0.0–255.255.255.255). Belum diubah; sebaiknya dihapus dan diganti aturan per IP.
