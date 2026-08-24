-- ============================================================
-- SaviSchedular — Universal Scheduler Database Setup
-- Database: SaviSchedular
-- Run this script ONCE to create all required tables
-- ============================================================

USE [SaviSchedular]
GO

-- ============================================================
-- TABLE 1: SchedulerJobTypes
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'SchedulerJobTypes' AND type = 'U')
BEGIN
    CREATE TABLE [dbo].[SchedulerJobTypes] (
        [JobTypeId]      INT            IDENTITY(1,1) NOT NULL,
        [JobTypeCode]    NVARCHAR(50)   NOT NULL,
        [JobTypeName]    NVARCHAR(200)  NOT NULL,
        [Description]    NVARCHAR(1000) NULL,
        [DefaultApiPath] NVARCHAR(500)  NULL,
        [HttpMethod]     NVARCHAR(10)   NOT NULL CONSTRAINT DF_JobTypes_Method DEFAULT 'POST',
        [IsActive]       BIT            NOT NULL CONSTRAINT DF_JobTypes_Active DEFAULT 1,
        [CreatedAt]      DATETIME       NOT NULL CONSTRAINT DF_JobTypes_Created DEFAULT GETDATE(),
        CONSTRAINT PK_SchedulerJobTypes PRIMARY KEY ([JobTypeId]),
        CONSTRAINT UQ_JobTypeCode UNIQUE ([JobTypeCode])
    )
    PRINT 'Table SchedulerJobTypes created.'
END
ELSE PRINT 'Table SchedulerJobTypes already exists.'
GO

-- ============================================================
-- TABLE 2: SchedulerGlobalConfig
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'SchedulerGlobalConfig' AND type = 'U')
BEGIN
    CREATE TABLE [dbo].[SchedulerGlobalConfig] (
        [ConfigKey]   NVARCHAR(100)  NOT NULL,
        [ConfigValue] NVARCHAR(2000) NULL,
        [Description] NVARCHAR(500)  NULL,
        [UpdatedAt]   DATETIME       NOT NULL CONSTRAINT DF_GlobalConfig_Updated DEFAULT GETDATE(),
        [UpdatedBy]   NVARCHAR(100)  NULL,
        CONSTRAINT PK_SchedulerGlobalConfig PRIMARY KEY ([ConfigKey])
    )
    PRINT 'Table SchedulerGlobalConfig created.'
END
ELSE PRINT 'Table SchedulerGlobalConfig already exists.'
GO

-- ============================================================
-- TABLE 3: SchoolSchedulerSettings
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'SchoolSchedulerSettings' AND type = 'U')
BEGIN
    CREATE TABLE [dbo].[SchoolSchedulerSettings] (
        [SchoolId]        BIGINT        NOT NULL,
        [SchoolName]      NVARCHAR(200) NOT NULL,
        [IsActive]        BIT           NOT NULL CONSTRAINT DF_SchSettings_Active DEFAULT 1,
        [DefaultTimezone] NVARCHAR(100) NOT NULL CONSTRAINT DF_SchSettings_TZ DEFAULT 'India Standard Time',
        [CreatedAt]       DATETIME      NOT NULL CONSTRAINT DF_SchSettings_Created DEFAULT GETDATE(),
        [UpdatedAt]       DATETIME      NOT NULL CONSTRAINT DF_SchSettings_Updated DEFAULT GETDATE(),
        [CreatedBy]       NVARCHAR(100) NULL,
        [Notes]           NVARCHAR(500) NULL,
        CONSTRAINT PK_SchoolSchedulerSettings PRIMARY KEY ([SchoolId])
    )
    PRINT 'Table SchoolSchedulerSettings created.'
END
ELSE PRINT 'Table SchoolSchedulerSettings already exists.'
GO

-- ============================================================
-- TABLE 4: SchedulerJobInstances
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'SchedulerJobInstances' AND type = 'U')
BEGIN
    CREATE TABLE [dbo].[SchedulerJobInstances] (
        [InstanceId]              BIGINT        IDENTITY(1,1) NOT NULL,
        [SchoolId]                BIGINT        NOT NULL,
        [JobTypeCode]             NVARCHAR(50)  NOT NULL,
        [ScheduledHour]           INT           NOT NULL,
        [ScheduledMinute]         INT           NOT NULL CONSTRAINT DF_JobInst_Min DEFAULT 0,
        [TimeZone]                NVARCHAR(100) NOT NULL CONSTRAINT DF_JobInst_TZ DEFAULT 'India Standard Time',
        [IsActive]                BIT           NOT NULL CONSTRAINT DF_JobInst_Active DEFAULT 1,
        [RunOnHolidays]           BIT           NOT NULL CONSTRAINT DF_JobInst_Holiday DEFAULT 0,
        [MisfireThresholdMinutes] INT           NOT NULL CONSTRAINT DF_JobInst_Misfire DEFAULT 15,
        [CreatedAt]               DATETIME      NOT NULL CONSTRAINT DF_JobInst_Created DEFAULT GETDATE(),
        [UpdatedAt]               DATETIME      NOT NULL CONSTRAINT DF_JobInst_Updated DEFAULT GETDATE(),
        [CreatedBy]               NVARCHAR(100) NULL,
        CONSTRAINT PK_SchedulerJobInstances PRIMARY KEY ([InstanceId]),
        CONSTRAINT UQ_School_JobType UNIQUE ([SchoolId], [JobTypeCode])
    )
    PRINT 'Table SchedulerJobInstances created.'
END
ELSE PRINT 'Table SchedulerJobInstances already exists.'
GO

-- ============================================================
-- TABLE 5: SchoolApiConfigs
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'SchoolApiConfigs' AND type = 'U')
BEGIN
    CREATE TABLE [dbo].[SchoolApiConfigs] (
        [ConfigId]       BIGINT         IDENTITY(1,1) NOT NULL,
        [SchoolId]       BIGINT         NOT NULL,
        [JobTypeCode]    NVARCHAR(50)   NOT NULL,
        [BaseUrl]        NVARCHAR(500)  NOT NULL,
        [ApiPath]        NVARCHAR(500)  NULL,
        [HttpMethod]     NVARCHAR(10)   NOT NULL CONSTRAINT DF_ApiConfig_Method DEFAULT 'POST',
        [CustomHeaders]  NVARCHAR(2000) NULL,
        [TimeoutMinutes] INT            NOT NULL CONSTRAINT DF_ApiConfig_Timeout DEFAULT 15,
        [IsActive]       BIT            NOT NULL CONSTRAINT DF_ApiConfig_Active DEFAULT 1,
        [CreatedAt]      DATETIME       NOT NULL CONSTRAINT DF_ApiConfig_Created DEFAULT GETDATE(),
        [UpdatedAt]      DATETIME       NOT NULL CONSTRAINT DF_ApiConfig_Updated DEFAULT GETDATE(),
        CONSTRAINT PK_SchoolApiConfigs PRIMARY KEY ([ConfigId]),
        CONSTRAINT UQ_School_JobType_Config UNIQUE ([SchoolId], [JobTypeCode])
    )
    PRINT 'Table SchoolApiConfigs created.'
END
ELSE PRINT 'Table SchoolApiConfigs already exists.'
GO

-- ============================================================
-- TABLE 6: SchedulerExecutionLogs
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'SchedulerExecutionLogs' AND type = 'U')
BEGIN
    CREATE TABLE [dbo].[SchedulerExecutionLogs] (
        [LogId]           BIGINT         IDENTITY(1,1) NOT NULL,
        [SchoolId]        BIGINT         NOT NULL,
        [SchoolName]      NVARCHAR(200)  NULL,
        [JobTypeCode]     NVARCHAR(50)   NULL,
        [TriggerType]     NVARCHAR(20)   NOT NULL,
        [StartedAt]       DATETIME       NOT NULL CONSTRAINT DF_ExecLog_Started DEFAULT GETDATE(),
        [CompletedAt]     DATETIME       NULL,
        [DurationSeconds] DECIMAL(10,2)  NULL,
        [Status]          NVARCHAR(20)   NOT NULL,
        [SkipReason]      NVARCHAR(100)  NULL,
        [ApiUrl]          NVARCHAR(1000) NULL,
        [HttpStatusCode]  INT            NULL,
        [ResponseBody]    NVARCHAR(MAX)  NULL,
        [ErrorMessage]    NVARCHAR(MAX)  NULL,
        [HangfireJobId]   NVARCHAR(100)  NULL,
        CONSTRAINT PK_SchedulerExecutionLogs PRIMARY KEY ([LogId])
    )
    CREATE INDEX IX_ExecLogs_School_Date ON [dbo].[SchedulerExecutionLogs] ([SchoolId], [StartedAt] DESC)
    CREATE INDEX IX_ExecLogs_Status_Date ON [dbo].[SchedulerExecutionLogs] ([Status], [StartedAt] DESC)
    PRINT 'Table SchedulerExecutionLogs created.'
END
ELSE PRINT 'Table SchedulerExecutionLogs already exists.'
GO

-- ============================================================
-- TABLE 7: SchedulerAuditLogs
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
        [ChangedAt] DATETIME      NOT NULL CONSTRAINT DF_AuditLog_Changed DEFAULT GETDATE(),
        [IPAddress] NVARCHAR(50)  NULL,
        [Notes]     NVARCHAR(500) NULL,
        CONSTRAINT PK_SchedulerAuditLogs PRIMARY KEY ([AuditId])
    )
    CREATE INDEX IX_AuditLogs_Table_Date ON [dbo].[SchedulerAuditLogs] ([TableName], [ChangedAt] DESC)
    PRINT 'Table SchedulerAuditLogs created.'
END
ELSE PRINT 'Table SchedulerAuditLogs already exists.'
GO

-- ============================================================
-- SEED: SchedulerJobTypes
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM [dbo].[SchedulerJobTypes])
BEGIN
    INSERT INTO [dbo].[SchedulerJobTypes] ([JobTypeCode],[JobTypeName],[Description],[DefaultApiPath],[HttpMethod],[IsActive]) VALUES
    ('AbsentWhatsApp',     'Absent WhatsApp Alert',   'Send WhatsApp notification to parents of absent students',          '/api/asapi/run-absent-whatsapp', 'POST', 1),
    ('FeeReminder',        'Fee Reminder',             'Send fee payment reminder to parents of students with pending dues', '/api/asapi/run-fee-reminder',    'POST', 1),
    ('AttendanceReport',   'Attendance Report Email',  'Send daily attendance report via email',                            '/api/report/attendance-email',   'POST', 1),
    ('ResultNotification', 'Result Notification',      'Notify parents about exam results',                                 '/api/asapi/result-notify',       'POST', 1),
    ('CustomApiCall',      'Custom API Call',          'Call any custom API endpoint (fully configurable)',                 NULL,                             'POST', 1)
    PRINT 'Job Types seed data inserted.'
END
ELSE PRINT 'Job Types already seeded.'
GO

-- ============================================================
-- SEED: SchedulerGlobalConfig
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM [dbo].[SchedulerGlobalConfig])
BEGIN
    INSERT INTO [dbo].[SchedulerGlobalConfig] ([ConfigKey],[ConfigValue],[Description]) VALUES
    ('DefaultBaseUrl',       'http://localhost:44548/',                        'Default API base URL used for all schools'),
    ('NotificationEmail',    'admin@savischools.com',                          'Admin alert email address'),
    ('MisfireThresholdMins', '15',                                             'Skip job if it fires more than this many minutes late'),
    ('SMTPHost',             'email-smtp.ap-south-1.amazonaws.com',            'SMTP server hostname'),
    ('SMTPPort',             '587',                                            'SMTP port number'),
    ('SMTPSender',           'info@savischools.com',                           'Email sender address'),
    ('SMTPUsername',         'AKIASUH4Y5HGXYHNP7XU',                          'SMTP username'),
    ('SMTPPassword',         'BOFGmm72jWwmu6+bSP36d40Awe2GmxZWqVIwjQW8Jo6m', 'SMTP password'),
    ('HolidayCheckEnabled',  'true',                                           'Enable or disable holiday check before running jobs'),
    ('AdminUITitle',         'SaviSchedular Admin',                            'Title displayed on the Admin UI')
    PRINT 'Global config seed data inserted.'
END
-- ============================================================
-- SEED: SchedulerJobInstances (Sample Data)
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM [dbo].[SchedulerJobInstances] WHERE [SchoolId] = 461 AND [JobTypeCode] = 'AbsentWhatsApp')
BEGIN
    INSERT INTO [dbo].[SchedulerJobInstances] 
        ([SchoolId], [JobTypeCode], [ScheduledHour], [ScheduledMinute], [TimeZone], [IsActive], [RunOnHolidays], [MisfireThresholdMinutes], [CreatedBy])
    VALUES 
        (461, 'AbsentWhatsApp', 8, 0, 'India Standard Time', 1, 0, 15, 'Admin');
    PRINT 'Job instance sample data inserted for School 461.'
END
ELSE PRINT 'Job instance for School 461 already exists.'
GO

-- Drop SchoolName column if it exists in SchedulerJobInstances
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('SchedulerJobInstances') AND name = 'SchoolName')
BEGIN
    ALTER TABLE [dbo].[SchedulerJobInstances] DROP COLUMN [SchoolName];
    PRINT 'Column SchoolName dropped from SchedulerJobInstances.'
END
GO

-- ============================================================
-- MIGRATION: Import legacy schedules from [savischoolprd002] database
-- ============================================================
IF EXISTS (SELECT 1 FROM [savischoolprd002].sys.objects WHERE name = 'SchoolSchedules' AND type = 'U')
BEGIN
    INSERT INTO [dbo].[SchedulerJobInstances] 
        ([SchoolId], [JobTypeCode], [ScheduledHour], [ScheduledMinute], [TimeZone], [IsActive], [RunOnHolidays], [MisfireThresholdMinutes], [CreatedAt], [UpdatedAt])
    SELECT 
        s.[SchoolId], 
        'AbsentWhatsApp', 
        s.[ScheduledHour], 
        s.[ScheduledMinute], 
        'India Standard Time', 
        s.[IsActive], 
        0, 
        15, 
        GETDATE(), 
        GETDATE()
    FROM [savischoolprd002].[dbo].[SchoolSchedules] s
    WHERE NOT EXISTS (
        SELECT 1 FROM [dbo].[SchedulerJobInstances] ji 
        WHERE ji.SchoolId = s.SchoolId AND ji.JobTypeCode = 'AbsentWhatsApp'
    );
    PRINT 'Migrated existing SchoolSchedules records from savischoolprd002 to SchedulerJobInstances.'
END
GO

IF EXISTS (SELECT 1 FROM [savischoolprd002].sys.objects WHERE name = 'SchoolSchedule' AND type = 'U')
BEGIN
    INSERT INTO [dbo].[SchedulerJobInstances] 
        ([SchoolId], [JobTypeCode], [ScheduledHour], [ScheduledMinute], [TimeZone], [IsActive], [RunOnHolidays], [MisfireThresholdMinutes], [CreatedAt], [UpdatedAt])
    SELECT 
        s.[SchoolId], 
        'AbsentWhatsApp', 
        s.[ScheduledHour], 
        s.[ScheduledMinute], 
        'India Standard Time', 
        s.[IsActive], 
        0, 
        15, 
        GETDATE(), 
        GETDATE()
    FROM [savischoolprd002].[dbo].[SchoolSchedule] s
    WHERE NOT EXISTS (
        SELECT 1 FROM [dbo].[SchedulerJobInstances] ji 
        WHERE ji.SchoolId = s.SchoolId AND ji.JobTypeCode = 'AbsentWhatsApp'
    );
    PRINT 'Migrated existing SchoolSchedule records from savischoolprd002 to SchedulerJobInstances.'
END
GO

PRINT ''
PRINT '======================================='
PRINT ' SaviSchedular Database Setup COMPLETE'
PRINT ' 7 Tables Created | Seed Data Ready'
PRINT '======================================='

