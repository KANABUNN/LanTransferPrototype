#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Utf8Bom = New-Object System.Text.UTF8Encoding($true)
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$SenderPath = Join-Path $Root "LanSender\Program.cs"
$ReceiverPath = Join-Path $Root "LanReceiver\Program.cs"

function Write-WithBackup([string]$Path, [string]$Content) {
    if (-not (Test-Path $Path)) {
        throw "File not found: $Path"
    }

    $Stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $BackupPath = "$Path.bak_$Stamp"
    Copy-Item -LiteralPath $Path -Destination $BackupPath -Force
    [System.IO.File]::WriteAllText($Path, $Content, $Utf8Bom)
    Write-Host "Updated: $Path"
    Write-Host "Backup : $BackupPath"
}

$SenderSource = @'
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

public sealed class SenderForm : Form
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
        try
        {
            NetworkStream stream = connection.Client.GetStream();
            byte[] buffer = new byte[1];

            while (!token.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, 1), token);

                if (read == 0)
                {
                    AddLog($"Client disconnected: {connection.DisplayName}");
                    break;
                }

                AddLog($"Unexpected inbound data was ignored: {connection.DisplayName}");
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
            AddLog($"Client communication ended: {connection.DisplayName}");
        }
        catch (SocketException)
        {
            AddLog($"Client communication ended: {connection.DisplayName}");
        }
        catch (Exception ex)
        {
            AddLog($"Client monitor error: {connection.DisplayName} / {ex.Message}");
        }
        finally
        {
            RemoveClient(connection);
        }
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

'@

$ReceiverSource = @'
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

public sealed class ReceiverForm : Form
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

    public ReceiverForm()
    {
        Text = "LAN Receiver - Step 6 Screen Monitor";
        Width = 1080;
        Height = 820;
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();

        _connectButton.Click += async (_, _) => await ConnectAsync();
        _disconnectButton.Click += (_, _) => DisconnectByUser();
        _clearButton.Click += (_, _) => _messageList.Items.Clear();
        _chooseFolderButton.Click += (_, _) => ChooseSaveFolder();
        _openFolderButton.Click += (_, _) => OpenSaveFolder();
        _openFullScreenButton.Click += (_, _) => OpenFullScreenMonitor();

        FormClosing += (_, _) =>
        {
            CloseFullScreenMonitor();
            DisconnectSilent();
            _lastScreenImage?.Dispose();
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
            TempPath = tempPath,
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

public sealed class FileReceiveSession : IDisposable
{
    public string FileName { get; init; } = "";

    public long FileSize { get; init; }

    public long ReceivedBytes { get; set; }

    public string FinalPath { get; init; } = "";

    public string TempPath { get; init; } = "";

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

'@

Write-WithBackup -Path $SenderPath -Content $SenderSource
Write-WithBackup -Path $ReceiverPath -Content $ReceiverSource

Write-Host "Step 6 screen monitor patch applied."
Write-Host "Run: dotnet build"
