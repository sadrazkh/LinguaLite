public static class ReportingClock
{
    public static DateOnly Today(IConfiguration configuration)
    {
        var timeZoneId = configuration["REPORT_TIME_ZONE"] ?? "Asia/Tehran";
        try
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)).DateTime);
        }
        catch (TimeZoneNotFoundException) when (timeZoneId.Equals("Asia/Tehran", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Iran Standard Time")).DateTime);
            }
            catch (TimeZoneNotFoundException)
            {
                return DateOnly.FromDateTime(DateTime.UtcNow);
            }
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(DateTime.UtcNow);
        }
        catch (InvalidTimeZoneException)
        {
            return DateOnly.FromDateTime(DateTime.UtcNow);
        }
    }
}
