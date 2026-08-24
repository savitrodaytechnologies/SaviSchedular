using System;

namespace SaviSchedular.Models
{
    // ─── DB Entity Models ───────────────────────────────────────────────────

    public class SchedulerJobInstanceModel
    {
        public long   InstanceId              { get; set; }
        public long   SchoolId                { get; set; }
        public string JobTypeCode             { get; set; }
        public string JobTypeName             { get; set; }
        public int    ScheduledHour           { get; set; }
        public int    ScheduledMinute         { get; set; }
        public string TimeZone                { get; set; }
        public bool   IsActive                { get; set; }
        public bool   RunOnHolidays           { get; set; }
        public int    MisfireThresholdMinutes { get; set; }
        public DateTime CreatedAt             { get; set; }
        public DateTime UpdatedAt             { get; set; }
        public string CreatedBy               { get; set; }
    }

    public class JobTypeModel
    {
        public int    JobTypeId      { get; set; }
        public string JobTypeCode    { get; set; }
        public string JobTypeName    { get; set; }
        public string Description    { get; set; }
        public string DefaultApiPath { get; set; }
        public string HttpMethod     { get; set; }
        public bool   IsActive       { get; set; }
        public DateTime CreatedAt    { get; set; }
    }

    public class SchoolApiConfigModel
    {
        public long   ConfigId       { get; set; }
        public long   SchoolId       { get; set; }
        public string JobTypeCode    { get; set; }
        public string BaseUrl        { get; set; }
        public string ApiPath        { get; set; }
        public string HttpMethod     { get; set; }
        public string CustomHeaders  { get; set; }
        public int    TimeoutMinutes { get; set; }
        public bool   IsActive       { get; set; }
        public DateTime CreatedAt    { get; set; }
        public DateTime UpdatedAt    { get; set; }
    }

    public class GlobalConfigModel
    {
        public string ConfigKey   { get; set; }
        public string ConfigValue { get; set; }
        public string Description { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string UpdatedBy   { get; set; }
    }

    public class ExecutionLogModel
    {
        public long    LogId           { get; set; }
        public long    SchoolId        { get; set; }
        public string  SchoolName      { get; set; }
        public string  JobTypeCode     { get; set; }
        public string  TriggerType     { get; set; }
        public DateTime StartedAt      { get; set; }
        public DateTime? CompletedAt   { get; set; }
        public decimal? DurationSeconds{ get; set; }
        public string  Status          { get; set; }
        public string  SkipReason      { get; set; }
        public string  ApiUrl          { get; set; }
        public int?    HttpStatusCode  { get; set; }
        public string  ResponseBody    { get; set; }
        public string  ErrorMessage    { get; set; }
        public string  HangfireJobId   { get; set; }
    }

    public class AuditLogModel
    {
        public long   AuditId   { get; set; }
        public string TableName { get; set; }
        public string RecordId  { get; set; }
        public string Action    { get; set; }
        public string OldValues { get; set; }
        public string NewValues { get; set; }
        public string ChangedBy { get; set; }
        public DateTime ChangedAt { get; set; }
        public string IPAddress { get; set; }
        public string Notes     { get; set; }
    }

    // ─── Request Models ──────────────────────────────────────────────────────

    public class SaveScheduleRequest
    {
        public long   SchoolId                { get; set; }
        public string JobTypeCode             { get; set; }
        public int    Hour                    { get; set; }
        public int    Minute                  { get; set; }
        public string TimeZone                { get; set; }
        public bool   RunOnHolidays           { get; set; }
        public int    MisfireThresholdMinutes { get; set; }
        public string CreatedBy               { get; set; }
    }

    public class SaveApiConfigRequest
    {
        public long   SchoolId       { get; set; }
        public string JobTypeCode    { get; set; }
        public string BaseUrl        { get; set; }
        public string ApiPath        { get; set; }
        public string HttpMethod     { get; set; }
        public string CustomHeaders  { get; set; }
        public int    TimeoutMinutes { get; set; }
    }

    public class SaveJobTypeRequest
    {
        public int    JobTypeId      { get; set; }
        public string JobTypeCode    { get; set; }
        public string JobTypeName    { get; set; }
        public string Description    { get; set; }
        public string DefaultApiPath { get; set; }
        public string HttpMethod     { get; set; }
    }

    public class UpdateGlobalConfigRequest
    {
        public string ConfigKey   { get; set; }
        public string ConfigValue { get; set; }
        public string UpdatedBy   { get; set; }
    }

    public class TestConnectionRequest
    {
        public string BaseUrl    { get; set; }
        public string ApiPath    { get; set; }
        public string HttpMethod { get; set; }
        public long   SchoolId   { get; set; }
    }

    // ─── Response / DTO Models ───────────────────────────────────────────────

    public class DashboardStats
    {
        public int ActiveSchools  { get; set; }
        public int ActiveJobs     { get; set; }
        public int SuccessToday   { get; set; }
        public int FailedToday    { get; set; }
        public int SkippedToday   { get; set; }
        public int RunningNow     { get; set; }
    }

    public class DashboardResponse
    {
        public DashboardStats Stats { get; set; }
        public object RecentLogs { get; set; }
        public int Total { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalPages { get; set; }
    }
}
