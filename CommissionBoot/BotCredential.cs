using MySqlConnector;

namespace CommissionBoot
{
    /// <summary>
    ///  RTPOS login kept in dailyCommission.bot_credential (see bot_credential.sql).
    ///  No active row = the bot does not run.
    /// </summary>
    public sealed record BotCredential(string Username, string Password)
    {
        // Latest active RTPOS login, or null when none is active.
        public static BotCredential? LoadActive(string connectionString)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();
            using var cmd = new MySqlCommand(
                "SELECT username, password FROM bot_credential WHERE is_active = 1 ORDER BY id DESC LIMIT 1", conn);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }
            return new BotCredential(reader.GetString(0), reader.GetString(1));
        }
    }
}
