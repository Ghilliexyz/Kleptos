using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Kleptos
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly string ytDlpPath = "yt-dlp.exe";
        private readonly string gitHubReleaseUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest";

        private static bool hasUpdate = false;

        public MainWindow()
        {
            // Installer Build
            VelopackApp.Build().Run();

            InitializeComponent();

            // Set the default download location to the user's "Downloads" folder
            SetDefaultOutputLocation();
            // Check for Updates
            CheckForYTDLPUpdate();
            // Manage Update Buttons
            ManageUpdateButtons();
        }

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            txtOutput.Text = string.Empty;

            // Validate URL
            if (string.IsNullOrEmpty(txtURL.Text) || txtURL.Text.Length < 8)
            {
                txtOutput.Text = "No link Found";
                return;
            }

            if (!(txtURL.Text.StartsWith("https://") || txtURL.Text.StartsWith("http://")))
            {
                txtOutput.Text = "Invalid Link";
                return;
            }

            SetDefaultOutputLocation();

            // Ensure output path ends with a backslash
            if (!txtFileOutput.Text.EndsWith("\\"))
            {
                txtFileOutput.Text += "\\";
            }

            // Validate folder existence
            if (!Directory.Exists(txtFileOutput.Text))
            {
                MessageBoxResult result = MessageBox.Show("The folder " + txtFileOutput.Text + " does not exist. Would you like to continue? \nThis will create a new folder.", "Folder Not Found!", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.No) return;
            }

            // Get selected format
            string fileExtension = "ext"; // Default (yt-dlp decides)
            ComboBoxItem typeItem = (ComboBoxItem)cmbFileFormats.SelectedItem;
            string cmbFileExtension = typeItem.Content.ToString()!;

            if (cmbFileExtension != "default")
            {
                fileExtension = cmbFileExtension.ToLower(); // Normalize to lowercase
            }

            // Construct command string
            string command = $"yt-dlp {GetFormat(fileExtension)} -o {GetVideoName(fileExtension)} --no-mtime \"{txtURL.Text}\"";

            // Run the command
            await RunCMD(command);
        }

        private async Task RunCMD(string cmd)
        {
            using (Process process = new Process())
            {
                process.StartInfo.FileName = "cmd.exe";
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardInput = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.UseShellExecute = false;

                ConcurrentQueue<string> outputQueue = new ConcurrentQueue<string>();

                process.OutputDataReceived += (sender, args) =>
                {
                    if (!string.IsNullOrEmpty(args.Data))
                    {
                        outputQueue.Enqueue(args.Data);
                        Dispatcher.Invoke(() =>
                        {
                            txtOutput.Text += args.Data + "\n";
                            scrollViewer.ScrollToEnd();
                        });
                    }
                };

                process.ErrorDataReceived += (sender, args) =>
                {
                    if (!string.IsNullOrEmpty(args.Data))
                    {
                        outputQueue.Enqueue(args.Data);
                        Dispatcher.Invoke(() =>
                        {
                            txtOutput.Text += args.Data + "\n";
                            scrollViewer.ScrollToEnd();
                        });
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (process.StandardInput.BaseStream.CanWrite)
                {
                    process.StandardInput.WriteLine(cmd);
                    process.StandardInput.WriteLine("exit");
                }

                await process.WaitForExitAsync();

                // Analyze Output to Determine Download Status
                bool isAlreadyDownloaded = outputQueue.Any(line =>
                    line.Contains("has already been downloaded") ||
                    line.Contains("[download]") && line.Contains("has already been downloaded"));

                bool isSuccess = outputQueue.Any(line =>
                    line.Contains("100%") || line.Contains("has been downloaded"));

                bool isFailure = outputQueue.Any(line =>
                    line.Contains("ERROR:") ||
                    line.Contains("unable to download") ||
                    line.Contains("not found"));

                // Display the result
                Dispatcher.Invoke(() =>
                {
                    if (isAlreadyDownloaded)
                    {
                        txtOutput.Text += "\n✅ File was already downloaded!\n";
                    }
                    else if (isSuccess)
                    {
                        txtOutput.Text += "\n🎉 Download successful!\n";
                    }
                    else if (isFailure)
                    {
                        txtOutput.Text += "\n❌ Download failed!\n";
                    }

                    scrollViewer.ScrollToEnd();
                });

                // Check for authentication-related messages AFTER process exits
                bool authRequired = outputQueue.Any(line =>
                    line.Contains("Please log in") ||
                    line.Contains("This video is private") ||
                    line.Contains("unable to download webpage") ||
                    line.Contains("Sign in to confirm you’re not a bot") ||
                    line.Contains("Use --cookies-from-browser or --cookies"));

                if (authRequired)
                {
                    await Dispatcher.Invoke(async () =>
                    {
                        MessageBoxResult result = MessageBox.Show("This video requires authentication. Extract cookies?", "Authentication Required", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                        if (result == MessageBoxResult.Yes)
                        {
                            await ExtractCookiesAndRetry(cmd);
                        }
                    });
                }
            }
        }

        private string GetFormat(string fileExtension)
        {
            string[] extractableAudioFormats = { "mp3", "aac", "m4a", "wav" };
            string[] videoFormats = { "mp4", "mkv", "flv", "3gp" };

            if (extractableAudioFormats.Contains(fileExtension))
            {
                return $"--extract-audio --audio-format {fileExtension} -f bestaudio";
            }
            else if (fileExtension == "webm")
            {
                return "-f bestaudio"; // WebM audio-only download
            }
            else if (videoFormats.Contains(fileExtension))
            {
                if (fileExtension == "mp4")
                {
                    return "-S vcodec:h264,res,acodec:aac";
                }
                else if (fileExtension == "flv")
                {
                    return "-f bestvideo+bestaudio --recode-video flv";
                }
                else
                {
                    return $"-f \"bv*[ext={fileExtension}]+ba[ext=m4a]/b[ext={fileExtension}]\"";
                }
            }
            else if (fileExtension == "ext")
            {
                return "-f bestvideo+bestaudio";
            }
            else
            {
                txtOutput.Text = "Unsupported format selected!";
                return string.Empty;
            }
        }

        private string GetVideoName(string fileExtension)
        {
            if (fileExtension == "ext")
            {
                fileExtension = "%(ext)s";
            }

            if (txtFileName.Text == string.Empty)
            {
                return $"\"{txtFileOutput.Text}\\%(title)s.{fileExtension}\"";
            }
            else
            {
                return $"\"{txtFileOutput.Text}\\{txtFileName.Text}.{fileExtension}\"";
            }
        }

        private async Task ExtractCookiesAndRetry(string originalCmd)
        {
            string browserName = GetDefaultBrowser();
            if (string.IsNullOrEmpty(browserName))
            {
                MessageBox.Show("Could not detect the default browser.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string cookiesFile = "cookies.txt";
            string extractCookiesCmd = $"yt-dlp --cookies-from-browser {browserName} -o {cookiesFile}";

            txtOutput.Text = "Extracting cookies...";
            await RunCMD(extractCookiesCmd);

            if (File.Exists(cookiesFile))
            {
                MessageBox.Show("Cookies extracted successfully! Retrying download...", "Success", MessageBoxButton.OK, MessageBoxImage.Information);

                // Modify the original command to use cookies
                string modifiedCmd = originalCmd + $" --cookies {cookiesFile}";
                await RunCMD(modifiedCmd);
            }
            else
            {
                MessageBox.Show("Failed to extract cookies.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void FileOutput_Click(object sender, RoutedEventArgs e)
        {
            OpenFolderDialog openFolderDialog = new OpenFolderDialog();

            openFolderDialog.ShowDialog();

            if (openFolderDialog.FolderName != string.Empty)
            {
                txtFileOutput.Text = openFolderDialog.FolderName;
            }
            else
            {
                SetDefaultOutputLocation();
            }

        }

        private void SetDefaultOutputLocation()
        {
            // Set the default download location to the user's "Downloads" folder
            if (string.IsNullOrEmpty(txtFileOutput.Text))
            {
                // Get the default Downloads folder path
                string downloadsPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads";
                txtFileOutput.Text = downloadsPath;
            }
        }

        //private async void GetCookies_Click(object sender, RoutedEventArgs e)
        //{
        //    txtOutput.Text = string.Empty;

        //    string browserName = GetDefaultBrowser();
        //    if (string.IsNullOrEmpty(browserName))
        //    {
        //        MessageBox.Show("Could not detect the default browser.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        //        return;
        //    }

        //    string cookiesFile = "cookies.txt"; // File to store extracted cookies
        //    string command = $"yt-dlp --cookies-from-browser {browserName} -o {cookiesFile} {txtURL.Text}";

        //    await RunCMD(command);

        //    if (File.Exists(cookiesFile))
        //    {
        //        string cookies = await File.ReadAllTextAsync(cookiesFile);
        //        MessageBox.Show("Cookies retrieved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        //        Console.WriteLine(cookies); // Use the cookies as needed
        //    }
        //    else
        //    {
        //        MessageBox.Show("Failed to retrieve cookies.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        //    }
        //}

        /// <summary>
        /// Detects the system's default browser name for yt-dlp.
        /// </summary>
        private string GetDefaultBrowser()
        {
            try
            {
                string registryKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice";
                string browserProgId = Registry.GetValue(registryKey, "ProgId", null) as string;

                if (string.IsNullOrEmpty(browserProgId))
                    return null;

                // Convert known ProgIds to yt-dlp's expected browser names
                // Chromium Browsers
                if (browserProgId.Contains("Chrome")) return "chrome";
                if (browserProgId.Contains("Chromium")) return "chromium";
                if (browserProgId.Contains("Opera")) return "opera";
                if (browserProgId.Contains("Edge")) return "edge";
                if (browserProgId.Contains("Vivaldi")) return "vivaldi";
                if (browserProgId.Contains("Brave")) return "brave";
                if (browserProgId.Contains("Whale")) return "whale";
                // Non-Chromium Browsers
                if (browserProgId.Contains("Firefox")) return "firefox";
                if (browserProgId.Contains("Safari")) return "safari";

                return null;
            }
            catch
            {
                return null;
            }
        }

        private async void CheckForYTDLPUpdate()
        {
            txtOutput.Text = string.Empty;
            txtOutput.Text = "Status: Checking for updates...";

            try
            {
                // Step 1: Get current yt-dlp version
                string currentVersion = GetCurrentYtDlpVersion();
                if (string.IsNullOrEmpty(currentVersion))
                {
                    txtOutput.Text = "Status: yt-dlp not found or failed to get version.";
                    return;
                }

                // Step 2: Get latest version from GitHub
                string latestVersion = await GetLatestYtDlpVersion();
                if (string.IsNullOrEmpty(latestVersion))
                {
                    txtOutput.Text = "Status: Failed to fetch latest version.";
                    return;
                }

                // Step 3: Compare versions
                if (currentVersion != latestVersion)
                {
                    txtOutput.Text = $"Status: Update found! Downloading {latestVersion}...";
                    await DownloadYtDlpUpdate(latestVersion);
                    txtOutput.Text = $"Status: Updated to {latestVersion} Successfully!";
                }
                else
                {
                    txtOutput.Text = $"Status: yt-dlp is up-to-date (Version: {currentVersion}).";
                }
            }
            catch (Exception ex)
            {
                txtOutput.Text = $"Status: Error - {ex.Message}";
            }
        }

        private string GetCurrentYtDlpVersion()
        {
            if (!File.Exists(ytDlpPath))
                return null;

            ProcessStartInfo processInfo = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (Process process = new Process { StartInfo = processInfo })
            {
                process.Start();
                string version = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                return process.ExitCode == 0 ? version : null;
            }
        }

        private async Task<string> GetLatestYtDlpVersion()
        {
            using (HttpClient client = new HttpClient())
            {
                // GitHub redirects /latest to the latest release URL
                HttpResponseMessage response = await client.GetAsync(gitHubReleaseUrl, HttpCompletionOption.ResponseHeadersRead);
                string redirectUrl = response.RequestMessage.RequestUri.ToString();
                // Extract version from URL (e.g., "2023.07.06" from "/yt-dlp/yt-dlp/releases/tag/2023.07.06")
                string version = Regex.Match(redirectUrl, @"tag/(.+)$").Groups[1].Value;
                return version;
            }
        }

        private async Task DownloadYtDlpUpdate(string version)
        {
            string downloadUrl = $"https://github.com/yt-dlp/yt-dlp/releases/download/{version}/yt-dlp.exe";
            string tempPath = Path.Combine(Path.GetTempPath(), "yt-dlp.exe");

            using (HttpClient client = new HttpClient())
            {
                byte[] fileBytes = await client.GetByteArrayAsync(downloadUrl);
                await File.WriteAllBytesAsync(tempPath, fileBytes);
            }

            // Replace the existing yt-dlp.exe (ensure it's not in use)
            if (File.Exists(ytDlpPath))
                File.Delete(ytDlpPath);
            File.Move(tempPath, ytDlpPath);
        }

        private async Task CheckForKleptosUpdate()
        {
            UpdateManager manager = new UpdateManager("https://github.com/Ghilliexyz/Kleptos/releases/latest");

            var newVersion = await manager.CheckForUpdatesAsync();
            if (newVersion == null) return;

            // show update button
            // make this depend if there is a new version or not
            hasUpdate = true;

            ManageUpdateButtons();
        }

        private static async Task UpdateKleptos()
        {
            UpdateManager manager = new UpdateManager("https://github.com/Ghilliexyz/Kleptos/releases/latest");

            ILogger logger = new FileLogger("log.txt");
            WindowsVelopackLocator locator = new WindowsVelopackLocator(logger);

            if (locator.IsPortable)
            {
                // do something
                MessageBoxResult result = MessageBox.Show("There is a new Kleptos Update Available\nPlease go to \nhttps://github.com/Ghilliexyz/Kleptos/releases/latest \nand download the portable version again.\n\n--------------**NOTE**--------------\nYou are seeing this message because you use the portable version.\nPlease consider installing the app instead.", "Kleptos Update Available",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var newVersion = await manager.CheckForUpdatesAsync();
            if (newVersion == null) return;

            // download new version
            await manager.DownloadUpdatesAsync(newVersion);

            // install new version and restart app
            manager.ApplyUpdatesAndRestart(newVersion);
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        private void CopyLogs_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(txtOutput.Text);
        }

        private async void Update_Click(object sender, RoutedEventArgs e)
        {
            await UpdateKleptos();
        }

        private async void CheckForUpdate_Click(object sender, RoutedEventArgs e)
        {
            await CheckForKleptosUpdate();
        }

        private void ManageUpdateButtons()
        {
            if (hasUpdate)
            {
                UpdateButton.Visibility = Visibility.Visible;
                CheckForUpdateButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                UpdateButton.Visibility = Visibility.Collapsed;
                CheckForUpdateButton.Visibility = Visibility.Visible;
            }
        }
    }
    class FileLogger : ILogger
    {
        private string filePath;

        public FileLogger(string filePath)
        {
            this.filePath = filePath;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            var message = formatter(state, exception);
            System.IO.File.AppendAllText(filePath, message + Environment.NewLine);
        }
    }
}