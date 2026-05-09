using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LanSender;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new SenderForm());
    }
}

public sealed partial class SenderForm : Form
{
    private const int FileChunkSize = 64 * 1024;

    private readonly TextBox _portBox = new();
    private readonly TextBox _messageBox = new();

    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly Button _sendAllButton = new();
    private readonly Button _sendSelectedButton = new();

    private readonly Button _addFilesButton = new();
    private readonly Button _addFolderButton = new();
    private readonly Button _clearFilesButton = new();
    private readonly Button _sendFileAllButton = new();
    private readonly Button _sendFileSelectedButton = new();
    private readonly Button _cancelTransferButton = new();

    private readonly Button _sendScreenOnceAllButton = new();
    private readonly Button _sendScreenOnceSelectedButton = new();
    private readonly Button _startScreenAllButton = new();
    private readonly Button _startScreenSelectedButton = new();
    private readonly Button _stopScreenButton = new();
    private readonly NumericUpDown _screenIntervalBox = new();
    private readonly NumericUpDown _screenQualityBox = new();
    private readonly Label _screenStatusLabel = new();

    private readonly ProgressBar _progressBar = new();
    private readonly Label _progressLabel = new();

    private readonly ListBox _clientList = new();
    private readonly ListBox _fileList = new();
    private readonly ListBox _logList = new();

    private readonly List<ClientConnection> _clients = new();
    private readonly List<TransferItem> _transferItems = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _serverCts;
    private CancellationTokenSource? _transferCts;
    private CancellationTokenSource? _screenStreamCts;

    private Task? _acceptTask;
    private bool _isSendingFile;
    private bool _isStreamingScreen;
    private long _screenFrameNo;

    public SenderForm()
    {
        Text = "LAN Sender - Step 6 Screen Monitor";
        Width = 1160;
        Height = 820;
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();
        InitializeOpenTargetFeature();

        _startButton.Click += (_, _) => StartServer();
        _stopButton.Click += (_, _) => StopServer();

        _sendAllButton.Click += async (_, _) => await SendMessageToAllAsync();
        _sendSelectedButton.Click += async (_, _) => await SendMessageToSelectedAsync();

        _addFilesButton.Click += (_, _) => AddFiles();
        _addFolderButton.Click += (_, _) => AddFolder();
        _clearFilesButton.Click += (_, _) => ClearTransferItems();

        _sendFileAllButton.Click += async (_, _) => await SendFilesToAllAsync();
        _sendFileSelectedButton.Click += async (_, _) => await SendFilesToSelectedAsync();
        _cancelTransferButton.Click += (_, _) => CancelTransfer();

        _sendScreenOnceAllButton.Click += async (_, _) => await SendScreenOnceToAllAsync();
        _sendScreenOnceSelectedButton.Click += async (_, _) => await SendScreenOnceToSelectedAsync();
        _startScreenAllButton.Click += async (_, _) => await StartScreenStreamAsync(selectedOnly: false);
        _startScreenSelectedButton.Click += async (_, _) => await StartScreenStreamAsync(selectedOnly: true);
        _stopScreenButton.Click += (_, _) => StopScreenStream();

        FormClosing += (_, _) => StopServer();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 6,
            ColumnCount = 2,
            Padding = new Padding(12),
        };

        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));

        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 155));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 65));

        var serverPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        serverPanel.Controls.Add(new Label
        {
            Text = "Port:",
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
        });

        _portBox.Text = "50000";
        _portBox.Width = 100;
        serverPanel.Controls.Add(_portBox);

        _startButton.Text = "Start";
        _startButton.Width = 90;
        serverPanel.Controls.Add(_startButton);

        _stopButton.Text = "Stop";
        _stopButton.Width = 90;
        _stopButton.Enabled = false;
        serverPanel.Controls.Add(_stopButton);

        root.Controls.Add(serverPanel, 0, 0);
        root.SetColumnSpan(serverPanel, 2);

        var clientGroup = new GroupBox
        {
            Text = "Connected clients",
            Dock = DockStyle.Fill,
        };

        _clientList.Dock = DockStyle.Fill;
        clientGroup.Controls.Add(_clientList);

        root.Controls.Add(clientGroup, 0, 1);
        root.SetRowSpan(clientGroup, 5);

        var messageGroup = new GroupBox
        {
            Text = "Text message",
            Dock = DockStyle.Fill,
        };

        _messageBox.Multiline = true;
        _messageBox.Dock = DockStyle.Fill;
        _messageBox.ScrollBars = ScrollBars.Vertical;
        _messageBox.Text = "Test message";

        messageGroup.Controls.Add(_messageBox);
        root.Controls.Add(messageGroup, 1, 1);

        var sendPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        _sendAllButton.Text = "Send to all";
        _sendAllButton.Width = 130;
        _sendAllButton.Enabled = false;
        sendPanel.Controls.Add(_sendAllButton);

        _sendSelectedButton.Text = "Send selected";
        _sendSelectedButton.Width = 130;
        _sendSelectedButton.Enabled = false;
        sendPanel.Controls.Add(_sendSelectedButton);

        root.Controls.Add(sendPanel, 1, 2);

        var fileGroup = new GroupBox
        {
            Text = "File / folder transfer",
            Dock = DockStyle.Fill,
        };

        var fileLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 5,
            ColumnCount = 1,
            Padding = new Padding(8),
        };

        fileLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        fileLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        fileLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        fileLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        fileLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

        var fileAddPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        _addFilesButton.Text = "Add files";
        _addFilesButton.Width = 120;
        fileAddPanel.Controls.Add(_addFilesButton);

        _addFolderButton.Text = "Add folder";
        _addFolderButton.Width = 120;
        fileAddPanel.Controls.Add(_addFolderButton);

        _clearFilesButton.Text = "Clear list";
        _clearFilesButton.Width = 120;
        fileAddPanel.Controls.Add(_clearFilesButton);

        fileLayout.Controls.Add(fileAddPanel, 0, 0);

        _fileList.Dock = DockStyle.Fill;
        fileLayout.Controls.Add(_fileList, 0, 1);

        var fileSendPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        _sendFileAllButton.Text = "Send files to all";
        _sendFileAllButton.Width = 140;
        _sendFileAllButton.Enabled = false;
        fileSendPanel.Controls.Add(_sendFileAllButton);

        _sendFileSelectedButton.Text = "Send files selected";
        _sendFileSelectedButton.Width = 155;
        _sendFileSelectedButton.Enabled = false;
        fileSendPanel.Controls.Add(_sendFileSelectedButton);

        _cancelTransferButton.Text = "Cancel";
        _cancelTransferButton.Width = 100;
        _cancelTransferButton.Enabled = false;
        fileSendPanel.Controls.Add(_cancelTransferButton);

        fileLayout.Controls.Add(fileSendPanel, 0, 2);

        _progressBar.Dock = DockStyle.Fill;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 1000;
        fileLayout.Controls.Add(_progressBar, 0, 3);

        _progressLabel.Text = "Idle";
        _progressLabel.Dock = DockStyle.Fill;
        _progressLabel.TextAlign = ContentAlignment.MiddleLeft;
        fileLayout.Controls.Add(_progressLabel, 0, 4);

        fileGroup.Controls.Add(fileLayout);
        root.Controls.Add(fileGroup, 1, 3);

        var screenGroup = new GroupBox
        {
            Text = "Screen monitor",
            Dock = DockStyle.Fill,
        };

        var screenLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            Padding = new Padding(8),
        };

        screenLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        screenLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        screenLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        screenLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var screenOptionsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        screenOptionsPanel.Controls.Add(new Label
        {
            Text = "Interval(ms):",
            AutoSize = true,
            Padding = new Padding(0, 6, 0, 0),
        });

        _screenIntervalBox.Minimum = 100;
        _screenIntervalBox.Maximum = 5000;
        _screenIntervalBox.Increment = 100;
        _screenIntervalBox.Value = 500;
        _screenIntervalBox.Width = 80;
        screenOptionsPanel.Controls.Add(_screenIntervalBox);

        screenOptionsPanel.Controls.Add(new Label
        {
            Text = "JPEG quality:",
            AutoSize = true,
            Padding = new Padding(12, 6, 0, 0),
        });

        _screenQualityBox.Minimum = 20;
        _screenQualityBox.Maximum = 95;
        _screenQualityBox.Increment = 5;
        _screenQualityBox.Value = 55;
        _screenQualityBox.Width = 70;
        screenOptionsPanel.Controls.Add(_screenQualityBox);

        screenLayout.Controls.Add(screenOptionsPanel, 0, 0);

        var screenOncePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        _sendScreenOnceAllButton.Text = "Send 1 frame to all";
        _sendScreenOnceAllButton.Width = 155;
        _sendScreenOnceAllButton.Enabled = false;
        screenOncePanel.Controls.Add(_sendScreenOnceAllButton);

        _sendScreenOnceSelectedButton.Text = "Send 1 frame selected";
        _sendScreenOnceSelectedButton.Width = 170;
        _sendScreenOnceSelectedButton.Enabled = false;
        screenOncePanel.Controls.Add(_sendScreenOnceSelectedButton);

        screenLayout.Controls.Add(screenOncePanel, 0, 1);

        var screenStreamPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        _startScreenAllButton.Text = "Start monitor all";
        _startScreenAllButton.Width = 150;
        _startScreenAllButton.Enabled = false;
        screenStreamPanel.Controls.Add(_startScreenAllButton);

        _startScreenSelectedButton.Text = "Start monitor selected";
        _startScreenSelectedButton.Width = 175;
        _startScreenSelectedButton.Enabled = false;
        screenStreamPanel.Controls.Add(_startScreenSelectedButton);

        _stopScreenButton.Text = "Stop monitor";
        _stopScreenButton.Width = 130;
        _stopScreenButton.Enabled = false;
        screenStreamPanel.Controls.Add(_stopScreenButton);

        screenLayout.Controls.Add(screenStreamPanel, 0, 2);

        _screenStatusLabel.Text = "Screen monitor idle";
        _screenStatusLabel.Dock = DockStyle.Fill;
        _screenStatusLabel.TextAlign = ContentAlignment.MiddleLeft;
        screenLayout.Controls.Add(_screenStatusLabel, 0, 3);

        screenGroup.Controls.Add(screenLayout);
        root.Controls.Add(screenGroup, 1, 4);

        var logGroup = new GroupBox
        {
            Text = "Log",
            Dock = DockStyle.Fill,
        };

        _logList.Dock = DockStyle.Fill;
        logGroup.Controls.Add(_logList);

        root.Controls.Add(logGroup, 1, 5);

        Controls.Add(root);
    }

    private void StartServer()
    {
        if (_serverCts is not null)
        {
            AddLog("Server is already running.");
            return;
        }

        if (!int.TryParse(_portBox.Text, out int port) || port < 1 || port > 65535)
        {
            AddLog("Invalid port number.");
            return;
        }

        try
        {
            _serverCts = new CancellationTokenSource();

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();

            _acceptTask = Task.Run(() => AcceptLoopAsync(_serverCts.Token));

            _startButton.Enabled = false;
            _stopButton.Enabled = true;
            SetSendButtonsEnabled(true);

            AddLog($"Listening on 0.0.0.0:{port}");
        }
        catch (Exception ex)
        {
            AddLog($"Failed to start server: {ex.Message}");
            StopServer();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        if (_listener is null) return;

        while (!token.IsCancellationRequested)
        {
            try
            {
                TcpClient tcpClient = await _listener.AcceptTcpClientAsync(token);
                tcpClient.NoDelay = true;

                var connection = new ClientConnection(tcpClient);

                lock (_clients)
                {
                    _clients.Add(connection);
                }

                AddClientToList(connection);
                AddLog($"Client connected: {connection.DisplayName}");

                _ = Task.Run(() => MonitorClientAsync(connection, token));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (Exception ex)
            {
                AddLog($"Accept error: {ex.Message}");
            }
        }
    }

    private async Task MonitorClientAsync(ClientConnection connection, CancellationToken token)
    {
        await MonitorClientPacketsAsync(connection, token);
    }

    private async Task SendMessageToAllAsync()
    {
        string text = _messageBox.Text;

        if (string.IsNullOrWhiteSpace(text))
        {
            AddLog("Message is empty.");
            return;
        }

        List<ClientConnection> snapshot = GetClientSnapshot();

        if (snapshot.Count == 0)
        {
            AddLog("No connected clients.");
            return;
        }

        int successCount = 0;

        foreach (var connection in snapshot)
        {
            bool ok = await SendMessageToClientAsync(connection, text);

            if (ok)
            {
                successCount++;
            }
        }

        AddLog($"Text sent: {successCount}/{snapshot.Count}");
    }

    private async Task SendMessageToSelectedAsync()
    {
        if (_clientList.SelectedItem is not ClientConnection connection)
        {
            AddLog("Select a client first.");
            return;
        }

        string text = _messageBox.Text;

        if (string.IsNullOrWhiteSpace(text))
        {
            AddLog("Message is empty.");
            return;
        }

        bool ok = await SendMessageToClientAsync(connection, text);

        if (ok)
        {
            AddLog($"Text sent to selected client: {connection.DisplayName}");
        }
    }

    private async Task<bool> SendMessageToClientAsync(ClientConnection connection, string text)
    {
        try
        {
            byte[] payload = Encoding.UTF8.GetBytes(text);

            await connection.WriteLock.WaitAsync();

            try
            {
                NetworkStream stream = connection.Client.GetStream();
                await NetPacket.WriteAsync(stream, PacketType.TextMessage, payload, CancellationToken.None);
            }
            finally
            {
                connection.WriteLock.Release();
            }

            return true;
        }
        catch (Exception ex)
        {
            AddLog($"Text send failed: {connection.DisplayName} / {ex.Message}");
            RemoveClient(connection);
            return false;
        }
    }

    private void AddFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select files to send",
            CheckFileExists = true,
            Multiselect = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        int added = 0;

        foreach (string path in dialog.FileNames)
        {
            if (TryAddTransferItem(path, Path.GetFileName(path)))
            {
                added++;
            }
        }

        AddLog($"Files added: {added}");
    }

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select a folder to send",
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        string rootFolder = dialog.SelectedPath;
        string rootName = Path.GetFileName(rootFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        int added = 0;

        foreach (string path in Directory.EnumerateFiles(rootFolder, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(rootFolder, path);
            string sendPath = Path.Combine(rootName, relative);

            if (TryAddTransferItem(path, sendPath))
            {
                added++;
            }
        }

        AddLog($"Folder added: {rootFolder} / {added}");
    }

    private bool TryAddTransferItem(string fullPath, string relativePath)
    {
        if (!File.Exists(fullPath))
        {
            return false;
        }

        string normalizedFullPath = Path.GetFullPath(fullPath);

        lock (_transferItems)
        {
            if (_transferItems.Any(x => string.Equals(x.FullPath, normalizedFullPath, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var info = new FileInfo(normalizedFullPath);

            var item = new TransferItem
            {
                FullPath = normalizedFullPath,
                RelativePath = relativePath,
                Size = info.Length,
            };

            _transferItems.Add(item);
            _fileList.Items.Add(item);

            return true;
        }
    }

    private void ClearTransferItems()
    {
        if (_isSendingFile)
        {
            AddLog("Cannot clear the list while transferring files.");
            return;
        }

        lock (_transferItems)
        {
            _transferItems.Clear();
        }

        _fileList.Items.Clear();
        UpdateProgress(0, 1, "Idle");
        AddLog("Transfer list cleared.");
    }

    private async Task SendFilesToAllAsync()
    {
        await SendFilesToTargetsAsync(GetClientSnapshot());
    }

    private async Task SendFilesToSelectedAsync()
    {
        if (_clientList.SelectedItem is not ClientConnection connection)
        {
            AddLog("Select a client first.");
            return;
        }

        await SendFilesToTargetsAsync(new List<ClientConnection> { connection });
    }

    private async Task SendFilesToTargetsAsync(List<ClientConnection> targets)
    {
        if (_isSendingFile)
        {
            AddLog("File transfer is already running.");
            return;
        }

        if (_isStreamingScreen)
        {
            AddLog("Stop screen monitor before file transfer.");
            return;
        }

        List<TransferItem> items;

        lock (_transferItems)
        {
            items = _transferItems.ToList();
        }

        if (items.Count == 0)
        {
            AddLog("Add files first.");
            return;
        }

        if (targets.Count == 0)
        {
            AddLog("No connected clients.");
            return;
        }

        items = items.Where(x => File.Exists(x.FullPath)).ToList();

        if (items.Count == 0)
        {
            AddLog("No existing files to send.");
            return;
        }

        long oneClientBytes = items.Sum(x => x.Size);
        long totalBytes = oneClientBytes * targets.Count;

        _isSendingFile = true;
        _transferCts = new CancellationTokenSource();

        SetSendButtonsEnabled(false);
        _cancelTransferButton.Enabled = true;

        long totalSentBytes = 0;
        int successClients = 0;
        int canceledClients = 0;
        int failedClients = 0;

        var stopwatch = Stopwatch.StartNew();

        UpdateProgress(0, Math.Max(totalBytes, 1), "Preparing file transfer");

        try
        {
            foreach (var connection in targets)
            {
                if (_transferCts.IsCancellationRequested)
                {
                    break;
                }

                TransferResult result = await SendBatchToClientAsync(
                    connection,
                    items,
                    _transferCts.Token,
                    sentBytes =>
                    {
                        totalSentBytes += sentBytes;

                        double seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
                        long speed = (long)(totalSentBytes / seconds);

                        UpdateProgress(
                            totalSentBytes,
                            Math.Max(totalBytes, 1),
                            $"Sending: {FormatBytes(totalSentBytes)} / {FormatBytes(totalBytes)} / {FormatBytes(speed)}/s");
                    });

                switch (result)
                {
                    case TransferResult.Success:
                        successClients++;
                        break;

                    case TransferResult.Canceled:
                        canceledClients++;
                        break;

                    case TransferResult.Failed:
                        failedClients++;
                        break;
                }
            }

            if (_transferCts.IsCancellationRequested)
            {
                UpdateProgress(totalSentBytes, Math.Max(totalBytes, 1), "File transfer canceled");
                AddLog($"File transfer canceled: success {successClients}, failed {failedClients}");
            }
            else
            {
                UpdateProgress(1000, 1000, $"File transfer done: success {successClients}, failed {failedClients}");
                AddLog($"File transfer done: success {successClients}, failed {failedClients}, canceled {canceledClients}");
            }
        }
        finally
        {
            stopwatch.Stop();

            _transferCts.Dispose();
            _transferCts = null;
            _isSendingFile = false;

            SetSendButtonsEnabled(_serverCts is not null);
            _cancelTransferButton.Enabled = false;
        }
    }

    private async Task<TransferResult> SendBatchToClientAsync(
        ClientConnection connection,
        List<TransferItem> items,
        CancellationToken token,
        Action<int> onChunkSent)
    {
        bool lockTaken = false;

        try
        {
            await connection.WriteLock.WaitAsync(token);
            lockTaken = true;

            NetworkStream stream = connection.Client.GetStream();

            long totalSize = items.Sum(x => x.Size);

            var batchInfo = new BatchStartInfo
            {
                FileCount = items.Count,
                TotalSize = totalSize,
            };

            await NetPacket.WriteAsync(
                stream,
                PacketType.BatchStart,
                JsonSerializer.SerializeToUtf8Bytes(batchInfo),
                token);

            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();

                var startInfo = new FileStartInfo
                {
                    FileName = item.RelativePath,
                    FileSize = item.Size,
                };

                await NetPacket.WriteAsync(
                    stream,
                    PacketType.FileStart,
                    JsonSerializer.SerializeToUtf8Bytes(startInfo),
                    token);

                byte[] buffer = new byte[FileChunkSize];

                await using var fileStream = new FileStream(
                    item.FullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: FileChunkSize,
                    useAsync: true);

                while (true)
                {
                    token.ThrowIfCancellationRequested();

                    int read = await fileStream.ReadAsync(buffer.AsMemory(0, buffer.Length), token);

                    if (read == 0)
                    {
                        break;
                    }

                    byte[] chunk = buffer.AsSpan(0, read).ToArray();

                    await NetPacket.WriteAsync(stream, PacketType.FileChunk, chunk, token);
                    onChunkSent(read);
                }

                await NetPacket.WriteAsync(stream, PacketType.FileEnd, Array.Empty<byte>(), token);
            }

            await NetPacket.WriteAsync(stream, PacketType.BatchEnd, Array.Empty<byte>(), token);

            AddLog($"Files sent: {connection.DisplayName} / {items.Count}");
            return TransferResult.Success;
        }
        catch (OperationCanceledException)
        {
            AddLog($"File transfer canceled: {connection.DisplayName}");

            try
            {
                if (lockTaken)
                {
                    NetworkStream stream = connection.Client.GetStream();

                    var cancelInfo = new TransferCancelInfo
                    {
                        Reason = "Canceled by sender.",
                    };

                    await NetPacket.WriteAsync(
                        stream,
                        PacketType.TransferCancel,
                        JsonSerializer.SerializeToUtf8Bytes(cancelInfo),
                        CancellationToken.None);
                }
            }
            catch
            {
            }

            return TransferResult.Canceled;
        }
        catch (Exception ex)
        {
            AddLog($"File transfer failed: {connection.DisplayName} / {ex.Message}");
            RemoveClient(connection);
            return TransferResult.Failed;
        }
        finally
        {
            if (lockTaken)
            {
                try
                {
                    connection.WriteLock.Release();
                }
                catch
                {
                }
            }
        }
    }

    private void CancelTransfer()
    {
        if (!_isSendingFile || _transferCts is null)
        {
            return;
        }

        _transferCts.Cancel();
        AddLog("File transfer cancel requested.");
    }

    private async Task SendScreenOnceToAllAsync()
    {
        await SendScreenFrameToTargetsAsync(GetClientSnapshot(), CancellationToken.None, logResult: true);
    }

    private async Task SendScreenOnceToSelectedAsync()
    {
        if (_clientList.SelectedItem is not ClientConnection connection)
        {
            AddLog("Select a client first.");
            return;
        }

        await SendScreenFrameToTargetsAsync(new List<ClientConnection> { connection }, CancellationToken.None, logResult: true);
    }

    private async Task StartScreenStreamAsync(bool selectedOnly)
    {
        if (_isStreamingScreen)
        {
            AddLog("Screen monitor is already running.");
            return;
        }

        if (_isSendingFile)
        {
            AddLog("Wait until file transfer is done.");
            return;
        }

        List<ClientConnection> fixedTargets;

        if (selectedOnly)
        {
            if (_clientList.SelectedItem is not ClientConnection connection)
            {
                AddLog("Select a client first.");
                return;
            }

            fixedTargets = new List<ClientConnection> { connection };
        }
        else
        {
            fixedTargets = new List<ClientConnection>();
        }

        if (!selectedOnly && GetClientSnapshot().Count == 0)
        {
            AddLog("No connected clients.");
            return;
        }

        _screenStreamCts = new CancellationTokenSource();
        _isStreamingScreen = true;
        SetSendButtonsEnabled(_serverCts is not null);
        UpdateScreenStatus("Screen monitor starting");

        bool useFixedTargets = selectedOnly;
        CancellationToken token = _screenStreamCts.Token;

        _ = Task.Run(() => ScreenStreamLoopAsync(useFixedTargets, fixedTargets, token));

        await Task.CompletedTask;
    }

    private async Task ScreenStreamLoopAsync(bool useFixedTargets, List<ClientConnection> fixedTargets, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        long sentFrames = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                int intervalMs = GetScreenIntervalMs();
                var loopStart = Stopwatch.StartNew();

                List<ClientConnection> targets = useFixedTargets ? fixedTargets.ToList() : GetClientSnapshot();

                if (targets.Count == 0)
                {
                    UpdateScreenStatus("Screen monitor stopped: no clients");
                    break;
                }

                ScreenCaptureData capture = CaptureVirtualScreenJpeg(GetScreenQuality());

                int success = 0;

                foreach (ClientConnection connection in targets)
                {
                    token.ThrowIfCancellationRequested();

                    bool ok = await SendScreenFrameToClientAsync(connection, capture, token);

                    if (ok)
                    {
                        success++;
                    }
                }

                sentFrames++;
                double fps = sentFrames / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);

                UpdateScreenStatus(
                    $"Monitoring: frame {capture.FrameNo}, {capture.Width}x{capture.Height}, {FormatBytes(capture.ImageBytes.Length)}, clients {success}/{targets.Count}, {fps:0.0} fps");

                int delayMs = Math.Max(1, intervalMs - (int)loopStart.ElapsedMilliseconds);
                await Task.Delay(delayMs, token);
            }
        }
        catch (OperationCanceledException)
        {
            UpdateScreenStatus("Screen monitor stopped");
        }
        catch (Exception ex)
        {
            AddLog($"Screen monitor error: {ex.Message}");
            UpdateScreenStatus("Screen monitor error");
        }
        finally
        {
            stopwatch.Stop();

            if (_screenStreamCts is not null)
            {
                try
                {
                    _screenStreamCts.Dispose();
                }
                catch
                {
                }
            }

            _screenStreamCts = null;
            _isStreamingScreen = false;
            SetSendButtonsEnabled(_serverCts is not null);
        }
    }

    private void StopScreenStream()
    {
        if (!_isStreamingScreen || _screenStreamCts is null)
        {
            return;
        }

        _screenStreamCts.Cancel();
        AddLog("Screen monitor stop requested.");
    }

    private async Task SendScreenFrameToTargetsAsync(List<ClientConnection> targets, CancellationToken token, bool logResult)
    {
        if (targets.Count == 0)
        {
            AddLog("No connected clients.");
            return;
        }

        ScreenCaptureData capture = CaptureVirtualScreenJpeg(GetScreenQuality());
        int success = 0;

        foreach (ClientConnection connection in targets)
        {
            bool ok = await SendScreenFrameToClientAsync(connection, capture, token);

            if (ok)
            {
                success++;
            }
        }

        UpdateScreenStatus($"Screen frame sent: {success}/{targets.Count}, {capture.Width}x{capture.Height}, {FormatBytes(capture.ImageBytes.Length)}");

        if (logResult)
        {
            AddLog($"Screen frame sent: {success}/{targets.Count}");
        }
    }

    private async Task<bool> SendScreenFrameToClientAsync(ClientConnection connection, ScreenCaptureData capture, CancellationToken token)
    {
        bool lockTaken = false;

        try
        {
            byte[] payload = BuildScreenFramePayload(capture);

            await connection.WriteLock.WaitAsync(token);
            lockTaken = true;

            NetworkStream stream = connection.Client.GetStream();
            await NetPacket.WriteAsync(stream, PacketType.ScreenFrame, payload, token);

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AddLog($"Screen frame send failed: {connection.DisplayName} / {ex.Message}");
            RemoveClient(connection);
            return false;
        }
        finally
        {
            if (lockTaken)
            {
                try
                {
                    connection.WriteLock.Release();
                }
                catch
                {
                }
            }
        }
    }

    private ScreenCaptureData CaptureVirtualScreenJpeg(int quality)
    {
        Rectangle bounds = SystemInformation.VirtualScreen;

        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        using var memoryStream = new MemoryStream();
        SaveJpeg(bitmap, memoryStream, quality);

        return new ScreenCaptureData
        {
            FrameNo = Interlocked.Increment(ref _screenFrameNo),
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Width = bounds.Width,
            Height = bounds.Height,
            ImageBytes = memoryStream.ToArray(),
        };
    }

    private static void SaveJpeg(Image image, Stream stream, int quality)
    {
        ImageCodecInfo? encoder = ImageCodecInfo.GetImageDecoders()
            .FirstOrDefault(x => string.Equals(x.FormatID.ToString(), ImageFormat.Jpeg.Guid.ToString(), StringComparison.OrdinalIgnoreCase));

        if (encoder is null)
        {
            image.Save(stream, ImageFormat.Jpeg);
            return;
        }

        using var encoderParameters = new EncoderParameters(1);
        encoderParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        image.Save(stream, encoder, encoderParameters);
    }

    private static byte[] BuildScreenFramePayload(ScreenCaptureData capture)
    {
        var info = new ScreenFrameInfo
        {
            Width = capture.Width,
            Height = capture.Height,
            Format = "jpeg",
            FrameNo = capture.FrameNo,
            CapturedAtUtc = capture.CapturedAtUtc,
        };

        byte[] infoBytes = JsonSerializer.SerializeToUtf8Bytes(info);
        byte[] payload = new byte[4 + infoBytes.Length + capture.ImageBytes.Length];

        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), infoBytes.Length);
        infoBytes.CopyTo(payload.AsSpan(4, infoBytes.Length));
        capture.ImageBytes.CopyTo(payload.AsSpan(4 + infoBytes.Length, capture.ImageBytes.Length));

        return payload;
    }

    private int GetScreenIntervalMs()
    {
        if (InvokeRequired)
        {
            return (int)Invoke(new Func<int>(GetScreenIntervalMs));
        }

        return (int)_screenIntervalBox.Value;
    }

    private int GetScreenQuality()
    {
        if (InvokeRequired)
        {
            return (int)Invoke(new Func<int>(GetScreenQuality));
        }

        return (int)_screenQualityBox.Value;
    }

    private void StopServer()
    {
        try
        {
            _screenStreamCts?.Cancel();
            _transferCts?.Cancel();
            _serverCts?.Cancel();

            try
            {
                _listener?.Stop();
            }
            catch
            {
            }

            List<ClientConnection> snapshot;

            lock (_clients)
            {
                snapshot = _clients.ToList();
                _clients.Clear();
            }

            foreach (var connection in snapshot)
            {
                connection.Dispose();
            }

            _serverCts?.Dispose();
        }
        finally
        {
            _serverCts = null;
            _listener = null;
            _acceptTask = null;
            _isSendingFile = false;
            _isStreamingScreen = false;

            if (!IsDisposed)
            {
                _startButton.Enabled = true;
                _stopButton.Enabled = false;
                SetSendButtonsEnabled(false);

                _clientList.Items.Clear();
                UpdateScreenStatus("Screen monitor idle");

                AddLog("Server stopped.");
            }
        }
    }

    private void SetSendButtonsEnabled(bool enabled)
    {
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetSendButtonsEnabled(enabled)));
            return;
        }

        bool messageActual = enabled && !_isSendingFile;
        bool fileActual = enabled && !_isSendingFile && !_isStreamingScreen;
        bool screenActual = enabled && !_isSendingFile && !_isStreamingScreen;

        _sendAllButton.Enabled = messageActual;
        _sendSelectedButton.Enabled = messageActual;

        _sendFileAllButton.Enabled = fileActual;
        _sendFileSelectedButton.Enabled = fileActual;
        _addFilesButton.Enabled = !_isSendingFile && !_isStreamingScreen;
        _addFolderButton.Enabled = !_isSendingFile && !_isStreamingScreen;
        _clearFilesButton.Enabled = !_isSendingFile && !_isStreamingScreen;
        _cancelTransferButton.Enabled = _isSendingFile;

        _sendScreenOnceAllButton.Enabled = screenActual;
        _sendScreenOnceSelectedButton.Enabled = screenActual;
        _startScreenAllButton.Enabled = screenActual;
        _startScreenSelectedButton.Enabled = screenActual;
        _stopScreenButton.Enabled = _isStreamingScreen;
        _screenIntervalBox.Enabled = !_isStreamingScreen;
        _screenQualityBox.Enabled = !_isStreamingScreen;
    }

    private List<ClientConnection> GetClientSnapshot()
    {
        lock (_clients)
        {
            return _clients.ToList();
        }
    }

    private void RemoveClient(ClientConnection connection)
    {
        bool removed;

        lock (_clients)
        {
            removed = _clients.Remove(connection);
        }

        if (!removed)
        {
            return;
        }

        connection.Dispose();
        RemoveClientFromList(connection);
    }

    private void AddClientToList(ClientConnection connection)
    {
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => AddClientToList(connection)));
            return;
        }

        _clientList.Items.Add(connection);
    }

    private void RemoveClientFromList(ClientConnection connection)
    {
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => RemoveClientFromList(connection)));
            return;
        }

        _clientList.Items.Remove(connection);
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

    private void UpdateScreenStatus(string message)
    {
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => UpdateScreenStatus(message)));
            return;
        }

        _screenStatusLabel.Text = message;
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

public sealed class ClientConnection : IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();

    public TcpClient Client { get; }

    public DateTime ConnectedAt { get; } = DateTime.Now;

    public SemaphoreSlim WriteLock { get; } = new(1, 1);

    public string DisplayName { get; }

    public ClientConnection(TcpClient client)
    {
        Client = client;

        string endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        DisplayName = $"{endpoint} / {ConnectedAt:HH:mm:ss}";
    }

    public override string ToString()
    {
        return DisplayName;
    }

    public void Dispose()
    {
        try
        {
            Client.Close();
        }
        catch
        {
        }

        WriteLock.Dispose();
    }
}

public sealed class TransferItem
{
    public required string FullPath { get; init; }

    public required string RelativePath { get; init; }

    public required long Size { get; init; }

    public override string ToString()
    {
        return $"{RelativePath} ({FormatBytes(Size)})";
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

public sealed class ScreenCaptureData
{
    public required long FrameNo { get; init; }

    public required DateTimeOffset CapturedAtUtc { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required byte[] ImageBytes { get; init; }
}

public enum TransferResult
{
    Success,
    Canceled,
    Failed,
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

public static class NetPacket
{
    private const int HeaderSize = 5;
    private const int MaxPayloadSize = 32 * 1024 * 1024;

    public static async Task WriteAsync(
        NetworkStream stream,
        byte packetType,
        byte[] payload,
        CancellationToken token)
    {
        if (payload.Length > MaxPayloadSize)
        {
            throw new InvalidOperationException("Payload is too large.");
        }

        byte[] header = new byte[HeaderSize];

        header[0] = packetType;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1, 4), payload.Length);

        await stream.WriteAsync(header.AsMemory(0, header.Length), token);
        await stream.WriteAsync(payload.AsMemory(0, payload.Length), token);
        await stream.FlushAsync(token);
    }
}
