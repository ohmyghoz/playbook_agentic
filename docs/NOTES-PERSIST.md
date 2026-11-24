# Catatan pengukuran: menyimpan event agent

Catatan ini mengukur berapa lama aplikasi menyimpan satu event agent ke
database. Angka ini jadi bahan Bab 10: setiap potongan teks yang mengalir
ke browser juga ditulis ke database lebih dulu.

Ringkasnya: waktu per event turun dari sekitar 120 ms menjadi sekitar 75 ms
(turun sekitar 38%) dengan mengurangi perjalanan ke database dari tiga
menjadi satu.

## Cara mengukur

- Test: `app/Surveillance.Web.Tests/Benchmark/PersistBenchmark.cs`, hanya jalan bila `RUN_BENCHMARK=1`.
- Database: Azure SQL serverless (`Surveillance`) yang sama dengan aplikasi, dibaca dari user-secrets. Laptop pengembang di Jakarta, server di region Azure Asia Tenggara.
- Isi: 10 event pemanasan, lalu 200 event `token` yang diukur satu per satu lewat `RunPump.PersistAsync`.
- Hasil: ditulis ke keluaran test dan ditambahkan ke `%TEMP%\persist-benchmark.txt`. Baris uji dihapus setelah test.

```powershell
$env:RUN_BENCHMARK = '1'
dotnet test app/Surveillance.Web.Tests --filter "Category=Benchmark"
```

## Hasil

Tanggal uji: 24 November 2026. Semua angka dalam milidetik per event.

| Versi | Rata-rata | Median | p95 | Maksimum |
|---|---|---|---|---|
| Sebelum, uji 1 | 120,2 | 118,3 | 131,3 | 151,5 |
| Sebelum, uji 2 | 126,6 | 120,6 | 164,0 | 224,7 |
| Sesudah, uji 1 | 74,5 | 72,2 | 85,2 | 100,1 |
| Sesudah, uji 2 | 79,4 | 77,5 | 93,3 | 107,6 |
| Sesudah, uji 3 | 79,3 | 72,7 | 132,6 | 202,0 |

## Yang diubah

Sebelum perubahan, setiap event butuh tiga perjalanan ke database:

1. Membaca baris kasus untuk mengetahui status dan `sequence` terakhir.
2. Memeriksa apakah event dengan `sequence` yang sama sudah tersimpan.
3. Menyimpan event dan memperbarui kasus.

Sesudah perubahan, setiap event hanya butuh satu perjalanan, yaitu langkah ketiga:

- **Status dan `sequence` terakhir disimpan di memori** selama satu pump membaca stream kasus itu. Hanya pump itu yang menulis event kasus tersebut, jadi nilai di memori tetap benar. Nilai ini dibaca ulang dari database setiap kali pump baru mulai, karena keputusan analis atau tombol stop bisa mengubah kasus di antara dua stream.
- **Duplikat ditangkap oleh indeks unik** `(RunId, Sequence)`, bukan dengan pencarian lebih dulu. Bila penyimpanan gagal karena indeks itu, aplikasi memeriksa sekali apakah event-nya memang sudah ada, lalu melewatinya.
- **Kasus diperbarui tanpa dibaca dulu.** Aplikasi hanya mengirim kolom `Status`, `LastSequence`, dan `UpdatedAt`.

## Yang dicoba tapi tidak dipakai

`MinBatchSize(1)` di penyedia SQL Server, supaya insert event dan update kasus
dikirim sebagai satu batch. Hasilnya tidak berbeda dari bawaan:

| `MinBatchSize` | Rata-rata | Median | p95 |
|---|---|---|---|
| Bawaan, uji 1 | 79,4 | 77,5 | 93,3 |
| 1, uji 1 | 79,0 | 76,6 | 89,5 |
| Bawaan, uji 2 | 79,3 | 72,7 | 132,6 |
| 1, uji 2 | 77,4 | 75,3 | 88,2 |

Selisihnya masih dalam rentang gangguan jaringan, jadi konfigurasinya tetap bawaan.

## Batasan

- Angka ini mencerminkan latensi jaringan dari laptop ke Azure. Aplikasi yang berjalan di region yang sama dengan database akan jauh lebih cepat.
- Satu event satu transaksi. Menggabungkan beberapa potongan teks ke satu transaksi akan lebih cepat lagi, tapi membuat event terlambat sampai ke browser dan menyulitkan penyambungan ulang dari `sequence` terakhir. Pilihan itu belum diambil.
- Nilai di memori hanya benar selama satu proses aplikasi yang menjalankan pump. Bila aplikasi dijalankan di lebih dari satu server, setiap kasus tetap harus dipompa oleh satu server saja.
