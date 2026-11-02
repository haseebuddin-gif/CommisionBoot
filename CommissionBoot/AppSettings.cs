using System.Text.Json;

namespace CommissionBoot
{
    /// <summary>
    ///  Connection details, read from appsettings.json next to the exe.
    ///  appsettings.json is git-ignored; appsettings.example.json shows the shape.
    ///  RTPOS login is in dailyCommission.bot_credential; email on / off in dailyCommission.email_setting.
    /// </summary>
    public sealed class AppSettings
    {
        public string ConnectionString { get; set; } = "";
        // dailyCommission database - raw ADR / SDR loads.
        public string DailyCommissionConnectionString { get; set; } = "";
        // TechnoComm API - builds daily_commission_summary after the raw load.
        public string CommissionApiUrl { get; set; } = "";
        public SmtpSettings Smtp { get; set; } = new();

        public sealed class SmtpSettings
        {
            public string Host { get; set; } = "";
            public int Port { get; set; }
            public string Username { get; set; } = "";
            public string Password { get; set; } = "";
        }

        public static AppSettings Load()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("appsettings.json not found - copy appsettings.example.json and fill it in.", path);
            }
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))
                   ?? throw new InvalidOperationException("appsettings.json is empty.");
        }
    }
}
