# Pengawasan pasar modal dengan tiga solusi agent

Aplikasi ASP.NET Core MVC yang memakai tiga solusi agentic AI secara bergantian lewat satu kontrak:
Microsoft Agent Framework (MAF), LangGraph, dan Google Agent Development Kit (ADK). Controller dan view
tidak tahu solusi mana yang sedang bekerja.

Proyek ini adalah contoh berjalan untuk Bagian IV playbook integrasi Agentic AI (Bab 10 sampai 13).
Keterbandingan antar-solusi lebih penting daripada kelengkapan fitur.

Keluaran agent adalah indikasi untuk ditelaah analis, bukan kesimpulan pelanggaran.

## Yang dibuktikan

- Tiga solusi agent yang berbeda bisa diintegrasikan lewat satu kontrak (`IAgentClient` dan katalog event).
- Front end bisa menangani teks mengalir, progres tool, approval, stop, error, dan proses panjang tanpa
  tahu solusi mana yang bekerja.
- Contract test yang sama lulus untuk keempat adapter. Hasilnya ada di
  [`contracts/COMPATIBILITY.md`](contracts/COMPATIBILITY.md).

## Dua use case

1. Investigator UMA: satu agent menyelidiki satu saham yang harga atau volumenya menyimpang, lalu
   menyusun draf memo. Agent berhenti sampai analis memilih setuju, revisi, atau tolak.
2. Deteksi akumulasi-distribusi terpola: job asinkron. Orchestrator (kode biasa) membagi kandidat ke
   worker saham, broker, dan kepemilikan, mengikuti jejak baru sampai batas kedalaman, lalu menyusun
   laporan yang menunggu approval.

## Arsitektur

```text
Browser ── SSE / polling / SignalR ──> Surveillance.Web (MVC)
                                          │  IAgentClient
            ┌──────────────┬──────────────┼──────────────────┐
         Fake           MAF (in-process)  LangGraph :8101    ADK :8102
                        gpt-5-mini        qwen3.8-27b        gemini-3.8-flash
                           │              (HTTP + SSE)       (HTTP + SSE)
                           └──────────────┴────────┬─────────┘
                                         MCP idx server :8765 (hanya baca)
```

| Komponen | Lokasi | Bahasa | Model |
|---|---|---|---|
| Kontrak | `contracts/`, `app/Surveillance.Agents.Abstractions` | JSON Schema, C# | - |
| Aplikasi MVC | `app/Surveillance.Web` | C# (.NET 10) | - |
| Test | `app/Surveillance.Web.Tests` | C# (xUnit) | - |
| Solusi 1: MAF | `agents/maf` | C# | `gpt-5-mini` (OpenAI) |
| Solusi 2: LangGraph | `agents/langgraph` | Python | `qwen/qwen3.8-27b` (OpenRouter) |
| Solusi 3: Google ADK | `agents/adk` | Python | `gemini-3.8-flash` (AI Studio) |
| Agent tiruan | `FakeAgentClient` di aplikasi | C# | tanpa model |

Database aplikasi adalah sumber kebenaran untuk kasus, event, dan approval. Pemetaan event bawaan tiap
framework ke katalog event ada di `EVENT-MAPPING.md` masing-masing solusi.

## Yang dibutuhkan

- .NET SDK 10
- Python 3.13 (untuk LangGraph dan ADK)
- [idx](https://github.com/fzn0x/idx) 0.8.4 atau lebih baru, untuk server MCP data pasar
- SQL Server atau Azure SQL
- Windows dengan PowerShell (skrip di `scripts/`)

Untuk mencoba aplikasi dengan agent tiruan, cukup .NET SDK dan database.

## Menjalankan dengan agent tiruan

Agent tiruan (`fake`) tidak memanggil model maupun data pasar. Semua halaman demo di `/lab` hanya aktif
dengan agent ini.

1. Simpan connection string database di user-secrets:

   ```powershell
   cd app\Surveillance.Web
   dotnet user-secrets set ConnectionStrings:Default "<connection string>"
   ```

2. Jalankan aplikasi. Migrasi database diterapkan saat aplikasi mulai.

   ```powershell
   dotnet run --project app\Surveillance.Web
   ```

3. Buka http://localhost:5272, lalu masuk dengan salah satu akun demo: `analis1`, `analis2`, atau
   `supervisor`. Login ini hanya untuk demo dan tidak memakai kata sandi.

## Menjalankan dengan agent sungguhan

Rahasia tidak pernah ditulis ke file yang di-commit.

| Nama | Disimpan di | Dipakai oleh |
|---|---|---|
| `IDX_MCP_TOKEN` | environment variable User | server MCP dan ketiga agent |
| `OPENAI_API_KEY` | user-secrets `app/Surveillance.Web` | MAF |
| `OPENROUTER_API_KEY`, `MODEL_BASE_URL`, `MODEL_NAME` | `agents/langgraph/.env` | LangGraph |
| `GOOGLE_API_KEY`, `MODEL_NAME` | `agents/adk/.env` | ADK |

Langkahnya:

1. Buat token acak untuk MCP dan simpan sebagai environment variable User `IDX_MCP_TOKEN`.
2. Isi API key MAF: `dotnet user-secrets set OPENAI_API_KEY "<key>"` di folder `app\Surveillance.Web`.
3. Salin `agents/langgraph/.env.example` dan `agents/adk/.env.example` menjadi `.env`, lalu isi API key.
4. Buat virtual environment untuk tiap service Python:

   ```powershell
   cd agents\langgraph   # ulangi untuk agents\adk
   py -3.13 -m venv .venv
   .venv\Scripts\python -m pip install -r requirements.txt
   ```

5. Jalankan aplikasi dengan `dotnet run --project app\Surveillance.Web` atau dari Visual Studio. Di
   lingkungan Development, aplikasi menjalankan `scripts\start-all.ps1` sendiri, sehingga server MCP,
   LangGraph, dan ADK ikut menyala, masing-masing di jendela sendiri. Service yang sudah berjalan
   tidak dinyalakan lagi, dan service tetap berjalan setelah aplikasi berhenti; tutup jendelanya untuk
   menghentikan. Aplikasi juga membaca `IDX_MCP_TOKEN` dari environment User bila terminalnya dibuka
   sebelum token disimpan. Matikan dengan `LocalServices:AutoStart = false` di
   `appsettings.Development.json`.

   Service juga bisa dinyalakan tanpa aplikasi:

   ```powershell
   .\scripts\start-all.ps1                 # MCP, LangGraph, dan ADK saja
   .\scripts\start-all.ps1 -Provider maf   # ditambah aplikasi web
   ```

6. Solusi aktif bisa diganti di halaman Setting (`/settings`, hanya untuk supervisor). Pilihan berlaku
   untuk investigasi baru; run yang sedang berjalan tetap memakai solusi awalnya.

Beberapa data broker dan aksi korporasi memakai sesi Stockbit milik pengguna. Login Stockbit dilakukan
sendiri lewat `idx stockbit login` di browser.

## Menjalankan test

```powershell
dotnet test                                    # semua test .NET, termasuk contract test
cd agents\langgraph; .venv\Scripts\python -m pytest
cd agents\adk;       .venv\Scripts\python -m pytest
```

Contract test menjalankan skenario yang sama terhadap fake, MAF, LangGraph, dan ADK dengan model
tiruan dari `scenarios.json`. Service Python dinyalakan otomatis oleh test. Test terhadap model
sungguhan diberi kategori `Live` dan hanya berjalan bila `RUN_LIVE_TESTS=1`, karena memakai API berbayar.

## Halaman utama

| Halaman | Isi |
|---|---|
| `/investigations` | mulai dan ikuti investigasi UMA |
| `/jobs` | job deteksi pola, dengan satu panel per worker |
| `/approvals` | antrean approval, pengambilan kasus, dan pengalihan |
| `/settings` | memilih solusi agent aktif |
| `/lab` | peragaan transport, pola UI, dan approval dengan agent tiruan |

## Dokumen

| Dokumen | Isi |
|---|---|
| [`CLAUDE.md`](CLAUDE.md) | rencana tahap, aturan, dan catatan keputusan |
| [`contracts/events.schema.json`](contracts/events.schema.json) | katalog event, sumber kebenaran kontrak |
| [`contracts/agent-service-api.md`](contracts/agent-service-api.md) | kontrak HTTP service agent Python |
| [`contracts/asd-investigator-uma.md`](contracts/asd-investigator-uma.md) | spesifikasi agent use case 1 |
| [`contracts/asd-pattern-detection.md`](contracts/asd-pattern-detection.md) | spesifikasi agent use case 2 |
| [`contracts/CHANGE-*.md`](contracts/) | riwayat perubahan kontrak dan alasannya |
| [`contracts/COMPATIBILITY.md`](contracts/COMPATIBILITY.md) | hasil contract test dan perbandingan tiga solusi |
| [`docs/diagrams/use-case-2-flow.drawio`](docs/diagrams/use-case-2-flow.drawio) | alur use case 2 langkah demi langkah, dengan file dan fungsi; buka di draw.io |
| [`docs/BOOK-INDEX.md`](docs/BOOK-INDEX.md) | butir outline Bab 10 sampai 13 dan contohnya di kode |
| [`docs/GAP-BAGIAN-IV.md`](docs/GAP-BAGIAN-IV.md) | status tiap butir dan batas yang disengaja |
| [`docs/TRANSPORTS.md`](docs/TRANSPORTS.md), [`docs/PROXY.md`](docs/PROXY.md) | SSE, polling, SignalR, dan konfigurasi proxy |
| [`docs/PATTERNS.md`](docs/PATTERNS.md) | pola UI agent |
| [`docs/APPROVAL-STATES.md`](docs/APPROVAL-STATES.md) | state machine approval |
| [`docs/RESTART.md`](docs/RESTART.md), [`docs/RETENTION.md`](docs/RETENTION.md) | perilaku saat restart dan retensi data |
| [`docs/ERD.md`](docs/ERD.md), [`docs/schema.sql`](docs/schema.sql) | skema database |

## Batasan

- Login hanya untuk demo. Penerapan sungguhan perlu penyedia identitas organisasi.
- Ambang deteksi adalah nilai awal untuk demo, bukan standar pengawasan.
- Data bandar belum tersedia selama sumbernya (IPOT) membalas HTTP 502.
- Data pasar berasal dari sumber publik yang tertunda; broker summary hanya sepuluh besar per sisi
  tanpa sesi Stockbit, dan data kepemilikan bersifat bulanan untuk pemegang di atas 1%.
