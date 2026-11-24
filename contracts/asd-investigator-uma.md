# Spesifikasi agent: Investigator UMA

Dokumen ini mengatur perilaku agent Investigator UMA di ketiga solusi (MAF,
LangGraph, Google ADK). Ketiganya harus mengikuti dokumen yang sama supaya
hasilnya bisa dibandingkan.

Ringkasnya:

- agent menyelidiki satu saham yang harga atau volumenya menyimpang pada satu tanggal bursa.
- agent hanya membaca data lewat MCP IDX, lalu menyusun draf memo.
- agent berhenti sampai analis memutuskan: setuju, revisi, atau tolak.
- memo berisi indikasi untuk ditelaah analis, bukan kesimpulan pelanggaran.
- agent boleh memanggil tool MCP paling banyak 15 kali per run, dan memo boleh direvisi paling banyak 2 kali.

Bentuk data yang disebut di sini didefinisikan di "events.schema.json". Tool
MCP dan bentuk keluarannya dijelaskan di "mcp-tools.md".

## Input

Aplikasi memulai run dengan `InvestigationRequest` (lihat
`$defs/InvestigationRequest` di skema).

| Field | Wajib | Isi |
|---|---|---|
| `caseId` | ya | ID kasus di database aplikasi |
| `symbol` | ya | Kode saham BEI, empat huruf kapital, misalnya `BBCA` |
| `anomalyDate` | ya | Tanggal bursa saat penyimpangan terdeteksi, `YYYY-MM-DD` |
| `trigger.kind` | ya | `price`, `volume`, atau `both` |
| `trigger.rule` | ya | Ambang yang terlampaui, misalnya "volume > 3x rata-rata 20 hari" |
| `trigger.priceChangePct`, `volume`, `avgVolume`, `volumeRatio` | tidak | Angka yang teramati oleh kode deteksi |
| `notes` | tidak | Catatan analis untuk agent |

Kode deteksi awal di aplikasi yang menentukan saham mana yang menyimpang. Agent
tidak memilih saham sendiri.

Jika input tidak sesuai skema, agent tidak memanggil model. Run langsung
ditutup dengan `error` berkode `invalid_input`.

## Tool yang boleh dipakai

Semua tool MCP hanya membaca data. Agent tidak boleh punya tool yang menulis ke
sistem luar.

| Tool | Kapan dipakai | Catatan |
|---|---|---|
| `idx_stockbit_status` | sekali di awal | Jika `logged_in=false`, lewati semua tool Stockbit |
| `idx_quote` | selalu | Harga, volume, dan rata-rata volume 3 bulan |
| `idx_history` | selalu | Baseline harga dan volume, `period: 3mo` |
| `idx_news` | selalu | Pakai `company_only: true` |
| `idx_stockbit_actions` | selalu, jika sesi Stockbit aktif | Ambil aksi korporasi di sekitar `anomalyDate` |
| `idx_corporate_actions` | jika sesi Stockbit tidak aktif | Cadangan dari IPOT |
| `idx_foreign` | bila perlu | Pakai `source: idx` |
| `idx_broker` | bila perlu | Pakai `date: anomalyDate` |
| `idx_bandar` | bila perlu | Hanya IPOT, bisa gagal |
| `idx_stockbit_movements` | bila perlu | Transaksi insider dan pemegang ≥5% |
| `submit_memo` | sekali per draf | Tool lokal. Memicu `approval_required` |

"Bila perlu" berarti agent memakainya jika harga, volume, berita, dan aksi
korporasi belum menjelaskan penyimpangan.

Tool yang tidak boleh dipakai:

- `idx_orderbook`, karena datanya realtime dan tidak relevan untuk kejadian yang sudah lewat.
- `idx_intraday`, karena belum diuji di Tahap 1.
- `idx_stockbit_login` dan tool MCP lain yang tidak ada di tabel.

Adapter mendaftarkan hanya tool di tabel ke agent. Daftar ini menjadi
allowlist, bukan sekadar saran di prompt.

### Tool lokal `submit_memo`

Argumennya satu objek `memo` dengan bentuk `$defs/Memo`. Tool ini tidak
menulis ke sistem luar. Adapter menjeda run, lalu memancarkan
`approval_required` berisi memo tersebut. Tool ini butuh persetujuan analis
sebelum dianggap selesai.

Urutan event untuk `submit_memo`:

1. `tool_call_started` dengan `toolName: submit_memo`.
2. `approval_required`. Run berhenti di sini.
3. Setelah analis memutuskan, `tool_call_finished` untuk `submit_memo` dengan `status: ok` dan ringkasan keputusan.
4. Lalu `done`, `error`, atau langkah revisi.

Jika run dihentikan saat menunggu keputusan, `tool_call_finished` untuk
`submit_memo` dipancarkan dengan `status: error` dan kode `cancelled`.

Jika memo tidak sesuai skema, adapter mengembalikan pesan kesalahan ke model
sebagai hasil tool, dan model memperbaikinya. Panggilan ini tidak dihitung
sebagai revisi.

## Alur kerja

Agent bekerja dengan urutan ini:

1. Pertama, panggil `idx_stockbit_status`.
2. Ukur penyimpangan dengan `idx_quote` dan `idx_history`. Bandingkan volume dan perubahan harga pada `anomalyDate` dengan rata-rata 3 bulan.
3. Cari penjelasan wajar dengan `idx_news` dan aksi korporasi.
4. Jika penjelasan wajar belum ditemukan, gali lebih dalam dengan `idx_broker`, `idx_bandar`, `idx_foreign`, dan `idx_stockbit_movements`.
5. Nilai apakah bukti cukup, lalu tentukan tingkat bukti dan saran tindak lanjut.
6. Terakhir, panggil `submit_memo` dan tunggu keputusan analis.

Selama bekerja, agent menulis narasi singkat tentang apa yang sedang
diperiksa dan apa yang ditemukan. Narasi ini menjadi event `token`.

Jika sebuah tool gagal, agent tidak berhenti. Agent mencatat tool itu di
`unavailableData` dan tidak menarik indikasi dari data yang tidak ada.
Error `blocked` boleh dicoba lagi sekali. Error lain tidak diulang.

## Event yang dipancarkan

Setiap event punya `runId`, `sequence`, `timestamp`, `type`, dan `payload`.

| `type` | Kapan muncul |
|---|---|
| `run_started` | Event pertama setiap run |
| `token` | Potongan narasi agent saat model mengalirkan teks |
| `tool_call_started` | Sebelum tool MCP atau `submit_memo` dijalankan |
| `tool_call_finished` | Setelah tool selesai, berhasil atau gagal |
| `approval_required` | Saat `submit_memo` dipanggil dengan memo yang sah |
| `done` | Analis menyetujui atau menolak, atau pengguna menghentikan run |
| `error` | Run gagal dan tidak bisa dilanjutkan |

Aturan urutan:

- `sequence` dimulai dari 1 dan naik tepat 1 per event, tanpa lompatan. Nomor ini berlanjut setelah run dilanjutkan dari jeda approval.
- `run_started` selalu menjadi event pertama.
- setiap `tool_call_started` punya tepat satu `tool_call_finished` dengan `toolCallId` yang sama. Pasangan ini tidak harus berurutan langsung jika tool dipanggil paralel.
- tidak ada `tool_call_started` baru selama run menunggu keputusan analis.
- setiap run ditutup oleh tepat satu `done` atau `error`. Setelah itu tidak ada event lagi.
- `approval_required` boleh muncul lebih dari sekali. Setiap keputusan `revise` menghasilkan draf baru dengan `revision` naik 1.

`tool_call_finished.summary` berisi ringkasan hasil dalam bahasa Indonesia,
paling panjang 500 karakter, misalnya "Harga 5.800 (+2,2%), volume 1,1x
rata-rata 3 bulan". Keluaran mentah tool tidak dikirim ke front end.

## Struktur memo

Memo mengikuti `$defs/Memo`. Teks memo ditulis dalam bahasa Indonesia.

| Field | Isi |
|---|---|
| `symbol`, `anomalyDate` | Sama dengan input |
| `summary` | Ringkasan indikasi dalam 2 hingga 4 kalimat |
| `anomaly` | Angka penyimpangan dan deskripsinya |
| `findings` | Daftar temuan. Setiap temuan punya `category`, `statement`, dan minimal satu `evidence` |
| `unavailableData` | Tool yang gagal atau dilewati, beserta alasannya |
| `evidenceLevel` | `strong`, `moderate`, `weak`, atau `insufficient` |
| `evidenceRationale` | Alasan tingkat bukti |
| `recommendation` | `escalate`, `monitor`, atau `close` |
| `recommendationRationale` | Alasan saran tindak lanjut |
| `disclaimer` | Selalu "Indikasi untuk ditelaah analis, bukan kesimpulan pelanggaran." |

Kategori temuan: `price_volume`, `news`, `corporate_action`, `broker`,
`bandar`, `foreign`, `insider`.

Setiap `evidence` merujuk `toolCallId` dari run yang sama. Dengan begitu
analis bisa menelusuri setiap klaim ke tool call yang menghasilkannya. Agent
tidak boleh menulis angka yang tidak berasal dari keluaran tool.

Panduan tingkat bukti:

| Tingkat | Artinya |
|---|---|
| `strong` | Beberapa sumber independen menunjukkan pola yang sama, dan tidak ada penjelasan wajar |
| `moderate` | Ada pola yang perlu ditelaah, tetapi buktinya dari satu sumber atau sebagian data tidak tersedia |
| `weak` | Penyimpangan kecil, atau ada penjelasan wajar yang sebagian cocok |
| `insufficient` | Data terlalu sedikit untuk menilai |

Saran tindak lanjut:

- `escalate`: analis perlu menelaah lebih lanjut.
- `monitor`: pantau pergerakan pada sesi berikutnya.
- `close`: penyimpangan sudah dijelaskan oleh informasi publik.

## Keputusan analis

Aplikasi melanjutkan run dengan `ApprovalDecision` (lihat
`$defs/ApprovalDecision`).

| `decision` | Komentar | Hasil |
|---|---|---|
| `approve` | tidak wajib | `done` dengan `outcome: approved` dan memo terakhir |
| `reject` | wajib | `done` dengan `outcome: rejected` dan memo terakhir. Komentar disimpan di database aplikasi |
| `revise` | wajib | Agent menerima komentar, boleh memanggil tool lagi, lalu memanggil `submit_memo` dengan draf baru |

`approvalId` dalam keputusan harus sama dengan `approvalId` pada
`approval_required` terakhir. Keputusan untuk approval lama ditolak oleh
adapter tanpa mengubah run.

Revisi dibatasi 2 kali. Jika analis meminta revisi ketiga, run ditutup dengan
`error` berkode `revision_limit_exceeded`. Aplikasi sebaiknya menonaktifkan
tombol revisi setelah `revision` mencapai 2.

Run yang menunggu keputusan tidak punya batas waktu. Run itu harus tetap bisa
dilanjutkan setelah aplikasi atau service agent dinyalakan ulang.

## Batas langkah

Satu langkah adalah satu panggilan tool MCP. Batasnya 15 per run, termasuk
panggilan selama revisi. `submit_memo` tidak dihitung.

Adapter yang menghitung, bukan model. Cara ini sama di ketiga framework,
karena masing-masing menghitung "iterasi" dengan cara yang berbeda.

- Saat 15 panggilan sudah terpakai, adapter tidak menjalankan tool MCP berikutnya. Adapter mengembalikan pesan ke model: "Batas panggilan tool tercapai. Susun memo dari data yang sudah ada."
- Jika model tetap meminta tool MCP setelah pesan itu, run ditutup dengan `error` berkode `step_limit_exceeded`.
- Batas iterasi bawaan framework diatur cukup longgar supaya batas ini yang tercapai lebih dulu.

`run_started.maxToolCalls` berisi angka 15. `done.toolCallsUsed` berisi
jumlah panggilan tool MCP yang benar-benar dijalankan.

## Pembatalan

Pengguna bisa menghentikan run kapan saja, termasuk saat run menunggu
keputusan analis. Adapter menghentikan panggilan model dan tool yang sedang
berjalan, lalu memancarkan `done` dengan `outcome: cancelled`.

Jika ada tool yang sedang berjalan saat dibatalkan, adapter lebih dulu
memancarkan `tool_call_finished` dengan `status: error` dan kode `cancelled`
supaya pasangan tool call tetap lengkap.

## Model error

Error yang menutup run memakai event `error` dengan `code`, `message`, dan
`retryable`. `message` ditulis dalam bahasa Indonesia untuk ditampilkan ke
analis. Pesan ini menjelaskan apa yang terjadi dan apa yang bisa dilakukan,
tanpa detail teknis atau rahasia.

| `code` | Kapan | `retryable` | Contoh `message` |
|---|---|---|---|
| `invalid_input` | Input tidak sesuai skema | `false` | "Data kasus tidak lengkap. Periksa kode saham dan tanggal, lalu mulai lagi." |
| `mcp_unavailable` | Server MCP IDX tidak bisa dihubungi saat run dimulai | `true` | "Server data pasar tidak bisa dihubungi. Coba lagi dalam beberapa menit." |
| `model_error` | Model gagal merespons setelah dicoba ulang, termasuk batas kuota | `true` | "Model AI sedang tidak merespons. Coba lagi dalam beberapa menit." |
| `step_limit_exceeded` | Model tetap meminta tool setelah batas 15 panggilan | `false` | "Agent mencapai batas 15 panggilan data tanpa menyusun memo." |
| `revision_limit_exceeded` | Analis meminta revisi ketiga | `false` | "Memo sudah direvisi 2 kali. Setujui atau tolak draf terakhir." |
| `internal` | Kesalahan lain di adapter atau service | `false` | "Terjadi kesalahan pada agent. Tim teknis sudah bisa melihat log-nya." |

Kegagalan satu tool MCP bukan error run. Kegagalan itu cukup muncul sebagai
`tool_call_finished` dengan `status: error`, lalu dicatat di
`unavailableData`.

Adapter boleh mencoba ulang panggilan model paling banyak 2 kali untuk error
sementara, seperti batas kuota atau HTTP 5xx, sebelum memancarkan
`model_error`.

## Bahasa dan sikap agent

- hasil agent selalu disebut indikasi, bukan bukti pelanggaran.
- hindari kata seperti "manipulasi", "pelanggaran", "pelaku", atau "goreng". Gunakan "pola yang perlu ditelaah" atau "konsentrasi transaksi".
- jangan menyebut nama orang selain yang muncul di keluaran tool. Nama pemegang dari `idx_stockbit_movements` boleh dikutip sebagai data publik.
- sebutkan sumber data, misalnya "broker summary Stockbit, daftar lengkap" atau "IPOT, 10 teratas".
- tulis angka dengan konvensi Indonesia, misalnya 1.367.976.930.000 atau Rp1,37 triliun, dan 2,2%.
- sinyal dari tool, seperti `signal` dari `idx_bandar`, adalah aturan transparan, bukan saran investasi.

## Instruksi sistem

Ketiga solusi memakai instruksi sistem yang sama. Adapter mengganti
`{symbol}`, `{anomalyDate}`, `{triggerRule}`, dan `{notes}` dari input.

```text
Kamu adalah asisten pengawasan pasar modal. Tugasmu menyelidiki saham {symbol}
yang terdeteksi menyimpang pada {anomalyDate} karena: {triggerRule}.
Catatan analis: {notes}

Langkah kerja:
1. Panggil idx_stockbit_status. Jika logged_in=false, jangan pakai tool Stockbit.
2. Ukur penyimpangan dengan idx_quote dan idx_history (period 3mo).
3. Cari penjelasan wajar dengan idx_news (company_only true) dan idx_stockbit_actions.
   Pakai idx_corporate_actions hanya jika logged_in=false.
4. Jika belum ada penjelasan wajar, periksa idx_broker, idx_bandar, idx_foreign,
   dan idx_stockbit_movements.
5. Panggil submit_memo dengan memo sesuai skema.

Aturan:
- Kamu hanya boleh memanggil tool MCP paling banyak 15 kali.
- Setiap temuan harus merujuk toolCallId yang menjadi buktinya.
- Jangan menulis angka yang tidak ada di keluaran tool.
- Jika tool gagal, catat di unavailableData dan lanjutkan.
- Hasilmu adalah indikasi untuk ditelaah analis, bukan kesimpulan pelanggaran.
  Jangan memakai kata manipulasi, pelanggaran, atau pelaku.
- Sebelum memanggil tool, tulis satu kalimat singkat tentang apa yang akan kamu
  periksa dan apa yang sudah kamu temukan.
- Tulis narasi dan memo dalam bahasa Indonesia, singkat dan lugas.
- Saat analis meminta revisi, perbaiki memo sesuai komentarnya lalu panggil
  submit_memo lagi.
```
