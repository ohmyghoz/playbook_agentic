# CHANGE-001: progres tahap dan bukti data

Status: diterima dan diterapkan di keempat adapter (Tahap 8, 25 November 2026).

Dokumen ini mencatat satu putaran negosiasi kontrak event antara front end dan
tim agent: apa yang dibutuhkan front end, kenapa tidak cukup tanpa mengubah
kontrak, apa yang diubah, dan bagaimana perubahan itu tetap aman untuk klien
lama.

## Ringkasan perubahan

- **Event baru `step_started`** dengan `step` (kode tahap dari ASD) dan `label`.
- **Dua field opsional di `tool_call_finished`**:
  - `source`, yaitu asal data: `server`, `provider`, `fetchedAt`, dan `stale`.
  - `resultPreview`, yaitu keluaran mentah tool, paling banyak 8192 byte UTF-8.
- **Tidak berubah:** `IAgentClient` dan `agent-service-api.md`. Endpoint juga tidak bertambah.

## Kebutuhan front end

Analis yang menunggu investigasi perlu tahu dua hal yang tidak terlihat dari
kontrak lama.

1. **Agent sedang di tahap mana.** Alur ASD punya urutan yang jelas: ukur
   penyimpangan, cari penjelasan, gali broker dan arus asing, nilai bukti, lalu
   memo. Front end ingin menampilkan daftar tahap yang bergerak, bukan hanya
   daftar tool call.
2. **Data apa yang mendasari sebuah temuan.** Memo merujuk `toolCallId`.
   Analis ingin membuka panggilan itu dan melihat sumber data, waktu data
   diambil, dan isinya. Kontrak lama hanya membawa `summary`, yaitu ringkasan
   paling panjang 500 karakter. ASD lama bahkan melarang keluaran mentah
   sampai ke front end.

## Alternatif tanpa perubahan kontrak, dan kenapa tidak cukup

| Alternatif | Kenapa tidak cukup |
|---|---|
| Front end menebak tahap dari nama tool | Aturan pemetaan ada di JavaScript, terpisah dari ASD. Setiap tool baru harus diubah di dua tempat. Tahap `assess` dan `memo` tidak punya tool yang bisa ditebak dari sisi browser. |
| Front end membaca tahap dari narasi `token` | Narasi ditulis model dan berbeda per model. Qwen menulis narasi di giliran terpisah, sedangkan gpt-5-mini awalnya tidak menulis narasi sama sekali. Tidak bisa diandalkan. |
| Memasukkan sumber dan waktu data ke `summary` | `summary` untuk dibaca manusia dan dibatasi 500 karakter. Front end harus mengurai teks bebas, dan isi mentah tetap tidak tersedia. |
| Endpoint baru di service agent untuk data mentah | Sudah ditolak di Keputusan kontrak: data mentah ikut di event, lalu aplikasi yang menyimpan dan menyajikannya. Endpoint baru juga harus dibuat di MAF yang tidak punya HTTP. |

## Usulan yang diterapkan

### `step_started`

```json
{ "type": "step_started", "payload": { "step": "deep_dive", "label": "Menggali broker dan arus asing" } }
```

| `step` | Langkah ASD | Kapan dipancarkan |
|---|---|---|
| `measure` | 1 dan 2 | Tepat setelah `run_started` |
| `explain` | 3 | Sebelum tool pertama: `idx_news`, `idx_stockbit_actions`, `idx_corporate_actions` |
| `deep_dive` | 4 | Sebelum tool pertama: `idx_broker`, `idx_bandar`, `idx_foreign`, `idx_stockbit_movements` |
| `assess` | 5 | Sebelum `tool_call_started` untuk `submit_memo` yang pertama |
| `memo` | 6 | Sebelum `approval_required` yang pertama |

Aturannya:

- Tahap hanya maju, boleh dilompati, dan muncul paling banyak sekali per run.
- Tool yang dipanggil saat revisi tetap berada di tahap `memo`.

**Siapa yang menentukan tahap.** Adapter yang menentukan tahap dari nama
tool, bukan model. Model tidak diberi tool `report_step`. Alasannya:

- Urutan tahap harus sama di keempat solusi.
- Contract test harus bisa memeriksa urutan itu.
- Model yang lupa melapor akan membuat progres macet.

Aturan ditulis sekali per bahasa:
- `Surveillance.Agents.Abstractions/InvestigationSteps.cs` untuk fake dan MAF;
- `agents/*/app/steps.py` untuk LangGraph dan ADK.

**Batasan yang disadari.** Model menilai bukti dan menyusun memo dalam satu
panggilan, sehingga `assess` dan `memo` muncul berdekatan. Tahap `assess`
menandai saat penilaian selesai dan memo diajukan, bukan saat model mulai
menilai. Narasi model sebelum sebuah tool bisa muncul sebelum `step_started`
untuk tool itu, karena teks mengalir sebelum model memutuskan tool.

### `source` dan `resultPreview`

```json
{
  "type": "tool_call_finished",
  "payload": {
    "toolCallId": "call_3", "toolName": "idx_quote", "status": "ok",
    "summary": "Harga 5.800 (+2,2%), volume 235.635.300 lembar, 1,1x rata-rata 3 bulan.",
    "source": { "server": "idx", "provider": "yahoo", "fetchedAt": "2026-11-24T14:09:39Z", "stale": false },
    "resultPreview": "{\"schema\":\"idx.quotes.v1\",\"provider\":\"yahoo\", ... }"
  }
}
```

- `source` diambil dari amplop JSON idx (`provider`, `fetched_at`, `stale`). Field yang tidak ada di amplop dibiarkan kosong.
- `resultPreview` berisi teks keluaran tool yang paling dalam, tanpa pembungkus konten MCP dan tanpa awalan `[toolCallId: …]`. Bila lebih dari 8192 byte UTF-8, teks dipotong di batas karakter dan diakhiri penanda `…[dipotong]` di baris baru. Penanda ini ikut dihitung dalam batas.
- Kedua field ada untuk tool MCP yang memberi hasil, baik berhasil maupun gagal dari sumber.
- Kedua field tidak ada untuk `submit_memo` dan untuk panggilan yang ditutup agent sendiri: dibatalkan, batas langkah, atau model gagal.

**Di aplikasi.** `RunPump` melepas `resultPreview` dari event sebelum event
disimpan dan disiarkan (`ResultPreviewSplitter.cs`). Pratinjau disimpan di
tabel `ToolResultPreviews`. Endpoint
`GET /investigations/{id}/tools/{toolCallId}/preview` menyajikannya saat
analis membuka bukti. SSE dan `events.json` tidak pernah membawa data
mentah, jadi aliran ke browser tetap kecil.

## Dampak ke tiap solusi

| Solusi | Perubahan | File |
|---|---|---|
| Fake | Memancarkan `step_started`. Membuat keluaran mentah tiruan dalam amplop idx; `idx_history` sengaja lebih dari 8 KB supaya pemotongan teruji. | `FakeAgentClient.cs` |
| MAF | `Emitter` memancarkan `step_started` di titik yang sama dengan event lain. Summarizer mengisi `source` dan `resultPreview` dari amplop yang sudah ia cari. Tahap terakhir disimpan di `RunState` supaya bertahan setelah jeda approval. | `MafAgentClient.cs`, `ToolResultSummarizer.cs`, `RunState.cs` |
| LangGraph | `_emit` di `RunManager` memancarkan `step_started`. Summarizer mengisi `source` dan `resultPreview`. Tahap disimpan di `RunInfo` (`service.db`). | `runs.py`, `summarizer.py`, `contract.py`, `steps.py` |
| ADK | Sama dengan LangGraph; file bersama disalin apa adanya. | `runs.py`, `summarizer.py`, `contract.py`, `steps.py` |

Graph LangGraph, agent ADK, dan instruksi sistem model tidak berubah.
Perubahan sepenuhnya ada di lapisan pemetaan event.

## Kompatibilitas ke belakang

- **Semua perubahan bersifat menambah.** Tidak ada field yang dihapus atau diganti namanya, dan tidak ada field wajib baru di event yang sudah ada.
- **Klien lama yang tidak mengenal `step_started` cukup mengabaikannya.**
  - JavaScript aplikasi membuang event yang tidak dikenalnya.
  - `EventSource` tidak memanggil listener untuk tipe yang tidak didaftarkan, tetapi `Last-Event-ID` tetap ikut maju.
  - `sequence` tetap naik tanpa lompatan, karena `step_started` memakai nomor seperti event lain.
- **Event lama di database tetap sah terhadap skema baru**, karena `source` dan `resultPreview` opsional. Halaman riwayat memutar kasus lama tanpa perubahan.
- **Klien yang memvalidasi dengan skema lama akan menolak `step_started`.** Skema lama memakai `enum` untuk `type`, dan `additionalProperties: false` di `tool_call_finished`. Karena itu skema dan adapter diubah bersamaan dalam satu commit. Untuk perubahan berikutnya: klien yang memvalidasi skema sebaiknya memperbarui skema lebih dulu, baru adapter.

## Cara memeriksa

Contract test di `app/Surveillance.Web.Tests/Contract/` berjalan untuk keempat adapter:

- `EventAssertions.AssertStepsAndEvidence`, dipanggil di setiap pemeriksaan run, memastikan:
  - `measure` tepat setelah `run_started`;
  - tahap hanya maju;
  - `assess` sebelum `submit_memo` dan `memo` sebelum `approval_required`;
  - setiap hasil MCP punya `source`;
  - `resultPreview` tidak lebih dari 8192 byte.
- `Steps_move_forward_and_mcp_results_carry_source_and_preview` memeriksa urutan tahap, isi `source`, dan pemotongan pratinjau `idx_history`.
- Test live memastikan setiap hasil MCP dari model sungguhan membawa `resultPreview`.

Hasilnya tercatat di `COMPATIBILITY.md`.
