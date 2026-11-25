# CHANGE-003: kode error `run_interrupted`

Status: diterapkan di Tahap 12 (25 November 2026), sesuai rencana Tahap 12c:
"ganti `error internal` dengan kode error yang jelas dan bisa diulang".

Nomor 003 dipakai karena CHANGE-002 dicadangkan untuk perubahan multi-agent di
Tahap 13.

## Kebutuhan

Run yang terputus karena aplikasi atau service agent dinyalakan ulang dulu
ditutup dengan `error` kode `internal`. Kode itu juga dipakai untuk bug dan
kegagalan tak terduga lain. Akibatnya analis tidak tahu apakah mengulang akan
berhasil, dan operator tidak bisa memisahkan gangguan karena restart dari
kerusakan sungguhan di log.

## Perubahan

- **`ErrorPayload.code`** mendapat satu nilai baru, `run_interrupted`. Nilai itu dipakai saat run hilang atau terputus di tengah langkah karena proses berhenti, dan selalu dengan `retryable: true`.
- **Fake.** Bila agent tidak lagi mengenal run setelah restart, aplikasi sendiri yang menulis error ini (`RunPump`, `afterRestart`).
- **MAF.** Adapter menulisnya saat menemukan run berstatus `Running` dari proses sebelumnya.
- **LangGraph dan ADK.** `RunManager.recover` menulisnya untuk run yang sedang berjalan saat service berhenti.

`IAgentClient` dan `agent-service-api.md` tidak berubah.

## Kompatibilitas ke belakang

- Perubahan bersifat menambah: nilai `enum` baru, tanpa field baru.
- Event lama dengan `internal` tetap sah.
- Klien yang memvalidasi dengan skema lama akan menolak `run_interrupted`, jadi skema dan adapter diubah dalam satu commit, seperti CHANGE-001.
- Front end tidak perlu perubahan khusus. Banner galat sudah menampilkan tombol mulai ulang untuk setiap error dengan `retryable: true`.

Perilaku lengkap per solusi ada di `docs/RESTART.md`.
