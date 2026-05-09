using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LanReceiver;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new ReceiverForm());
    }
}

public sealed partial class ReceiverForm : Form
{
    private readonly TextBox _hostBox = new();
    private readonly TextBox _portBox = new();
    private readonly TextBox _saveFolderBox = new();

    private readonly Button _connectButton = new();
    private readonly Button _disconnectButton = new();
    private readonly Button _clearButton = new();
    private readonly Button _chooseFolderButton = new();
    private readonly Button _openFolderButton = new();
    private readonly Button _openFullScreenButton = new();

    private readonly CheckBox _openFolderAfterReceiveCheck = new();
    private readonly CheckBox _autoFullScreenCheck = new();

    private readonly Label _statusLabel = new();
    private readonly Label _progressLabel = new();
    private readonly Label _screenInfoLabel = new();
    private readonly ProgressBar _progressBar = new();

    private readonly PictureBox _screenPicture = new();
    private readonly ListBox _messageList = new();
    private readonly ListBox _logList = new();
    private readonly NotifyIcon _trayIcon = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly System.Windows.Forms.Timer _autoConnectTimer = new();

    private ReceiverAutoConfig _autoConfig = new();
    private int _nextServerIndex;
    private bool _exitRequested;
    private bool _autoConnectLoopRunning;

    private TcpClient? _client;
    private CancellationTokenSource? _cts;
    private bool _manualDisconnect;

    private FileReceiveSession? _fileSession;

    private bool _batchActive;
    private int _batchExpectedFiles;
    private int _batchReceivedFiles;
    private long _batchExpectedBytes;
    private long _batchReceivedBytes;
    private readonly Stopwatch _batchStopwatch = new();

    private Image? _lastScreenImage;
    private Form? _fullScreenForm;
    private PictureBox? _fullScreenPicture;

    public ReceiverForm()
    {
        Text = "LAN Receiver - Step 6 Screen Monitor";
        Width = 1080;
        Height = 820;
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();
        InitializeOpenTargetFeature();
        LoadAutoConfig();
        ApplyAutoConfigToUi();
        BuildTraySupport();

        Shown += async (_, _) => await StartAutoConnectOnShownAsync();

        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized && _autoConfig.MinimizeToTray)
            {
                HideToTray();
            }
        };

        _connectButton.Click += async (_, _) => await ConnectAsync();
        _disconnectButton.Click += (_, _) => DisconnectByUser();
        _clearButton.Click += (_, _) => _messageList.Items.Clear();
        _chooseFolderButton.Click += (_, _) => ChooseSaveFolder();
        _openFolderButton.Click += (_, _) => OpenSaveFolder();
        _openFullScreenButton.Click += (_, _) => OpenFullScreenMonitor();

        FormClosing += (_, e) =>
        {
            if (!_exitRequested && _autoConfig.CloseButtonToTray)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            _exitRequested = true;
            _autoConnectTimer.Stop();
            _trayIcon.Visible = false;
            CloseFullScreenMonitor();
            DisconnectSilent();
            _lastScreenImage?.Dispose();
            _trayIcon.Dispose();
            _trayMenu.Dispose();
        };
    }

    private void BuildUi()
    {
        string defaultFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "LanReceivedFiles");

        Directory.CreateDirectory(defaultFolder);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 7,
            ColumnCount = 1,
            Padding = new Padding(12),
        };

        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 27));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 27));

        var connectPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        connectPanel.Controls.Add(new Label
        {
            Text = "Server IP:",
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
        });

        _hostBox.Text = "127.0.0.1";
        _hostBox.Width = 160;
        connectPanel.Controls.Add(_hostBox);

        connectPanel.Controls.Add(new Label
        {
            Text = "Port:",
            AutoSize = true,
            Padding = new Padding(12, 8, 0, 0),
        });

        _portBox.Text = "50000";
        _portBox.Width = 100;
        connectPanel.Controls.Add(_portBox);

        _connectButton.Text = "Connect";
        _connectButton.Width = 90;
        connectPanel.Controls.Add(_connectButton);

        _disconnectButton.Text = "Disconnect";
        _disconnectButton.Width = 105;
        _disconnectButton.Enabled = false;
        connectPanel.Controls.Add(_disconnectButton);

        _clearButton.Text = "Clear";
        _clearButton.Width = 90;
        connectPanel.Controls.Add(_clearButton);

        root.Controls.Add(connectPanel, 0, 0);

        var savePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
        };

        savePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        savePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        savePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        savePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));

        savePanel.Controls.Add(new Label
        {
            Text = "Save to:",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        _saveFolderBox.Text = defaultFolder;
        _saveFolderBox.Dock = DockStyle.Fill;
        _saveFolderBox.ReadOnly = true;
        savePanel.Controls.Add(_saveFolderBox, 1, 0);

        _chooseFolderButton.Text = "Choose";
        _chooseFolderButton.Dock = DockStyle.Fill;
        savePanel.Controls.Add(_chooseFolderButton, 2, 0);

        _openFolderButton.Text = "Open";
        _openFolderButton.Dock = DockStyle.Fill;
        savePanel.Controls.Add(_openFolderButton, 3, 0);

        root.Controls.Add(savePanel, 0, 1);

        var optionPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        _openFolderAfterReceiveCheck.Text = "Open save folder after receiving files";
        _openFolderAfterReceiveCheck.AutoSize = true;
        _openFolderAfterReceiveCheck.Checked = false;
        optionPanel.Controls.Add(_openFolderAfterReceiveCheck);

        _autoFullScreenCheck.Text = "Auto full screen on first screen frame";
        _autoFullScreenCheck.AutoSize = true;
        _autoFullScreenCheck.Checked = true;
        optionPanel.Controls.Add(_autoFullScreenCheck);

        _openFullScreenButton.Text = "Full screen";
        _openFullScreenButton.Width = 110;
        optionPanel.Controls.Add(_openFullScreenButton);

        root.Controls.Add(optionPanel, 0, 2);

        var statusPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
        };

        statusPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        statusPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        statusPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _statusLabel.Text = "Disconnected";
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        statusPanel.Controls.Add(_statusLabel, 0, 0);

        _progressLabel.Text = "Idle";
        _progressLabel.Dock = DockStyle.Fill;
        _progressLabel.TextAlign = ContentAlignment.MiddleLeft;
        statusPanel.Controls.Add(_progressLabel, 0, 1);

        _progressBar.Dock = DockStyle.Fill;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 1000;
        statusPanel.Controls.Add(_progressBar, 0, 2);

        root.Controls.Add(statusPanel, 0, 3);

        var screenGroup = new GroupBox
        {
            Text = "Screen monitor preview",
            Dock = DockStyle.Fill,
        };

        var screenLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(8),
        };

        screenLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        screenLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _screenInfoLabel.Text = "No screen frame received.";
        _screenInfoLabel.Dock = DockStyle.Fill;
        _screenInfoLabel.TextAlign = ContentAlignment.MiddleLeft;
        screenLayout.Controls.Add(_screenInfoLabel, 0, 0);

        _screenPicture.Dock = DockStyle.Fill;
        _screenPicture.BackColor = Color.Black;
        _screenPicture.SizeMode = PictureBoxSizeMode.Zoom;
        screenLayout.Controls.Add(_screenPicture, 0, 1);

        screenGroup.Controls.Add(screenLayout);
        root.Controls.Add(screenGroup, 0, 4);

        var messageGroup = new GroupBox
        {
            Text = "Received messages / files",
            Dock = DockStyle.Fill,
        };

        _messageList.Dock = DockStyle.Fill;
        messageGroup.Controls.Add(_messageList);

        root.Controls.Add(messageGroup, 0, 5);

        var logGroup = new GroupBox
        {
            Text = "Log",
            Dock = DockStyle.Fill,
        };

        _logList.Dock = DockStyle.Fill;
        logGroup.Controls.Add(_logList);

        root.Controls.Add(logGroup, 0, 6);

        Controls.Add(root);
    }

    private async Task ConnectAsync()
    {
        if (_client is not null)
        {
            AddLog("Already connected.");
            return;
        }

        string host = _hostBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(host))
        {
            AddLog("Enter server IP.");
            return;
        }

        if (!int.TryParse(_portBox.Text, out int port) || port < 1 || port > 65535)
        {
            AddLog("Invalid port number.");
            return;
        }

        try
        {
            _manualDisconnect = false;

            _client = new TcpClient();
            _client.NoDelay = true;

            await _client.ConnectAsync(host, port);

            _cts = new CancellationTokenSource();

            SetConnectedUi(true, $"Connected to {host}:{port}");
            AddLog($"Connected: {host}:{port}");

            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            AddLog($"Connection failed: {ex.Message}");
            DisconnectSilent();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        if (_client is null) return;

        try
        {
            NetworkStream stream = _client.GetStream();

            while (!token.IsCancellationRequested)
            {
                ReceivedPacket? packet = await NetPacket.ReadAsync(stream, token);

                if (packet is null)
                {
                    if (!_manualDisconnect)
                    {
                        AddLog("Server disconnected.");
                    }

                    break;
                }

                await HandlePacketAsync(packet);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
            if (!_manualDisconnect)
            {
                AddLog("Server communication ended.");
            }
        }
        catch (SocketException)
        {
            if (!_manualDisconnect)
            {
                AddLog("Server communication ended.");
            }
        }
        catch (Exception ex)
        {
            if (!_manualDisconnect)
            {
                AddLog($"Receive error: {ex.Message}");
            }
        }
        finally
        {
            CloseCurrentFileSession(deleteTempFile: true);
            ResetBatchState();
            DisconnectFromWorker();
        }
    }

    private async Task HandlePacketAsync(ReceivedPacket packet)
    {
        switch (packet.Type)
        {
            case PacketType.TextMessage:
                {
                    string text = Encoding.UTF8.GetString(packet.Payload);
                    AddMessage($"Message: {text}");
                    break;
                }

            case PacketType.BatchStart:
                {
                    StartBatchReceive(packet.Payload);
                    break;
                }

            case PacketType.FileStart:
                {
                    StartFileReceive(packet.Payload);
                    break;
                }

            case PacketType.FileChunk:
                {
                    await WriteFileChunkAsync(packet.Payload);
                    break;
                }

            case PacketType.FileEnd:
                {
                    FinishFileReceive();
                    break;
                }

            case PacketType.ScreenFrame:
                {
                    HandleScreenFrame(packet.Payload);
                    break;
                }

            case PacketType.OpenTarget:
                {
                    await HandleOpenTargetCommandAsync(packet.Payload);
                    break;
                }

            case PacketType.TransferCancel:
                {
                    HandleTransferCancel(packet.Payload);
                    break;
                }

            case PacketType.BatchEnd:
                {
                    FinishBatchReceive();
                    break;
                }

            default:
                {
                    AddLog($"Unknown packet type: {packet.Type}");
                    break;
                }
        }
    }

    private void HandleScreenFrame(byte[] payload)
    {
        try
        {
            if (payload.Length < 4)
            {
                AddLog("Invalid screen frame payload.");
                return;
            }

            int infoLength = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(0, 4));

            if (infoLength < 1 || infoLength > payload.Length - 4)
            {
                AddLog("Invalid screen frame metadata.");
                return;
            }

            ScreenFrameInfo? info = JsonSerializer.Deserialize<ScreenFrameInfo>(payload.AsSpan(4, infoLength));

            if (info is null)
            {
                AddLog("Screen frame metadata parse failed.");
                return;
            }

            byte[] imageBytes = payload.AsSpan(4 + infoLength).ToArray();

            using var memoryStream = new MemoryStream(imageBytes);
            using Image loadedImage = Image.FromStream(memoryStream);
            var frameImage = new Bitmap(loadedImage);

            UpdateScreenFrame(frameImage, info, imageBytes.Length);
        }
        catch (Exception ex)
        {
            AddLog($"Screen frame receive failed: {ex.Message}");
        }
    }

    private void UpdateScreenFrame(Image frameImage, ScreenFrameInfo info, int byteSize)
    {
        if (IsDisposed)
        {
            frameImage.Dispose();
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => UpdateScreenFrame(frameImage, info, byteSize)));
            return;
        }

        Image? old = _lastScreenImage;
        _lastScreenImage = frameImage;
        _screenPicture.Image = frameImage;

        if (_fullScreenPicture is not null)
        {
            _fullScreenPicture.Image = frameImage;
        }

        old?.Dispose();

        _screenInfoLabel.Text = $"Frame {info.FrameNo} / {info.Width}x{info.Height} / {FormatBytes(byteSize)} / {DateTime.Now:HH:mm:ss}";

        if (_autoFullScreenCheck.Checked && _fullScreenForm is null)
        {
            OpenFullScreenMonitor();
        }
    }

    private void OpenFullScreenMonitor()
    {
        if (_fullScreenForm is not null)
        {
            _fullScreenForm.Activate();
            return;
        }

        var form = new Form
        {
            Text = "LAN Screen Monitor",
            FormBorderStyle = FormBorderStyle.None,
            WindowState = FormWindowState.Maximized,
            BackColor = Color.Black,
            KeyPreview = true,
            TopMost = true,
        };

        var picture = new PictureBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = _lastScreenImage,
        };

        form.Controls.Add(picture);

        form.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                form.Close();
            }
        };

        picture.DoubleClick += (_, _) => form.Close();

        form.FormClosed += (_, _) =>
        {
            picture.Image = null;
            _fullScreenPicture = null;
            _fullScreenForm = null;
        };

        _fullScreenForm = form;
        _fullScreenPicture = picture;
        form.Show(this);
    }

    private void CloseFullScreenMonitor()
    {
        try
        {
            _fullScreenForm?.Close();
        }
        catch
        {
        }

        _fullScreenForm = null;
        _fullScreenPicture = null;
    }

    private void StartBatchReceive(byte[] payload)
    {
        CloseCurrentFileSession(deleteTempFile: true);
        ResetBatchState();

        BatchStartInfo? info = JsonSerializer.Deserialize<BatchStartInfo>(payload);

        if (info is null || info.FileCount < 0 || info.TotalSize < 0)
        {
            AddLog("Invalid batch start data.");
            return;
        }

        _batchActive = true;
        _batchExpectedFiles = info.FileCount;
        _batchExpectedBytes = info.TotalSize;
        _batchReceivedFiles = 0;
        _batchReceivedBytes = 0;

        _batchStopwatch.Restart();

        UpdateProgress(0, Math.Max(_batchExpectedBytes, 1), $"Batch receive start: {_batchExpectedFiles} files / {FormatBytes(_batchExpectedBytes)}");
        AddLog($"Batch receive start: {_batchExpectedFiles} files / {FormatBytes(_batchExpectedBytes)}");
    }

    private void StartFileReceive(byte[] payload)
    {
        CloseCurrentFileSession(deleteTempFile: true);

        FileStartInfo? info = JsonSerializer.Deserialize<FileStartInfo>(payload);

        if (info is null || string.IsNullOrWhiteSpace(info.FileName) || info.FileSize < 0)
        {
            AddLog("Invalid file start data.");
            return;
        }

        string saveFolder = GetSaveFolder();
        Directory.CreateDirectory(saveFolder);

        string safeRelativePath = SanitizeRelativePath(info.FileName);
        string finalPath = CreateUniqueFilePath(saveFolder, safeRelativePath);
        string? finalDirectory = Path.GetDirectoryName(finalPath);

        if (!string.IsNullOrWhiteSpace(finalDirectory))
        {
            Directory.CreateDirectory(finalDirectory);
        }

        string tempPath = finalPath + ".part";

        var stream = new FileStream(
            tempPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            useAsync: true);

        _fileSession = new FileReceiveSession
        {
            FileName = safeRelativePath,
            FileSize = info.FileSize,
            FinalPath = finalPath,
            TempPath = tempPath,            OpenAfterReceive = info.OpenAfterReceive,
            OpenRequestId = info.OpenRequestId,
            Stream = stream,
        };

        UpdateProgress(
            _batchReceivedBytes,
            Math.Max(_batchExpectedBytes > 0 ? _batchExpectedBytes : info.FileSize, 1),
            $"Receiving: {safeRelativePath}");

        AddLog($"File receive start: {safeRelativePath} / {FormatBytes(info.FileSize)}");
    }

    private async Task WriteFileChunkAsync(byte[] payload)
    {
        if (_fileSession is null)
        {
            AddLog("File chunk arrived before file start. Ignored.");
            return;
        }

        await _fileSession.Stream.WriteAsync(payload.AsMemory(0, payload.Length));
        _fileSession.ReceivedBytes += payload.Length;

        if (_fileSession.ReceivedBytes > _fileSession.FileSize)
        {
            AddLog("Received file size exceeded expected size. File receive aborted.");
            CloseCurrentFileSession(deleteTempFile: true);
            UpdateProgress(0, 1, "Receive failed");
            return;
        }

        long displayCurrent;
        long displayTotal;

        if (_batchActive)
        {
            displayCurrent = _batchReceivedBytes + _fileSession.ReceivedBytes;
            displayTotal = Math.Max(_batchExpectedBytes, 1);
        }
        else
        {
            displayCurrent = _fileSession.ReceivedBytes;
            displayTotal = Math.Max(_fileSession.FileSize, 1);
        }

        double seconds = Math.Max(_batchStopwatch.Elapsed.TotalSeconds, 0.001);
        long speed = (long)(displayCurrent / seconds);

        UpdateProgress(
            displayCurrent,
            displayTotal,
            $"Receiving: {_fileSession.FileName} / {FormatBytes(displayCurrent)} / {FormatBytes(displayTotal)} / {FormatBytes(speed)}/s");
    }

    private void FinishFileReceive()
    {
        if (_fileSession is null)
        {
            AddLog("File end arrived without file session.");
            return;
        }

        string fileName = _fileSession.FileName;
        long receivedBytes = _fileSession.ReceivedBytes;
        long fileSize = _fileSession.FileSize;
        string finalPath = _fileSession.FinalPath;
        string tempPath = _fileSession.TempPath;

        try
        {
            _fileSession.Stream.Flush();
            _fileSession.Stream.Close();

            if (receivedBytes != fileSize)
            {
                AddLog($"File size mismatch: {fileName} / {FormatBytes(receivedBytes)} / {FormatBytes(fileSize)}");

                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                }

                UpdateProgress(0, 1, "Receive failed");
                return;
            }

            File.Move(tempPath, finalPath);

            _batchReceivedFiles++;
            _batchReceivedBytes += receivedBytes;

            AddMessage($"File received: {fileName}");
            AddLog($"Saved: {finalPath}");

            long displayTotal = _batchActive ? Math.Max(_batchExpectedBytes, 1) : Math.Max(fileSize, 1);
            long displayCurrent = _batchActive ? _batchReceivedBytes : receivedBytes;

            UpdateProgress(displayCurrent, displayTotal, $"File received: {fileName}");
            if (_fileSession.OpenAfterReceive)
            {
                _ = OpenReceivedFileAndReportAsync(finalPath, fileName, _fileSession.OpenRequestId);
            }
        }
        finally
        {
            _fileSession.Dispose();
            _fileSession = null;
        }
    }

    private void FinishBatchReceive()
    {
        CloseCurrentFileSession(deleteTempFile: true);

        _batchStopwatch.Stop();

        AddMessage($"Batch receive done: {_batchReceivedFiles}/{_batchExpectedFiles} files");
        AddLog($"Batch receive done: {_batchReceivedFiles}/{_batchExpectedFiles} files / {FormatBytes(_batchReceivedBytes)}");

        UpdateProgress(1000, 1000, $"Batch receive done: {_batchReceivedFiles}/{_batchExpectedFiles} files");

        bool shouldOpen = GetOpenFolderAfterReceive();

        ResetBatchState();

        if (shouldOpen)
        {
            OpenSaveFolder();
        }
    }

    private void HandleTransferCancel(byte[] payload)
    {
        string reason = "Canceled by sender.";

        try
        {
            TransferCancelInfo? info = JsonSerializer.Deserialize<TransferCancelInfo>(payload);

            if (info is not null && !string.IsNullOrWhiteSpace(info.Reason))
            {
                reason = info.Reason;
            }
        }
        catch
        {
        }

        CloseCurrentFileSession(deleteTempFile: true);
        ResetBatchState();

        UpdateProgress(0, 1, "Receive canceled");
        AddLog($"Receive canceled: {reason}");
        AddMessage($"Receive canceled: {reason}");
    }

    private void CloseCurrentFileSession(bool deleteTempFile)
    {
        if (_fileSession is null)
        {
            return;
        }

        string tempPath = _fileSession.TempPath;

        try
        {
            _fileSession.Dispose();
        }
        catch
        {
        }

        if (deleteTempFile)
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }

        _fileSession = null;
    }

    private void ResetBatchState()
    {
        _batchStopwatch.Reset();

        _batchActive = false;
        _batchExpectedFiles = 0;
        _batchReceivedFiles = 0;
        _batchExpectedBytes = 0;
        _batchReceivedBytes = 0;
    }

    private void LoadAutoConfig()
    {
        string configFile = FindConfigFile();

        try
        {
            if (!File.Exists(configFile))
            {
                _autoConfig = ReceiverAutoConfig.CreateDefault();
                SaveAutoConfig(configFile, _autoConfig);
                AddLog($"Created config: {configFile}");
                return;
            }

            string json = File.ReadAllText(configFile, Encoding.UTF8);
            ReceiverAutoConfig? loaded = JsonSerializer.Deserialize<ReceiverAutoConfig>(json);

            _autoConfig = loaded ?? ReceiverAutoConfig.CreateDefault();
            _autoConfig.Normalize();
            AddLog($"Loaded config: {configFile}");
        }
        catch (Exception ex)
        {
            _autoConfig = ReceiverAutoConfig.CreateDefault();
            AddLog($"Config load failed. Defaults used: {ex.Message}");
        }
    }

    private static string FindConfigFile()
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

    private static void SaveAutoConfig(string path, ReceiverAutoConfig config)
    {
        string? directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true,
        });

        File.WriteAllText(path, json, Encoding.UTF8);
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
        _trayMenu.Items.Add("Disconnect", null, (_, _) => DisconnectByUser());
        _trayMenu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _trayIcon.Text = "LAN Receiver";
        _trayIcon.Icon = SystemIcons.Application;
        _trayIcon.ContextMenuStrip = _trayMenu;
        _trayIcon.Visible = true;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        _autoConnectTimer.Interval = Math.Max(1000, _autoConfig.RetryIntervalSeconds * 1000);
        _autoConnectTimer.Tick += async (_, _) => await TryAutoConnectOnceAsync(force: false);
    }

    private async Task StartAutoConnectOnShownAsync()
    {
        if (_autoConfig.StartMinimizedToTray)
        {
            HideToTray();
        }

        if (_autoConfig.AutoConnectEnabled)
        {
            ScheduleAutoReconnect();
            await TryAutoConnectOnceAsync(force: false);
        }
    }

    private async Task TryAutoConnectOnceAsync(bool force)
    {
        if (IsDisposed || _exitRequested || _autoConnectLoopRunning)
        {
            return;
        }

        if (_client is not null)
        {
            _autoConnectTimer.Stop();
            return;
        }

        if (!force && !_autoConfig.AutoConnectEnabled && !_autoConfig.AutoReconnectEnabled)
        {
            return;
        }

        List<ServerEndpoint> servers = _autoConfig.Servers
            .Where(x => !string.IsNullOrWhiteSpace(x.Host) && x.Port > 0 && x.Port <= 65535)
            .ToList();

        if (servers.Count == 0)
        {
            AddLog("No valid server endpoint in receiver_config.json.");
            return;
        }

        _autoConnectLoopRunning = true;
        _autoConnectTimer.Stop();

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

            if (_client is null && !_exitRequested && (_autoConfig.AutoConnectEnabled || _autoConfig.AutoReconnectEnabled))
            {
                ScheduleAutoReconnect();
            }
        }
    }

    private void SetConnectionInputs(ServerEndpoint server)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => SetConnectionInputs(server)));
            return;
        }

        _hostBox.Text = server.Host;
        _portBox.Text = server.Port.ToString();
    }

    private void ScheduleAutoReconnect()
    {
        if (IsDisposed || _exitRequested)
        {
            return;
        }

        if (_manualDisconnect)
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
        _autoConnectTimer.Start();
    }

    private void HideToTray()
    {
        if (IsDisposed)
        {
            return;
        }

        Hide();
        ShowInTaskbar = false;
        _trayIcon.Visible = true;
    }

    private void RestoreFromTray()
    {
        if (IsDisposed)
        {
            return;
        }

        Show();
        ShowInTaskbar = true;

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        _trayIcon.Visible = false;
        Close();
    }
    private void ChooseSaveFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select a save folder",
            UseDescriptionForTitle = true,
            SelectedPath = _saveFolderBox.Text,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _saveFolderBox.Text = dialog.SelectedPath;
            AddLog($"Save folder changed: {dialog.SelectedPath}");
        }
    }

    private string GetSaveFolder()
    {
        if (InvokeRequired)
        {
            return (string)Invoke(new Func<string>(GetSaveFolder));
        }

        string folder = _saveFolderBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "LanReceivedFiles");

            _saveFolderBox.Text = folder;
        }

        Directory.CreateDirectory(folder);
        return folder;
    }

    private bool GetOpenFolderAfterReceive()
    {
        if (InvokeRequired)
        {
            return (bool)Invoke(new Func<bool>(GetOpenFolderAfterReceive));
        }

        return _openFolderAfterReceiveCheck.Checked;
    }

    private void OpenSaveFolder()
    {
        try
        {
            string folder = GetSaveFolder();
            Directory.CreateDirectory(folder);

            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AddLog($"Failed to open save folder: {ex.Message}");
        }
    }

    private void DisconnectByUser()
    {
        _manualDisconnect = true;
        DisconnectSilent();
        AddLog("Disconnected.");
    }

    private void DisconnectSilent()
    {
        try
        {
            _cts?.Cancel();
            _client?.Close();
            _cts?.Dispose();
        }
        catch
        {
        }
        finally
        {
            CloseCurrentFileSession(deleteTempFile: true);
            ResetBatchState();

            _cts = null;
            _client = null;

            if (!IsDisposed)
            {
                SetConnectedUi(false, "Disconnected");
            }
        }
    }

    private void DisconnectFromWorker()
    {
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            BeginInvoke(new Action(DisconnectFromWorker));
            return;
        }

        try
        {
            _cts?.Cancel();
            _client?.Close();
            _cts?.Dispose();
        }
        catch
        {
        }

        _cts = null;
        _client = null;

        SetConnectedUi(false, "Disconnected");
        ScheduleAutoReconnect();
    }

    private void SetConnectedUi(bool connected, string status)
    {
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetConnectedUi(connected, status)));
            return;
        }

        _connectButton.Enabled = !connected;
        _disconnectButton.Enabled = connected;
        _hostBox.Enabled = !connected;
        _portBox.Enabled = !connected;
        _statusLabel.Text = status;
    }

    private void UpdateProgress(long current, long total, string label)
    {
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => UpdateProgress(current, total, label)));
            return;
        }

        int value = 0;

        if (total > 0)
        {
            value = (int)Math.Clamp(current * 1000 / total, 0, 1000);
        }

        _progressBar.Value = value;
        _progressLabel.Text = label;
    }

    private void AddMessage(string message)
    {
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => AddMessage(message)));
            return;
        }

        _messageList.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private void AddLog(string message)
    {
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => AddLog(message)));
            return;
        }

        _logList.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private static string SanitizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "received_file";
        }

        string normalized = path.Replace('\\', '/');

        string[] parts = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x != "." && x != "..")
            .Select(SanitizeFileName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        if (parts.Length == 0)
        {
            return "received_file";
        }

        return Path.Combine(parts);
    }

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "unnamed";
        }

        foreach (char c in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(c, '_');
        }

        return fileName;
    }

    private static string CreateUniqueFilePath(string rootFolder, string relativePath)
    {
        string fullPath = Path.Combine(rootFolder, relativePath);

        string? directory = Path.GetDirectoryName(fullPath);
        string fileName = Path.GetFileName(fullPath);

        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = rootFolder;
        }

        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);

        string path = Path.Combine(directory, fileName);

        int index = 1;

        while (File.Exists(path) || File.Exists(path + ".part"))
        {
            string newFileName = $"{baseName} ({index}){extension}";
            path = Path.Combine(directory, newFileName);
            index++;
        }

        return path;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}

public sealed class ReceiverAutoConfig
{
    public bool AutoConnectEnabled { get; set; } = true;

    public bool AutoReconnectEnabled { get; set; } = true;

    public int RetryIntervalSeconds { get; set; } = 5;

    public bool CloseButtonToTray { get; set; } = true;

    public bool MinimizeToTray { get; set; } = true;

    public bool StartMinimizedToTray { get; set; } = false;

    public bool AutoFullScreenOnFirstFrame { get; set; } = true;

    public List<ServerEndpoint> Servers { get; set; } = new()
    {
        new ServerEndpoint
        {
            Host = "127.0.0.1",
            Port = 50000,
        },
    };

    public static ReceiverAutoConfig CreateDefault()
    {
        return new ReceiverAutoConfig();
    }

    public void Normalize()
    {
        if (RetryIntervalSeconds < 1)
        {
            RetryIntervalSeconds = 5;
        }

        Servers ??= new List<ServerEndpoint>();

        if (Servers.Count == 0)
        {
            Servers.Add(new ServerEndpoint
            {
                Host = "127.0.0.1",
                Port = 50000,
            });
        }
    }
}

public sealed class ServerEndpoint
{
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 50000;

    public override string ToString()
    {
        return $"{Host}:{Port}";
    }
}
public sealed class FileReceiveSession : IDisposable
{
    public string FileName { get; init; } = "";

    public long FileSize { get; init; }

    public long ReceivedBytes { get; set; }

    public string FinalPath { get; init; } = "";

    public string TempPath { get; init; } = "";
    public bool OpenAfterReceive { get; init; }

    public string OpenRequestId { get; init; } = "";

    public required FileStream Stream { get; init; }

    public void Dispose()
    {
        Stream.Dispose();
    }
}

public sealed class BatchStartInfo
{
    public int FileCount { get; set; }

    public long TotalSize { get; set; }
}

public sealed class FileStartInfo
{
    public string FileName { get; set; } = "";

    public long FileSize { get; set; }
    public bool OpenAfterReceive { get; set; }

    public string OpenRequestId { get; set; } = "";
}

public sealed class TransferCancelInfo
{
    public string Reason { get; set; } = "";
}

public sealed class ScreenFrameInfo
{
    public int Width { get; set; }

    public int Height { get; set; }

    public string Format { get; set; } = "jpeg";

    public long FrameNo { get; set; }

    public DateTimeOffset CapturedAtUtc { get; set; }
}

public static class PacketType
{
    public const byte TextMessage = 1;

    public const byte FileStart = 2;
    public const byte FileChunk = 3;
    public const byte FileEnd = 4;

    public const byte ScreenFrame = 5;
    public const byte WebOpen = 6;

    public const byte TransferCancel = 7;
    public const byte BatchStart = 8;
    public const byte BatchEnd = 9;

    public const byte OpenTarget = 10;
    public const byte OpenTargetResult = 11;
}

public sealed class ReceivedPacket
{
    public required byte Type { get; init; }

    public required byte[] Payload { get; init; }
}

public static class NetPacket
{
    private const int HeaderSize = 5;
    private const int MaxPayloadSize = 32 * 1024 * 1024;

    public static async Task<ReceivedPacket?> ReadAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] header = new byte[HeaderSize];

        bool headerRead = await ReadExactAsync(stream, header, token);

        if (!headerRead)
        {
            return null;
        }

        byte packetType = header[0];
        int payloadLength = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1, 4));

        if (payloadLength < 0 || payloadLength > MaxPayloadSize)
        {
            throw new InvalidOperationException("Invalid payload size.");
        }

        byte[] payload = new byte[payloadLength];

        bool payloadRead = await ReadExactAsync(stream, payload, token);

        if (!payloadRead)
        {
            return null;
        }

        return new ReceivedPacket
        {
            Type = packetType,
            Payload = payload,
        };
    }

    private static async Task<bool> ReadExactAsync(
        NetworkStream stream,
        byte[] buffer,
        CancellationToken token)
    {
        int offset = 0;

        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(offset, buffer.Length - offset),
                token);

            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
