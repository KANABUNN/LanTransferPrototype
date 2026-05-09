using System.Buffers.Binary;
using System.Diagnostics;
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

    private Task? _acceptTask;
    private bool _isSendingFile;

    public SenderForm()
    {
        Text = "LAN Sender - Step 4 Multi File Transfer";
        Width = 1100;
        Height = 760;
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

        FormClosing += (_, _) => StopServer();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 5,
            ColumnCount = 2,
            Padding = new Padding(12),
        };

        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));

        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 52));

        var serverPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        serverPanel.Controls.Add(new Label
        {
            Text = "待受ポート:",
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
        });

        _portBox.Text = "50000";
        _portBox.Width = 100;
        serverPanel.Controls.Add(_portBox);

        _startButton.Text = "開始";
        _startButton.Width = 90;
        serverPanel.Controls.Add(_startButton);

        _stopButton.Text = "停止";
        _stopButton.Width = 90;
        _stopButton.Enabled = false;
        serverPanel.Controls.Add(_stopButton);

        root.Controls.Add(serverPanel, 0, 0);
        root.SetColumnSpan(serverPanel, 2);

        var clientGroup = new GroupBox
        {
            Text = "接続中クライアント",
            Dock = DockStyle.Fill,
        };

        _clientList.Dock = DockStyle.Fill;
        clientGroup.Controls.Add(_clientList);

        root.Controls.Add(clientGroup, 0, 1);
        root.SetRowSpan(clientGroup, 4);

        var messageGroup = new GroupBox
        {
            Text = "送信メッセージ",
            Dock = DockStyle.Fill,
        };

        _messageBox.Multiline = true;
        _messageBox.Dock = DockStyle.Fill;
        _messageBox.ScrollBars = ScrollBars.Vertical;
        _messageBox.Text = "テストメッセージ";

        messageGroup.Controls.Add(_messageBox);
        root.Controls.Add(messageGroup, 1, 1);

        var sendPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        _sendAllButton.Text = "全員へ送信";
        _sendAllButton.Width = 130;
        _sendAllButton.Enabled = false;
        sendPanel.Controls.Add(_sendAllButton);

        _sendSelectedButton.Text = "選択先へ送信";
        _sendSelectedButton.Width = 130;
        _sendSelectedButton.Enabled = false;
        sendPanel.Controls.Add(_sendSelectedButton);

        root.Controls.Add(sendPanel, 1, 2);

        var fileGroup = new GroupBox
        {
            Text = "ファイル・フォルダ送信",
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

        _addFilesButton.Text = "ファイル追加";
        _addFilesButton.Width = 120;
        fileAddPanel.Controls.Add(_addFilesButton);

        _addFolderButton.Text = "フォルダ追加";
        _addFolderButton.Width = 120;
        fileAddPanel.Controls.Add(_addFolderButton);

        _clearFilesButton.Text = "一覧クリア";
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

        _sendFileAllButton.Text = "全員へ送信";
        _sendFileAllButton.Width = 130;
        _sendFileAllButton.Enabled = false;
        fileSendPanel.Controls.Add(_sendFileAllButton);

        _sendFileSelectedButton.Text = "選択先へ送信";
        _sendFileSelectedButton.Width = 140;
        _sendFileSelectedButton.Enabled = false;
        fileSendPanel.Controls.Add(_sendFileSelectedButton);

        _cancelTransferButton.Text = "キャンセル";
        _cancelTransferButton.Width = 120;
        _cancelTransferButton.Enabled = false;
        fileSendPanel.Controls.Add(_cancelTransferButton);

        fileLayout.Controls.Add(fileSendPanel, 0, 2);

        _progressBar.Dock = DockStyle.Fill;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 1000;
        fileLayout.Controls.Add(_progressBar, 0, 3);

        _progressLabel.Text = "待機中";
        _progressLabel.Dock = DockStyle.Fill;
        _progressLabel.TextAlign = ContentAlignment.MiddleLeft;
        fileLayout.Controls.Add(_progressLabel, 0, 4);

        fileGroup.Controls.Add(fileLayout);
        root.Controls.Add(fileGroup, 1, 3);

        var logGroup = new GroupBox
        {
            Text = "ログ",
            Dock = DockStyle.Fill,
        };

        _logList.Dock = DockStyle.Fill;
        logGroup.Controls.Add(_logList);

        root.Controls.Add(logGroup, 1, 4);

        Controls.Add(root);
    }

    private void StartServer()
    {
        if (_serverCts is not null)
        {
            AddLog("すでにサーバーは起動しています。");
            return;
        }

        if (!int.TryParse(_portBox.Text, out int port) || port < 1 || port > 65535)
        {
            AddLog("ポート番号が不正です。");
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

            AddLog($"待受開始: 0.0.0.0:{port}");
        }
        catch (Exception ex)
        {
            AddLog($"待受開始失敗: {ex.Message}");
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
                AddLog($"クライアント接続: {connection.DisplayName}");

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
                AddLog($"接続受付エラー: {ex.Message}");
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
                    AddLog($"クライアント切断: {connection.DisplayName}");
                    break;
                }

                AddLog($"想定外の受信データを破棄: {connection.DisplayName}");
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
            AddLog($"クライアント通信終了: {connection.DisplayName}");
        }
        catch (SocketException)
        {
            AddLog($"クライアント通信終了: {connection.DisplayName}");
        }
        catch (Exception ex)
        {
            AddLog($"クライアント監視エラー: {connection.DisplayName} / {ex.Message}");
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
            AddLog("送信するメッセージが空です。");
            return;
        }

        List<ClientConnection> snapshot;

        lock (_clients)
        {
            snapshot = _clients.ToList();
        }

        if (snapshot.Count == 0)
        {
            AddLog("接続中のクライアントがありません。");
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

        AddLog($"全体送信完了: {successCount}/{snapshot.Count} 台");
    }

    private async Task SendMessageToSelectedAsync()
    {
        if (_clientList.SelectedItem is not ClientConnection connection)
        {
            AddLog("送信先クライアントを選択してください。");
            return;
        }

        string text = _messageBox.Text;

        if (string.IsNullOrWhiteSpace(text))
        {
            AddLog("送信するメッセージが空です。");
            return;
        }

        bool ok = await SendMessageToClientAsync(connection, text);

        if (ok)
        {
            AddLog($"選択先へ送信完了: {connection.DisplayName}");
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
            AddLog($"送信失敗: {connection.DisplayName} / {ex.Message}");
            RemoveClient(connection);
            return false;
        }
    }

    private void AddFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "送信するファイルを選択",
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

        AddLog($"ファイル追加: {added} 件");
    }

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "送信するフォルダを選択してください",
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

        AddLog($"フォルダ追加: {rootFolder} / {added} 件");
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
            AddLog("送信中は一覧をクリアできません。");
            return;
        }

        lock (_transferItems)
        {
            _transferItems.Clear();
        }

        _fileList.Items.Clear();
        UpdateProgress(0, 1, "待機中");
        AddLog("送信ファイル一覧をクリアしました。");
    }

    private async Task SendFilesToAllAsync()
    {
        List<ClientConnection> snapshot;

        lock (_clients)
        {
            snapshot = _clients.ToList();
        }

        await SendFilesToTargetsAsync(snapshot);
    }

    private async Task SendFilesToSelectedAsync()
    {
        if (_clientList.SelectedItem is not ClientConnection connection)
        {
            AddLog("送信先クライアントを選択してください。");
            return;
        }

        await SendFilesToTargetsAsync(new List<ClientConnection> { connection });
    }

    private async Task SendFilesToTargetsAsync(List<ClientConnection> targets)
    {
        if (_isSendingFile)
        {
            AddLog("すでにファイル送信中です。");
            return;
        }

        List<TransferItem> items;

        lock (_transferItems)
        {
            items = _transferItems.ToList();
        }

        if (items.Count == 0)
        {
            AddLog("送信するファイルを追加してください。");
            return;
        }

        if (targets.Count == 0)
        {
            AddLog("接続中のクライアントがありません。");
            return;
        }

        items = items.Where(x => File.Exists(x.FullPath)).ToList();

        if (items.Count == 0)
        {
            AddLog("送信可能なファイルがありません。");
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

        UpdateProgress(0, Math.Max(totalBytes, 1), "送信準備中");

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
                            $"送信中: {FormatBytes(totalSentBytes)} / {FormatBytes(totalBytes)} / {FormatBytes(speed)}/s");
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
                UpdateProgress(totalSentBytes, Math.Max(totalBytes, 1), "送信キャンセル済み");
                AddLog($"送信キャンセル: 成功 {successClients} 台 / 失敗 {failedClients} 台");
            }
            else
            {
                UpdateProgress(1000, 1000, $"送信完了: 成功 {successClients} 台 / 失敗 {failedClients} 台");
                AddLog($"送信完了: 成功 {successClients} 台 / 失敗 {failedClients} 台 / キャンセル {canceledClients} 台");
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

            AddLog($"ファイル送信成功: {connection.DisplayName} / {items.Count} 件");
            return TransferResult.Success;
        }
        catch (OperationCanceledException)
        {
            AddLog($"ファイル送信キャンセル: {connection.DisplayName}");

            try
            {
                if (lockTaken)
                {
                    NetworkStream stream = connection.Client.GetStream();

                    var cancelInfo = new TransferCancelInfo
                    {
                        Reason = "送信側でキャンセルされました。",
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
            AddLog($"ファイル送信失敗: {connection.DisplayName} / {ex.Message}");
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
        AddLog("送信キャンセルを要求しました。");
    }

    private void StopServer()
    {
        try
        {
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

            if (!IsDisposed)
            {
                _startButton.Enabled = true;
                _stopButton.Enabled = false;
                SetSendButtonsEnabled(false);

                _clientList.Items.Clear();

                AddLog("サーバー停止");
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

        bool actual = enabled && !_isSendingFile;

        _sendAllButton.Enabled = actual;
        _sendSelectedButton.Enabled = actual;
        _sendFileAllButton.Enabled = actual;
        _sendFileSelectedButton.Enabled = actual;

        _addFilesButton.Enabled = !_isSendingFile;
        _addFolderButton.Enabled = !_isSendingFile;
        _clearFilesButton.Enabled = !_isSendingFile;

        _cancelTransferButton.Enabled = _isSendingFile;
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
    private const int MaxPayloadSize = 4 * 1024 * 1024;

    public static async Task WriteAsync(
        NetworkStream stream,
        byte packetType,
        byte[] payload,
        CancellationToken token)
    {
        if (payload.Length > MaxPayloadSize)
        {
            throw new InvalidOperationException("送信データが大きすぎます。");
        }

        byte[] header = new byte[HeaderSize];

        header[0] = packetType;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1, 4), payload.Length);

        await stream.WriteAsync(header.AsMemory(0, header.Length), token);
        await stream.WriteAsync(payload.AsMemory(0, payload.Length), token);
        await stream.FlushAsync(token);
    }
}