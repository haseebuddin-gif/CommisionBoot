namespace CommissionBoot
{
    public partial class Form1 : Form
    {
        private readonly AppSettings _settings;
        private RtposAutomation? _automation;
        private readonly EmailNotifier _email;
        private readonly RawFileLoader _loader;

        public Form1()
        {
            InitializeComponent();
            _settings = AppSettings.Load();
            _email = new EmailNotifier(_settings, Log);
            _loader = new RawFileLoader(_settings.DailyCommissionConnectionString, Log);
        }

        // Runs automatically as soon as the window appears.
        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            var loaded = new List<string>();
            try
            {
                _automation = new RtposAutomation(Log);
                await Task.Run(() =>
                {
                    _loader.EnsureTablesExist();
                    _automation.Login(_settings.Rtpos.Username, _settings.Rtpos.Password);
                    loaded.Add(LoadToDb(_automation.DownloadSalesDetailedRaw(), "SDR"));
                    loaded.Add(LoadToDb(_automation.DownloadActivationDetail(), "ADR"));
                });
                Log("Done.");
                // One email for the whole run, only after both reports are loaded.
                await Task.Run(() => _email.SendSuccess(loaded));
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
                await Task.Run(() => _email.SendError(ex, loaded));
            }

            // Bot run is finished (success or error) - close the app; OnFormClosing quits Chrome.
            Close();
        }

        // Loads the downloaded csv into dailyCommission, then removes the local copy.
        // If the load fails the file stays in the temp download folder for checking.
        private string LoadToDb(string downloaded, string source)
        {
            var result = _loader.Load(downloaded, source);
            File.Delete(downloaded);
            return result.ToString();
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
