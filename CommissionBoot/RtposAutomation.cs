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
        ///  Sales Reports > Sales Detailed (Raw), Exclude Bill Payments, Last 7 Days, Raw Excel.
        ///  Returns the path of the downloaded file (local temp folder).
        /// </summary>
        public string DownloadSalesDetailedRaw()
        {
            var wait = new WebDriverWait(Driver, TimeSpan.FromSeconds(30));

            OpenMenuReport("SalesReportDetail.fwx", "Sales Reports > Sales Detailed (Raw)");

            var excludeBox = wait.Until(d => d.FindElement(By.Name("chkNoPayment")));
            if (!excludeBox.Selected)
            {
                ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].click();", excludeBox);
            }
            _log("Exclude Bill Payments: checked");

            SelectLast7Days();

            return DownloadOnClick(
                By.XPath("//span[@class='button-inner' and normalize-space()='Raw Excel']"), "Raw Excel");
        }

        /// <summary>
        ///  Activation Reports > Activation Detail (XLS), Last 7 Days, Export.
        ///  Returns the path of the downloaded file (local temp folder).
        /// </summary>
        public string DownloadActivationDetail()
        {
            OpenMenuReport("ActivationReport_Detail.fwx", "Activation Reports > Activation Detail (XLS)");
            SelectLast7Days();
            return DownloadOnClick(By.CssSelector("input[type='submit'][name='btnExcel'][value='Export']"), "Export");
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

        private void SelectLast7Days()
        {
            var driver = Driver;
            var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(30));

            _log("Selecting Report Range > Last 7 Days...");
            driver.FindElement(By.Id("reportrange")).Click();
            var last7 = wait.Until(d => d.FindElements(By.CssSelector(".daterangepicker li[data-range-key='Last 7 Days']"))
                .FirstOrDefault(e => e.Displayed));
            last7!.Click();

            var expectedStart = DateTime.Today.AddDays(-6).ToString("MM/dd/yyyy");
            wait.Until(d => d.FindElement(By.Id("frmStart")).GetAttribute("value") == expectedStart);
            _log($"Range: {driver.FindElement(By.Id("frmStart")).GetAttribute("value")} - {driver.FindElement(By.Id("frmEnd")).GetAttribute("value")}");
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
