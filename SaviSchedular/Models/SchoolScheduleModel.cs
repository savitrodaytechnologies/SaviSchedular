namespace SaviSchedular.Models
{
    public class SchoolScheduleModel
    {
        public long   SchoolId        { get; set; }
        public string SchoolName      { get; set; }
        public int    ScheduledHour   { get; set; }
        public int    ScheduledMinute { get; set; }
        public bool   IsActive        { get; set; }
    }

    public class SchoolScheduleRequest
    {
        public long   SchoolId   { get; set; }
        public string SchoolName { get; set; }
        public int    Hour       { get; set; }
        public int    Minute     { get; set; }
    }
}
