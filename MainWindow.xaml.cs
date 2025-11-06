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

        private string cookiesTxtFile = String.Empty;

        private static bool hasUpdate = false;

        public MainWindow()
        {
            // Installer Build
            VelopackApp.Build().Run();

            InitializeComponent();

            // Check for a new version for Kleptos
            CheckForKleptosUpdate();

            // Set the default download location to the user's "Downloads" folder
            SetDefaultOutputLocation();
            // Check for Updates
            CheckForYTDLPUpdate();
            // Manage the update buttons visibility
            ManageUpdateButton();
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

            string command = string.Empty;

            if (cbThumbnailOnly.IsChecked == true)
            {
                command = $"yt-dlp --skip-download --write-thumbnail --cookies \"{cookiesTxtFile}\" -o {GetVideoName(fileExtension)} --no-mtime \"{txtURL.Text}\"";
            }
            else
            {
                command = $"yt-dlp {GetFormat(fileExtension)} --cookies \"{cookiesTxtFile}\" -o {GetVideoName(fileExtension)} --no-mtime \"{txtURL.Text}\"";
            }

            // Construct command string

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
                        MessageBoxResult result = MessageBox.Show("Please download any extention that allows you to download cookies, I recommend `Get cookies.txt LOCALLY`. \n\nOnce downloaded press `cookies` and locate the file you extracted from chrome. \nPressing `Ok` will open the extention in your browser.",
                            "Authentication Required",
                            MessageBoxButton.OKCancel,
                            MessageBoxImage.Warning);

                        if(result == MessageBoxResult.OK)
                        {
                            string url = "https://chromewebstore.google.com/detail/cclelndahbckbenkjhflpdbgdldlbecc";
                            Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
                        }
                    });
                }
            }
        }

        private string GetFormat(string fileExtension)
        {
            string[] extractableAudioFormats = { "mp3", "aac", "m4a", "wav" };
            string[] videoFormats = { "mp4", "mov", "mkv", "flv", "3gp" };

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
                else if (fileExtension == "mov")
                {
                    return "-S vcodec:h264,res,acodec:aac --merge-output-format mov";
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

            if(cbThumbnailOnly.IsChecked == true)
            {
                if (txtFileName.Text == string.Empty)
                {
                    return $"\"{txtFileOutput.Text}\\%(title)s\"";
                }
                else
                {
                    return $"\"{txtFileOutput.Text}\\{txtFileName.Text}\"";
                }
            }
            else
            {
                if (txtFileName.Text == string.Empty)
                {
                    return $"\"{txtFileOutput.Text}\\%(title)s.{fileExtension}\"";
                }
                else
                {
                    return $"\"{txtFileOutput.Text}\\{txtFileName.Text}.{fileExtension}\"";
                }
            }
        }

        private async Task ExtractCookiesAndRetry(string originalCmd)
        {
            string browserName = GetDefaultBrowser();
            if (string.IsNullOrEmpty(browserName))
            {
                MessageBox.Show(
                    "Your default browser is not supported for cookie extraction.\n\n" +
                    "Supported browsers: Chrome, Edge, Firefox, Brave, Opera, Vivaldi, Chromium.",
                    "Unsupported Browser",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }

            MessageBox.Show("Using browser cookies from: " + browserName, "Info", MessageBoxButton.OK, MessageBoxImage.Information);

            // Modify the original command to use browser cookies directly
            string modifiedCmd = originalCmd + $" --cookies-from-browser {browserName}";
            txtOutput.Text = "Retrying download with browser cookies...";
            await RunCMD(modifiedCmd);
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

        /// <summary>
        /// Detects the system's default browser name for yt-dlp.
        /// </summary>
        private string GetDefaultBrowser()
        {
            try
            {
                // Primary: Check current user's HTTP association
                string registryKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice";
                string browserProgId = Registry.GetValue(registryKey, "ProgId", null) as string;

                // Fallback: Check HKEY_CLASSES_ROOT for .html default handler
                if (string.IsNullOrEmpty(browserProgId))
                {
                    string fallbackKey = @"HKEY_CLASSES_ROOT\.html";
                    browserProgId = Registry.GetValue(fallbackKey, null, null) as string;
                }

                if (string.IsNullOrEmpty(browserProgId))
                    return null;

                browserProgId = browserProgId.ToLowerInvariant(); // normalize casing

                // Chromium Browsers
                if (browserProgId.Contains("chrome")) return "chrome";
                if (browserProgId.Contains("chromium")) return "chromium";
                if (browserProgId.Contains("opera")) return "opera";
                if (browserProgId.Contains("edge")) return "edge";
                if (browserProgId.Contains("vivaldi")) return "vivaldi";
                if (browserProgId.Contains("brave")) return "brave";

                // Non-Chromium
                if (browserProgId.Contains("firefox")) return "firefox";
                if (browserProgId.Contains("safari")) return "safari"; // Rare on Windows

                // Optional note: Whale and others not supported by yt-dlp yet
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
            // Configure GithubSource with the logger
            GithubSource githubSource = GetGithubSource();

            // Pass the GithubSource and logger to UpdateManager
            UpdateManager manager = new UpdateManager(githubSource);

            if (!manager.IsInstalled)
            {
                MessageBoxResult result = MessageBox.Show(
                    "You cannot check for an update since you're using a version that isn't installed." +
                    "\nPlease go to \nhttps://github.com/Ghilliexyz/Kleptos/releases/latest" +
                    "\nand manually check for a new update or download and install Kleptos instead for auto updates." +
                    $"\n\nCurrent {txtKleptosVersion.Text}",
                    "Kleptos Uninstalled Version Detected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                // Return if the app isn't installed and or correct Portable version
                return;
            }

            // Check for updates
            var newVersion = await manager.CheckForUpdatesAsync();
            if (newVersion == null)
            {
                return;
            }

            // make this depend if there is a new version or not
            hasUpdate = true;

            // show update button
            ManageUpdateButton();
        }

        private static async Task UpdateKleptos()
        {
            // Initialize your custom logger
            ILogger logger = new FileLogger("log.txt");

            // Configure GithubSource with the logger
            GithubSource githubSource = GetGithubSource();

            // Pass the GithubSource and logger to UpdateManager
            UpdateManager manager = new UpdateManager(githubSource);

            try
            {
                // Check for updates
                var newVersion = await manager.CheckForUpdatesAsync();
                if (newVersion == null)
                {
                    return;
                }

                // Download new version
                await manager.DownloadUpdatesAsync(newVersion);

                // Install new version and restart app
                manager.ApplyUpdatesAndRestart(newVersion);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during update process: {Message}", ex.Message);
            }
        }

        private static GithubSource GetGithubSource()
        {
            return new GithubSource(
                repoUrl: "https://github.com/Ghilliexyz/Kleptos", 
                accessToken: null,
                prerelease: false
            );
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

        private void ManageUpdateButton()
        {
            if (hasUpdate)
            {
                UpdateButton.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateButton.Visibility = Visibility.Collapsed;
            }
        }

        private void FileCookies_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();

            openFileDialog.ShowDialog();

            if (openFileDialog.FileName != string.Empty)
            {
                cookiesTxtFile = openFileDialog.FileName;
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