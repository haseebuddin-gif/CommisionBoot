namespace CommissionBoot
{
    public partial class Form1 : Form
    {
        private const string OutputRoot = @"\\192.168.1.3\Bot_Data_IT\Commission";

        private readonly AppSettings _settings;
        private RtposAutomation? _automation;
        private readonly EmailNotifier _email;

        public Form1()
        {
            InitializeComponent();
            _settings = AppSettings.Load();
            _email = new EmailNotifier(_settings, Log);
        }

        // Runs automatically as soon as the window appears.
        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            var saved = new List<string>();
            try
            {
                _automation = new RtposAutomation(Log);
                await Task.Run(() =>
                {
                    _automation.Login(_settings.Rtpos.Username, _settings.Rtpos.Password);
                    saved.Add(SaveToShare(_automation.DownloadSalesDetailedRaw()));
                    saved.Add(SaveToShare(_automation.DownloadActivationDetail()));
                });
                Log("Done.");
                // One email for the whole run, only after both reports are saved.
                await Task.Run(() => _email.SendSuccess(saved));
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
                await Task.Run(() => _email.SendError(ex, saved));
            }

            // Bot run is finished (success or error) - close the app; OnFormClosing quits Chrome.
            Close();
        }

        // One folder per day: \\192.168.1.3\Bot_Data_IT\Commission\yyyy-MM-dd
        private string SaveToShare(string downloaded)
        {
            var targetDir = Path.Combine(OutputRoot, DateTime.Today.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(targetDir);
            var target = Path.Combine(targetDir, Path.GetFileName(downloaded));
            File.Copy(downloaded, target, overwrite: true);
            File.Delete(downloaded);
            Log($"Saved to {target}");
            return target;
        }

        private void Log(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(() => Log(message));
                return;
            }
            txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _automation?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
