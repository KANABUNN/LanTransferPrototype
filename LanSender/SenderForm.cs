using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LanSender.Contracts;
using LanSender.Protocol;
using LanSender.ScreenStreaming;
using LanSender.Transfers;

namespace LanSender;

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
    private readonly NumericUpDown _screenFpsBox = new();
    private readonly NumericUpDown _screenQualityBox = new();
    private readonly NumericUpDown _screenScaleBox = new();
    private readonly ComboBox _screenCaptureSourceBox = new();
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

    private bool _isSendingFile;
    private bool _isStreamingScreen;
    private string? _activeScreenStreamId;

    public SenderForm()
    {
        Text = "LAN Sender - Screen Video Refactor";
        Width = 1180;
        Height = 830;
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 160));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 65));

        var serverPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        serverPanel.Controls.Add(new Label { Text = "待受ポート:", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
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

        var clientGroup = new GroupBox { Text = "接続中クライアント", Dock = DockStyle.Fill };
        _clientList.Dock = DockStyle.Fill;
        clientGroup.Controls.Add(_clientList);
        root.Controls.Add(clientGroup, 0, 1);
        root.SetRowSpan(clientGroup, 5);

        var messageGroup = new GroupBox { Text = "送信メッセージ", Dock = DockStyle.Fill };
        _messageBox.Multiline = true;
        _messageBox.Dock = DockStyle.Fill;
        _messageBox.ScrollBars = ScrollBars.Vertical;
        _messageBox.Text = "テストメッセージ";
        messageGroup.Controls.Add(_messageBox);
        root.Controls.Add(messageGroup, 1, 1);

        var sendPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _sendAllButton.Text = "全員へ送信";
        _sendAllButton.Width = 130;
        _sendAllButton.Enabled = false;
        sendPanel.Controls.Add(_sendAllButton);

        _sendSelectedButton.Text = "選択先へ送信";
        _sendSelectedButton.Width = 140;
        _sendSelectedButton.Enabled = false;
        sendPanel.Controls.Add(_sendSelectedButton);
        root.Controls.Add(sendPanel, 1, 2);

        var fileGroup = new GroupBox { Text = "ファイル・フォルダ送信", Dock = DockStyle.Fill };
        var fileLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new Padding(8) };
        fileLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        fileLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        fileLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        fileLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        fileLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

        var fileAddPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
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

        var fileSendPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _sendFileAllButton.Text = "全員へ送信";
        _sendFileAllButton.Width = 130;
        _sendFileAllButton.Enabled = false;
        fileSendPanel.Controls.Add(_sendFileAllButton);

        _sendFileSelectedButton.Text = "選択先へ送信";
        _sendFileSelectedButton.Width = 140;
        _sendFileSelectedButton.Enabled = false;
        fileSendPanel.Controls.Add(_sendFileSelectedButton);

        _cancelTransferButton.Text = "キャンセル";
        _cancelTransferButton.Width = 110;
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

        var screenGroup = new GroupBox { Text = "画面配信（DXGI/MJPEG 60FPS試験）", Dock = DockStyle.Fill };
        var screenLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(8) };
        screenLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        screenLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        screenLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        screenLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var screenOptionsPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        screenOptionsPanel.Controls.Add(new Label { Text = "FPS:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        _screenFpsBox.Minimum = 1;
        _screenFpsBox.Maximum = 60;
        _screenFpsBox.Value = 30;
        _screenFpsBox.Width = 70;
        screenOptionsPanel.Controls.Add(_screenFpsBox);

        screenOptionsPanel.Controls.Add(new Label { Text = "JPEG品質:", AutoSize = true, Padding = new Padding(12, 6, 0, 0) });
        _screenQualityBox.Minimum = 20;
        _screenQualityBox.Maximum = 95;
        _screenQualityBox.Increment = 5;
        _screenQualityBox.Value = 70;
        _screenQualityBox.Width = 70;
        screenOptionsPanel.Controls.Add(_screenQualityBox);

        screenOptionsPanel.Controls.Add(new Label { Text = "Scale:%", AutoSize = true, Padding = new Padding(12, 6, 0, 0) });
        _screenScaleBox.Minimum = 25;
        _screenScaleBox.Maximum = 100;
        _screenScaleBox.Increment = 5;
        _screenScaleBox.Value = 60;
        _screenScaleBox.Width = 70;
        screenOptionsPanel.Controls.Add(_screenScaleBox);

        screenOptionsPanel.Controls.Add(new Label { Text = "Source:", AutoSize = true, Padding = new Padding(12, 6, 0, 0) });
        _screenCaptureSourceBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _screenCaptureSourceBox.Width = 90;
        _screenCaptureSourceBox.Items.Add(ScreenCaptureSource.DxgiPrimary);
        _screenCaptureSourceBox.Items.Add(ScreenCaptureSource.Primary);
        _screenCaptureSourceBox.Items.Add(ScreenCaptureSource.Virtual);
        _screenCaptureSourceBox.SelectedItem = ScreenCaptureSource.DxgiPrimary;
        screenOptionsPanel.Controls.Add(_screenCaptureSourceBox);
        screenLayout.Controls.Add(screenOptionsPanel, 0, 0);

        var screenOncePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _sendScreenOnceAllButton.Text = "1フレーム全員";
        _sendScreenOnceAllButton.Width = 140;
        _sendScreenOnceAllButton.Enabled = false;
        screenOncePanel.Controls.Add(_sendScreenOnceAllButton);

        _sendScreenOnceSelectedButton.Text = "1フレーム選択先";
        _sendScreenOnceSelectedButton.Width = 150;
        _sendScreenOnceSelectedButton.Enabled = false;
        screenOncePanel.Controls.Add(_sendScreenOnceSelectedButton);
        screenLayout.Controls.Add(screenOncePanel, 0, 1);

        var screenStreamPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _startScreenAllButton.Text = "動画配信 全員";
        _startScreenAllButton.Width = 140;
        _startScreenAllButton.Enabled = false;
        screenStreamPanel.Controls.Add(_startScreenAllButton);

        _startScreenSelectedButton.Text = "動画配信 選択先";
        _startScreenSelectedButton.Width = 150;
        _startScreenSelectedButton.Enabled = false;
        screenStreamPanel.Controls.Add(_startScreenSelectedButton);

        _stopScreenButton.Text = "配信停止";
        _stopScreenButton.Width = 110;
        _stopScreenButton.Enabled = false;
        screenStreamPanel.Controls.Add(_stopScreenButton);
        screenLayout.Controls.Add(screenStreamPanel, 0, 2);

        _screenStatusLabel.Text = "30FPS画面配信待機中";
        _screenStatusLabel.Dock = DockStyle.Fill;
        _screenStatusLabel.TextAlign = ContentAlignment.MiddleLeft;
        screenLayout.Controls.Add(_screenStatusLabel, 0, 3);

        screenGroup.Controls.Add(screenLayout);
        root.Controls.Add(screenGroup, 1, 4);

        var logGroup = new GroupBox { Text = "ログ", Dock = DockStyle.Fill };
        _logList.Dock = DockStyle.Fill;
        logGroup.Controls.Add(_logList);
        root.Controls.Add(logGroup, 1, 5);

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

            _ = Task.Run(() => AcceptLoopAsync(_serverCts.Token));

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
            while (!token.IsCancellationRequested)
            {
                ReceivedPacket? packet = await NetPacket.ReadAsync(stream, token);
                if (packet is null)
                {
                    AddLog($"クライアント切断: {connection.DisplayName}");
                    break;
                }

                if (packet.Value.Type == PacketType.KeepAlive)
                {
                    continue;
                }

                if (packet.Value.Type == PacketType.OpenTargetResult)
                {
                    HandleOpenTargetResult(connection, packet.Value.Payload);
                    continue;
                }

                AddLog($"クライアントからの受信を破棄: {connection.DisplayName} / {PacketType.ToName(packet.Value.Type)}");
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

    private void HandleOpenTargetResult(ClientConnection connection, byte[] payload)
    {
        try
        {
            OpenTargetResult? result = JsonSerializer.Deserialize<OpenTargetResult>(payload);

            if (result is null)
            {
                AddLog($"Open target result parse failed: {connection.DisplayName}");
                return;
            }

            string status = result.Success ? "OK" : "FAIL";
            AddLog($"Open target result [{status}]: {connection.DisplayName} / {result.TargetType} / {result.DisplayName} / {result.Message}");
        }
        catch (Exception ex)
        {
            AddLog($"Open target result receive failed: {connection.DisplayName} / {ex.Message}");
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

        List<ClientConnection> snapshot = GetClientSnapshot();
        if (snapshot.Count == 0)
        {
            AddLog("接続中のクライアントがありません。");
            return;
        }

        int success = 0;
        foreach (ClientConnection connection in snapshot)
        {
            if (await SendPacketToClientAsync(connection, PacketType.TextMessage, Encoding.UTF8.GetBytes(text), CancellationToken.None))
            {
                success++;
            }
        }

        AddLog($"全体送信完了: {success}/{snapshot.Count} 台");
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

        bool ok = await SendPacketToClientAsync(connection, PacketType.TextMessage, Encoding.UTF8.GetBytes(text), CancellationToken.None);
        if (ok)
        {
            AddLog($"選択先へ送信完了: {connection.DisplayName}");
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

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

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

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

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
        if (!File.Exists(fullPath)) return false;

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

    private async Task SendFilesToAllAsync() => await SendFilesToTargetsAsync(GetClientSnapshot());

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

        if (_isStreamingScreen)
        {
            AddLog("画面動画配信を停止してからファイルを送信してください。");
            return;
        }

        List<TransferItem> items;
        lock (_transferItems)
        {
            items = _transferItems.ToList();
        }

        items = items.Where(x => File.Exists(x.FullPath)).ToList();
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

        long oneClientBytes = items.Sum(x => x.Size);
        long totalBytes = oneClientBytes * targets.Count;
        long totalSentBytes = 0;
        int successClients = 0;
        int canceledClients = 0;
        int failedClients = 0;
        var stopwatch = Stopwatch.StartNew();

        _isSendingFile = true;
        _transferCts = new CancellationTokenSource();
        SetSendButtonsEnabled(_serverCts is not null);
        _cancelTransferButton.Enabled = true;
        UpdateProgress(0, Math.Max(totalBytes, 1), "送信準備中");

        try
        {
            foreach (ClientConnection connection in targets)
            {
                if (_transferCts.IsCancellationRequested) break;

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

            string label = _transferCts.IsCancellationRequested
                ? $"送信キャンセル: 成功 {successClients} 台 / 失敗 {failedClients} 台"
                : $"送信完了: 成功 {successClients} 台 / 失敗 {failedClients} 台 / キャンセル {canceledClients} 台";

            UpdateProgress(_transferCts.IsCancellationRequested ? totalSentBytes : 1000, _transferCts.IsCancellationRequested ? Math.Max(totalBytes, 1) : 1000, label);
            AddLog(label);
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

            var batchInfo = new BatchStartInfo
            {
                FileCount = items.Count,
                TotalSize = items.Sum(x => x.Size),
            };

            await NetPacket.WriteAsync(stream, PacketType.BatchStart, JsonSerializer.SerializeToUtf8Bytes(batchInfo), token);

            foreach (TransferItem item in items)
            {
                token.ThrowIfCancellationRequested();

                var startInfo = new FileStartInfo { FileName = item.RelativePath, FileSize = item.Size };
                await NetPacket.WriteAsync(stream, PacketType.FileStart, JsonSerializer.SerializeToUtf8Bytes(startInfo), token);

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
                    if (read == 0) break;

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
            if (lockTaken)
            {
                try
                {
                    NetworkStream stream = connection.Client.GetStream();
                    var cancelInfo = new TransferCancelInfo { Reason = "送信側でキャンセルされました。" };
                    await NetPacket.WriteAsync(stream, PacketType.TransferCancel, JsonSerializer.SerializeToUtf8Bytes(cancelInfo), CancellationToken.None);
                }
                catch
                {
                }
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
                try { connection.WriteLock.Release(); } catch { }
            }
        }
    }

    private void CancelTransfer()
    {
        if (!_isSendingFile || _transferCts is null) return;
        _transferCts.Cancel();
        AddLog("送信キャンセルを要求しました。");
    }

    private async Task SendScreenOnceToAllAsync() => await SendSingleScreenFrameToTargetsAsync(GetClientSnapshot(), CancellationToken.None, true);

    private async Task SendScreenOnceToSelectedAsync()
    {
        if (_clientList.SelectedItem is not ClientConnection connection)
        {
            AddLog("送信先クライアントを選択してください。");
            return;
        }

        await SendSingleScreenFrameToTargetsAsync(new List<ClientConnection> { connection }, CancellationToken.None, true);
    }

    private async Task StartScreenStreamAsync(bool selectedOnly)
    {
        if (_isStreamingScreen)
        {
            AddLog("すでに画面動画配信中です。");
            return;
        }

        if (_isSendingFile)
        {
            AddLog("ファイル送信が終わってから開始してください。");
            return;
        }

        List<ClientConnection> fixedTargets = new();
        if (selectedOnly)
        {
            if (_clientList.SelectedItem is not ClientConnection selected)
            {
                AddLog("送信先クライアントを選択してください。");
                return;
            }
            fixedTargets.Add(selected);
        }
        else if (GetClientSnapshot().Count == 0)
        {
            AddLog("接続中のクライアントがありません。");
            return;
        }

        string streamId = Guid.NewGuid().ToString("N");
        _activeScreenStreamId = streamId;
        _screenStreamCts = new CancellationTokenSource();
        _isStreamingScreen = true;
        SetSendButtonsEnabled(_serverCts is not null);
        UpdateScreenStatus("画面動画配信を開始しています...");

        var options = new ScreenVideoOptions
        {
            StreamId = streamId,
            Fps = (int)_screenFpsBox.Value,
            Quality = (int)_screenQualityBox.Value,
            ScalePercent = (int)_screenScaleBox.Value,
            CaptureSource = _screenCaptureSourceBox.SelectedItem?.ToString() ?? ScreenCaptureSource.Primary,
        };

        List<ClientConnection> InitialTargets() => selectedOnly ? fixedTargets.ToList() : GetClientSnapshot();
        await SendScreenVideoStartAsync(InitialTargets(), options, CancellationToken.None);

        CancellationToken token = _screenStreamCts.Token;
        var streamer = new ScreenVideoStreamer(new ScreenCaptureService());
        streamer.StatsChanged += stats => UpdateScreenStatus(FormatScreenStats(stats));
_ = Task.Run(async () =>
        {
            try
            {
                await streamer.RunAsync(
                    options,
                    async (frame, ct) => await BroadcastScreenFrameAsync(selectedOnly ? fixedTargets.ToList() : GetClientSnapshot(), ct, false, frame),
                    token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AddLog($"画面動画配信エラー: {ex.Message}");
            }
            finally
            {
                await SendScreenVideoStopAsync(InitialTargets(), streamId, CancellationToken.None);
                _isStreamingScreen = false;
                _screenStreamCts?.Dispose();
                _screenStreamCts = null;
                _activeScreenStreamId = null;
                SetSendButtonsEnabled(_serverCts is not null);
                UpdateScreenStatus("30FPS画面配信待機中");
            }
        });
    }

    private async Task SendSingleScreenFrameToTargetsAsync(List<ClientConnection> targets, CancellationToken token, bool logResult, ScreenFrame? preCapturedFrame = null)
    {
        if (targets.Count == 0)
        {
            if (logResult) AddLog("接続中のクライアントがありません。");
            return;
        }

        ScreenFrame frame = preCapturedFrame ?? new ScreenCaptureService().CaptureVirtualScreenJpeg(
            Guid.NewGuid().ToString("N"),
            1,
            (int)_screenQualityBox.Value);

        byte[] payload = JpegFrameEncoder.BuildFramePayload(frame.Info, frame.ImageBytes);
        int success = 0;

        foreach (ClientConnection connection in targets)
        {
            token.ThrowIfCancellationRequested();
            bool ok = await SendPacketToClientAsync(connection, PacketType.ScreenVideoFrame, payload, token);
            if (ok) success++;
        }

        if (logResult)
        {
            AddLog($"画面フレーム送信: {success}/{targets.Count} 台 / {frame.Info.Width}x{frame.Info.Height} / {FormatBytes(frame.ImageBytes.Length)}");
        }
    }

    private async Task<int> BroadcastScreenFrameAsync(List<ClientConnection> targets, CancellationToken token, bool logResult, ScreenFrame frame)
    {
        if (targets.Count == 0)
        {
            return 0;
        }

        byte[] payload = JpegFrameEncoder.BuildFramePayload(frame.Info, frame.ImageBytes);
        int success = 0;

        foreach (ClientConnection connection in targets)
        {
            token.ThrowIfCancellationRequested();
            if (await SendPacketToClientAsync(connection, PacketType.ScreenVideoFrame, payload, token))
            {
                success++;
            }
        }

        if (logResult)
        {
            AddLog($"画面フレーム送信: {success}/{targets.Count} 台");
        }

        return success;
    }

    private async Task SendScreenVideoStartAsync(List<ClientConnection> targets, ScreenVideoOptions options, CancellationToken token)
    {
        var info = new ScreenVideoStartInfo
        {
            StreamId = options.StreamId,
            Fps = options.Fps,
            Quality = options.Quality,
            Format = "jpeg/mjpeg",
            StartedAtUtc = DateTime.UtcNow,
        };

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(info);
        foreach (ClientConnection connection in targets)
        {
            await SendPacketToClientAsync(connection, PacketType.ScreenVideoStart, payload, token);
        }
    }

    private async Task SendScreenVideoStopAsync(List<ClientConnection> targets, string streamId, CancellationToken token)
    {
        var info = new ScreenVideoStopInfo
        {
            StreamId = streamId,
            StoppedAtUtc = DateTime.UtcNow,
            Reason = "送信側で停止しました。",
        };

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(info);
        foreach (ClientConnection connection in targets)
        {
            await SendPacketToClientAsync(connection, PacketType.ScreenVideoStop, payload, token);
        }
    }

    private static string FormatScreenStats(ScreenStreamStats stats)
    {
        string phase = stats.IsWarmingUp ? "warmup" : "stable";
        string timer = stats.TimerResolutionRequested ? "1ms" : "default";
        double frameKb = stats.LastFrameBytes / 1024.0;
        string effective = stats.EffectiveTargetFps != stats.TargetFps ? $" / eff {stats.EffectiveTargetFps}fps" : string.Empty;
        string adaptive = stats.AdaptiveMode ? $" / q {stats.Quality} / {stats.AdaptiveState}" : $" / q {stats.Quality}";

        return $"動画配信中: target {stats.TargetFps}fps{effective} / actual {stats.RecentFps:0.0}fps / avg {stats.AverageFps:0.0}fps / {phase} / scale {stats.ScalePercent}% / source {stats.CaptureSource}{adaptive} / cap {stats.CaptureMs:0.0}ms (copy {stats.CopyMs:0.0} enc {stats.EncodeMs:0.0}) / send {stats.SendMs:0.0}ms / loop {stats.LoopMs:0.0}ms / margin {stats.MarginMs:0.0}ms / late +{stats.RecentLateFrames}/total {stats.TotalLateFrames} / {frameKb:0.#} KB / {stats.RecentMbps:0.0} Mbps / timer {timer}";
    }
private void StopScreenStream()
    {
        if (!_isStreamingScreen || _screenStreamCts is null) return;
        _screenStreamCts.Cancel();
        AddLog($"画面動画配信停止を要求しました: {_activeScreenStreamId}");
    }

    private async Task<bool> SendPacketToClientAsync(ClientConnection connection, byte packetType, byte[] payload, CancellationToken token)
    {
        try
        {
            await connection.WriteLock.WaitAsync(token);
            try
            {
                NetworkStream stream = connection.Client.GetStream();
                await NetPacket.WriteAsync(stream, packetType, payload, token);
            }
            finally
            {
                connection.WriteLock.Release();
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AddLog($"送信失敗: {connection.DisplayName} / {PacketType.ToName(packetType)} / {ex.Message}");
            RemoveClient(connection);
            return false;
        }
    }

    private void StopServer()
    {
        try
        {
            _screenStreamCts?.Cancel();
            _transferCts?.Cancel();
            _serverCts?.Cancel();

            try { _listener?.Stop(); } catch { }

            List<ClientConnection> snapshot;
            lock (_clients)
            {
                snapshot = _clients.ToList();
                _clients.Clear();
            }

            foreach (ClientConnection connection in snapshot)
            {
                connection.Dispose();
            }

            _serverCts?.Dispose();
        }
        finally
        {
            _serverCts = null;
            _listener = null;
            _isSendingFile = false;
            _isStreamingScreen = false;

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

        bool fileAllowed = enabled && !_isSendingFile && !_isStreamingScreen;
        bool messageAllowed = enabled && !_isSendingFile;
        bool screenAllowed = enabled && !_isSendingFile;

        _sendAllButton.Enabled = messageAllowed;
        _sendSelectedButton.Enabled = messageAllowed;

        _addFilesButton.Enabled = !_isSendingFile && !_isStreamingScreen;
        _addFolderButton.Enabled = !_isSendingFile && !_isStreamingScreen;
        _clearFilesButton.Enabled = !_isSendingFile && !_isStreamingScreen;
        _sendFileAllButton.Enabled = fileAllowed;
        _sendFileSelectedButton.Enabled = fileAllowed;
        _cancelTransferButton.Enabled = _isSendingFile;

        _sendScreenOnceAllButton.Enabled = screenAllowed && !_isStreamingScreen;
        _sendScreenOnceSelectedButton.Enabled = screenAllowed && !_isStreamingScreen;
        _startScreenAllButton.Enabled = screenAllowed && !_isStreamingScreen;
        _startScreenSelectedButton.Enabled = screenAllowed && !_isStreamingScreen;
        _stopScreenButton.Enabled = _isStreamingScreen;
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

        if (!removed) return;
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

        int value = total > 0 ? (int)Math.Clamp(current * 1000 / total, 0, 1000) : 0;
        _progressBar.Value = value;
        _progressLabel.Text = label;
    }

    private void UpdateScreenStatus(string label)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => UpdateScreenStatus(label)));
            return;
        }

        _screenStatusLabel.Text = label;
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
