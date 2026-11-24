# Memilih transport event

Halaman investigasi bisa menerima event agent lewat tiga transport. Ketiganya
membaca event yang sama dari database dan memberi hasil yang sama: urutan
`sequence` utuh, tanpa event hilang atau ganda. Yang berbeda hanya cara
pengirimannya.

## Cara memilih

- Bawaan aplikasi: `Transport:Default` di `appsettings.json` (`sse`, `polling`, atau `signalr`).
- Per halaman: parameter `?transport=`, misalnya `/investigations/{id}?transport=signalr`.
- Nilai yang tidak dikenal kembali ke bawaan.

## Perbandingan

| Pertimbangan | SSE | Polling | SignalR |
|---|---|---|---|
| Arah | Server ke browser | Browser bertanya | Dua arah |
| Koneksi | Satu GET panjang per halaman | Satu request per interval | Satu koneksi, WebSocket bila tersedia |
| Jeda event (diukur di `/lab/transport`) | Sekitar 80 ms | Sekitar setengah interval; 2 detik memberi sekitar 1 detik | Setara SSE |
| Jumlah request untuk satu investigasi | 1, ditambah satu per sambung ulang | Satu per interval selama halaman terbuka | 1 koneksi, ditambah `JoinCase` per sambung ulang |
| Menyambung kembali | Otomatis oleh browser lewat `Last-Event-ID` | Selalu mulai dari `after=` terakhir | `withAutomaticReconnect`, lalu `JoinCase` dari `sequence` terakhir |
| Proxy | Perlu ping dan buffering mati (`docs/PROXY.md`) | Tidak ada syarat khusus | Perlu WebSocket, atau jatuh ke SSE atau long polling; perlu sticky session bila lebih dari satu instance |
| Library di browser | Tidak ada (`EventSource` bawaan) | Tidak ada (`fetch`) | `@microsoft/signalr` (48 KB) |
| Cocok untuk | Bawaan: satu arah, sederhana, cepat | Jaringan yang memutus koneksi panjang; klien yang hanya sesekali membuka | Bila nanti ada pesan dari browser ke server di koneksi yang sama, atau notifikasi lintas halaman (Tahap 13) |

Pilihan bawaan aplikasi ini adalah SSE. Alasannya:
- semua interaksi dari browser ke server (keputusan, stop) sudah berupa POST biasa;
- SSE tidak butuh library;
- menyambung kembali sudah ditangani browser.

## Di mana kodenya

| Bagian | File |
|---|---|
| Antarmuka transport di JavaScript | `wwwroot/js/transports/transport.js` |
| SSE | `wwwroot/js/transports/transport-sse.js`; server: `InvestigationController.Events`, `SseKeepAlive.cs` |
| Polling | `wwwroot/js/transports/transport-polling.js`; server: `InvestigationController.EventsJson` |
| SignalR | `wwwroot/js/transports/transport-signalr.js`; server: `Hubs/InvestigationHub.cs`, `Hubs/InvestigationHubRelay.cs` |
| Sumber event live untuk ketiganya | `Investigations/EventBroadcaster.cs` |
| Pembanding berdampingan | `/lab/transport` (`LabController.Transport`, `wwwroot/js/lab-transport.js`) |
| Contoh "sebelum" tanpa streaming | `/lab/sync` (`LabController.Sync`) |
