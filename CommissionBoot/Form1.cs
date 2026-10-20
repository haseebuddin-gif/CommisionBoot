namespace CommissionBoot
{
    public partial class Form1 : Form
    {
        // TESTING: true = combined SDR / ADR files are saved to TestOutputRoot and the database is not touched.
        // Set to false to insert into dailyCommission again.
        private static readonly bool TestingSaveToFile = false;
        private const string TestOutputRoot = @"\\192.168.1.3\Bot_Data_IT\Commission";

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
                    if (!TestingSaveToFile) _loader.EnsureTablesExist();
                    _automation.Login(_settings.Rtpos.Username, _settings.Rtpos.Password);

                    // Day 1 of the month up to today, one file per day.
                    var to = DateTime.Today;
                    var from = new DateTime(to.Year, to.Month, 1);
                    var sdrFiles = _automation.DownloadSalesDetailedRawByDay(from, to);
                    var adrFiles = _automation.DownloadActivationDetailByDay(from, to);

                    // Insert only after every day downloaded fine.
                    loaded.Add(LoadToDb(sdrFiles, "SDR", from, to));
                    loaded.Add(LoadToDb(adrFiles, "ADR", from, to));
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

        // Loads the daily csv files into dailyCommission as one batch, then removes the local copies.
        // If the load fails the files stay in the temp download folder for checking.
        private string LoadToDb(List<string> downloaded, string source, DateTime from, DateTime to)
        {
            if (TestingSaveToFile)
            {
                // One folder per day: \\192.168.1.3\Bot_Data_IT\Commission\yyyy-MM-dd
                var targetDir = Path.Combine(TestOutputRoot, DateTime.Today.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(targetDir);
                var target = Path.Combine(targetDir, $"{source}_combined_{from:yyyy-MM-dd}_to_{to:yyyy-MM-dd}_{DateTime.Now:HHmmss}.csv");
                _loader.SaveCombinedCsv(downloaded, target);
                foreach (var file in downloaded) File.Delete(file);
                return $"{source}: {downloaded.Count} daily files combined - {target} (TEST, not inserted)";
            }

            var batchName = $"{source} daily {from:yyyy-MM-dd} to {to:yyyy-MM-dd} ({downloaded.Count} files)";
            var result = _loader.Load(downloaded, source, batchName);
            foreach (var file in downloaded) File.Delete(file);
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
