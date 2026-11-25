# Skema database aplikasi

Database milik aplikasi (SQL Server atau Azure SQL) adalah sumber kebenaran
untuk kasus, event, dan keputusan. State internal tiap framework agent (file
`App_Data/maf-runs`, `checkpoints.db` LangGraph, `sessions.db` ADK) hanya
dipakai untuk melanjutkan eksekusi.

Diagram ini mengikuti model EF Core di
`app/Surveillance.Web/Data/SurveillanceDbContext.cs` dan migrasi di
`Data/Migrations/`. Skrip DDL lengkap ada di `docs/schema.sql`, dibuat dengan
perintah berikut:

```powershell
cd app/Surveillance.Web
dotnet ef migrations script --idempotent --output ../../docs/schema.sql
```

```mermaid
erDiagram
    Cases ||--o{ Events : "event agent"
    Cases ||--o{ Decisions : "keputusan analis"
    Cases ||--o{ ToolResultPreviews : "data mentah tool"
    Cases ||--o{ CaseAuditRecords : "tindakan aplikasi"

    Cases {
        uniqueidentifier Id PK
        nvarchar Symbol "kode saham"
        date AnomalyDate
        nvarchar TriggerJson "AnomalyTrigger"
        nvarchar Notes
        nvarchar Provider "fake, maf, langgraph, adk"
        nvarchar RunId "run di agent"
        nvarchar Status "CaseStateMachine"
        bigint LastSequence
        datetime2 AwaitingSince "draf mulai menunggu"
        int ApprovalTimeoutMinutes "null = Approval:ExpiryHours"
        nvarchar ClaimedBy "pemegang (nama tampilan)"
        nvarchar ClaimedByUser
        datetime2 ClaimActivityAt
        datetime2 TokensCompactedAt "retensi"
        datetime2 EventsPurgedAt "retensi"
        datetime2 CreatedAt
        datetime2 UpdatedAt
    }

    Events {
        bigint Id PK
        uniqueidentifier CaseId FK
        nvarchar RunId "unik bersama Sequence"
        bigint Sequence
        nvarchar Type "katalog event"
        datetime2 Timestamp
        nvarchar PayloadJson "tanpa resultPreview"
        datetime2 ReceivedAt
    }

    Decisions {
        bigint Id PK
        uniqueidentifier CaseId FK
        nvarchar ApprovalId
        nvarchar Decision "approve, revise, reject, expire"
        nvarchar Comment
        nvarchar DecidedBy "pengguna login atau Sistem"
        datetime2 DecidedAt
    }

    ToolResultPreviews {
        bigint Id PK
        uniqueidentifier CaseId FK
        nvarchar RunId "unik bersama ToolCallId"
        nvarchar ToolCallId
        nvarchar ToolName
        nvarchar Preview "maks. 8 KB"
        datetime2 CreatedAt
    }

    CaseAuditRecords {
        bigint Id PK
        uniqueidentifier CaseId FK
        datetime2 At
        nvarchar Actor
        nvarchar Action "claim, release, assign, expire"
        nvarchar Detail
    }

    Settings {
        nvarchar Key PK
        nvarchar Value
        datetime2 UpdatedAt
    }
```

## Indeks penting

| Tabel | Indeks | Gunanya |
|---|---|---|
| `Events` | Unik `(RunId, Sequence)` | Menangkap event ganda tanpa query tambahan (`docs/NOTES-PERSIST.md`) |
| `Events` | `(CaseId, Sequence)` | Putar ulang dan `events.json?after=` |
| `Cases` | `(Status, AwaitingSince)` | Antrean approval dan pemeriksaan kedaluwarsa |
| `ToolResultPreviews` | Unik `(RunId, ToolCallId)` | Satu pratinjau per panggilan tool |
| `CaseAuditRecords` | `(CaseId, At)` | Garis waktu audit |

## Akses data

Semua akses lewat repository di `app/Surveillance.Web/Data/Repositories/`.
`RepositoryBoundaryTests` memastikan tidak ada `DbContext` yang dipakai di luar
folder `Data` dan `Program.cs`.

| Repository | Isi |
|---|---|
| `ICaseRepository` | Kasus: buat, baca, daftar, antrean, run aktif, cursor untuk `RunPump` |
| `IEventRepository` | Event dan pratinjau: tambah dalam satu perjalanan ke database, putar ulang, retensi |
| `IApprovalRepository` | Keputusan, pegangan kasus, kedaluwarsa (semuanya update bersyarat), dan audit |
| `ISettingsRepository` | Solusi agent aktif dari halaman Setting |
