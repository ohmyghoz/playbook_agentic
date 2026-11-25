# Spesifikasi agent: Deteksi akumulasi-distribusi terpola

Agent ini mencari pola akumulasi dan distribusi yang terkoordinasi di beberapa
saham sekaligus. Satu orchestrator membagi kandidat ke worker saham, worker
broker, dan worker kepemilikan. Worker broker bisa menemukan saham baru, yang
menjadi jejak berikutnya sampai batas kedalaman.

Use case ini berjalan sebagai job asinkron. Analis memulai job, boleh menutup
browser, lalu menelaah laporannya. Laporan berhenti di `approval_required`
sampai analis memutuskan setuju, revisi, atau tolak.

Keluaran agent adalah indikasi untuk ditelaah analis, bukan kesimpulan
pelanggaran.

## Input

`InvestigationRequest` dengan `useCase: "pattern_detection"` dan `job`
(`$defs/PatternJob` di `events.schema.json`):

| Field | Isi |
|---|---|
| `windowStart`, `windowEnd` | Jendela pengamatan, 10 hari bursa |
| `candidates` | Hasil penyaringan awal: kode saham dan perubahan harga dalam jendela |
| `limits` | Batas yang ditegakkan agent, dari konfigurasi `PatternDetection` |

Penyaringan awal bukan agent. Aplikasi menjalankannya sebagai kode biasa
(`Patterns/CandidateScreening.cs`):

1. `idx_screen` dengan `ret1m>=15` dan `ret1m<=-15` untuk mencari saham yang bergerak besar dalam sebulan.
2. `idx_history` untuk menghitung perubahan harga 10 hari bursa terakhir setiap saham itu.
3. Simpan saham dengan perubahan minimal 15% (naik atau turun), urutkan dari yang terbesar, dan ambil paling banyak 10.

## Peran dan tool

Tool yang dipakai hanya tool baca MCP IDX.

| Peran | `agentName` | Tool |
|---|---|---|
| Orchestrator | `orchestrator` | Tidak memanggil tool data. Hanya mendelegasikan, menilai temuan, dan menyusun laporan. |
| Worker saham | `saham-<KODE>` | `idx_history`, `idx_technical`, `idx_broker`, `idx_bandar`, `idx_foreign`, `idx_news`, `idx_stockbit_actions` |
| Worker broker | `broker-<KODE>` | `idx_broker`, `idx_market_brokers`, `idx_members`, `idx_screen` |
| Worker kepemilikan | `kepemilikan-<n>` | `idx_ownership_ticker`, `idx_ownership_graph`, `idx_ownership_entity`, `idx_ownership_changes` |

Laporan diajukan lewat tool lokal `submit_report`, yang butuh persetujuan
analis. Bentuknya sama dengan `submit_memo` di investigator UMA, tetapi
isinya `PatternReport`.

`idx_bandar_screen` dan `idx_bandar_broker` tidak dipakai selama IPOT membalas
HTTP 502 (Catatan keputusan 25 November 2026). Worker broker mencari jejak
dengan memanggil `idx_broker` pada saham kandidat dan pada saham ramai dari
`idx_screen` (preset `high-volume`).

## Alur kerja

1. Orchestrator membaca kandidat dan memulai satu worker saham per kandidat.
2. **Worker saham** memeriksa pola harga, volume, dan konsentrasi broker satu saham. Logikanya sama dengan investigator UMA, tanpa langkah memo. Worker ini melaporkan tiga broker pembeli teratas bila porsi beli bersihnya mencapai `minTopBuyerSharePct`.
3. Untuk setiap broker dominan, paling banyak `brokersPerStock`, orchestrator memulai **worker broker**. Worker itu memeriksa di saham lain mana saja broker yang sama menunjukkan pola serupa. Saham yang ditemukan diusulkan sebagai jejak baru (`leads` di `worker_finished`).
4. Orchestrator mengikuti jejak baru dengan worker saham pada kedalaman berikutnya. Ini berlaku selama kedalaman belum melewati `maxDepth` dan jumlah saham belum melewati `maxStocks`.
5. Orchestrator mengelompokkan saham yang berbagi broker dominan menjadi klaster. Untuk setiap klaster, **worker kepemilikan** memeriksa apakah saham-saham itu punya pemegang saham yang sama atau terafiliasi.
6. Orchestrator menyusun laporan, lalu memanggil `submit_report` dan menunggu keputusan analis.

Orchestrator tidak mengikuti broker yang aktif di lebih dari
`maxBrokerSpreadPct` (30%) saham kandidat, karena broker sebesar itu hampir
selalu muncul di mana-mana. Pengecualiannya: porsi beli bersih broker itu di
saham tersebut melewati `minTopBuyerSharePct`. Setiap jejak yang tidak diikuti
dicatat di `leadsNotFollowed` beserta alasannya.

## Event

Semua event katalog berlaku seperti di investigator UMA, dengan tambahan
berikut:

- **`agentName` di setiap event.** Isinya `orchestrator` untuk event milik run (`run_started`, `approval_required`, `done`, `error`, dan narasi orchestrator), atau id worker untuk event worker.
- **`run_started.useCase`** bernilai `pattern_detection`. `symbol` tidak diisi, dan `maxToolCalls` berlaku untuk seluruh job.
- **`worker_started`** dipancarkan sebelum event pertama sebuah worker. Isinya `workerId` (sama dengan `agentName`), `role`, `target`, `depth`, dan `reason`.
- **`worker_finished`** dipancarkan setelah event terakhir worker itu. Isinya `status` (`ok`, `error`, `cancelled`, `limit_reached`), `summary`, dan `leads`.
- **`step_started` tidak dipakai.** Lima tahap ASD UMA tidak berlaku di sini; progres dibaca dari worker.

Aturan urutan tambahan:

- Setiap `worker_started` punya tepat satu `worker_finished` dengan `workerId` yang sama, sebelum `approval_required` atau sebelum event akhir run.
- Setiap `tool_call_started` dan `tool_call_finished` milik worker membawa `agentName` worker itu, dan hanya muncul di antara `worker_started` dan `worker_finished` worker tersebut.
- Worker boleh berjalan berselang. Event beberapa worker boleh bercampur, tetapi `sequence` tetap satu deret untuk seluruh job.

## Batas

Batas diambil dari `job.limits` dan ditegakkan di kode, bukan hanya di prompt.

| Batas | Nilai awal | Saat tercapai |
|---|---|---|
| Kedalaman rantai | 2 | Jejak berikutnya dicatat di `leadsNotFollowed` |
| Saham per job, termasuk jejak baru | 15 | Sama |
| Broker yang diikuti per saham | 3 | Sama |
| Langkah per worker | 12 tool call | Worker selesai dengan `status: limit_reached` |
| Tool call per job | 150 | Worker yang berjalan ditutup `limit_reached`, lalu orchestrator menyusun laporan |
| Durasi per job | 20 menit | Sama |

Batas yang tercapai tidak membuat job gagal. Laporan disusun dari temuan yang
ada, dan batas itu dicatat di `limitsReached`.

## Struktur laporan

`PatternReport` di `events.schema.json`:

1. `summary`: ringkasan indikasi, ditambah `counts` (klaster, saham, broker) dan periode pengamatan (`periodStart`, `periodEnd`).
2. `clusters`: per klaster, saham, broker dominan, kronologi akumulasi dan distribusi, serta keterkaitan kepemilikan bila ada.
3. `findings` dalam setiap klaster: setiap pernyataan merujuk ke tool call yang mendasarinya (`evidence`).
4. `alternativeExplanations`: berita, aksi korporasi, atau alasan lain yang wajar.
5. `leadsNotFollowed` dan `limitsReached`.
6. `dataLimitations`, misalnya: broker summary hanya sepuluh besar per sisi, harga tertunda, dan kepemilikan bulanan di atas 1%.
7. `followUps`: usulan tindak lanjut untuk analis.

Tidak boleh ada kalimat yang menyimpulkan manipulasi atau pelanggaran.
`disclaimer` selalu bernilai "Indikasi untuk ditelaah analis, bukan
kesimpulan pelanggaran."

## Keputusan analis, pembatalan, dan error

- **Keputusan analis.** Setuju, revisi, dan tolak berlaku seperti di investigator UMA, termasuk batas 2 revisi. Revisi meminta orchestrator memperbaiki laporan. Orchestrator boleh memulai worker baru selama batas job belum habis.
- **Pembatalan.** Tombol Hentikan menutup setiap worker yang berjalan dengan `worker_finished` (`status: cancelled`) setelah tool call terbukanya ditutup, lalu run berakhir dengan `done(cancelled)`.
- **Error.** Kode error sama dengan katalog. Worker yang gagal tidak menggagalkan job: worker selesai dengan `status: error`, dan orchestrator mencatatnya di `dataLimitations`.

## Implementasi

| Solusi | Status |
|---|---|
| Agent tiruan (`FakeAgentClient`) | Tahap 13: orchestrator, tiga jenis worker yang berjalan berselang, satu jejak baru, lalu approval laporan |
| MAF, LangGraph, ADK | Tahap 14 |
