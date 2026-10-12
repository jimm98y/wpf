// DMTF dates and intervals, the text form WMI uses for every datetime property:
//   yyyymmddHHMMSS.mmmmmmsUUU      (s = + or -, UUU = offset from UTC in minutes)
//   ddddddddHHMMSS.mmmmmm:000      (an interval)

using System.Globalization;

namespace System.Management
{
    public sealed partial class ManagementDateTimeConverter
    {
        internal ManagementDateTimeConverter() { }

        public static DateTime ToDateTime(string dmtfDate)
        {
            ArgumentNullException.ThrowIfNull(dmtfDate);
            if (dmtfDate.Length != 25 || dmtfDate[14] != '.' || (dmtfDate[21] != '+' && dmtfDate[21] != '-'))
                throw new ArgumentOutOfRangeException(nameof(dmtfDate));
            try
            {
                int year = Field(dmtfDate, 0, 4, 1), month = Field(dmtfDate, 4, 2, 1), day = Field(dmtfDate, 6, 2, 1);
                int hour = Field(dmtfDate, 8, 2, 0), minute = Field(dmtfDate, 10, 2, 0), second = Field(dmtfDate, 12, 2, 0);
                long micro = dmtfDate.Substring(15, 6) == "******" ? 0 : long.Parse(dmtfDate.Substring(15, 6), CultureInfo.InvariantCulture);
                int offset = dmtfDate.Substring(22, 3) == "***" ? 0 : int.Parse(dmtfDate.Substring(22, 3), CultureInfo.InvariantCulture);
                if (dmtfDate[21] == '-') offset = -offset;

                var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified).AddTicks(micro * 10);
                // The value is in the given offset; .NET returns it as local time.
                DateTime utc = local.AddMinutes(-offset);
                return DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
            }
            catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
            {
                throw new ArgumentOutOfRangeException(nameof(dmtfDate), e.Message);
            }
        }

        public static string ToDmtfDateTime(DateTime date)
        {
            TimeSpan offset = date.Kind == DateTimeKind.Utc ? TimeSpan.Zero : TimeZoneInfo.Local.GetUtcOffset(date);
            int minutes = (int)Math.Abs(offset.TotalMinutes);
            long micro = (date.Ticks % TimeSpan.TicksPerSecond) / 10;
            return date.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "." + micro.ToString("D6", CultureInfo.InvariantCulture)
                + (offset < TimeSpan.Zero ? "-" : "+") + minutes.ToString("D3", CultureInfo.InvariantCulture);
        }

        public static string ToDmtfTimeInterval(TimeSpan timespan)
        {
            if (timespan.Ticks < 0 || timespan.Days > 99999999) throw new ArgumentOutOfRangeException(nameof(timespan));
            long micro = (timespan.Ticks % TimeSpan.TicksPerSecond) / 10;
            return timespan.Days.ToString("D8", CultureInfo.InvariantCulture) + timespan.Hours.ToString("D2", CultureInfo.InvariantCulture)
                + timespan.Minutes.ToString("D2", CultureInfo.InvariantCulture) + timespan.Seconds.ToString("D2", CultureInfo.InvariantCulture)
                + "." + micro.ToString("D6", CultureInfo.InvariantCulture) + ":000";
        }

        public static TimeSpan ToTimeSpan(string dmtfTimespan)
        {
            ArgumentNullException.ThrowIfNull(dmtfTimespan);
            if (dmtfTimespan.Length != 25 || dmtfTimespan[14] != '.' || dmtfTimespan.Substring(21) != ":000")
                throw new ArgumentOutOfRangeException(nameof(dmtfTimespan));
            try
            {
                int days = int.Parse(dmtfTimespan.Substring(0, 8), CultureInfo.InvariantCulture);
                int hours = int.Parse(dmtfTimespan.Substring(8, 2), CultureInfo.InvariantCulture);
                int minutes = int.Parse(dmtfTimespan.Substring(10, 2), CultureInfo.InvariantCulture);
                int seconds = int.Parse(dmtfTimespan.Substring(12, 2), CultureInfo.InvariantCulture);
                long micro = long.Parse(dmtfTimespan.Substring(15, 6), CultureInfo.InvariantCulture);
                return new TimeSpan(days, hours, minutes, seconds).Add(TimeSpan.FromTicks(micro * 10));
            }
            catch (Exception e) when (e is FormatException or OverflowException)
            {
                throw new ArgumentOutOfRangeException(nameof(dmtfTimespan), e.Message);
            }
        }

        private static int Field(string s, int start, int length, int whenStars)
        {
            string f = s.Substring(start, length);
            return f.Trim('*').Length == 0 ? whenStars : int.Parse(f, CultureInfo.InvariantCulture);
        }
    }
}
