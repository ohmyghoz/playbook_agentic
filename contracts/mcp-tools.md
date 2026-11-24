# Kontrak tool MCP idx untuk investigasi UMA

Dokumen ini mencatat tool MCP `idx` (github.com/fzn0x/idx) yang dipakai untuk
menyelidiki saham yang terkena UMA (Unusual Market Activity) di BEI.

- Server: `scripts/start-mcp.ps1` → `idx mcp --http 127.0.0.1:8765`
- Endpoint: `http://127.0.0.1:8765/mcp`, header `Authorization: Bearer $IDX_MCP_TOKEN`
- Health check: `GET http://127.0.0.1:8765/healthz`
- Diuji pada 2026-11-24 dengan ticker BBCA.

## Amplop respons (berlaku untuk semua tool)

Setiap tool mengembalikan amplop JSON yang sama dengan `idx -o json`:

```json
{
  "schema": "idx.<nama>.v1",
  "provider": "yahoo",
  "fetched_at": "2026-11-24T14:09:39Z",
  "cached": false,
  "stale": false,
  "stale_reason": "offline | fetch_failed",   // hanya jika stale=true
  "warnings": ["..."],                        // opsional
  "data": { }
}
```

- Field yang tidak dikirim sumber bernilai `null`. Tidak ada angka yang ditaksir.
- Harga tertunda (delayed) sesuai yang dipublikasikan sumber.
- `stale=true` berarti yang dikirim adalah salinan cache lama.

Bentuk error:

```json
{ "error": { "code": "provider", "message": "ipot: broker summary for BBCA: HTTP 502" }, "exit_code": 3 }
```

Kode error yang perlu ditangani: `not_found` (ticker tidak dikenal),
`offline_miss` (mode offline tanpa cache), `blocked` (ditolak proteksi bot,
coba lagi sekitar 1 menit), `provider` (sumber gagal, misalnya HTTP 5xx).

## Ringkasan tool untuk UMA

| Pertanyaan investigasi | Tool | Status uji 2026-11-24 |
|---|---|---|
| Seberapa jauh harga & volume menyimpang hari ini? | `idx_quote` | ✅ berhasil |
| Bagaimana pola harga/volume sebelumnya? | `idx_history` | ✅ berhasil |
| Apakah ada berita yang menjelaskan pergerakan? | `idx_news` | ✅ berhasil |
| Apakah asing masuk/keluar? | `idx_foreign` | ✅ berhasil |
| Ada aksi korporasi (dividen, split, rights, RUPS)? | `idx_stockbit_actions` | ✅ berhasil via Stockbit |
| Aksi korporasi versi IPOT (cadangan) | `idx_corporate_actions` | ❌ IPOT HTTP 502 |
| Apakah insider atau pemegang ≥5% bertransaksi? | `idx_stockbit_movements` | ✅ berhasil via Stockbit |
| Seperti apa antrean order saat ini? | `idx_orderbook` | ✅ berhasil via Stockbit |
| Broker mana yang membeli/menjual? | `idx_broker` | ✅ berhasil via Stockbit (IPOT HTTP 502) |
| Apakah ada akumulasi/distribusi bandar? | `idx_bandar` | ❌ IPOT HTTP 502 |
| Kapan lonjakan terjadi dalam sesi? | `idx_intraday` | belum diuji |
| Apakah pemegang saham >1% berubah? | `idx_ownership_changes` | belum diuji |

Untuk `idx_bandar` dan `idx_corporate_actions`, bentuk keluaran di bawah
diambil dari JSON Schema resmi (`idx schema bandar`, `idx schema actions`,
idx 0.8.4), bukan dari contoh nyata. Pada 2026-11-24 IPOT membalas HTTP 502
untuk BBCA dan TLKM, dengan dan tanpa `date`, lewat MCP maupun CLI. Karena itu
agent wajib menangani error `provider` dari tool berbasis IPOT dan mencatatnya
sebagai "data tidak tersedia", bukan berhenti.

Tool yang butuh login Stockbit **boleh dipakai** di proyek ini. Sebelum
memanggilnya, agent wajib memanggil `idx_stockbit_status` dulu. Jika
`logged_in=false`, agent tidak boleh meminta password, cookie, atau token di
chat. Agent cukup meminta pengguna menjalankan `idx stockbit login` di
terminal. Login lewat halaman (`idx_stockbit_login`) hanya dimulai jika
pengguna memintanya. Pada 2026-11-24, token akses berlaku sekitar 24 jam dan
bisa diperbarui otomatis selama sekitar 7 hari (`renewable=true`).

---

## 1. `idx_quote` — quote tertunda

**Parameter**

| Nama | Tipe | Wajib | Keterangan |
|---|---|---|---|
| `symbols` | string | ya | Satu atau beberapa ticker, dipisah koma/spasi, mis. `"BBCA,BBRI"` |

**Relevansi UMA:** bandingkan `volume` dengan `avg_volume_3m`, `change_pct`
dengan rentang harian, dan posisi `price` terhadap `sma50`/`sma200` serta
`week52_high`/`week52_low`. `behind_session=true` menandakan saham tidak
diperdagangkan (mis. suspensi).

**Contoh keluaran (BBCA, 2026-11-24, nyata):**

```json
{
  "schema": "idx.quotes.v1",
  "provider": "yahoo",
  "fetched_at": "2026-11-24T14:09:39Z",
  "cached": false,
  "stale": false,
  "data": [
    {
      "symbol": "BBCA",
      "name": "PT Bank Central Asia Tbk",
      "currency": "IDR",
      "price": 5800.0,
      "change": 125.0,
      "change_pct": 2.2026432,
      "previous_close": 5675.0,
      "open": 5775.0,
      "day_high": 5850.0,
      "day_low": 5750.0,
      "volume": 235635300,
      "avg_volume_3m": 212048028,
      "market_cap": 712682177036288.0,
      "pe_trailing": 12.008282,
      "price_to_book": 2.5138795,
      "dividend_yield_pct": 6.71,
      "week52_high": 8500.0,
      "week52_low": 4740.0,
      "sma50": 5660.6,
      "sma200": 6220.275,
      "shares_outstanding": 122876240600,
      "market_state": "POSTPOST",
      "market_time": "2026-11-24T16:14:14+07:00",
      "source": "yahoo:quote",
      "behind_session": false
    }
  ]
}
```

(Beberapa field dipotong: `financial_currency`, `pe_forward`, `eps_ttm`.)

## 2. `idx_broker` — broker summary

**Parameter**

| Nama | Tipe | Wajib | Keterangan |
|---|---|---|---|
| `symbols` | string | ya | Hingga 10 ticker |
| `date` | string | tidak | Satu hari bursa `YYYY-MM-DD`. Tanpa tanggal = sesi terakhir |
| `from` / `to` | string | tidak | Rentang tanggal; tidak boleh digabung dengan `date` |
| `board` | string | tidak | `all` (default), `regular`, `cash`, `negotiated` |
| `investor` | string | tidak | `all` (default), `foreign`, `domestic` |
| `source` | string | tidak | `auto` (default; Stockbit jika sesi aktif, selain itu IPOT), `ipot`, atau `stockbit`. Untuk investigasi, pakai `stockbit` agar semua broker tercantum |

**Relevansi UMA:** melihat apakah transaksi terkonsentrasi di sedikit broker,
jenis broker (`kind`), dan nilai bersih asing.

**Hasil uji:** berhasil dengan `source=stockbit`. IPOT (`source=ipot` atau
`auto` tanpa sesi Stockbit) membalas HTTP 502 untuk BBCA (dengan dan tanpa
`date`) dan TLKM.

**Contoh keluaran (BBCA, `source: stockbit`, sesi 2026-11-24, nyata):**
`data` berupa array, satu elemen per ticker. Daftar `buyers` dan `sellers`
dipotong ke 3 baris dari 53 pembeli dan 70 penjual.

```json
{
  "schema": "idx.broker.v1",
  "provider": "stockbit",
  "fetched_at": "2026-11-24T14:31:25Z",
  "cached": false,
  "stale": false,
  "data": [
    {
      "symbol": "BBCA",
      "name": "BANK CENTRAL ASIA Tbk",
      "from": "2026-11-24",
      "to": "2026-11-24",
      "investor": "all",
      "board": "all",
      "buyers": [
        { "rank": 1, "code": "BK", "name": "J.P. MORGAN SEKURITAS INDONESIA",
          "kind": "foreign", "lots": 593024.08, "value": 344125446400.0,
          "average_price": 5802.891619510628 },
        { "rank": 2, "code": "KZ", "name": "CLSA SEKURITAS INDONESIA",
          "kind": "foreign", "lots": 412452.0, "value": 239322602500.0,
          "average_price": 5802.4352530718725 },
        { "rank": 3, "code": "AK", "name": "UBS SEKURITAS INDONESIA",
          "kind": "foreign", "lots": 322786.62, "value": 187759378200.0,
          "average_price": 5816.82655247606 }
      ],
      "sellers": [
        { "rank": 1, "code": "CC", "name": "MANDIRI SEKURITAS",
          "kind": "state_owned", "lots": 501120.0, "value": 291033515000.0,
          "average_price": 5807.661139048531 },
        { "rank": 2, "code": "YU", "name": "CGS INTERNATIONAL SEKURITAS INDONESIA",
          "kind": "foreign", "lots": 362692.87, "value": 210497489925.0,
          "average_price": 5803.739398709437 },
        { "rank": 3, "code": "ZP", "name": "MAYBANK SEKURITAS INDONESIA",
          "kind": "foreign", "lots": 257145.43, "value": 149103699025.0,
          "average_price": 5798.419167900437 }
      ],
      "total_value": 1369011170175.0,
      "total_lots": 2357950.41,
      "foreign_net_value": 409645414900.0,
      "average_price": 5805.937073015034,
      "buy_value": 1369011170175.0,
      "sell_value": 1369011170175.0,
      "rounded": false,
      "buyer_count": 53,
      "seller_count": 70,
      "buyers_complete": true,
      "sellers_complete": true,
      "source": "stockbit"
    }
  ]
}
```

Catatan field:
- `kind` bernilai `foreign`, `local`, atau `state_owned` (BUMN), atau `null`
  jika broker tidak ada di daftar anggota bursa.
- `lots` bisa pecahan, misalnya `593024.08`, karena Stockbit ikut menghitung
  transaksi di bawah 1 lot.
- Dengan `investor=all`, `buy_value` dan `sell_value` sama dengan
  `total_value`.
- Rata-rata harga beberapa broker kecil bisa jauh dari harga pasar (pada
  contoh ini `CD` 6461, sementara harga pasar sekitar 5800). Ini kemungkinan
  transaksi di pasar negosiasi. Untuk membandingkan, ulangi dengan
  `board=regular`.

Perbedaan sumber:
- **Stockbit:** semua broker, angka tepat (`rounded=false`,
  `buyers_complete=true`).
- **IPOT:** hanya 10 pembeli dan 10 penjual teratas (`buyers_complete=false`),
  dan angka ≥ 1 juta dibulatkan ke satu desimal (`rounded=true`).

Agent wajib mencatat `source` di memo, karena kesimpulan soal konsentrasi
broker bisa berbeda antara daftar lengkap dan daftar top 10.

## 3. `idx_bandar` — bandarmology (pelengkap broker summary)

**Parameter**

| Nama | Tipe | Wajib | Keterangan |
|---|---|---|---|
| `symbols` | string | ya | Hingga 10 ticker (3 jika `daily=true`) |
| `period` | string | tidak | `1d`, `1w` (default), `1m`, `3m` |
| `date` / `from` / `to` | string | tidak | Satu hari atau rentang, `YYYY-MM-DD` |
| `top` | integer | tidak | Broker per sisi untuk sinyal utama: 1, 3, 5 (default), 10 |
| `board`, `investor` | string | tidak | Sama dengan `idx_broker` |
| `daily` | boolean | tidak | Tambahkan deret per sesi (satu request per sesi) |

**Relevansi UMA:** `signal` dan `phase` membantu menilai apakah lonjakan
didorong segelintir broker. `reasons` berisi aturan yang menghasilkan
pembacaan itu dalam kalimat biasa, cocok dikutip di memo sebagai indikasi.

**Hasil uji:** gagal, sumbernya sama (IPOT HTTP 502).

**Bentuk keluaran (dari `idx schema bandar`, belum ada contoh nyata):**

```json
{
  "schema": "idx.bandar.v1",
  "data": [
    {
      "symbol": "BBCA",
      "name": "...",
      "period": "1d | 1w | 1m | 3m | custom",
      "from": "YYYY-MM-DD",
      "to": "YYYY-MM-DD",
      "top": 5,
      "signal": "big_accumulation | accumulation | neutral | distribution | big_distribution",
      "phase": "quiet_accumulation | markup | quiet_distribution | markdown | neutral | null",
      "reasons": ["..."],
      "price": 0,
      "price_change_pct": 0,
      "bandar_average": 0,
      "bandar_gain_pct": 0,
      "net_value": 0,
      "net_lots": 0,
      "net_pct": 0,
      "total_value": 0,
      "total_lots": 0,
      "foreign_net_value": 0,
      "foreign_net_pct": 0,
      "rounded": true,
      "buyers": [ { "...": "bentuk sama dengan buyers di idx_broker" } ],
      "sellers": [ { "...": "bentuk sama dengan buyers di idx_broker" } ],
      "tiers": [
        { "top": 1, "signal": "...", "buy_value": 0, "sell_value": 0, "net_value": 0,
          "buy_lots": 0, "sell_lots": 0, "net_lots": 0, "net_pct": 0,
          "buy_average": 0, "sell_average": 0, "buy_share_pct": 0, "sell_share_pct": 0 }
      ],
      "daily": {
        "days": [ "... hanya jika daily=true ..." ],
        "accumulation_days": 0, "distribution_days": 0, "neutral_days": 0,
        "streak": 0, "cumulative_net_value": 0
      }
    }
  ]
}
```

## 4. `idx_news` — berita

**Parameter**

| Nama | Tipe | Wajib | Keterangan |
|---|---|---|---|
| `symbol` | string | tidak | Ticker; tanpa ini hanya berita pasar umum |
| `company_only` | boolean | tidak | `true` = hanya berita yang menyebut emiten |
| `limit` | integer | tidak | Default 20, `0` = semua |
| `sources` | string[] | tidak | `kontan`, `cnbcindonesia`, `bisnis`, `all` |

**Relevansi UMA:** UMA sering muncul tanpa informasi material. Jika
`company_matches` = 0 atau beritanya tidak menjelaskan lonjakan, itu menjadi
temuan. Untuk investigasi, pakai `company_only: true` agar berita pasar umum
tidak tercampur.

**Contoh keluaran (BBCA, `limit: 5`, nyata, dipotong ke 2 item):**

```json
{
  "schema": "idx.news.v1",
  "provider": "news",
  "fetched_at": "2026-11-24T14:09:39Z",
  "cached": false,
  "stale": false,
  "data": {
    "symbol": "BBCA",
    "matched_name": "BANK CENTRAL ASIA",
    "sources": ["kontan/investasi", "kontan/keuangan", "kontan/industri",
                "cnbcindonesia/market", "cnbcindonesia/research"],
    "scanned": 275,
    "company_matches": 1,
    "company_only": false,
    "items": [
      {
        "title": "Usai MSCI - Presiden Bicara, Simak 5 Saham Pilihan Hari Ini",
        "url": "https://www.cnbcindonesia.com/market/20261124081410-17-787830/usai-msci--presiden-bicara-simak-5-saham-pilihan-hari-ini",
        "source": "cnbcindonesia/market",
        "published": "2026-11-24T08:54:40+07:00",
        "summary": "IHSG menguat 1,35% ke 5.215,85, didorong saham BBCA dan TPIA. Investor asing mencatat net sell Rp750 miliar.",
        "matched_by": "ticker",
        "scope": "company",
        "sentiment": 0.0,
        "positive_terms": ["menguat"],
        "negative_terms": ["sell"]
      },
      {
        "title": "Riyadh Air Bidik Pasar Umrah Indonesia,Targetkan Penerbangan Jakarta-Riyadh Tiap Hari",
        "url": "https://industri.kontan.co.id/news/riyadh-air-bidik-pasar-umrah-indonesiatargetkan-penerbangan-jakarta-riyadh-tiap-hari",
        "source": "kontan/industri",
        "published": "2026-11-24T21:07:26+07:00",
        "summary": "Riyadh Air membidik pasar umrah Indonesia ...",
        "matched_by": "all",
        "scope": "market",
        "sentiment": 0.0,
        "positive_terms": [],
        "negative_terms": []
      }
    ]
  }
}
```

`scope` membedakan berita emiten (`company`) dan berita pasar (`market`).
`sentiment` adalah skor leksikon sederhana, bukan analisis.

## 5. `idx_history` — riwayat harga dan volume

**Parameter**

| Nama | Tipe | Wajib | Keterangan |
|---|---|---|---|
| `symbol` | string | ya | Satu ticker |
| `period` | string | tidak | `1d`, `5d`, `1mo`, `3mo`, `6mo`, `1y` (default), `2y`, `5y`, `10y`, `ytd`, `max` |
| `interval` | string | tidak | `1m` … `1h`, `1d` (default), `1wk`, `1mo` |
| `limit` | integer | tidak | Jumlah candle terakhir yang dikirim (default 60). `summary` tetap mencakup seluruh periode |

**Relevansi UMA:** `summary.avg_volume` dan rentang `high`/`low` menjadi
pembanding normal. Candle terakhir menunjukkan sejak kapan penyimpangan dimulai.

**Contoh keluaran (BBCA, `period: 3mo`, `limit: 5`, nyata, dipotong ke 2 candle):**

```json
{
  "schema": "idx.history.v1",
  "provider": "yahoo",
  "fetched_at": "2026-11-24T14:25:31Z",
  "cached": true,
  "stale": false,
  "warnings": ["candles truncated to the last 5; summary covers the full period"],
  "data": {
    "symbol": "BBCA",
    "name": "PT Bank Central Asia Tbk",
    "currency": "IDR",
    "period": "3mo",
    "interval": "1d",
    "candles": [
      { "time": "2026-11-23T09:00:00+07:00", "open": 5475.0, "high": 5750.0,
        "low": 5425.0, "close": 5675.0, "adj_close": 5675.0, "volume": 200829700 },
      { "time": "2026-11-24T09:00:00+07:00", "open": 5775.0, "high": 5850.0,
        "low": 5750.0, "close": 5800.0, "adj_close": 5800.0, "volume": 235635300 }
    ],
    "summary": {
      "start": "2026-08-24T09:00:00+07:00",
      "end": "2026-11-24T09:00:00+07:00",
      "first_close": 6400.0,
      "last_close": 5800.0,
      "change": -600.0,
      "change_pct": -9.375,
      "high": 6850.0,
      "low": 4740.0,
      "avg_volume": 210424313,
      "candles": 66
    }
  }
}
```

## 6. `idx_foreign` — arus dana asing per saham

**Parameter**

| Nama | Tipe | Wajib | Keterangan |
|---|---|---|---|
| `symbol` | string | ya | Satu ticker |
| `days` | integer | tidak | Sesi terakhir (default 20; `idx` maks. 250, `ipot` maks. 60). Tidak boleh digabung dengan `from`/`to` |
| `from` / `to` | string | tidak | Rentang tanggal `YYYY-MM-DD` |
| `source` | string | tidak | `idx` atau `ipot`. Pakai `idx` agar tidak bergantung pada IPOT |

**Relevansi UMA:** `foreign_net` harian dan `totals.streak` menunjukkan apakah
asing ikut mendorong pergerakan atau justru berlawanan arah.

**Contoh keluaran (BBCA, `days: 5`, nyata, dipotong ke 2 hari):**

```json
{
  "schema": "idx.foreign.v1",
  "provider": "idx",
  "fetched_at": "2026-11-24T14:25:33Z",
  "cached": true,
  "stale": false,
  "data": {
    "symbol": "BBCA",
    "name": "BANK CENTRAL ASIA Tbk",
    "from": "2026-11-18",
    "to": "2026-11-24",
    "days": [
      { "date": "2026-11-23", "close": 5675.0, "change": 125.0,
        "volume": 200829700.0, "value": 1129583777500.0,
        "foreign_buy": 111277200.0, "foreign_sell": 80474800.0,
        "foreign_net": 30802400.0, "foreign_net_value_at_close": 174803620000.0 },
      { "date": "2026-11-24", "close": 5800.0, "change": 125.0,
        "volume": 235635300.0, "value": 1367976930000.0,
        "foreign_buy": 172578200.0, "foreign_sell": 102060200.0,
        "foreign_net": 70518000.0, "foreign_net_value_at_close": 409004400000.0 }
    ],
    "totals": {
      "sessions": 5,
      "foreign_buy": 609198100.0,
      "foreign_sell": 505689600.0,
      "foreign_net": 103508500.0,
      "foreign_net_value_at_close": 614400205000.0,
      "net_buy_sessions": 3,
      "net_sell_sessions": 2,
      "streak": 2
    }
  }
}
```

`foreign_buy`, `foreign_sell`, dan `foreign_net` dalam lembar saham.
`foreign_net_value_at_close` adalah perkiraan nilai IDR (`foreign_net` × harga
penutupan), bukan nilai transaksi sebenarnya.

## 7. `idx_stockbit_actions` — aksi korporasi (sumber utama)

**Parameter**

| Nama | Tipe | Wajib | Keterangan |
|---|---|---|---|
| `symbol` | string | tidak | Satu ticker: seluruh riwayat saham itu, terbaru dulu. Tanpa ini: kalender pasar |
| `kind` | string | tidak | Dipisah koma: `dividend`, `stock_dividend`, `bonus`, `rights`, `warrant`, `split`, `reverse_split`, `rups`, `public_expose`, `ipo`, `tender_offer`, atau `all`. Tiap jenis = satu request ke Stockbit |
| `from` / `to` | string | tidak | Rentang tanggal `YYYY-MM-DD` (untuk kalender pasar) |

**Relevansi UMA:** cum date dividen, split, rights issue, tender offer, atau RUPS
yang dekat dengan tanggal lonjakan bisa menjadi penjelasan wajar atas pergerakan.
Untuk investigasi, saring dengan tanggal di sekitar UMA karena hasilnya bisa
berisi puluhan baris (BBCA: 55 baris sejak 2009).

**Contoh keluaran (BBCA, nyata, dipotong ke 2 dari 55 baris; field `null`
lainnya dihapus):**

```json
{
  "schema": "idx.stockbit.actions.v1",
  "provider": "stockbit",
  "fetched_at": "2026-11-24T14:32:11Z",
  "cached": false,
  "stale": false,
  "data": {
    "symbol": "BBCA",
    "name": "BANK CENTRAL ASIA Tbk",
    "from": null,
    "to": null,
    "kinds": ["dividend", "stock_dividend", "bonus", "rights", "warrant", "split",
              "reverse_split", "rups", "public_expose", "tender_offer"],
    "total": 55,
    "actions": [
      { "id": "118146", "symbol": "BBCA", "kind": "dividend", "date": "2026-11-27",
        "cum_date": "2026-11-27", "ex_date": "2026-11-30",
        "recording_date": "2026-12-01", "distribution_date": "2026-12-16",
        "dividend_per_share": 35.0, "currency": "IDR" },
      { "id": "156753", "symbol": "BBCA", "kind": "public_expose", "date": "2026-09-09",
        "time": "16:00:00",
        "venue": "Public Expose Live 2026 yang tercantum pada website idx.co.id" }
    ]
  }
}
```

Field lain per baris (bernilai `null` jika tidak relevan): `ratio_old`,
`ratio_new`, `factor`, `price`, `trading_start`/`trading_end`,
`exercise_start`/`exercise_end`, `offer_start`/`offer_end`, `listing_date`,
`shares`, `shares_pct`, `note`.

## 8. `idx_stockbit_movements` — transaksi insider dan pemegang ≥5%

**Parameter:** `symbol` (tanpa ini = seluruh pasar), `from`/`to`
(`YYYY-MM-DD`), `holder` (`holder_id`), `action` (`buy`, `sell`, `transfer`,
`cross`, `corporate_action`), dan parameter lain sesuai skema tool.

**Relevansi UMA:** pembelian atau penjualan oleh direksi, komisaris, atau
pemegang ≥5% di sekitar tanggal UMA adalah konteks penting bagi analis.

**Contoh keluaran (BBCA, nyata, dipotong ke 1 dari 50 baris, nama disamarkan):**

```json
{
  "schema": "idx.stockbit.movements.v1",
  "provider": "stockbit",
  "fetched_at": "2026-11-24T14:32:51Z",
  "cached": false,
  "stale": false,
  "data": {
    "symbol": "BBCA",
    "pages_read": 1,
    "more": true,
    "movements": [
      { "date": "2026-03-25", "symbol": "BBCA", "holder": "<nama pemegang>",
        "holder_id": "<id>", "roles": ["director"], "foreign": false,
        "action": "buy", "source": "idx",
        "shares_from": 193206, "shares_to": 341139, "shares_change": 147933,
        "pct_from": 0.0002, "pct_to": 0.0003, "pct_change": 0.0001,
        "price": 6982.0, "estimated_price": null,
        "value": 1032868206.0, "estimated_value": null,
        "purpose": null, "broker": null, "broker_group": null }
    ]
  }
}
```

`source=idx` berarti keterbukaan insider dari IDX (ada harga transaksi).
Perubahan pemegang ≥5% dari KSEI tidak menyertakan harga, sehingga harga dan
nilainya hanya perkiraan (`estimated_price`, `estimated_value`). `more=true`
berarti masih ada halaman berikutnya.

## 9. `idx_corporate_actions` — aksi korporasi versi IPOT (cadangan)

**Parameter**

| Nama | Tipe | Wajib | Keterangan |
|---|---|---|---|
| `symbol` | string | ya | Satu ticker |
| `since` | string | tidak | Peristiwa sejak tanggal ini, `YYYY-MM-DD` (default setahun lalu; jadwal mendatang ikut) |

**Relevansi UMA:** cum date dividen, split, rights issue, atau RUPS yang dekat
dengan tanggal lonjakan bisa menjadi penjelasan wajar atas pergerakan.

**Hasil uji:** gagal. IPOT membalas HTTP 502 (`ipot: corporate actions for BBCA`).

**Bentuk keluaran (dari `idx schema actions`, belum ada contoh nyata):**

```json
{
  "schema": "idx.actions.v1",
  "data": {
    "symbol": "BBCA",
    "name": "...",
    "since": "YYYY-MM-DD",
    "actions": [
      { "kind": "cash_dividend | stock_dividend | stock_split | reverse_split | bonus | rights_issue | ...",
        "label": "Cash Deviden", "status": "Final | Tentative | ...",
        "amount": 0, "ratio_old": null, "ratio_new": null,
        "cum_date": "YYYY-MM-DD", "ex_date": "YYYY-MM-DD",
        "recording_date": "YYYY-MM-DD", "distribution_date": "YYYY-MM-DD",
        "end_date": null, "details": "..." }
    ],
    "meetings": [
      { "date": "YYYY-MM-DD", "time": "HH:MM:SS", "venue": "...",
        "status": "...", "agenda": "..." }
    ],
    "warrants_rights": [
      { "code": "...", "kind": "Right | Warrant", "exercise_price": 0,
        "shares": 0, "listing_date": "YYYY-MM-DD", "maturity_date": "YYYY-MM-DD",
        "status": "...", "details": "..." }
    ]
  }
}
```

`label` disalin apa adanya dari sumber, termasuk salah ejaan seperti
`Cash Deviden`. Untuk logika, pakai `kind`.

---

## Tool pendukung

| Tool | Parameter utama | Schema | Guna untuk UMA |
|---|---|---|---|
| `idx_intraday` | `symbol` (wajib), `interval` (`1m` default … `1h`), `limit` (default 120, 0 = semua) | `idx.intraday.v1` | Bar per menit + VWAP; menunjukkan kapan lonjakan terjadi. Tertunda ±10 menit |
| `idx_ownership_changes` | `from`, `to` (wajib, tanggal rilis), `symbol`, `min_pct_change`, `limit`, `offset` | `idx.ownership.changes.v1` | Perubahan pemegang >1% bulanan (KSEI) |
| `idx_orderbook` | `symbol` (wajib) | `idx.orderbook.v1` | Kedalaman antrean bid/ask realtime dan foreign buy/sell. Butuh sesi Stockbit. Diuji 2026-11-24: berhasil (BBCA, 21 bid, 40 ask; tiap level `price`, `shares`, `orders`) |
| `idx_stockbit_status` | (tanpa parameter) | — | Wajib dipanggil sebelum tool Stockbit: `logged_in`, `renewable`, masa berlaku token (tanpa nilai token) |

## Catatan operasional

- Ketergantungan sumber: `idx_quote` dan `idx_history` memakai Yahoo,
  `idx_foreign` (dengan `source=idx`) memakai idx.co.id, `idx_news` memakai
  portal berita. `idx_broker` memakai Stockbit jika sesinya aktif, dan IPOT
  jika tidak. `idx_bandar` dan `idx_corporate_actions` selalu memakai IPOT,
  jadi keduanya gagal bersamaan saat IPOT bermasalah, meskipun sesi Stockbit
  aktif.
- Saat sumber gagal, agent mencatat di memo bahwa datanya tidak tersedia, dan
  tidak menarik indikasi dari data yang tidak ada.
- Jika `idx_bandar` gagal, agent boleh menghitung sendiri selisih nilai beli
  dan jual top-N broker dari `idx_broker`. Hasilnya harus ditandai sebagai
  hitungan agent, bukan sinyal `idx_bandar`.
- Error `blocked` biasanya hilang dalam sekitar satu menit; coba lagi sekali
  sebelum menyerah. Error `provider` (HTTP 5xx) tidak perlu diulang berkali-kali.
- Tool Stockbit boleh dipakai setelah `idx_stockbit_status` menunjukkan
  `logged_in=true` (lihat ringkasan di atas). Sesi tersimpan di
  `%APPDATA%\idx\stockbit-session.json`. File ini berisi kredensial, jadi
  jangan di-commit atau dibagikan.
- Sinyal dan skor dari tool adalah aturan transparan, bukan saran investasi.
  Di memo, hasilnya disebut indikasi untuk ditelaah analis.
