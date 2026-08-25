using System;
using System.Collections.Generic;

namespace SaviSchedular.Models
{
    // ═══════════════════════════════════════════════════════════════
    // DB ENTITY MODELS
    // ═══════════════════════════════════════════════════════════════

    public class ProductModel
    {
        public int    ProductId       { get; set; }
        public string ProductCode     { get; set; }
        public string ProductName     { get; set; }
        public string BaseUrl         { get; set; }
        public string ApiToken        { get; set; }
        public string TokenType       { get; set; }
        public string TokenHeaderName { get; set; }
        public string AuthType        { get; set; }
        public string TokenUrl        { get; set; }
        public string ClientId        { get; set; }
        public string ClientSecret    { get; set; }
        public string RsaPrivateKey   { get; set; }
        public string RsaPublicKey    { get; set; }
        public string Audience        { get; set; }
        public string Issuer          { get; set; }
        public string Description     { get; set; }
        public bool   IsActive        { get; set; }
        public DateTime CreatedAt     { get; set; }
        public string CreatedBy       { get; set; }
    }

    public class ProductJobTypeModel
    {
        public int    JobTypeId      { get; set; }
        public int    ProductId      { get; set; }
        public string ProductName    { get; set; }  // JOIN for display
        public string JobTypeCode    { get; set; }
        public string JobTypeName    { get; set; }
        public string DefaultApiPath { get; set; }
        public string HttpMethod     { get; set; }
        public string Description    { get; set; }
        public bool   IsActive       { get; set; }
        public DateTime CreatedAt    { get; set; }
    }

    public class ProductClientModel
    {
        public long   ClientId      { get; set; }
        public int    ProductId     { get; set; }
        public string ProductName   { get; set; }  // JOIN for display
        public string ClientName    { get; set; }
        public string ExternalId    { get; set; }
        public string CustomBaseUrl { get; set; }
        public bool   IsActive      { get; set; }
        public DateTime CreatedAt   { get; set; }
        public string CreatedBy     { get; set; }
    }

    public class SchedulerJobInstanceModel
    {
        public long   InstanceId              { get; set; }
        public long   ClientId               { get; set; }
        public int    ProductId              { get; set; }
        public int    JobTypeId              { get; set; }
        // JOIN fields for display
        public string ClientName             { get; set; }
        public string ExternalId             { get; set; }
        public string ProductName            { get; set; }
        public string ProductCode            { get; set; }
        public string JobTypeCode            { get; set; }
        public string JobTypeName            { get; set; }
        public string DefaultApiPath         { get; set; }
        public string BaseUrl                { get; set; }
        public string CustomBaseUrl          { get; set; }
        public string ApiToken               { get; set; }
        public string TokenType              { get; set; }
        public string TokenHeaderName        { get; set; }
        public string AuthType               { get; set; }
        public string TokenUrl               { get; set; }
        public string OAuthClientId          { get; set; }
        public string ClientSecret           { get; set; }
        public string RsaPrivateKey          { get; set; }
        public string RsaPublicKey           { get; set; }
        public string Audience               { get; set; }
        public string Issuer                 { get; set; }
        // Instance-specific
        public string CustomApiPath          { get; set; }
        public string CustomApiToken         { get; set; }
        public string PayloadJson            { get; set; }
        public string HttpMethod             { get; set; }
        public int    ScheduledHour          { get; set; }
        public int    ScheduledMinute        { get; set; }
        public string TimeZone               { get; set; }
        public bool   IsActive               { get; set; }
        public bool   RunOnHolidays          { get; set; }
        public int    MisfireThresholdMinutes{ get; set; }
        public string LastStatus { get; set; }
        public DateTime? LastRunAt { get; set; }
        public DateTime CreatedAt            { get; set; }
        public DateTime UpdatedAt            { get; set; }
        public string CreatedBy              { get; set; }
    }

    public class ExecutionLogModel
    {
        public long    LogId           { get; set; }
        public long?   InstanceId      { get; set; }
        public long?   ClientId        { get; set; }
        public int?    ProductId       { get; set; }
        public string  ClientName      { get; set; }
        public string  ExternalId      { get; set; }
        public string  JobTypeCode     { get; set; }
        public string  TriggerType     { get; set; }
        public DateTime StartedAt      { get; set; }
        public DateTime? CompletedAt   { get; set; }
        public decimal? DurationSeconds{ get; set; }
        public string  Status          { get; set; }
        public string  SkipReason      { get; set; }
        public string  ApiUrl          { get; set; }
        public string  PayloadSent     { get; set; }
        public int?    HttpStatusCode  { get; set; }
        public string  ResponseBody    { get; set; }
        public string  ErrorMessage    { get; set; }
        public string  HangfireJobId   { get; set; }
    }

    public class ApiClientModel
    {
        public int    ApiClientId       { get; set; }
        public string ClientName        { get; set; }
        public string ApiKey            { get; set; }
        public string AllowedProductIds { get; set; }
        public bool   IsActive          { get; set; }
        public DateTime CreatedAt       { get; set; }
        public string CreatedBy         { get; set; }
        public DateTime? LastUsedAt     { get; set; }
    }

    public class GlobalConfigModel
    {
        public string ConfigKey   { get; set; }
        public string ConfigValue { get; set; }
        public string Description { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string UpdatedBy   { get; set; }
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

    // ═══════════════════════════════════════════════════════════════
    // REQUEST MODELS
    // ═══════════════════════════════════════════════════════════════

    public class SaveProductRequest
    {
        public int    ProductId       { get; set; }
        public string ProductCode     { get; set; }
        public string ProductName     { get; set; }
        public string BaseUrl         { get; set; }
        public string ApiToken        { get; set; }
        public string TokenType       { get; set; }
        public string TokenHeaderName { get; set; }
        public string AuthType        { get; set; }
        public string TokenUrl        { get; set; }
        public string ClientId        { get; set; }
        public string ClientSecret    { get; set; }
        public string RsaPrivateKey   { get; set; }
        public string RsaPublicKey    { get; set; }
        public string Audience        { get; set; }
        public string Issuer          { get; set; }
        public string Description     { get; set; }
        public bool   IsActive        { get; set; }
    }

    public class SaveJobTypeRequest
    {
        public int    JobTypeId      { get; set; }
        public int    ProductId      { get; set; }
        public string JobTypeCode    { get; set; }
        public string JobTypeName    { get; set; }
        public string DefaultApiPath { get; set; }
        public string HttpMethod     { get; set; }
        public string Description    { get; set; }
        public bool   IsActive       { get; set; }
    }

    public class SaveClientRequest
    {
        public long   ClientId      { get; set; }
        public int    ProductId     { get; set; }
        public string ClientName    { get; set; }
        public string ExternalId    { get; set; }
        public string CustomBaseUrl { get; set; }
        public bool   IsActive      { get; set; }
    }

    public class SaveScheduleRequest
    {
        public long   InstanceId              { get; set; }
        public long   ClientId               { get; set; }
        public int    ProductId              { get; set; }
        public int    JobTypeId              { get; set; }
        public string CustomApiPath          { get; set; }
        public string CustomApiToken         { get; set; }
        public string PayloadJson            { get; set; }
        public int    ScheduledHour          { get; set; }
        public int    ScheduledMinute        { get; set; }
        public string TimeZone               { get; set; }
        public bool   IsActive               { get; set; }
        public bool   RunOnHolidays          { get; set; }
        public int    MisfireThresholdMinutes{ get; set; }
    }

    public class SaveApiClientRequest
    {
        public int    ApiClientId       { get; set; }
        public string ClientName        { get; set; }
        public string AllowedProductIds { get; set; }
        public bool   IsActive          { get; set; }
    }

    public class UpdateGlobalConfigRequest
    {
        public string ConfigKey   { get; set; }
        public string ConfigValue { get; set; }
        public string UpdatedBy   { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════
    // EXTERNAL API REQUEST MODELS
    // ═══════════════════════════════════════════════════════════════

    public class ExternalScheduleRequest
    {
        public string ProductCode             { get; set; }
        public string ProductName             { get; set; }
        public string JobTypeCode             { get; set; }
        public string JobTypeName             { get; set; }
        public string DefaultApiPath          { get; set; }
        public string HttpMethod              { get; set; }
        public string ExternalId              { get; set; }
        public string ClientName              { get; set; }
        public int    ScheduledHour           { get; set; }
        public int    ScheduledMinute         { get; set; }
        public string CustomApiPath         { get; set; }
        public string CustomApiToken        { get; set; }
        public string PayloadJson             { get; set; }
        public bool   IsActive                { get; set; }
        public bool   RunOnHolidays           { get; set; }
        public string TimeZone                { get; set; }
    }

    public class ExternalTriggerRequest
    {
        public string ProductCode { get; set; }
        public string JobTypeCode { get; set; }
        public string ExternalId  { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════
    // RESPONSE / DTO MODELS
    // ═══════════════════════════════════════════════════════════════

    public class DashboardStats
    {
        public int TotalProducts  { get; set; }
        public int TotalClients   { get; set; }
        public int ActiveSchedules{ get; set; }
        public int SuccessToday   { get; set; }
        public int FailedToday    { get; set; }
        public int SkippedToday   { get; set; }
        public int RunningNow     { get; set; }
    }

    public class DashboardResponse
    {
        public DashboardStats Stats      { get; set; }
        public object         RecentLogs { get; set; }
        public int            Total      { get; set; }
        public int            Page       { get; set; }
        public int            PageSize   { get; set; }
        public int            TotalPages { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════
    // AUTH & USER MANAGEMENT MODELS
    // ═══════════════════════════════════════════════════════════════

    public class AdminUserModel
    {
        public int       UserId       { get; set; }
        public string    Username     { get; set; }
        public string    PasswordHash { get; set; }
        public string    Salt         { get; set; }
        public string    FullName     { get; set; }
        public string    Email        { get; set; }
        public string    Role         { get; set; }
        public bool      IsActive     { get; set; }
        public DateTime  CreatedAt    { get; set; }
        public DateTime? LastLoginAt  { get; set; }
    }

    public class LoginRequest
    {
        public string Username { get; set; }
        public string Password { get; set; }
    }

    public class LoginResponse
    {
        public string Token    { get; set; }
        public int    UserId   { get; set; }
        public string Username { get; set; }
        public string FullName { get; set; }
        public string Role     { get; set; }
    }

    public class SaveAdminUserRequest
    {
        public int    UserId   { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string FullName { get; set; }
        public string Email    { get; set; }
        public string Role     { get; set; }
        public bool   IsActive { get; set; }
    }

    public class ResetPasswordRequest
    {
        public int    UserId      { get; set; }
        public string NewPassword { get; set; }
    }
}
