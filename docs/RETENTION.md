# Retensi dan pembersihan data

`RetentionService` berjalan setiap `Retention:CheckMinutes` (bawaan 6 jam).
Setiap langkahnya aman dijalankan ulang: putaran kedua tidak mengubah apa pun
yang sudah dibersihkan.

## Kebijakan di aplikasi

| Data | Aturan (bawaan) | Pengaturan |
|---|---|---|
| Pratinjau data mentah (`ToolResultPreviews`) | Dihapus 30 hari setelah disimpan | `Retention:PreviewDays` |
| Event `token` kasus yang sudah selesai | Digabung 60 menit setelah kasus selesai | `Retention:CompactAfterMinutes` |
| Semua event kasus yang sudah selesai | Dihapus 90 hari setelah kasus selesai | `Retention:EventDays` |
| File state MAF (`App_Data/maf-runs/*.json`) | Dihapus bila tidak diubah selama 30 hari | `Retention:MafRunDays` |

Yang tidak pernah dihapus: baris kasus, keputusan analis (`Decisions`), dan
catatan audit (`CaseAuditRecords`). Pertanyaan "siapa memutuskan apa" tetap
bisa dijawab setelah event dibuang.

### Penggabungan token

**Kenapa digabung.** Satu run menghasilkan puluhan event `token` berisi
potongan narasi sekitar 60 karakter. Setelah run selesai, potongan itu tidak
lagi dibutuhkan satu per satu.

**Cara menggabungkan.** Setiap rangkaian event `token` yang berurutan
digabung ke event pertamanya, lalu sisanya dihapus.

**Dampaknya:**

- Narasi utuh tetap sama, dan semua event selain `token` tetap ada.
- Setelah penggabungan, `sequence` yang tersimpan punya lompatan. Ini diterima karena run sudah selesai, sehingga tidak ada klien yang menyambung kembali di tengah run.
- Halaman riwayat dan `events.json` membuang event berdasarkan `sequence` terakhir yang tampil, sehingga lompatan tidak mengganggu.
- Putar ulang dengan kecepatan "waktu asli" menjadi lebih kasar, karena narasi muncul per giliran model, bukan per potongan.

Kasus yang sudah digabung ditandai `TokensCompactedAt`.

### Penghapusan event

Event dan pratinjau kasus yang selesai lebih dari 90 hari dihapus, lalu kasus
ditandai `EventsPurgedAt`. Halaman investigasi untuk kasus itu tetap terbuka
dan menampilkan keputusan, tetapi narasi dan panggilan data sudah tidak ada.

## Service Python

Service LangGraph dan ADK menyimpan state sendiri di folder datanya. Tahap ini
tidak menjadwalkan pembersihan otomatis untuk keduanya. Setelah sebuah run
selesai, state itu tidak lagi dibaca, jadi aman dibersihkan berkala
(misalnya lewat Task Scheduler). Jalankan saat service mati, atau saat tidak
ada run yang menunggu approval lebih dari batas waktu aplikasi (24 jam).

### LangGraph (`agents/langgraph/data/`)

- **`service.db`**: run dan event per run, tabel `runs` dan `events`. Kolom `body` di `runs` adalah JSON dengan `status`.

  ```sql
  -- service.db: run yang selesai, beserta event-nya
  DELETE FROM events WHERE run_id IN (SELECT run_id FROM runs WHERE json_extract(body, '$.status') = 'finished');
  DELETE FROM runs WHERE json_extract(body, '$.status') = 'finished';
  VACUUM;
  ```

- **`checkpoints.db`**: checkpoint `AsyncSqliteSaver`. Thread-nya sama dengan `run_id`. Hapus checkpoint untuk `run_id` yang sudah dihapus dari `service.db`. Bila tidak ada run yang sedang menunggu, file ini boleh dihapus seluruhnya; service membuatnya lagi saat start.

### ADK (`agents/adk/data/`)

- **`service.db`**: sama dengan LangGraph.
- **`sessions.db`**: `DatabaseSessionService`. Session ADK memakai `run_id` sebagai id session. Aturannya sama dengan `checkpoints.db`.

## Kode dan test

| Bagian | File |
|---|---|
| Layanan dan pengaturan | `Investigations/RetentionService.cs` (`RetentionOptions`) |
| Query | `Data/Repositories/EfRepositories.cs` (`DeletePreviewsOlderThanAsync`, `CompactTokensAsync`, `PurgeEventsAsync`) |
| Test dengan data berumur buatan | `DurabilityTests.Retention_compacts_tokens_deletes_old_data_and_is_idempotent` |
