-- ============================================================
-- SaviSchedular v2.0 — Fresh Start Database Setup
-- Database: SaviSchedular
-- Run this script ONCE on a fresh database
-- ============================================================

USE [SaviSchedular]
GO

-- ============================================================
-- TABLE 1: Products
-- Top-level product registration (SaviSchools, 10xViral, etc.)
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'Products' AND type = 'U')
BEGIN
    CREATE TABLE [dbo].[Products] (
        [ProductId]       INT            IDENTITY(1,1) NOT NULL,
        [ProductCode]     NVARCHAR(50)   NOT NULL,
        [ProductName]     NVARCHAR(200)  NOT NULL,
        [BaseUrl]         NVARCHAR(500)  NOT NULL,
        [ApiToken]        NVARCHAR(500)  NULL,
        [TokenType]       NVARCHAR(20)   NOT NULL CONSTRAINT DF_Products_TokenType DEFAULT 'Bearer',
        [TokenHeaderName] NVARCHAR(100)  NOT NULL CONSTRAINT DF_Products_HeaderName DEFAULT 'Authorization',
        [Description]     NVARCHAR(1000) NULL,
        [IsActive]        BIT            NOT NULL CONSTRAINT DF_Products_Active DEFAULT 1,
        [CreatedAt]       DATETIME       NOT NULL CONSTRAINT DF_Products_Created DEFAULT GETDATE(),
        [CreatedBy]       NVARCHAR(100)  NULL,
        CONSTRAINT PK_Products PRIMARY KEY ([ProductId]),
        CONSTRAINT UQ_ProductCode UNIQUE ([ProductCode])
    )
    PRINT 'Table Products created.'
END
ELSE PRINT 'Table Products already exists.'
GO

-- Seed default ApiToken for SaviSchools if empty
UPDATE Products SET ApiToken = 'SAVI_SECRET_KEY_2026' WHERE ProductCode = 'SAVISCHOOLS' AND (ApiToken IS NULL OR ApiToken = '');
GO

-- ============================================================
-- TABLE 2: ProductJobTypes
-- Job types per product (WhatsApp Alert, Fee Reminder, etc.)
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'ProductJobTypes' AND type = 'U')
BEGIN
    CREATE TABLE [dbo].[ProductJobTypes] (
        [JobTypeId]      INT            IDENTITY(1,1) NOT NULL,
        [ProductId]      INT            NOT NULL,
        [JobTypeCode]    NVARCHAR(50)   NOT NULL,
        [JobTypeName]    NVARCHAR(200)  NOT NULL,
        [DefaultApiPath] NVARCHAR(500)  NULL,
        [HttpMethod]     NVARCHAR(10)   NOT NULL CONSTRAINT DF_PJT_Method DEFAULT 'POST',
        [Description]    NVARCHAR(1000) NULL,
        [IsActive]       BIT            NOT NULL CONSTRAINT DF_PJT_Active DEFAULT 1,
        [CreatedAt]      DATETIME       NOT NULL CONSTRAINT DF_PJT_Created DEFAULT GETDATE(),
        CONSTRAINT PK_ProductJobTypes PRIMARY KEY ([JobTypeId]),
        CONSTRAINT UQ_Product_JobTypeCode UNIQUE ([ProductId], [JobTypeCode]),
        CONSTRAINT FK_PJT_Product FOREIGN KEY ([ProductId]) REFERENCES [Products]([ProductId])
    )
    PRINT 'Table ProductJobTypes created.'
END
ELSE PRINT 'Table ProductJobTypes already exists.'
GO

-- ============================================================
-- TABLE 3: ProductClients
-- Clients/Schools registered under each product
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'ProductClients' AND type = 'U')
BEGIN
    CREATE TABLE [dbo].[ProductClients] (
        [ClientId]      BIGINT         IDENTITY(1,1) NOT NULL,
        [ProductId]     INT            NOT NULL,
        [ClientName]    NVARCHAR(200)  NOT NULL,
        [ExternalId]    NVARCHAR(100)  NOT NULL,
        [CustomBaseUrl] NVARCHAR(500)  NULL,
        [IsActive]      BIT            NOT NULL CONSTRAINT DF_PC_Active DEFAULT 1,
        [CreatedAt]     DATETIME       NOT NULL CONSTRAINT DF_PC_Created DEFAULT GETDATE(),
        [CreatedBy]     NVARCHAR(100)  NULL,
        CONSTRAINT PK_ProductClients PRIMARY KEY ([ClientId]),
        CONSTRAINT UQ_Product_ExternalId UNIQUE ([ProductId], [ExternalId]),
        CONSTRAINT FK_PC_Product FOREIGN KEY ([ProductId]) REFERENCES [Products]([ProductId])
    )
    PRINT 'Table ProductClients created.'
END
ELSE PRINT 'Table ProductClients already exists.'
GO

-- ============================================================
-- TABLE 4: SchedulerJobInstances
-- Schedule per client per job type
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'SchedulerJobInstances' AND type = 'U')
BEGIN
    CREATE TABLE [dbo].[SchedulerJobInstances] (
        [InstanceId]              BIGINT         IDENTITY(1,1) NOT NULL,
        [ClientId]                BIGINT         NOT NULL,
        [ProductId]               INT            NOT NULL,
        [JobTypeId]               INT            NOT NULL,
        [CustomApiPath]           NVARCHAR(500)  NULL,
        [CustomApiToken]          NVARCHAR(500)  NULL,
        [PayloadJson]             NVARCHAR(MAX)  NULL,
        [ScheduledHour]           INT            NOT NULL,
        [ScheduledMinute]         INT            NOT NULL CONSTRAINT DF_JI_Min DEFAULT 0,
        [TimeZone]                NVARCHAR(100)  NOT NULL CONSTRAINT DF_JI_TZ DEFAULT 'India Standard Time',
        [IsActive]                BIT            NOT NULL CONSTRAINT DF_JI_Active DEFAULT 1,
        [RunOnHolidays]           BIT            NOT NULL CONSTRAINT DF_JI_Holiday DEFAULT 0,
        [MisfireThresholdMinutes] INT            NOT NULL CONSTRAINT DF_JI_Misfire DEFAULT 15,
        [CreatedAt]               DATETIME       NOT NULL CONSTRAINT DF_JI_Created DEFAULT GETDATE(),
        [UpdatedAt]               DATETIME       NOT NULL CONSTRAINT DF_JI_Updated DEFAULT GETDATE(),
        [CreatedBy]               NVARCHAR(100)  NULL,
        CONSTRAINT PK_SchedulerJobInstances PRIMARY KEY ([InstanceId]),
        CONSTRAINT UQ_Client_JobType UNIQUE ([ClientId], [JobTypeId]),
        CONSTRAINT FK_JI_Client  FOREIGN KEY ([ClientId])  REFERENCES [ProductClients]([ClientId]),
        CONSTRAINT FK_JI_Product FOREIGN KEY ([ProductId]) REFERENCES [Products]([ProductId]),
        CONSTRAINT FK_JI_JobType FOREIGN KEY ([JobTypeId]) REFERENCES [ProductJobTypes]([JobTypeId])
    )
    PRINT 'Table SchedulerJobInstances created.'
END
ELSE PRINT 'Table SchedulerJobInstances already exists.'
GO

-- ============================================================
-- TABLE 5: SchedulerExecutionLogs
-- Log of every job run
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'SchedulerExecutionLogs' AND type = 'U')
BEGIN
    CREATE TABLE [dbo].[SchedulerExecutionLogs] (
        [LogId]           BIGINT         IDENTITY(1,1) NOT NULL,
        [InstanceId]      BIGINT         NULL,
        [ClientId]        BIGINT         NULL,
        [ProductId]       INT            NULL,
        [ClientName]      NVARCHAR(200)  NULL,
        [ExternalId]      NVARCHAR(100)  NULL,
        [JobTypeCode]     NVARCHAR(50)   NULL,
        [TriggerType]     NVARCHAR(20)   NOT NULL,
        [StartedAt]       DATETIME       NOT NULL CONSTRAINT DF_EL_Started DEFAULT GETDATE(),
        [CompletedAt]     DATETIME       NULL,
        [DurationSeconds] DECIMAL(10,2)  NULL,
        [Status]          NVARCHAR(20)   NOT NULL,
        [SkipReason]      NVARCHAR(100)  NULL,
        [ApiUrl]          NVARCHAR(1000) NULL,
        [PayloadSent]     NVARCHAR(MAX)  NULL,
        [HttpStatusCode]  INT            NULL,
        [ResponseBody]    NVARCHAR(MAX)  NULL,
        [ErrorMessage]    NVARCHAR(MAX)  NULL,
        [HangfireJobId]   NVARCHAR(100)  NULL,
        CONSTRAINT PK_SchedulerExecutionLogs PRIMARY KEY ([LogId])
    )
    CREATE INDEX IX_EL_Client_Date ON [dbo].[SchedulerExecutionLogs] ([ClientId], [StartedAt] DESC)
    CREATE INDEX IX_EL_Status_Date ON [dbo].[SchedulerExecutionLogs] ([Status], [StartedAt] DESC)
    CREATE INDEX IX_EL_Product_Date ON [dbo].[SchedulerExecutionLogs] ([ProductId], [StartedAt] DESC)
    PRINT 'Table SchedulerExecutionLogs created.'
END
ELSE PRINT 'Table SchedulerExecutionLogs already exists.'
GO

-- ============================================================
-- TABLE 6: ApiClients
-- External projects that can create schedules via API
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'ApiClients' AND type = 'U')
BEGIN
    CREATE TABLE [dbo].[ApiClients] (
        [ApiClientId]       INT            IDENTITY(1,1) NOT NULL,
        [ClientName]        NVARCHAR(100)  NOT NULL,
        [ApiKey]            NVARCHAR(200)  NOT NULL,
        [AllowedProductIds] NVARCHAR(500)  NULL,
        [IsActive]          BIT            NOT NULL CONSTRAINT DF_AC_Active DEFAULT 1,
        [CreatedAt]         DATETIME       NOT NULL CONSTRAINT DF_AC_Created DEFAULT GETDATE(),
        [CreatedBy]         NVARCHAR(100)  NULL,
        [LastUsedAt]        DATETIME       NULL,
        CONSTRAINT PK_ApiClients PRIMARY KEY ([ApiClientId]),
        CONSTRAINT UQ_ApiKey UNIQUE ([ApiKey])
    )
    PRINT 'Table ApiClients created.'
END
ELSE PRINT 'Table ApiClients already exists.'
GO

-- ============================================================
-- TABLE 7: SchedulerGlobalConfig
-- System-wide settings (SMTP, etc.)
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'SchedulerGlobalConfig' AND type = 'U')
BEGIN
    CREATE TABLE [dbo].[SchedulerGlobalConfig] (
        [ConfigKey]   NVARCHAR(100)  NOT NULL,
        [ConfigValue] NVARCHAR(2000) NULL,
        [Description] NVARCHAR(500)  NULL,
        [UpdatedAt]   DATETIME       NOT NULL CONSTRAINT DF_GC_Updated DEFAULT GETDATE(),
        [UpdatedBy]   NVARCHAR(100)  NULL,
        CONSTRAINT PK_SchedulerGlobalConfig PRIMARY KEY ([ConfigKey])
    )
    PRINT 'Table SchedulerGlobalConfig created.'
END
ELSE PRINT 'Table SchedulerGlobalConfig already exists.'
GO

-- ============================================================
-- TABLE 8: SchedulerAuditLogs
-- Change history for config/schedule changes
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'SchedulerAuditLogs' AND type = 'U')
BEGIN
    CREATE TABLE [dbo].[SchedulerAuditLogs] (
        [AuditId]   BIGINT        IDENTITY(1,1) NOT NULL,
        [TableName] NVARCHAR(100) NOT NULL,
        [RecordId]  NVARCHAR(100) NULL,
        [Action]    NVARCHAR(20)  NOT NULL,
        [OldValues] NVARCHAR(MAX) NULL,
        [NewValues] NVARCHAR(MAX) NULL,
        [ChangedBy] NVARCHAR(100) NULL,
        [ChangedAt] DATETIME      NOT NULL CONSTRAINT DF_AL_Changed DEFAULT GETDATE(),
        [IPAddress] NVARCHAR(50)  NULL,
        [Notes]     NVARCHAR(500) NULL,
        CONSTRAINT PK_SchedulerAuditLogs PRIMARY KEY ([AuditId])
    )
    CREATE INDEX IX_AL_Table_Date ON [dbo].[SchedulerAuditLogs] ([TableName], [ChangedAt] DESC)
    PRINT 'Table SchedulerAuditLogs created.'
END
ELSE PRINT 'Table SchedulerAuditLogs already exists.'
GO

-- ============================================================
-- SEED: SchedulerGlobalConfig
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM [dbo].[SchedulerGlobalConfig])
BEGIN
    INSERT INTO [dbo].[SchedulerGlobalConfig] ([ConfigKey],[ConfigValue],[Description]) VALUES
    ('NotificationEmail',    'admin@savischools.com',                          'Admin alert email address'),
    ('SMTPHost',             'email-smtp.ap-south-1.amazonaws.com',            'SMTP server hostname'),
    ('SMTPPort',             '587',                                            'SMTP port number'),
    ('SMTPSender',           'info@savischools.com',                           'Email sender address'),
    ('SMTPUsername',         '',                                               'SMTP username'),
    ('SMTPPassword',         '',                                               'SMTP password'),
    ('HolidayCheckEnabled',  'false',                                          'Enable or disable holiday check before running jobs'),
    ('AdminUITitle',         'SaviSchedular v2.0',                             'Title displayed on the Admin UI')
    PRINT 'Global config seed data inserted.'
END
ELSE PRINT 'Global config already seeded.'
GO

-- ============================================================
-- SEED: Products (Sample)
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM [dbo].[Products])
BEGIN
    INSERT INTO [dbo].[Products] ([ProductCode],[ProductName],[BaseUrl],[TokenType],[TokenHeaderName],[Description],[IsActive],[CreatedBy]) VALUES
    ('SAVISCHOOLS', 'SaviSchools',  'https://abc.savischools.com', 'Bearer', 'Authorization', 'SaviSchools School Management System', 1, 'Admin'),
    ('10XVIRAL',    '10xViral',     'https://api.10xviral.com',    'Bearer', 'Authorization', '10xViral Marketing Platform',          1, 'Admin'),
    ('SAVIPLATTER', 'SaviPlatter',  'https://api.saviplatter.com', 'Bearer', 'Authorization', 'SaviPlatter Restaurant Management',    1, 'Admin')
    PRINT 'Products seed data inserted.'
END
ELSE PRINT 'Products already seeded.'
GO

-- ============================================================
-- SEED: ProductJobTypes (Sample — SaviSchools)
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM [dbo].[ProductJobTypes])
BEGIN
    DECLARE @saviId INT = (SELECT ProductId FROM [dbo].[Products] WHERE ProductCode = 'SAVISCHOOLS')
    DECLARE @viralId INT = (SELECT ProductId FROM [dbo].[Products] WHERE ProductCode = '10XVIRAL')

    INSERT INTO [dbo].[ProductJobTypes] ([ProductId],[JobTypeCode],[JobTypeName],[DefaultApiPath],[HttpMethod],[Description],[IsActive]) VALUES
    (@saviId, 'WHATSAPP_ABSENT',  'WhatsApp Absent Alert',  '/api/asapi/run-absent-whatsapp', 'POST', 'Send WhatsApp to parents of absent students', 1),
    (@saviId, 'FEE_REMINDER',     'Fee Reminder',           '/api/asapi/run-fee-reminder',    'POST', 'Send fee payment reminder to parents',         1),
    (@saviId, 'ATTENDANCE_REPORT','Attendance Report Email', '/api/report/attendance-email',   'POST', 'Send daily attendance report via email',        1),
    (@saviId, 'RESULT_NOTIFY',    'Result Notification',    '/api/asapi/result-notify',       'POST', 'Notify parents about exam results',             1),
    (@viralId,'CONTENT_PUBLISH',  'Content Auto Publish',   '/api/content/publish',           'POST', 'Auto publish scheduled content',               1)
    PRINT 'ProductJobTypes seed data inserted.'
END
ELSE PRINT 'ProductJobTypes already seeded.'
GO

PRINT ''
PRINT '======================================='
PRINT ' SaviSchedular v2.0 DB Setup COMPLETE'
PRINT ' 8 Tables | Fresh Start Ready'
PRINT '======================================='
