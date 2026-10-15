using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MySqlConnector;

namespace CommissionBoot
{
    /// <summary>
    ///  Emails the active recipients in commissionReport.email_recipients.
    ///  Never throws - a failed email is only logged so the bot keeps running.
    /// </summary>
    public sealed class EmailNotifier
    {
        private const string FromName = "Commission Bot";

        private const int To = 1, Cc = 2, Bcc = 3;

        private readonly AppSettings _settings;
        private readonly Action<string> _log;

        public EmailNotifier(AppSettings settings, Action<string> log)
        {
            _settings = settings;
            _log = log;
        }

        public void SendSuccess(IReadOnlyList<string> loaded)
        {
            Send("Commission Bot - Reports loaded",
                $"<p>All reports downloaded and loaded into the database.</p>" +
                ItemList(loaded) +
                $"<p><b>Time:</b> {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>");
        }

        public void SendError(Exception ex, IReadOnlyList<string> loaded)
        {
            Send("Commission Bot - ERROR",
                $"<p>The bot failed.</p>" +
                $"<p><b>Error:</b> {Html(ex.Message)}<br/><b>Time:</b> {DateTime.Now:yyyy-MM-dd HH:mm:ss}<br/><b>Machine:</b> {Html(Environment.MachineName)}</p>" +
                (loaded.Count > 0 ? "<p>Loaded before the error:</p>" + ItemList(loaded) : "<p>No report was loaded.</p>") +
                $"<pre>{Html(ex.ToString())}</pre>");
        }

        private static string ItemList(IEnumerable<string> items) =>
            "<ul>" + string.Concat(items.Select(i => $"<li>{Html(i)}</li>")) + "</ul>";

        private void Send(string subject, string htmlBody)
        {
            try
            {
                var recipients = GetActiveRecipients();
                if (recipients.Count == 0)
                {
                    _log("Email skipped - no active recipients.");
                    return;
                }

                var email = new MimeMessage();
                var smtpSettings = _settings.Smtp;
                email.From.Add(new MailboxAddress(FromName, smtpSettings.Username));
                email.Subject = subject;
                email.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();
                foreach (var (name, address, type) in recipients)
                {
                    var mailbox = new MailboxAddress(name, address);
                    switch (type)
                    {
                        case Cc: email.Cc.Add(mailbox); break;
                        case Bcc: email.Bcc.Add(mailbox); break;
                        default: email.To.Add(mailbox); break;
                    }
                }

                using var smtp = new SmtpClient();
                smtp.Connect(smtpSettings.Host, smtpSettings.Port, SecureSocketOptions.StartTls);
                smtp.Authenticate(smtpSettings.Username, smtpSettings.Password);
                smtp.Send(email);
                smtp.Disconnect(true);

                _log($"Email sent: {subject} ({recipients.Count} recipient(s))");
            }
            catch (Exception ex)
            {
                _log("Email failed: " + ex.Message);
            }
        }

        private List<(string Name, string Email, int Type)> GetActiveRecipients()
        {
            var result = new List<(string, string, int)>();
            using var conn = new MySqlConnection(_settings.ConnectionString);
            conn.Open();
            using var cmd = new MySqlCommand(
                "SELECT name, email, recipient_type_id FROM email_recipients WHERE is_active = 1", conn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
            }
            return result;
        }

        private static string Html(string text) => System.Net.WebUtility.HtmlEncode(text);
    }
}
