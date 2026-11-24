# SSE dan SignalR di belakang proxy

Aliran event investigasi bisa terputus atau tertahan bukan karena aplikasi,
melainkan karena proxy di depannya. Dokumen ini menjelaskan apa yang sudah
dilakukan aplikasi, lalu memberi contoh konfigurasi untuk IIS, nginx, dan
Azure App Service.

## Yang sudah dilakukan aplikasi

Endpoint SSE `GET /investigations/{id}/events`
(`InvestigationController.Events`, `SseKeepAlive.cs`):

| Mekanisme | Nilai | Kegunaan |
|---|---|---|
| `ping` pertama | Langsung saat koneksi dibuka | Respons mulai mengalir walau run sedang menunggu approval, sehingga `EventSource` memanggil `onopen` dan proxy melihat respons. |
| `ping` berkala | Setiap `Transport:SsePingSeconds` detik tanpa event (bawaan 15) | Mencegah proxy memutus koneksi yang diam. Event `ping` tidak punya `id`, jadi `Last-Event-ID` tidak bergeser, dan browser mengabaikannya karena tidak ada listener untuk tipe itu. |
| `retry:` | `Transport:SseRetryMilliseconds` (bawaan 3000) | `EventSource` menunggu 3 detik sebelum menyambung ulang, bukan nilai bawaan browser. |
| `X-Accel-Buffering: no` | Setiap respons SSE | nginx tidak menahan respons di buffer. |
| `Cache-Control: no-cache` | Setiap respons SSE | Proxy dan browser tidak menyimpan aliran di cache. |
| Menyambung kembali | `Last-Event-ID` atau `?after=` | Event yang terlewat saat koneksi putus diputar ulang dari database. |

Polling (`GET /investigations/{id}/events.json?after=`) adalah request biasa
dan tidak butuh konfigurasi khusus. SignalR menegosiasikan WebSocket, lalu
turun ke SSE atau long polling bila WebSocket ditolak proxy.

## Batas waktu diam di setiap lapisan

| Lapisan | Batas diam bawaan | Dampak tanpa ping |
|---|---|---|
| Azure App Service (load balancer front end) | 230 detik | Koneksi diputus; `EventSource` menyambung ulang |
| nginx `proxy_read_timeout` | 60 detik | Koneksi diputus setiap menit |
| IIS ARR (reverse proxy) | 120 detik (`timeout` di `proxy`) | Koneksi diputus |
| Azure Application Gateway | 20 detik (bisa diubah) | Koneksi diputus sangat sering |

Ping 15 detik berada di bawah semua batas itu, kecuali Application Gateway
yang batas bawaannya perlu dinaikkan.

## nginx

```nginx
location /investigations/ {
    proxy_pass http://surveillance_web;
    proxy_http_version 1.1;
    proxy_set_header Connection "";
    proxy_buffering off;          # sama dengan header X-Accel-Buffering: no
    proxy_cache off;
    proxy_read_timeout 300s;      # lebih panjang dari interval ping
}

location /hubs/ {
    proxy_pass http://surveillance_web;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;   # WebSocket untuk SignalR
    proxy_set_header Connection "upgrade";
    proxy_read_timeout 300s;
}
```

## IIS

Aplikasi ASP.NET Core di IIS memakai ASP.NET Core Module (in-process). Ada dua
hal yang perlu diperhatikan:

- **Kompresi dinamis** menahan respons sampai buffer penuh. Matikan untuk SSE:

  ```xml
  <!-- web.config -->
  <configuration>
    <location path="investigations">
      <system.webServer>
        <urlCompression doDynamicCompression="false" />
      </system.webServer>
    </location>
    <system.webServer>
      <webSocket enabled="true" />   <!-- SignalR WebSocket; fitur WebSocket Protocol harus terpasang di Windows -->
    </system.webServer>
  </configuration>
  ```

- **ARR sebagai reverse proxy** memakai batas waktu proxy bawaan 120 detik.
  Ping 15 detik sudah cukup. Dua hal lain perlu diperiksa:
  - `responseBufferLimit` handler ARR harus `0`, supaya event tidak ditahan.
  - Jangan pasang aturan rewrite outbound untuk jalur event, karena aturan
    outbound membuat ARR menahan respons untuk ditulis ulang.

  Nilai pasti dan letak pengaturannya bergantung pada versi IIS dan ARR yang
  dipakai. Periksa di server sebelum dipakai di produksi; contoh ini belum
  diuji di IIS.

## Azure App Service

- Aktifkan **Web sockets** (Configuration → General settings) untuk SignalR.
- Aktifkan **ARR affinity** bila aplikasi berjalan lebih dari satu instance.
  SignalR tanpa Azure SignalR Service butuh sticky session. Event SSE
  sebenarnya bisa dilayani instance mana saja, karena diputar dari database.
  Namun `RunPump` dan `EventBroadcaster` hidup di memori satu instance, jadi
  event live hanya muncul di instance yang menjalankan pump (lihat
  `docs/NOTES-PERSIST.md`, bagian Batasan).
- Batas diam 230 detik tidak bisa diubah. Ping 15 detik menjaga koneksi tetap
  hidup.
- **Always On** perlu aktif supaya `RunPump` dan `StartupRecovery` tidak
  berhenti saat situs dianggap tidak aktif.

## Cara memeriksa

```powershell
# Ping pertama harus langsung terlihat, lalu satu ping setiap 15 detik saat run menunggu approval.
curl.exe -N -H "Cookie: <cookie login>" http://localhost:5272/investigations/<id>/events
```

Halaman `/lab/transport` menampilkan jumlah request dan jeda per transport.
Kalau jeda SSE tiba-tiba sama dengan interval polling atau lebih besar, kemungkinan ada proxy yang menahan buffer.
