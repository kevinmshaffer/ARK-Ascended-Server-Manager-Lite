﻿using ARK_Server_Manager.Lib.Model;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WPFSharp.Globalizer;

namespace ARK_Server_Manager.Lib
{
    public class ServerRuntime : DependencyObject, IDisposable
    {
        private const int DIRECTORIES_PER_LINE = 200;

        public event EventHandler StatusUpdate;

        public enum ServerStatus
        {
            Unknown,
            Stopping,
            Stopped,
            Initializing,
            Running,
            Updating,
            Uninstalled
        }

        public enum SteamStatus
        {
            Unknown,
            NeedPublicIP,
            Unavailable,
            WaitingForPublication,
            Available
        }

        private readonly GlobalizedApplication _globalizer = GlobalizedApplication.Instance;
        private readonly List<PropertyChangeNotifier> profileNotifiers = new List<PropertyChangeNotifier>();
        private Process serverProcess;
        private IAsyncDisposable updateRegistration;

        #region Properties

        public static readonly DependencyProperty SteamProperty = DependencyProperty.Register(nameof(Steam), typeof(SteamStatus), typeof(ServerRuntime), new PropertyMetadata(SteamStatus.Unknown));
        public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(nameof(Status), typeof(ServerStatus), typeof(ServerRuntime), new PropertyMetadata(ServerStatus.Unknown));
        public static readonly DependencyProperty StatusStringProperty = DependencyProperty.Register(nameof(StatusString), typeof(string), typeof(ServerRuntime), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty MaxPlayersProperty = DependencyProperty.Register(nameof(MaxPlayers), typeof(int), typeof(ServerRuntime), new PropertyMetadata(0));
        public static readonly DependencyProperty PlayersProperty = DependencyProperty.Register(nameof(Players), typeof(int), typeof(ServerRuntime), new PropertyMetadata(0));
        public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(nameof(Version), typeof(Version), typeof(ServerRuntime), new PropertyMetadata(new Version()));
        public static readonly DependencyProperty ProfileSnapshotProperty = DependencyProperty.Register(nameof(ProfileSnapshot), typeof(ServerProfileSnapshot), typeof(ServerRuntime), new PropertyMetadata(null));
        public static readonly DependencyProperty TotalModCountProperty = DependencyProperty.Register(nameof(TotalModCount), typeof(int), typeof(ServerRuntime), new PropertyMetadata(0));

        public SteamStatus Steam
        {
            get { return (SteamStatus)GetValue(SteamProperty); }
            protected set { SetValue(SteamProperty, value); }
        }

        public ServerStatus Status
        {
            get { return (ServerStatus)GetValue(StatusProperty); }
            protected set { SetValue(StatusProperty, value); }
        }

        public string StatusString
        {
            get { return (string)GetValue(StatusStringProperty); }
            protected set { SetValue(StatusStringProperty, value); }
        }

        public int MaxPlayers
        {
            get { return (int)GetValue(MaxPlayersProperty); }
            protected set { SetValue(MaxPlayersProperty, value); }
        }

        public int Players
        {
            get { return (int)GetValue(PlayersProperty); }
            protected set { SetValue(PlayersProperty, value); }
        }

        public Version Version
        {
            get { return (Version)GetValue(VersionProperty); }
            protected set { SetValue(VersionProperty, value); }
        }

        public ServerProfileSnapshot ProfileSnapshot
        {
            get { return (ServerProfileSnapshot)GetValue(ProfileSnapshotProperty); }
            set { SetValue(ProfileSnapshotProperty, value); }
        }

        public int TotalModCount
        {
            get { return (int)GetValue(TotalModCountProperty); }
            protected set { SetValue(TotalModCountProperty, value); }
        }

        #endregion

        public void Dispose()
        {
            this.updateRegistration?.DisposeAsync().DoNotWait();
        }

        public Task AttachToProfile(ServerProfile profile)
        {
            AttachToProfileCore(profile);
            GetProfilePropertyChanges(profile);
            return TaskUtils.FinishedTask;
        }

        private void AttachToProfileCore(ServerProfile profile)
        {
            UnregisterForUpdates();

            this.ProfileSnapshot = ServerProfileSnapshot.Create(profile);

            if (Version.TryParse(profile.LastInstalledVersion, out Version lastInstalled))
            {
                this.Version = lastInstalled;
            }

           RegisterForUpdates();
        }

        private void GetProfilePropertyChanges(ServerProfile profile)
        {
            foreach(var notifier in profileNotifiers)
            {
                notifier.Dispose();
            }

            profileNotifiers.Clear();
            profileNotifiers.AddRange(PropertyChangeNotifier.GetNotifiers(
                profile,
                new[] {
                    ServerProfile.ProfileNameProperty,
                    ServerProfile.InstallDirectoryProperty,
                    ServerProfile.QueryPortProperty,
                    ServerProfile.ServerPortProperty,
                    ServerProfile.ServerIPProperty,
                    ServerProfile.MaxPlayersProperty,

                    ServerProfile.ServerMapProperty,
                    ServerProfile.ServerModIdsProperty,
                },
                (s, p) =>
                {
                    if (Status == ServerStatus.Stopped || Status == ServerStatus.Uninstalled || Status == ServerStatus.Unknown)
                    {
                        AttachToProfileCore(profile);
                    }
                }));
        }

        private void GetServerEndpoints(out IPEndPoint localServerQueryEndPoint, out IPEndPoint steamServerQueryEndPoint)
        {
            localServerQueryEndPoint = null;
            steamServerQueryEndPoint = null;

            //
            // Get the local endpoint for querying the local network
            //
            if (!ushort.TryParse(this.ProfileSnapshot.QueryPort.ToString(), out ushort port))
            {
                Debug.WriteLine($"Port is out of range ({this.ProfileSnapshot.QueryPort})");
                return;
            }

            IPAddress localServerIpAddress;
			if (!String.IsNullOrWhiteSpace(this.ProfileSnapshot.ServerIP) && IPAddress.TryParse("192.168.50.21", out localServerIpAddress))
			//if (!String.IsNullOrWhiteSpace(this.ProfileSnapshot.ServerIP) && IPAddress.TryParse(this.ProfileSnapshot.ServerIP, out localServerIpAddress))
            {
                // Use the explicit Server IP
                localServerQueryEndPoint = new IPEndPoint(localServerIpAddress, Convert.ToUInt16(this.ProfileSnapshot.QueryPort));
            }
            else
            {
                // No Server IP specified, use Loopback
                localServerQueryEndPoint = new IPEndPoint(IPAddress.Loopback, Convert.ToUInt16(this.ProfileSnapshot.QueryPort));
            }

            //
            // Get the public endpoint for querying Steam
            //
            steamServerQueryEndPoint = null;
            if (!String.IsNullOrWhiteSpace(Config.Default.MachinePublicIP))
            {
                IPAddress steamServerIpAddress;
                if (IPAddress.TryParse(Config.Default.MachinePublicIP, out steamServerIpAddress))
                {
                    // Use the Public IP explicitly specified
                    steamServerQueryEndPoint = new IPEndPoint(steamServerIpAddress, Convert.ToUInt16(this.ProfileSnapshot.QueryPort));
                }
                else
                {
                    // Resolve the IP from the DNS name provided
                    try
                    {
                        var addresses = Dns.GetHostAddresses(Config.Default.MachinePublicIP);
                        if (addresses.Length > 0)
                        {
                            steamServerQueryEndPoint = new IPEndPoint(addresses[0], Convert.ToUInt16(this.ProfileSnapshot.QueryPort));
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Failed to resolve DNS address {0}: {1}\r\n{2}", Config.Default.MachinePublicIP, ex.Message, ex.StackTrace);
                    }
                }
            }
        }

        public string GetServerExe()
        {
            return Path.Combine(this.ProfileSnapshot.InstallDirectory, Config.Default.ServerBinaryRelativePath, Config.Default.ServerExe);
        }

        public string GetServerLauncherFile()
        {
            return Path.Combine(this.ProfileSnapshot.InstallDirectory, Config.Default.ServerConfigRelativePath, Config.Default.LauncherFile);
        }

        private void ProcessStatusUpdate(IAsyncDisposable registration, ServerStatusWatcher.ServerStatusUpdate update)
        {
            if(!Object.ReferenceEquals(registration, this.updateRegistration))
            {
                return;
            }

            TaskUtils.RunOnUIThreadAsync(() =>
            {
                var oldStatus = this.Status;
                switch (update.Status)
                {
                    case ServerStatusWatcher.ServerStatus.NotInstalled:
                        UpdateServerStatus(ServerStatus.Uninstalled, SteamStatus.Unavailable);
                        break;

                    case ServerStatusWatcher.ServerStatus.Initializing:
                        UpdateServerStatus(ServerStatus.Initializing, SteamStatus.Unavailable);
                        break;

                    case ServerStatusWatcher.ServerStatus.Stopped:
                        UpdateServerStatus(ServerStatus.Stopped, SteamStatus.Unavailable);
                        break;

                    case ServerStatusWatcher.ServerStatus.Unknown:
                        UpdateServerStatus(ServerStatus.Unknown, SteamStatus.Unknown);
                        break;

                    case ServerStatusWatcher.ServerStatus.RunningLocalCheck:
                        UpdateServerStatus(ServerStatus.Running, this.Steam != SteamStatus.Available ? SteamStatus.WaitingForPublication : this.Steam);
                        break;

                    case ServerStatusWatcher.ServerStatus.RunningExternalCheck:
                        UpdateServerStatus(ServerStatus.Running, SteamStatus.WaitingForPublication);
                        break;

                    case ServerStatusWatcher.ServerStatus.Published:
                        UpdateServerStatus(ServerStatus.Running, SteamStatus.Available);
                        break;
                }

                this.Players = update.Players?.Count ?? 0;
                this.MaxPlayers = update.ServerInfo?.MaxPlayers ?? this.ProfileSnapshot.MaxPlayerCount;

                if (update.ServerInfo != null)
                {
                    var match = Regex.Match(update.ServerInfo.Name, @"\(v([0-9]+\.[0-9]*)\)");
                    if (match.Success && match.Groups.Count >= 2)
                    {
                        var serverVersion = match.Groups[1].Value;
                        if (!String.IsNullOrWhiteSpace(serverVersion) && Version.TryParse(serverVersion, out Version temp))
                        {
                            this.Version = temp;
                        }
                    }
                }

                this.serverProcess = update.Process;

                StatusUpdate?.Invoke(this, EventArgs.Empty);
            }).DoNotWait();
        }

        private void RegisterForUpdates()
        {
            if (this.updateRegistration == null)
            {
                GetServerEndpoints(out IPEndPoint localServerQueryEndPoint, out IPEndPoint steamServerQueryEndPoint);
                if (localServerQueryEndPoint == null || steamServerQueryEndPoint == null)
                    return;

                this.updateRegistration = ServerStatusWatcher.Instance.RegisterForUpdates(this.ProfileSnapshot.InstallDirectory, this.ProfileSnapshot.ProfileId, localServerQueryEndPoint, steamServerQueryEndPoint, ProcessStatusUpdate);
            }
        }

        private void UnregisterForUpdates()
        {
            this.updateRegistration?.DisposeAsync().DoNotWait();
            this.updateRegistration = null;
        }


        private void CheckServerWorldFileExists()
        {
            var serverApp = new ServerApp()
            {
                BackupWorldFile = false,
                DeleteOldServerBackupFiles = false,
                SendEmails = false,
                OutputLogs = false
            };
            serverApp.CheckServerWorldFileExists(ProfileSnapshot);
        }

        public Task StartAsync()
        {
            if(!Environment.Is64BitOperatingSystem)
            {
                MessageBox.Show("ARK: Survival Evolved(tm) Server requires a 64-bit operating system to run.  Your operating system is 32-bit and therefore the Ark Server Manager cannot start the server.  You may still load and save profiles and settings files for use on other machines.", "64-bit OS Required", MessageBoxButton.OK, MessageBoxImage.Error);
                return TaskUtils.FinishedTask;
            }

            switch(this.Status)
            {
                case ServerStatus.Running:
                case ServerStatus.Initializing:
                case ServerStatus.Stopping:
                    Debug.WriteLine("Server {0} already running.", this.ProfileSnapshot.ProfileName);
                    return TaskUtils.FinishedTask;
            }

            UnregisterForUpdates();
            UpdateServerStatus(ServerStatus.Initializing, this.Steam);

            var serverExe = GetServerExe();
            var launcherExe = GetServerLauncherFile();

            if (Config.Default.ManageFirewallAutomatically)
            {
                var ports = new List<int>() { this.ProfileSnapshot.ServerPort , this.ProfileSnapshot.QueryPort };
                if(this.ProfileSnapshot.UseRawSockets)
                {
                    ports.Add(this.ProfileSnapshot.ServerPort + 1);
                }
                if (this.ProfileSnapshot.RCONEnabled)
                {
                    ports.Add(this.ProfileSnapshot.RCONPort);
                }

                if (!FirewallUtils.EnsurePortsOpen(serverExe, ports.ToArray(), $"{Config.Default.FirewallRulePrefix} {this.ProfileSnapshot.ServerName}"))
                {
                    var result = MessageBox.Show("Failed to automatically set firewall rules.  If you are running custom firewall software, you may need to set your firewall rules manually.  You may turn off automatic firewall management in Settings.\r\n\r\nWould you like to continue running the server anyway?", "Automatic Firewall Management Error", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (result == MessageBoxResult.No)
                    {
                        return TaskUtils.FinishedTask;
                    }
                }
            }

            CheckServerWorldFileExists();

            try
            {
                var startInfo = new ProcessStartInfo()
                {
                    FileName = launcherExe
                };

                var process = Process.Start(startInfo);
                process.EnableRaisingEvents = true;
            }
            catch (Win32Exception ex)
            {
                throw new FileNotFoundException(String.Format("Unable to find {0} at {1}.  Server Install Directory: {2}", Config.Default.LauncherFile, launcherExe, this.ProfileSnapshot.InstallDirectory), launcherExe, ex);
            }
            finally
            {
                RegisterForUpdates();
            }
            
            return TaskUtils.FinishedTask;            
        }

        public async Task StopAsync()
        {
            switch(this.Status)
            {
                case ServerStatus.Running:
                case ServerStatus.Initializing:
                    try
                    {
                        if (this.serverProcess != null)
                        {
                            UpdateServerStatus(ServerStatus.Stopping, SteamStatus.Unavailable);

                            await ProcessUtils.SendStop(this.serverProcess);
                        }

                        if (this.serverProcess.HasExited)
                        {
                            CheckServerWorldFileExists();
                        }
                    }
                    catch(InvalidOperationException)
                    {                    
                    }
                    finally
                    {
                        UpdateServerStatus(ServerStatus.Stopped, SteamStatus.Unavailable);
                    }
                    break;
            }            
        }


        public async Task<bool> UpgradeAsync(CancellationToken cancellationToken, bool updateServer, ServerBranchSnapshot branch, bool validate, ProgressDelegate progressCallback)
        {
            if (updateServer && !Environment.Is64BitOperatingSystem)
            {
                var result = MessageBox.Show("The ARK server requires a 64-bit operating system to run. Your operating system is 32-bit and therefore the Ark Server Manager will be unable to start the server, but you may still install it or load and save profiles and settings files for use on other machines.\r\n\r\nDo you wish to continue?", "64-bit OS Required", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.No)
                {
                    return false;
                }
            }

            try
            {
                await StopAsync();

                bool isNewInstallation = this.Status == ServerStatus.Uninstalled;

                UpdateServerStatus(ServerStatus.Updating, this.Steam);

                // Run the SteamCMD to install the server
                var steamCmdFile = SteamCmdUpdater.GetSteamCmdFile();
                if (string.IsNullOrWhiteSpace(steamCmdFile) || !File.Exists(steamCmdFile))
                {
                    progressCallback?.Invoke(0, $"{SteamCmdUpdater.OUTPUT_PREFIX} ***********************************");
                    progressCallback?.Invoke(0, $"{SteamCmdUpdater.OUTPUT_PREFIX} ERROR: SteamCMD could not be found. Expected location is {steamCmdFile}");
                    progressCallback?.Invoke(0, $"{SteamCmdUpdater.OUTPUT_PREFIX} ***********************************");
                    return false;
                }

                // record the start time of the process, this is used to determine if any files changed in the download process.
                var startTime = DateTime.Now;

                var gotNewVersion = false;
                var downloadSuccessful = false;
                var success = false;

                if (updateServer)
                {
                    // *********************
                    // Server Update Section
                    // *********************

                    progressCallback?.Invoke(0, $"{SteamCmdUpdater.OUTPUT_PREFIX} Starting server update.");
                    progressCallback?.Invoke(0, $"{SteamCmdUpdater.OUTPUT_PREFIX} Server branch: {ServerApp.GetBranchName(branch?.BranchName)}.");

                    // create the branch arguments
                    var steamCmdInstallServerBetaArgs = new StringBuilder();
                    if (!string.IsNullOrWhiteSpace(branch?.BranchName))
                    {
                        steamCmdInstallServerBetaArgs.AppendFormat(Config.Default.SteamCmdInstallServerBetaNameArgsFormat, branch.BranchName);
                        if (!string.IsNullOrWhiteSpace(branch?.BranchPassword))
                        {
                            steamCmdInstallServerBetaArgs.Append(" ");
                            steamCmdInstallServerBetaArgs.AppendFormat(Config.Default.SteamCmdInstallServerBetaPasswordArgsFormat, branch?.BranchPassword);
                        }
                    }

                    // Check if this is a new server installation.
                    if (isNewInstallation && Config.Default.AutoUpdate_EnableUpdate && !string.IsNullOrWhiteSpace(Config.Default.AutoUpdate_CacheDir))
                    {
                        var branchName = string.IsNullOrWhiteSpace(branch?.BranchName) ? Config.Default.DefaultServerBranchName : branch.BranchName;
                        var cacheFolder = IOUtils.NormalizePath(Path.Combine(Config.Default.AutoUpdate_CacheDir, $"{Config.Default.ServerBranchFolderPrefix}{branchName}"));

                        // check if the auto-update facility is enabled and the cache folder defined.
                        if (!string.IsNullOrWhiteSpace(cacheFolder) && Directory.Exists(cacheFolder))
                        {
                            // Auto-Update enabled and cache foldler exists.
                            progressCallback?.Invoke(0, $"{SteamCmdUpdater.OUTPUT_PREFIX} Installing server from local cache...may take a while to copy all the files.");

                            // Install the server files from the cache.
                            var installationFolder = this.ProfileSnapshot.InstallDirectory;
                            int count = 0;
                            await Task.Run(() =>
                                ServerApp.DirectoryCopy(cacheFolder, installationFolder, true, Config.Default.AutoUpdate_UseSmartCopy, (p, m, n) =>
                                    {
                                        count++;
                                        progressCallback?.Invoke(0, ".", count % DIRECTORIES_PER_LINE == 0);
                                    }), cancellationToken);
                        }
                    }

                    progressCallback?.Invoke(0, "\r\n");
                    progressCallback?.Invoke(0, $"{SteamCmdUpdater.OUTPUT_PREFIX} Updating server from steam.\r\n");

                    downloadSuccessful = !Config.Default.SteamCmdRedirectOutput;
                    DataReceivedEventHandler serverOutputHandler = (s, e) =>
                    {
                        var dataValue = e.Data ?? string.Empty;
                        progressCallback?.Invoke(0, dataValue);
                        if (!gotNewVersion && dataValue.Contains("downloading,"))
                        {
                            gotNewVersion = true;
                        }
                        if (dataValue.StartsWith("Success!"))
                        {
                            downloadSuccessful = true;
                        }
                    };

                    var steamCmdInstallServerArgsFormat = Config.Default.SteamCmdInstallServerArgsFormat;
                    var steamCmdArgs = String.Format(steamCmdInstallServerArgsFormat, this.ProfileSnapshot.InstallDirectory, Config.Default.AppIdServer, steamCmdInstallServerBetaArgs, validate ? "validate" : string.Empty);

                    success = await ServerUpdater.UpgradeServerAsync(steamCmdFile, steamCmdArgs, this.ProfileSnapshot.InstallDirectory, Config.Default.SteamCmdRedirectOutput ? serverOutputHandler : null, cancellationToken, ProcessWindowStyle.Minimized);
                    if (success && downloadSuccessful)
                    {
                        progressCallback?.Invoke(0, $"{SteamCmdUpdater.OUTPUT_PREFIX} Finished server update.");

                        if (Directory.Exists(this.ProfileSnapshot.InstallDirectory))
                        {
                            if (!Config.Default.SteamCmdRedirectOutput)
                                // check if any of the server files have changed.
                                gotNewVersion = ServerApp.HasNewServerVersion(this.ProfileSnapshot.InstallDirectory, startTime);

                            progressCallback?.Invoke(0, $"{SteamCmdUpdater.OUTPUT_PREFIX} New server version - {gotNewVersion.ToString().ToUpperInvariant()}.");
                        }

                        progressCallback?.Invoke(0, "\r\n");
                    }
                    else
                    {
                        success = false;
                        progressCallback?.Invoke(0, $"{SteamCmdUpdater.OUTPUT_PREFIX} ****************************");
                        progressCallback?.Invoke(0, $"{SteamCmdUpdater.OUTPUT_PREFIX} ERROR: Failed server update.");
                        progressCallback?.Invoke(0, $"{SteamCmdUpdater.OUTPUT_PREFIX} ****************************\r\n");

                        if (Config.Default.SteamCmdRedirectOutput)
                            progressCallback?.Invoke(0, $"{SteamCmdUpdater.OUTPUT_PREFIX} If the server update keeps failing try disabling the '{_globalizer.GetResourceString("GlobalSettings_SteamCmdRedirectOutputLabel")}' option in the settings window.\r\n");
                    }
                }
                else
                    success = true;

                
                progressCallback?.Invoke(0, $"{SteamCmdUpdater.OUTPUT_PREFIX} Finished upgrade process.");
                return success;
            }
            catch (TaskCanceledException)
            {
                return false;
            }
            finally
            {
                UpdateServerStatus(ServerStatus.Stopped, Steam);
            }
        }

        private void UpdateServerStatus(ServerStatus serverStatus, SteamStatus steamStatus)
        {
            this.Status = serverStatus;
            this.Steam = steamStatus;

            UpdateServerStatusString();
        }

        public void UpdateServerStatusString()
        {
            switch (Status)
            {
                case ServerStatus.Initializing:
                    StatusString = _globalizer.GetResourceString("ServerSettings_RuntimeStatusInitializingLabel");
                    break;
                case ServerStatus.Running:
                    StatusString = _globalizer.GetResourceString("ServerSettings_RuntimeStatusRunningLabel");
                    break;
                case ServerStatus.Stopped:
                    StatusString = _globalizer.GetResourceString("ServerSettings_RuntimeStatusStoppedLabel");
                    break;
                case ServerStatus.Stopping:
                    StatusString = _globalizer.GetResourceString("ServerSettings_RuntimeStatusStoppingLabel");
                    break;
                case ServerStatus.Uninstalled:
                    StatusString = _globalizer.GetResourceString("ServerSettings_RuntimeStatusUninstalledLabel");
                    break;
                case ServerStatus.Unknown:
                    StatusString = _globalizer.GetResourceString("ServerSettings_RuntimeStatusUnknownLabel");
                    break;
                case ServerStatus.Updating:
                    StatusString = _globalizer.GetResourceString("ServerSettings_RuntimeStatusUpdatingLabel");
                    break;
                default:
                    StatusString = _globalizer.GetResourceString("ServerSettings_RuntimeStatusUnknownLabel");
                    break;
            }
        }
    }
}
