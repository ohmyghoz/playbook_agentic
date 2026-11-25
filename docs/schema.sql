IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124150620_Initial'
)
BEGIN
    CREATE TABLE [Cases] (
        [Id] uniqueidentifier NOT NULL,
        [Symbol] nvarchar(8) NOT NULL,
        [AnomalyDate] date NOT NULL,
        [TriggerJson] nvarchar(max) NOT NULL,
        [Notes] nvarchar(max) NULL,
        [Provider] nvarchar(32) NOT NULL,
        [RunId] nvarchar(128) NULL,
        [Status] nvarchar(32) NOT NULL,
        [LastSequence] bigint NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Cases] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124150620_Initial'
)
BEGIN
    CREATE TABLE [Decisions] (
        [Id] bigint NOT NULL IDENTITY,
        [CaseId] uniqueidentifier NOT NULL,
        [ApprovalId] nvarchar(128) NOT NULL,
        [Decision] nvarchar(16) NOT NULL,
        [Comment] nvarchar(max) NULL,
        [DecidedBy] nvarchar(128) NOT NULL,
        [DecidedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Decisions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Decisions_Cases_CaseId] FOREIGN KEY ([CaseId]) REFERENCES [Cases] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124150620_Initial'
)
BEGIN
    CREATE TABLE [Events] (
        [Id] bigint NOT NULL IDENTITY,
        [CaseId] uniqueidentifier NOT NULL,
        [RunId] nvarchar(128) NOT NULL,
        [Sequence] bigint NOT NULL,
        [Type] nvarchar(32) NOT NULL,
        [Timestamp] datetime2 NOT NULL,
        [PayloadJson] nvarchar(max) NOT NULL,
        [ReceivedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Events] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Events_Cases_CaseId] FOREIGN KEY ([CaseId]) REFERENCES [Cases] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124150620_Initial'
)
BEGIN
    CREATE INDEX [IX_Cases_CreatedAt] ON [Cases] ([CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124150620_Initial'
)
BEGIN
    CREATE INDEX [IX_Decisions_CaseId] ON [Decisions] ([CaseId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124150620_Initial'
)
BEGIN
    CREATE INDEX [IX_Events_CaseId_Sequence] ON [Events] ([CaseId], [Sequence]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124150620_Initial'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Events_RunId_Sequence] ON [Events] ([RunId], [Sequence]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124150620_Initial'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261124150620_Initial', N'10.0.15');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124191001_AddAppSettings'
)
BEGIN
    CREATE TABLE [Settings] (
        [Key] nvarchar(64) NOT NULL,
        [Value] nvarchar(256) NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Settings] PRIMARY KEY ([Key])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124191001_AddAppSettings'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261124191001_AddAppSettings', N'10.0.15');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124195021_AddToolResultPreviews'
)
BEGIN
    CREATE TABLE [ToolResultPreviews] (
        [Id] bigint NOT NULL IDENTITY,
        [CaseId] uniqueidentifier NOT NULL,
        [RunId] nvarchar(128) NOT NULL,
        [ToolCallId] nvarchar(128) NOT NULL,
        [ToolName] nvarchar(64) NOT NULL,
        [Preview] nvarchar(max) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_ToolResultPreviews] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ToolResultPreviews_Cases_CaseId] FOREIGN KEY ([CaseId]) REFERENCES [Cases] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124195021_AddToolResultPreviews'
)
BEGIN
    CREATE INDEX [IX_ToolResultPreviews_CaseId_ToolCallId] ON [ToolResultPreviews] ([CaseId], [ToolCallId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124195021_AddToolResultPreviews'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ToolResultPreviews_RunId_ToolCallId] ON [ToolResultPreviews] ([RunId], [ToolCallId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261124195021_AddToolResultPreviews'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261124195021_AddToolResultPreviews', N'10.0.15');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261125030427_AddApprovalClaimsAndAudit'
)
BEGIN
    ALTER TABLE [Cases] ADD [ApprovalTimeoutMinutes] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261125030427_AddApprovalClaimsAndAudit'
)
BEGIN
    ALTER TABLE [Cases] ADD [AwaitingSince] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261125030427_AddApprovalClaimsAndAudit'
)
BEGIN
    ALTER TABLE [Cases] ADD [ClaimActivityAt] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261125030427_AddApprovalClaimsAndAudit'
)
BEGIN
    ALTER TABLE [Cases] ADD [ClaimedBy] nvarchar(128) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261125030427_AddApprovalClaimsAndAudit'
)
BEGIN
    ALTER TABLE [Cases] ADD [ClaimedByUser] nvarchar(64) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261125030427_AddApprovalClaimsAndAudit'
)
BEGIN
    CREATE TABLE [CaseAuditRecords] (
        [Id] bigint NOT NULL IDENTITY,
        [CaseId] uniqueidentifier NOT NULL,
        [At] datetime2 NOT NULL,
        [Actor] nvarchar(128) NOT NULL,
        [Action] nvarchar(32) NOT NULL,
        [Detail] nvarchar(2000) NULL,
        CONSTRAINT [PK_CaseAuditRecords] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CaseAuditRecords_Cases_CaseId] FOREIGN KEY ([CaseId]) REFERENCES [Cases] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261125030427_AddApprovalClaimsAndAudit'
)
BEGIN
    CREATE INDEX [IX_Cases_Status_AwaitingSince] ON [Cases] ([Status], [AwaitingSince]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261125030427_AddApprovalClaimsAndAudit'
)
BEGIN
    CREATE INDEX [IX_CaseAuditRecords_CaseId_At] ON [CaseAuditRecords] ([CaseId], [At]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261125030427_AddApprovalClaimsAndAudit'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261125030427_AddApprovalClaimsAndAudit', N'10.0.15');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261125031747_AddRetentionMarkers'
)
BEGIN
    ALTER TABLE [Cases] ADD [EventsPurgedAt] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261125031747_AddRetentionMarkers'
)
BEGIN
    ALTER TABLE [Cases] ADD [TokensCompactedAt] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261125031747_AddRetentionMarkers'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261125031747_AddRetentionMarkers', N'10.0.15');
END;

COMMIT;
GO

