using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;

namespace CommissionBoot
{
    /// <summary>
    ///  Drives the RTBDI portal (myrtpos.com/newbdi) in Chrome.
    /// </summary>
    public sealed class RtposAutomation : IDisposable
    {
        public const string LoginUrl = "https://www.myrtpos.com/newbdi/index.fwx";

        private readonly Action<string> _log;
        private readonly string _downloadDir;
        private IWebDriver? _driver;

        public RtposAutomation(Action<string> log)
        {
            _log = log;
            // Chrome downloads here first; finished files are then copied to the network share.
            _downloadDir = Path.Combine(Path.GetTempPath(), "CommissionBoot", "downloads");
        }

        public IWebDriver Driver => _driver ?? throw new InvalidOperationException("Browser not started.");

        public void Login(string username, string password)
        {
            if (_driver == null)
            {
                _log("Starting Chrome...");
                Directory.CreateDirectory(_downloadDir);
                var options = new ChromeOptions();
                options.AddArgument("--start-maximized");
                options.AddUserProfilePreference("download.default_directory", _downloadDir);
                options.AddUserProfilePreference("download.prompt_for_download", false);
                options.AddUserProfilePreference("safebrowsing.enabled", true);
                // Selenium Manager downloads the matching chromedriver automatically.
                // Longer command timeout (default 60s) - the portal can be slow while building reports.
                _driver = new ChromeDriver(ChromeDriverService.CreateDefaultService(), options, TimeSpan.FromMinutes(5));
            }

            var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(30));

            _log($"Opening {LoginUrl}");
            _driver.Navigate().GoToUrl(LoginUrl);

            var userBox = wait.Until(d => d.FindElement(By.Id("secUserID")));
            userBox.Clear();
            userBox.SendKeys(username);

            var passBox = _driver.FindElement(By.Id("secPassword"));
            passBox.Clear();
            passBox.SendKeys(password);

            _log("Submitting login...");
            _driver.FindElement(By.CssSelector("input[type='submit'][value='Login']")).Click();

            // Login succeeded once the username box is gone from the page.
            try
            {
                wait.Until(d => d.FindElements(By.Id("secUserID")).Count == 0);
            }
            catch (WebDriverTimeoutException)
            {
                throw new InvalidOperationException("Login failed - still on the login page. Check username/password.");
            }

            _log($"Logged in. Current page: {_driver.Url}");
        }

        /// <summary>
        ///  Sales Reports > Sales Detailed (Raw), Exclude Bill Payments, Raw Excel -
        ///  one Custom Range download per day from <paramref name="from"/> to <paramref name="to"/>.
        ///  Returns the downloaded files (local temp folder), oldest day first.
        /// </summary>
        public List<string> DownloadSalesDetailedRawByDay(DateTime from, DateTime to)
        {
            OpenMenuReport("SalesReportDetail.fwx", "Sales Reports > Sales Detailed (Raw)");

            var files = new List<string>();
            for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
            {
                // Checked every day in case the page reloaded after the previous download.
                var excludeBox = new WebDriverWait(Driver, TimeSpan.FromSeconds(30))
                    .Until(d => d.FindElement(By.Name("chkNoPayment")));
                if (!excludeBox.Selected)
                {
                    ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].click();", excludeBox);
                }

                SelectCustomDay(day);
                files.Add(DownloadOnClick(
                    By.XPath("//span[@class='button-inner' and normalize-space()='Raw Excel']"), $"Raw Excel ({day:MM/dd/yyyy})"));
            }
            return files;
        }

        /// <summary>
        ///  Activation Reports > Activation Detail (XLS), Export -
        ///  one Custom Range download per day from <paramref name="from"/> to <paramref name="to"/>.
        ///  Returns the downloaded files (local temp folder), oldest day first.
        /// </summary>
        public List<string> DownloadActivationDetailByDay(DateTime from, DateTime to)
        {
            OpenMenuReport("ActivationReport_Detail.fwx", "Activation Reports > Activation Detail (XLS)");

            var files = new List<string>();
            for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
            {
                SelectCustomDay(day);
                files.Add(DownloadOnClick(
                    By.CssSelector("input[type='submit'][name='btnExcel'][value='Export']"), $"Export ({day:MM/dd/yyyy})"));
            }
            return files;
        }

        private void OpenMenuReport(string href, string name)
        {
            var wait = new WebDriverWait(Driver, TimeSpan.FromSeconds(30));
            _log($"Opening {name}...");
            // The link sits in a hover menu, so click it via JS instead of hovering.
            var link = wait.Until(d => d.FindElement(By.CssSelector($"a[href='{href}']")));
            ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].click();", link);
            wait.Until(d => d.Url.Contains(href, StringComparison.OrdinalIgnoreCase)
                         && d.FindElements(By.Id("reportrange")).Count > 0);
        }

        // Report Range > Custom Range, start = end = day, Apply.
        private void SelectCustomDay(DateTime day)
        {
            var driver = Driver;
            var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(30));
            var value = day.ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture);

            _log($"Selecting Report Range > Custom Range {value}...");
            wait.Until(d => d.FindElement(By.Id("reportrange"))).Click();
            var custom = wait.Until(d => d.FindElements(By.CssSelector(".daterangepicker li[data-range-key='Custom Range']"))
                .FirstOrDefault(e => e.Displayed));
            custom!.Click();

            // Typing into the picker's start/end boxes fires its 'change' handler, same as a user would.
            ((IJavaScriptExecutor)driver).ExecuteScript("""
                var picker = $('#reportrange').data('daterangepicker');
                picker.container.find('input[name="daterangepicker_start"]').val(arguments[0]).trigger('change');
                picker.container.find('input[name="daterangepicker_end"]').val(arguments[0]).trigger('change');
                """, value);

            var apply = wait.Until(d => d.FindElements(By.CssSelector(".daterangepicker .applyBtn"))
                .FirstOrDefault(e => e.Displayed && e.Enabled));
            apply!.Click();

            wait.Until(d => d.FindElement(By.Id("frmStart")).GetAttribute("value") == value
                         && d.FindElement(By.Id("frmEnd")).GetAttribute("value") == value);
            _log($"Range: {value} - {value}");
        }

        private string DownloadOnClick(By button, string name)
        {
            var existing = Directory.GetFiles(_downloadDir).ToHashSet(StringComparer.OrdinalIgnoreCase);

            _log($"Clicking {name}...");
            var element = Driver.FindElement(button);
            ((IJavaScriptExecutor)Driver).ExecuteScript("var el = arguments[0]; setTimeout(function () { el.click(); }, 0);", element);

            var file = WaitForDownload(existing, TimeSpan.FromMinutes(10));
            _log($"Downloaded: {Path.GetFileName(file)}");
            return file;
        }
        private string WaitForDownload(HashSet<string> existing, TimeSpan timeout)
        {
            var deadline = DateTime.Now + timeout;
            while (DateTime.Now < deadline)
            {
                var files = Directory.GetFiles(_downloadDir).Where(f => !existing.Contains(f)).ToList();
                var inProgress = files.Any(f => f.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase)
                                             || f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
                if (!inProgress && files.Count > 0)
                {
                    return files.OrderByDescending(File.GetLastWriteTime).First();
                }
                Thread.Sleep(1000);
            }
            throw new TimeoutException("Raw Excel download did not finish in time.");
        }

        public void Dispose()
        {
            _driver?.Quit();
            _driver = null;
        }
    }
}
