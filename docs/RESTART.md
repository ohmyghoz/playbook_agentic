# Perilaku saat aplikasi atau service agent dinyalakan ulang

Saat aplikasi start, `StartupRecovery` memasang ulang `RunPump` untuk setiap
kasus yang sedang `Starting` atau `Running`. Kasus yang `AwaitingApproval`
tidak disentuh, karena run-nya sedang diam dan baru dilanjutkan saat analis
memutuskan.

Ada dua kemungkinan hasil:

- **Run dilanjutkan.** Agent masih mengenal run itu. Event yang sudah tersimpan dilewati, dan event baru disimpan seperti biasa.
- **Run ditutup dengan error `run_interrupted`.** Agent tidak lagi mengenal run itu, atau run-nya terputus di tengah langkah. Error ini bisa diulang (`retryable: true`), sehingga halaman menawarkan **Mulai ulang dengan data yang sama**. Kode ini ditambahkan di Tahap 12 (`contracts/CHANGE-003-run-interrupted.md`); sebelumnya kejadian ini tercatat sebagai `internal`.

## Tabel perilaku per solusi

| Kejadian | Fake | MAF | LangGraph | ADK |
|---|---|---|---|---|
| Aplikasi restart, run sedang menunggu approval | Run hilang (di memori). Keputusan analis berikutnya gagal dengan `internal`. | Dilanjutkan dari file JSON di `App_Data/maf-runs` | Dilanjutkan; service tetap hidup | Dilanjutkan; service tetap hidup |
| Aplikasi restart, run sedang berjalan | Ditutup `run_interrupted` oleh aplikasi | Ditutup `run_interrupted` oleh adapter MAF, karena session yang tersimpan berhenti di tengah giliran model | Berjalan terus di service; pump menyambung lagi dari `sequence` terakhir dan tidak ada yang hilang | Sama dengan LangGraph |
| Service agent restart, run sedang menunggu approval | – | – (MAF di dalam aplikasi) | Dilanjutkan (`AsyncSqliteSaver`) | Dilanjutkan (`DatabaseSessionService`) |
| Service agent restart, run sedang berjalan | – | – | Ditutup `run_interrupted` oleh service (`RunManager.recover`) | Ditutup `run_interrupted` oleh service (`RunManager.recover`) |
| Aplikasi dan service restart bersamaan, run sedang berjalan | – | – | `run_interrupted` dari service, lalu diteruskan pump | Sama dengan LangGraph |

## Alasan desain

- **Jeda approval dilanjutkan, langkah yang terputus tidak.** Saat menunggu approval, state agent lengkap dan stabil: giliran model sudah selesai, dan `submit_memo` menunggu jawaban. Di tengah langkah, panggilan model atau tool mungkin sudah setengah jalan. Mengulangnya bisa menggandakan tool call atau narasi. Menutup run dengan pesan yang jelas lebih jujur daripada mencoba menebak.
- **Aplikasi tetap sumber kebenaran.** Semua event sampai titik putus sudah tersimpan. "Mulai ulang dengan data yang sama" membuat kasus baru, sehingga kasus lama tetap utuh untuk audit.
- **Kodenya bisa diulang.** `run_interrupted` dibedakan dari `internal`, supaya analis tahu bahwa ini bukan kerusakan data atau bug agent, dan bahwa mengulang kemungkinan berhasil.
- **Agent tiruan sengaja tidak tahan restart.** Run-nya hanya di memori. Ini dipakai untuk memperagakan `run_interrupted` tanpa mematikan service sungguhan.

## Kode

| Bagian | File |
|---|---|
| Memasang ulang pump | `Investigations/StartupRecovery.cs`, `InvestigationService.RecoverAsync` |
| Kode error saat run hilang | `RunPump.PumpAsync` (`afterRestart`), `RunPump.InterruptedMessage` |
| MAF | `agents/maf/MafAgentClient.cs` (`RunPhase.Running` saat stream dibuka) |
| LangGraph, ADK | `agents/*/app/runs.py` (`RunManager.recover`) |
| Test | `DurabilityTests.A_run_lost_in_a_restart_ends_with_a_retryable_run_interrupted`; `test_run_interrupted_mid_stream_is_closed_on_restart` di kedua service Python |
