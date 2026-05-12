using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LanReceiver.Contracts;
using LanReceiver.Protocol;
using LanReceiver.ScreenStreaming;
using LanReceiver.Transfers;
using LanShared.Ui;

namespace LanReceiver;

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
    private string? _activeStreamId;
    private readonly object _screenDecodeLock = new();
    private byte[]? _pendingScreenFramePayload;
    private long _pendingScreenFrameGeneration;
    private long _screenFrameGeneration;
    private bool _screenDecodeWorkerRunning;
    private long _screenFramesDroppedBeforeDecode;
    private long _screenFramesDisplayed;

    public ReceiverForm()
    {
        Text = "LAN Receiver - Screen Video Refactor";
        Width = 1080;
        Height = 820;
        MinimumSize = new Size(720, 520);
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();
        InitializeOpenTargetFeature();
        InitializeH264ReceiverFeature();

        _connectButton.Click += async (_, _) => await ConnectAsync();
        _disconnectButton.Click += (_, _) => DisconnectByUser();
        _clearButton.Click += (_, _) => _messageList.Items.Clear();
        _chooseFolderButton.Click += (_, _) => ChooseSaveFolder();
        _openFolderButton.Click += (_, _) => OpenSaveFolder();
        _openFullScreenButton.Click += (_, _) => OpenFullScreenMonitor();

        FormClosing += (_, _) =>
        {
            CloseFullScreenMonitor();
            StopH264Player();
            DisconnectSilent();
            _lastScreenImage?.Dispose();
        };
    }

    private void BuildUi()
    {
        var scrollHost = new ModernScrollHost
        {
            Dock = DockStyle.Fill,
            BackColor = ModernScrollPalette.Background,
        };

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
            BackColor = ModernScrollPalette.Background,
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

        connectPanel.Controls.Add(new Label { Text = "Server IP:", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        _hostBox.Text = "127.0.0.1";
        _hostBox.Width = 160;
        connectPanel.Controls.Add(_hostBox);

        connectPanel.Controls.Add(new Label { Text = "Port:", AutoSize = true, Padding = new Padding(12, 8, 0, 0) });
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

        var savePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
        savePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        savePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        savePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        savePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));

        savePanel.Controls.Add(new Label { Text = "Save to:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
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
        optionPanel.Controls.Add(_openFolderAfterReceiveCheck);

        _autoFullScreenCheck.Text = "Auto full screen on first screen frame";
        _autoFullScreenCheck.AutoSize = true;
        _autoFullScreenCheck.Checked = true;
        optionPanel.Controls.Add(_autoFullScreenCheck);

        _openFullScreenButton.Text = "Full screen";
        _openFullScreenButton.Width = 110;
        optionPanel.Controls.Add(_openFullScreenButton);
        root.Controls.Add(optionPanel, 0, 2);

        var statusPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
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

        var screenGroup = new GroupBox { Text = "Screen monitor preview", Dock = DockStyle.Fill };
        var screenLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(8) };
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

        var messageGroup = new GroupBox { Text = "Received messages / files", Dock = DockStyle.Fill };
        _messageList.Dock = DockStyle.Fill;
        messageGroup.Controls.Add(_messageList);
        root.Controls.Add(messageGroup, 0, 5);

        var logGroup = new GroupBox { Text = "Log", Dock = DockStyle.Fill };
        _logList.Dock = DockStyle.Fill;
        logGroup.Controls.Add(_logList);
        root.Controls.Add(logGroup, 0, 6);

        scrollHost.SetContent(root, new Size(1020, 760));
        Controls.Add(scrollHost);
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
            _client = new TcpClient { NoDelay = true };
            await _client.ConnectAsync(host, port);

            _cts = new CancellationTokenSource();
            StopAutoReconnect();
            SetConnectedUi(true, $"Connected to {host}:{port}");
            AddLog($"Connected: {host}:{port}");

            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            AddLog($"Connection failed: {ex.Message}");
            DisconnectSilent();
            ScheduleAutoReconnectFromWorker();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        if (_client is null)
        {
            return;
        }

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

                await HandlePacketAsync(packet.Value);
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
            StopH264Player();
            ResetScreenMonitor("Disconnected");
            DisconnectFromWorker();
        }
    }

    private async Task HandlePacketAsync(ReceivedPacket packet)
    {
        switch (packet.Type)
        {
            case PacketType.TextMessage:
                AddMessage($"Message: {Encoding.UTF8.GetString(packet.Payload)}");
                break;

            case PacketType.BatchStart:
                StartBatchReceive(packet.Payload);
                break;

            case PacketType.FileStart:
                StartFileReceive(packet.Payload);
                break;

            case PacketType.FileChunk:
                await WriteFileChunkAsync(packet.Payload);
                break;

            case PacketType.FileEnd:
                FinishFileReceive();
                break;

            case PacketType.BatchEnd:
                FinishBatchReceive();
                break;

            case PacketType.ScreenVideoStart:
                HandleScreenVideoStart(packet.Payload);
                break;

            case PacketType.ScreenVideoFrame:
                HandleScreenFrame(packet.Payload);
                break;

            case PacketType.ScreenVideoStop:
                HandleScreenVideoStop(packet.Payload);
                break;

            case PacketType.TransferCancel:
                HandleTransferCancel(packet.Payload);
                break;

            case PacketType.OpenTarget:
                await HandleOpenTargetCommandAsync(packet.Payload);
                break;


            case PacketType.ScreenH264Start:
                await HandleH264StreamStartAsync(packet.Payload);
                break;

            case PacketType.ScreenH264Data:
                await HandleH264StreamDataAsync(packet.Payload);
                break;

            case PacketType.ScreenH264Stop:
                HandleH264StreamStop(packet.Payload);
                break;
            default:
                AddLog($"Unsupported packet: {PacketType.ToName(packet.Type)}");
                break;
        }
    }

    private void HandleScreenVideoStart(byte[] payload)
    {
        try
        {
            ScreenVideoStartInfo? info = JsonSerializer.Deserialize<ScreenVideoStartInfo>(payload);
            if (info is null)
            {
                AddLog("Invalid screen video start info.");
                return;
            }

            _activeStreamId = info.StreamId;
            Interlocked.Increment(ref _screenFrameGeneration);
            UpdateScreenInfo($"Screen video start: {info.Fps}fps / {info.Quality}% / {info.Format}");
            AddLog($"Screen video start: stream={info.StreamId}");
        }
        catch (Exception ex)
        {
            AddLog($"Screen video start failed: {ex.Message}");
        }
    }

    private void HandleScreenFrame(byte[] payload)
    {
        bool startWorker = false;

        lock (_screenDecodeLock)
        {
            if (_pendingScreenFramePayload is not null)
            {
                _screenFramesDroppedBeforeDecode++;
            }

            long generation = Interlocked.Read(ref _screenFrameGeneration);
            _pendingScreenFramePayload = payload;
            _pendingScreenFrameGeneration = generation;

            if (!_screenDecodeWorkerRunning)
            {
                _screenDecodeWorkerRunning = true;
                startWorker = true;
            }
        }

        if (startWorker)
        {
            _ = Task.Run(ProcessLatestScreenFramesAsync);
        }
    }

    private async Task ProcessLatestScreenFramesAsync()
    {
        while (!IsDisposed)
        {
            byte[]? payload;
            long payloadGeneration;

            lock (_screenDecodeLock)
            {
                payload = _pendingScreenFramePayload;
                payloadGeneration = _pendingScreenFrameGeneration;
                _pendingScreenFramePayload = null;

                if (payload is null)
                {
                    _screenDecodeWorkerRunning = false;
                    return;
                }
            }

            try
            {
                DecodedScreenFrame decoded = ScreenFrameDecoder.Decode(payload);
                if (payloadGeneration == Interlocked.Read(ref _screenFrameGeneration))
                {
                    Interlocked.Increment(ref _screenFramesDisplayed);
                    UpdateScreenFrame(decoded.Image, decoded.Info, decoded.ByteSize);
                }
                else
                {
                    decoded.Image.Dispose();
                }
            }
            catch (Exception ex)
            {
                AddLog($"Screen frame decode failed: {ex.Message}");
            }

            await Task.Yield();
        }

        lock (_screenDecodeLock)
        {
            _screenDecodeWorkerRunning = false;
        }
    }    private void HandleScreenVideoStop(byte[] payload)
    {
        string reason = "stopped";
        try
        {
            ScreenVideoStopInfo? info = JsonSerializer.Deserialize<ScreenVideoStopInfo>(payload);
            if (info is not null && !string.IsNullOrWhiteSpace(info.Reason))
            {
                reason = info.Reason;
            }
        }
        catch
        {
        }

        _activeStreamId = null;
        ResetScreenMonitor($"Screen video stopped: {reason}");
        AddLog($"Screen video stopped: {reason}");
    }

    private void ResetScreenMonitor(string text)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ResetScreenMonitor(text)));
            return;
        }

        Interlocked.Increment(ref _screenFrameGeneration);
        lock (_screenDecodeLock)
        {
            _pendingScreenFramePayload = null;
            _pendingScreenFrameGeneration = Interlocked.Read(ref _screenFrameGeneration);
        }

        Image? old = _lastScreenImage;
        _lastScreenImage = null;
        _screenPicture.Image = null;
        if (_fullScreenPicture is not null)
        {
            _fullScreenPicture.Image = null;
        }
        CloseFullScreenMonitor();
        old?.Dispose();
        _screenInfoLabel.Text = text;
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
        long shownFrames = Interlocked.Read(ref _screenFramesDisplayed);
        long droppedBeforeDecode = Interlocked.Read(ref _screenFramesDroppedBeforeDecode);
        _screenInfoLabel.Text = $"Frame {info.FrameNo} / {info.Width}x{info.Height} / {FormatBytes(byteSize)} / {_activeStreamId ?? "single"} / shown {shownFrames} / decodeDrop {droppedBeforeDecode} / {DateTime.Now:HH:mm:ss}";

        if (_autoFullScreenCheck.Checked && _fullScreenForm is null)
        {
            OpenFullScreenMonitor();
        }
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
        var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);

        _fileSession = new FileReceiveSession
        {
            FileName = safeRelativePath,
            FinalPath = finalPath,
            TempPath = tempPath,
            FileSize = info.FileSize,
            OpenAfterReceive = info.OpenAfterReceive,
            OpenRequestId = info.OpenRequestId,
            Stream = stream,
        };

        if (!_batchActive)
        {
            _batchStopwatch.Restart();
        }

        UpdateProgress(_batchReceivedBytes, Math.Max(_batchExpectedBytes > 0 ? _batchExpectedBytes : info.FileSize, 1), $"Receiving: {safeRelativePath}");
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
            AddLog("Received file size exceeded expected size.");
            CloseCurrentFileSession(deleteTempFile: true);
            UpdateProgress(0, 1, "Receive failed");
            return;
        }

        long current = _batchActive ? _batchReceivedBytes + _fileSession.ReceivedBytes : _fileSession.ReceivedBytes;
        long total = _batchActive ? Math.Max(_batchExpectedBytes, 1) : Math.Max(_fileSession.FileSize, 1);
        double seconds = Math.Max(_batchStopwatch.Elapsed.TotalSeconds, 0.001);
        long speed = (long)(current / seconds);

        UpdateProgress(current, total, $"Receiving: {_fileSession.FileName} / {FormatBytes(current)} / {FormatBytes(total)} / {FormatBytes(speed)}/s");
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
        bool openAfterReceive = _fileSession.OpenAfterReceive;
        string openRequestId = _fileSession.OpenRequestId;

        try
        {
            _fileSession.Stream.Flush();
            _fileSession.Stream.Close();

            if (receivedBytes != fileSize)
            {
                AddLog($"File size mismatch: {fileName} / {FormatBytes(receivedBytes)} / {FormatBytes(fileSize)}");
                TryDelete(tempPath);
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

            if (!_batchActive)
            {
                _batchStopwatch.Stop();

                if (GetOpenFolderAfterReceive())
                {
                    OpenSaveFolder();
                }
            }

            if (openAfterReceive)
            {
                _ = OpenReceivedFileAndReportAsync(finalPath, fileName, openRequestId);
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

    private void DisconnectByUser()
    {
        _manualDisconnect = true;
        StopAutoReconnect();
        StopH264Player();
        ResetScreenMonitor("Disconnected");
        DisconnectSilent();
        SetConnectedUi(false, "Disconnected");
        AddLog("Disconnected by user.");
    }

    private void DisconnectSilent()
    {
        try { _cts?.Cancel(); } catch { }
        try { _client?.Close(); } catch { }
        try { _client?.Dispose(); } catch { }
        try { _cts?.Dispose(); } catch { }

        _client = null;
        _cts = null;
    }

    private void DisconnectFromWorker()
    {
        try { _client?.Close(); } catch { }
        try { _client?.Dispose(); } catch { }
        try { _cts?.Dispose(); } catch { }

        _client = null;
        _cts = null;
        _activeStreamId = null;

        if (!IsDisposed)
        {
            BeginInvoke(new Action(() =>
            {
                SetConnectedUi(false, "Disconnected");
                ScheduleAutoReconnectFromWorker();
            }));
        }
    }

    private void ChooseSaveFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select save folder.",
            UseDescriptionForTitle = true,
            SelectedPath = GetSaveFolder(),
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _saveFolderBox.Text = dialog.SelectedPath;
        }
    }

    private void OpenSaveFolder()
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(OpenSaveFolder));
            return;
        }

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
            AddLog($"Open save folder failed: {ex.Message}");
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
        try { _fullScreenForm?.Close(); } catch { }
        _fullScreenForm = null;
        _fullScreenPicture = null;
    }

    private string GetSaveFolder()
    {
        if (IsDisposed)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LanReceivedFiles");
        }

        if (InvokeRequired)
        {
            return (string)Invoke(new Func<string>(GetSaveFolder));
        }

        string folder = _saveFolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LanReceivedFiles");
            _saveFolderBox.Text = folder;
        }
        return folder;
    }

    private bool GetOpenFolderAfterReceive()
    {
        if (IsDisposed)
        {
            return false;
        }

        if (InvokeRequired)
        {
            return (bool)Invoke(new Func<bool>(GetOpenFolderAfterReceive));
        }

        return _openFolderAfterReceiveCheck.Checked;
    }
    private static string SanitizeRelativePath(string relativePath)
    {
        string[] parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var safeParts = new List<string>();

        foreach (string part in parts)
        {
            if (part is "." or "..")
            {
                continue;
            }

            string safe = string.Join("_", part.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (!string.IsNullOrWhiteSpace(safe))
            {
                safeParts.Add(safe);
            }
        }

        return safeParts.Count == 0 ? "received_file" : Path.Combine(safeParts.ToArray());
    }

    private static string CreateUniqueFilePath(string saveFolder, string relativePath)
    {
        string basePath = Path.GetFullPath(Path.Combine(saveFolder, relativePath));
        string fullSaveFolder = Path.GetFullPath(saveFolder);

        if (!basePath.StartsWith(fullSaveFolder, StringComparison.OrdinalIgnoreCase))
        {
            basePath = Path.Combine(fullSaveFolder, Path.GetFileName(relativePath));
        }

        if (!File.Exists(basePath))
        {
            return basePath;
        }

        string? directory = Path.GetDirectoryName(basePath);
        string name = Path.GetFileNameWithoutExtension(basePath);
        string extension = Path.GetExtension(basePath);

        for (int i = 1; i < 10000; i++)
        {
            string candidate = Path.Combine(directory ?? fullSaveFolder, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory ?? fullSaveFolder, $"{name}_{DateTime.Now:yyyyMMdd_HHmmssfff}{extension}");
    }

    private void CloseCurrentFileSession(bool deleteTempFile)
    {
        if (_fileSession is null)
        {
            return;
        }

        string tempPath = _fileSession.TempPath;
        try { _fileSession.Dispose(); } catch { }
        if (deleteTempFile)
        {
            TryDelete(tempPath);
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

    private void SetConnectedUi(bool connected, string status)
    {
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

    private void UpdateProgress(long current, long total, string text)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => UpdateProgress(current, total, text)));
            return;
        }

        total = Math.Max(total, 1);
        current = Math.Clamp(current, 0, total);
        _progressBar.Value = (int)Math.Clamp(current * 1000 / total, 0, 1000);
        _progressLabel.Text = text;
    }

    private void UpdateScreenInfo(string text)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => UpdateScreenInfo(text)));
            return;
        }

        _screenInfoLabel.Text = text;
    }

    private void AddMessage(string message)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => AddMessage(message)));
            return;
        }

        _messageList.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private void AddLog(string message)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => AddLog(message)));
            return;
        }

        _logList.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
