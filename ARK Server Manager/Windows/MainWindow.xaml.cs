﻿using ARK_Server_Manager.Lib;
using EO.Wpf;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using WPFSharp.Globalizer;
using System.Threading;
using NLog;
using System.Reflection;
using ARK_Server_Manager.Utils;

namespace ARK_Server_Manager
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public static MainWindow Instance
        {
            get;
            private set;
        }

        private GlobalizedApplication _globalizer = GlobalizedApplication.Instance;
        private ActionQueue versionChecker;
        private ActionQueue scheduledTaskChecker;

        public static readonly DependencyProperty IsIpValidProperty = DependencyProperty.Register(nameof(IsIpValid), typeof(bool), typeof(MainWindow));
        public static readonly DependencyProperty CurrentConfigProperty = DependencyProperty.Register(nameof(CurrentConfig), typeof(Config), typeof(MainWindow));
        public static readonly DependencyProperty ServerManagerProperty = DependencyProperty.Register(nameof(ServerManager), typeof(ServerManager), typeof(MainWindow), new PropertyMetadata(null));
        public static readonly DependencyProperty AutoBackupStateProperty = DependencyProperty.Register(nameof(AutoBackupState), typeof(Microsoft.Win32.TaskScheduler.TaskState), typeof(MainWindow), new PropertyMetadata(Microsoft.Win32.TaskScheduler.TaskState.Unknown));
        public static readonly DependencyProperty AutoBackupStateStringProperty = DependencyProperty.Register(nameof(AutoBackupStateString), typeof(string), typeof(MainWindow), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty AutoBackupNextRunTimeProperty = DependencyProperty.Register(nameof(AutoBackupNextRunTime), typeof(string), typeof(MainWindow), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty AutoUpdateStateProperty = DependencyProperty.Register(nameof(AutoUpdateState), typeof(Microsoft.Win32.TaskScheduler.TaskState), typeof(MainWindow), new PropertyMetadata(Microsoft.Win32.TaskScheduler.TaskState.Unknown));
        public static readonly DependencyProperty AutoUpdateStateStringProperty = DependencyProperty.Register(nameof(AutoUpdateStateString), typeof(string), typeof(MainWindow), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty AutoUpdateNextRunTimeProperty = DependencyProperty.Register(nameof(AutoUpdateNextRunTime), typeof(string), typeof(MainWindow), new PropertyMetadata(string.Empty));

        public bool IsIpValid
        {
            get { return (bool)GetValue(IsIpValidProperty); }
            set { SetValue(IsIpValidProperty, value); }
        }

        public Config CurrentConfig
        {
            get { return GetValue(CurrentConfigProperty) as Config; }
            set { SetValue(CurrentConfigProperty, value); }
        }

        public ServerManager ServerManager
        {
            get { return (ServerManager)GetValue(ServerManagerProperty); }
            set { SetValue(ServerManagerProperty, value); }
        }

        public Microsoft.Win32.TaskScheduler.TaskState AutoBackupState
        {
            get { return (Microsoft.Win32.TaskScheduler.TaskState)GetValue(AutoBackupStateProperty); }
            set { SetValue(AutoBackupStateProperty, value); }
        }

        public string AutoBackupStateString
        {
            get { return (string)GetValue(AutoBackupStateStringProperty); }
            set { SetValue(AutoBackupStateStringProperty, value); }
        }

        public string AutoBackupNextRunTime
        {
            get { return (string)GetValue(AutoBackupNextRunTimeProperty); }
            set { SetValue(AutoBackupNextRunTimeProperty, value); }
        }

        public Microsoft.Win32.TaskScheduler.TaskState AutoUpdateState
        {
            get { return (Microsoft.Win32.TaskScheduler.TaskState)GetValue(AutoUpdateStateProperty); }
            set { SetValue(AutoUpdateStateProperty, value); }
        }

        public string AutoUpdateStateString
        {
            get { return (string)GetValue(AutoUpdateStateStringProperty); }
            set { SetValue(AutoUpdateStateStringProperty, value); }
        }

        public string AutoUpdateNextRunTime
        {
            get { return (string)GetValue(AutoUpdateNextRunTimeProperty); }
            set { SetValue(AutoUpdateNextRunTimeProperty, value); }
        }

        public bool IsAdministrator
        {
            get;
            set;
        }

        public MainWindow()
        {
            this.CurrentConfig = Config.Default;

            InitializeComponent();
            WindowUtils.RemoveDefaultResourceDictionary(this);

            MainWindow.Instance = this;
            this.ServerManager = ServerManager.Instance;

            this.DataContext = this;
            this.versionChecker = new ActionQueue();
            this.scheduledTaskChecker = new ActionQueue();

            IsAdministrator = SecurityUtils.IsAdministrator();
            if (IsAdministrator)
                this.Title = _globalizer.GetResourceString("MainWindow_TitleWithAdmin");

            // hook into the language change event
            GlobalizedApplication.Instance.GlobalizationManager.ResourceDictionaryChangedEvent += ResourceDictionaryChangedEvent;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            //
            // Kick off the initialization.
            //
            TaskUtils.RunOnUIThreadAsync(() =>
                {
                    // We need to load the set of existing servers, or create a blank one if we don't have any...
                    foreach (var profile in Directory.EnumerateFiles(Config.Default.ConfigDirectory, "*" + Config.Default.ProfileExtension))
                    {
                        try
                        {
                            ServerManager.Instance.AddFromPath(profile);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(String.Format(_globalizer.GetResourceString("MainWindow_ProfileLoad_FailedLabel"), profile, ex.Message, ex.StackTrace), _globalizer.GetResourceString("MainWindow_ProfileLoad_FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Exclamation);
                        }
                    }

                    ServerManager.Instance.CheckProfiles();

                    Tabs.SelectedIndex = 0;
                }).DoNotWait();

            this.scheduledTaskChecker.PostAction(CheckForScheduledTasks).DoNotWait();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            RCONWindow.CloseAllWindows();
            PlayerListWindow.CloseAllWindows();
            this.versionChecker.DisposeAsync().DoNotWait();

            var installFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var backupFolder = IOUtils.NormalizePath(string.IsNullOrWhiteSpace(Config.Default.BackupPath)
                ? Path.Combine(Config.Default.DataDir, Config.Default.BackupDir)
                : Path.Combine(Config.Default.BackupPath));
            SettingsUtils.BackupUserConfigSettings(Config.Default, "userconfig.json", installFolder, backupFolder);
        }

        private void ResourceDictionaryChangedEvent(object source, ResourceDictionaryChangedEventArgs e)
        {
            this.scheduledTaskChecker.PostAction(CheckForScheduledTasks).DoNotWait();
        }

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            var logFolder = SteamCmdUpdater.GetLogFolder();
            if (!Directory.Exists(logFolder))
                logFolder = Config.Default.DataDir;
            Process.Start("explorer.exe", logFolder);
        }

        private void RCON_Click(object sender, RoutedEventArgs e)
        {
            var window = new OpenRCONWindow();
            window.Closed += Window_Closed;
            window.Owner = this;
            window.ShowDialog();
        }

        private async void RefreshPublicIP_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await App.DiscoverMachinePublicIP(forceOverride: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Refresh Public IP Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Settings_Click(object sender, RoutedEventArgs args)
        {
            var window = new SettingsWindow();
            window.Closed += Window_Closed;
            window.Owner = this;
            window.ShowDialog();
        }

        private async void SteamCMD_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(_globalizer.GetResourceString("MainWindow_SteamCmd_Label"), _globalizer.GetResourceString("MainWindow_SteamCmd_Title"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                ProgressWindow window = null;

                try
                {
                    var updater = new SteamCmdUpdater();
                    var cancelSource = new CancellationTokenSource();

                    window = new ProgressWindow(_globalizer.GetResourceString("Progress_ReinstallSteamCmd_WindowTitle"));
                    window.Closed += Window_Closed;
                    window.Owner = this;
                    window.Show();

                    await Task.Delay(1000);
                    await updater.ReinstallSteamCmdAsync(new Progress<SteamCmdUpdater.Update>(u =>
                    {
                        var resourceString = string.IsNullOrWhiteSpace(u.StatusKey) ? null : _globalizer.GetResourceString(u.StatusKey);
                        var message = resourceString != null ? $"{SteamCmdUpdater.OUTPUT_PREFIX} {resourceString}" : u.StatusKey;
                        window?.AddMessage(message);

                        if (u.FailureText != null)
                        {
                            message = string.Format(_globalizer.GetResourceString("MainWindow_SteamCmd_FailedLabel"), u.FailureText);
                            window?.AddMessage(message);
                            MessageBox.Show(message, _globalizer.GetResourceString("MainWindow_SteamCmd_FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }), cancelSource.Token);
                }
                catch (Exception ex)
                {
                    var message = string.Format(_globalizer.GetResourceString("MainWindow_SteamCmd_FailedLabel"), ex.Message);
                    window?.AddMessage(message);
                    MessageBox.Show(message, _globalizer.GetResourceString("MainWindow_SteamCmd_FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    if (window != null)
                        window.CloseWindow();
                }
            }
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            this.Activate();
        }

        public void Servers_AddNew(object sender, NewItemRequestedEventArgs e)
        {
            var index = this.ServerManager.AddNew();
            ((EO.Wpf.TabControl)e.Source).SelectedIndex = index;
        }

        public void Servers_Remove(object sender, TabItemCloseEventArgs args)
        {
            args.Canceled = true;
            var server = ServerManager.Instance.Servers[args.ItemIndex];
            var result = MessageBox.Show(_globalizer.GetResourceString("MainWindow_ProfileDelete_Label"), String.Format(_globalizer.GetResourceString("MainWindow_ProfileDelete_Title"), server.Profile.ProfileName), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if(result == MessageBoxResult.Yes)
            {
                ServerManager.Instance.Remove(server, deleteProfile: true);
                args.Canceled = false;
            }
        }

        private void AutoBackupTaskRun_Click(object sender, RoutedEventArgs e)
        {
            var taskKey = TaskSchedulerUtils.ComputeKey(Config.Default.DataDir);

            try
            {
                TaskSchedulerUtils.RunAutoBackup(taskKey, null);
            }
            catch (Exception)
            {
                // Ignore.
            }
        }

        private void AutoBackupTaskState_Click(object sender, RoutedEventArgs e)
        {
            if (!IsAdministrator)
            {
                MessageBox.Show(_globalizer.GetResourceString("MainWindow_TaskAdminErrorLabel"), _globalizer.GetResourceString("MainWindow_TaskAdminErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var taskKey = TaskSchedulerUtils.ComputeKey(Config.Default.DataDir);

            try
            {
                TaskSchedulerUtils.SetAutoBackupState(taskKey, null, null);
            }
            catch (Exception)
            {
                // Ignore.
            }
        }

        private void AutoUpdateTaskRun_Click(object sender, RoutedEventArgs e)
        {
            var taskKey = TaskSchedulerUtils.ComputeKey(Config.Default.DataDir);

            try
            {
                TaskSchedulerUtils.RunAutoUpdate(taskKey, null);
            }
            catch (Exception)
            {
                // Ignore.
            }
        }

        private void AutoUpdateTaskState_Click(object sender, RoutedEventArgs e)
        {
            if (!IsAdministrator)
            {
                MessageBox.Show(_globalizer.GetResourceString("MainWindow_TaskAdminErrorLabel"), _globalizer.GetResourceString("MainWindow_TaskAdminErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var taskKey = TaskSchedulerUtils.ComputeKey(Config.Default.DataDir);

            try
            {
                TaskSchedulerUtils.SetAutoUpdateState(taskKey, null, null);
            }
            catch (Exception)
            {
                // Ignore.
            }
        }

        private async Task CheckForScheduledTasks()
        {
            var taskKey = TaskSchedulerUtils.ComputeKey(Config.Default.DataDir);

            TaskUtils.RunOnUIThreadAsync(() =>
            {
                try
                {
                    var backupState = TaskSchedulerUtils.TaskStateAutoBackup(taskKey, null, out DateTime backupnextRunTime);
                    var updateState = TaskSchedulerUtils.TaskStateAutoUpdate(taskKey, null, out DateTime updatenextRunTime);

                    this.AutoBackupState = backupState;
                    this.AutoUpdateState = updateState;

                    this.AutoBackupStateString = GetTaskStateString(AutoBackupState);
                    this.AutoUpdateStateString = GetTaskStateString(AutoUpdateState);

                    this.AutoBackupNextRunTime = backupnextRunTime == DateTime.MinValue ? string.Empty : $"{_globalizer.GetResourceString("MainWindow_TaskRunTimeLabel")} {backupnextRunTime.ToString("G")}";
                    this.AutoUpdateNextRunTime = updatenextRunTime == DateTime.MinValue ? string.Empty : $"{_globalizer.GetResourceString("MainWindow_TaskRunTimeLabel")} {updatenextRunTime.ToString("G")}";

                    Logger.Debug("CheckForScheduledTasks performed");
                }
                catch (Exception)
                {
                    // Ignore.
                }
            }).DoNotWait();

            await Task.Delay(Config.Default.ScheduledTasksCheckTime * 1 * 1000);
            this.scheduledTaskChecker.PostAction(CheckForScheduledTasks).DoNotWait();
        }

        private string GetTaskStateString(Microsoft.Win32.TaskScheduler.TaskState taskState)
        {
            switch (taskState)
            {
                case Microsoft.Win32.TaskScheduler.TaskState.Disabled:
                    return _globalizer.GetResourceString("MainWindow_TaskStateDisabledLabel");
                case Microsoft.Win32.TaskScheduler.TaskState.Queued:
                    return _globalizer.GetResourceString("MainWindow_TaskStateQueuedLabel");
                case Microsoft.Win32.TaskScheduler.TaskState.Ready:
                    return _globalizer.GetResourceString("MainWindow_TaskStateReadyLabel");
                case Microsoft.Win32.TaskScheduler.TaskState.Running:
                    return _globalizer.GetResourceString("MainWindow_TaskStateRunningLabel");
                case Microsoft.Win32.TaskScheduler.TaskState.Unknown:
                    return _globalizer.GetResourceString("MainWindow_TaskStateUnknownLabel");
                default:
                    return _globalizer.GetResourceString("MainWindow_TaskStateUnknownLabel");
            }
        }
    }
}
