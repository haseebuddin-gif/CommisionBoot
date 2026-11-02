namespace CommissionBoot
{
    public partial class Form1 : Form
    {
        // TESTING: true = combined SDR / ADR files are only saved to TestOutputRoot and the database is not touched.
        // Set to false to insert into dailyCommission again.
        private static readonly bool TestingSaveToFile = false;
        // Combined SDR / ADR files are always copied here too, one folder per day.
        private const string TestOutputRoot = @"\\192.168.1.3\Bot_Data_IT\Commission";

        private readonly AppSettings _settings;
        private RtposAutomation? _automation;
        private readonly RawFileLoader _loader;

        public Form1()
        {
            InitializeComponent();
            _settings = AppSettings.Load();
            _loader = new RawFileLoader(_settings.DailyCommissionConnectionString, Log);
        }

        // Runs automatically as soon as the window appears.
        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // RTPOS login: dailyCommission.bot_credential, email recipients: dailyCommission.email_setting.
            BotCredential? rtpos;
            try
            {
                rtpos = await Task.Run(() => BotCredential.LoadActive(_settings.DailyCommissionConnectionString));
            }
            catch (Exception ex)
            {
                Log("ERROR reading bot_credential: " + ex.Message);
                Close();
                return;
            }

            if (rtpos == null)
            {
                Log("No active row in bot_credential - bot not run.");
                Close();
                return;
            }
            var email = new EmailNotifier(_settings, Log);

            var loaded = new List<string>();
            try
            {
                _automation = new RtposAutomation(Log);
                await Task.Run(() =>
                {
                    if (!TestingSaveToFile) _loader.EnsureTablesExist();
                    _automation.Login(rtpos.Username, rtpos.Password);

                    // Day 1 of the month up to today, one file per day.
                    var to = DateTime.Today;
                    var from = new DateTime(to.Year, to.Month, 1);
                    var sdrFiles = _automation.DownloadSalesDetailedRawByDay(from, to);
                    var adrFiles = _automation.DownloadActivationDetailByDay(from, to);

                    // Insert only after every day downloaded fine.
                    loaded.Add(LoadToDb(sdrFiles, "SDR", from, to));
                    loaded.Add(LoadToDb(adrFiles, "ADR", from, to));
                });
                // Raw data is in - build today's daily_commission_summary with the current filters.
                if (!TestingSaveToFile) loaded.Add(await RefreshCommissionSummaryAsync());
                Log("Done.");
                // One email for the whole run, only after both reports are loaded.
                await Task.Run(() => email.SendSuccess(loaded));
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
                await Task.Run(() => email.SendError(ex, loaded));
            }

            // Bot run is finished (success or error) - close the app; OnFormClosing quits Chrome.
            Close();
        }

        // Loads the daily csv files into dailyCommission as one batch, copies the combined file to the drive,
        // then removes the local copies. If the load fails the files stay in the temp download folder for checking.
        private string LoadToDb(List<string> downloaded, string source, DateTime from, DateTime to)
        {
            if (TestingSaveToFile)
            {
                var testTarget = SaveToDrive(downloaded, source, from, to);
                foreach (var file in downloaded) File.Delete(file);
                return $"{source}: {downloaded.Count} daily files combined - {testTarget} (TEST, not inserted)";
            }

            var batchName = $"{source} daily {from:yyyy-MM-dd} to {to:yyyy-MM-dd} ({downloaded.Count} files)";
            var result = _loader.Load(downloaded, source, batchName);

            // A drive problem must not stop the commission summary, so it is only reported.
            string driveNote;
            try
            {
                driveNote = "drive: " + SaveToDrive(downloaded, source, from, to);
            }
            catch (Exception ex)
            {
                Log($"Drive copy failed ({source}): {ex.Message}");
                driveNote = "drive copy FAILED: " + ex.Message;
            }

            foreach (var file in downloaded) File.Delete(file);
            return $"{result} - {driveNote}";
        }

        // One folder per day: \\192.168.1.3\Bot_Data_IT\Commission\yyyy-MM-dd
        private string SaveToDrive(List<string> downloaded, string source, DateTime from, DateTime to)
        {
            var targetDir = Path.Combine(TestOutputRoot, DateTime.Today.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(targetDir);
            var target = Path.Combine(targetDir, $"{source}_combined_{from:yyyy-MM-dd}_to_{to:yyyy-MM-dd}_{DateTime.Now:HHmmss}.csv");
            _loader.SaveCombinedCsv(downloaded, target);
            Log($"Saved to drive: {target}");
            return target;
        }

        // Calls the TechnoComm API: v_final_summary -> daily_commission_summary for today.
        private async Task<string> RefreshCommissionSummaryAsync()
        {
            if (string.IsNullOrWhiteSpace(_settings.CommissionApiUrl))
            {
                throw new InvalidOperationException("CommissionApiUrl is empty in appsettings.json - commission summary not refreshed.");
            }
            Log("Building daily commission summary...");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            var url = $"{_settings.CommissionApiUrl.TrimEnd('/')}/DailyCommission/RefreshDailyCommissionSummary";
            var response = await client.PostAsync(url, null);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Commission summary failed ({(int)response.StatusCode}): {body}");
            }
            Log("Daily commission summary saved.");
            return $"Commission summary: saved for {DateTime.Today:yyyy-MM-dd}";
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
