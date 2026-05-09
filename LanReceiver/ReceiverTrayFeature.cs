namespace LanReceiver;

public sealed partial class ReceiverForm
{
    private readonly NotifyIcon _trayIcon = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly System.Windows.Forms.Timer _autoConnectTimer = new();

    private ReceiverAutoConfig _autoConfig = ReceiverAutoConfig.CreateDefault();
    private int _nextServerIndex;
    private bool _exitRequested;
    private bool _autoConnectLoopRunning;
    private bool _trayFeatureInitialized;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        InitializeTrayAndAutoConnectFeature();

        if (_autoConfig.StartMinimizedToTray)
        {
            BeginInvoke(new Action(HideToTray));
        }

        if (_autoConfig.AutoConnectEnabled)
        {
            ScheduleAutoReconnect();
            _ = TryAutoConnectOnceAsync(force: false);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        if (WindowState == FormWindowState.Minimized && _autoConfig.MinimizeToTray)
        {
            HideToTray();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exitRequested && _autoConfig.CloseButtonToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try
        {
            _autoConnectTimer.Stop();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayMenu.Dispose();
        }
        catch
        {
        }

        base.OnFormClosed(e);
    }

    private void InitializeTrayAndAutoConnectFeature()
    {
        if (_trayFeatureInitialized)
        {
            return;
        }

        _trayFeatureInitialized = true;

        try
        {
            string configFile = FindReceiverConfigFile();
            _autoConfig = ReceiverAutoConfig.LoadOrCreate(configFile);
            ApplyAutoConfigToUi();
            AddLog($"Loaded config: {configFile}");
        }
        catch (Exception ex)
        {
            _autoConfig = ReceiverAutoConfig.CreateDefault();
            ApplyAutoConfigToUi();
            AddLog($"Config load failed. Defaults used: {ex.Message}");
        }

        BuildTraySupport();
    }

    private void ApplyAutoConfigToUi()
    {
        if (_autoConfig.Servers.Count > 0)
        {
            _hostBox.Text = _autoConfig.Servers[0].Host;
            _portBox.Text = _autoConfig.Servers[0].Port.ToString();
        }

        _autoFullScreenCheck.Checked = _autoConfig.AutoFullScreenOnFirstFrame;
    }

    private void BuildTraySupport()
    {
        _trayMenu.Items.Clear();
        _trayMenu.Items.Add("Show receiver", null, (_, _) => RestoreFromTray());
        _trayMenu.Items.Add("Reconnect", null, async (_, _) =>
        {
            _manualDisconnect = false;
            DisconnectSilent();
            await TryAutoConnectOnceAsync(force: true);
        });
        _trayMenu.Items.Add("Disconnect", null, (_, _) => DisconnectByUserFromTray());
        _trayMenu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _trayIcon.Text = "LAN Receiver";
        _trayIcon.Icon = SystemIcons.Application;
        _trayIcon.ContextMenuStrip = _trayMenu;
        _trayIcon.Visible = true;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        _autoConnectTimer.Interval = Math.Max(1000, _autoConfig.RetryIntervalSeconds * 1000);
        _autoConnectTimer.Tick += async (_, _) => await TryAutoConnectOnceAsync(force: false);
    }

    private void HideToTray()
    {
        if (IsDisposed)
        {
            return;
        }

        ShowInTaskbar = false;
        Hide();
    }

    private void RestoreFromTray()
    {
        if (IsDisposed)
        {
            return;
        }

        Show();
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        _autoConnectTimer.Stop();
        _trayIcon.Visible = false;
        Close();
    }

    private void DisconnectByUserFromTray()
    {
        _manualDisconnect = true;
        StopAutoReconnect();
        DisconnectSilent();
        SetConnectedUi(false, "Disconnected");
        AddLog("Disconnected by tray menu.");
    }

    private async Task TryAutoConnectOnceAsync(bool force)
    {
        if (IsDisposed || _exitRequested || _autoConnectLoopRunning)
        {
            return;
        }

        if (_client is not null)
        {
            StopAutoReconnect();
            return;
        }

        if (!force && !_autoConfig.AutoConnectEnabled && !_autoConfig.AutoReconnectEnabled)
        {
            return;
        }

        List<ServerEndpoint> servers = _autoConfig.Servers
            .Where(x => !string.IsNullOrWhiteSpace(x.Host) && x.Port is >= 1 and <= 65535)
            .ToList();

        if (servers.Count == 0)
        {
            AddLog("No valid server endpoint in receiver_config.json.");
            return;
        }

        _autoConnectLoopRunning = true;
        StopAutoReconnect();

        try
        {
            for (int i = 0; i < servers.Count && _client is null; i++)
            {
                ServerEndpoint server = servers[_nextServerIndex % servers.Count];
                _nextServerIndex = (_nextServerIndex + 1) % servers.Count;

                SetConnectionInputs(server);
                AddLog($"Auto connect try: {server.Host}:{server.Port}");

                await ConnectAsync();

                if (_client is not null)
                {
                    AddLog($"Auto connect succeeded: {server.Host}:{server.Port}");
                    return;
                }

                await Task.Delay(300);
            }
        }
        finally
        {
            _autoConnectLoopRunning = false;

            if (_client is null && !_exitRequested && !_manualDisconnect && (_autoConfig.AutoConnectEnabled || _autoConfig.AutoReconnectEnabled))
            {
                ScheduleAutoReconnect();
            }
        }
    }

    private void SetConnectionInputs(ServerEndpoint server)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetConnectionInputs(server)));
            return;
        }

        _hostBox.Text = server.Host;
        _portBox.Text = server.Port.ToString();
    }

    private void ScheduleAutoReconnectFromWorker()
    {
        if (IsDisposed || _exitRequested || _manualDisconnect)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(ScheduleAutoReconnectFromWorker));
            return;
        }

        ScheduleAutoReconnect();
    }

    private void ScheduleAutoReconnect()
    {
        if (IsDisposed || _exitRequested || _manualDisconnect)
        {
            return;
        }

        if (!_autoConfig.AutoReconnectEnabled && !_autoConfig.AutoConnectEnabled)
        {
            return;
        }

        if (_client is not null)
        {
            return;
        }

        _autoConnectTimer.Interval = Math.Max(1000, _autoConfig.RetryIntervalSeconds * 1000);
        _autoConnectTimer.Stop();
        _autoConnectTimer.Start();
    }

    private void StopAutoReconnect()
    {
        try
        {
            _autoConnectTimer.Stop();
        }
        catch
        {
        }
    }

    private static string FindReceiverConfigFile()
    {
        string baseDir = AppContext.BaseDirectory;
        string outputConfig = Path.Combine(baseDir, "receiver_config.json");

        if (File.Exists(outputConfig))
        {
            return outputConfig;
        }

        string projectConfig = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "receiver_config.json"));
        if (File.Exists(projectConfig))
        {
            return projectConfig;
        }

        string currentConfig = Path.Combine(Environment.CurrentDirectory, "receiver_config.json");
        if (File.Exists(currentConfig))
        {
            return currentConfig;
        }

        return outputConfig;
    }
}
