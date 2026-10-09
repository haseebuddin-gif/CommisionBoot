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

        public void SendSuccess(IReadOnlyList<string> savedFiles)
        {
            Send("Commission Bot - Reports downloaded",
                $"<p>All reports downloaded successfully.</p>" +
                FileList(savedFiles) +
                $"<p><b>Time:</b> {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>");
        }

        public void SendError(Exception ex, IReadOnlyList<string> savedFiles)
        {
            Send("Commission Bot - ERROR",
                $"<p>The bot failed.</p>" +
                $"<p><b>Error:</b> {Html(ex.Message)}<br/><b>Time:</b> {DateTime.Now:yyyy-MM-dd HH:mm:ss}<br/><b>Machine:</b> {Html(Environment.MachineName)}</p>" +
                (savedFiles.Count > 0 ? "<p>Saved before the error:</p>" + FileList(savedFiles) : "<p>No report was saved.</p>") +
                $"<pre>{Html(ex.ToString())}</pre>");
        }

        private static string FileList(IEnumerable<string> files) =>
            "<ul>" + string.Concat(files.Select(f => $"<li><b>{Html(Path.GetFileName(f))}</b> - {Html(f)}</li>")) + "</ul>";

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
