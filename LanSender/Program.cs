using System.Buffers.Binary;
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
    private readonly TextBox _filePathBox = new();

    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly Button _sendAllButton = new();
    private readonly Button _sendSelectedButton = new();
    private readonly Button _browseFileButton = new();
    private readonly Button _sendFileAllButton = new();
    private readonly Button _sendFileSelectedButton = new();

    private readonly ProgressBar _progressBar = new();
    private readonly Label _progressLabel = new();

    private readonly ListBox _clientList = new();
    private readonly ListBox _logList = new();

    private readonly List<ClientConnection> _clients = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private bool _isSendingFile;

    public SenderForm()
    {
        Text = "LAN Sender - Step 3 File Transfer";
        Width = 960;
        Height = 680;
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();

        _startButton.Click += (_, _) => StartServer();
        _stopButton.Click += (_, _) => StopServer();

        _sendAllButton.Click += async (_, _) => await SendMessageToAllAsync();
        _sendSelectedButton.Click += async (_, _) => await SendMessageToSelectedAsync();

        _browseFileButton.Click += (_, _) => BrowseFile();
        _sendFileAllButton.Click += async (_, _) => await SendFileToAllAsync();
        _sendFileSelectedButton.Click += async (_, _) => await SendFileToSelectedAsync();

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

        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));

        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

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
            Text = "ファイル送信",
            Dock = DockStyle.Fill,
        };

        var fileLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 2,
            Padding = new Padding(8),
        };

        fileLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fileLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));

        fileLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        fileLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        fileLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        fileLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _filePathBox.Dock = DockStyle.Fill;
        _filePathBox.ReadOnly = true;
        fileLayout.Controls.Add(_filePathBox, 0, 0);

        _browseFileButton.Text = "ファイル選択";
        _browseFileButton.Dock = DockStyle.Fill;
        fileLayout.Controls.Add(_browseFileButton, 1, 0);

        var fileButtonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        _sendFileAllButton.Text = "全員へファイル送信";
        _sendFileAllButton.Width = 150;
        _sendFileAllButton.Enabled = false;
        fileButtonPanel.Controls.Add(_sendFileAllButton);

        _sendFileSelectedButton.Text = "選択先へファイル送信";
        _sendFileSelectedButton.Width = 170;
        _sendFileSelectedButton.Enabled = false;
        fileButtonPanel.Controls.Add(_sendFileSelectedButton);

        fileLayout.Controls.Add(fileButtonPanel, 0, 1);
        fileLayout.SetColumnSpan(fileButtonPanel, 2);

        _progressBar.Dock = DockStyle.Fill;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 1000;
        fileLayout.Controls.Add(_progressBar, 0, 2);
        fileLayout.SetColumnSpan(_progressBar, 2);

        _progressLabel.Text = "待機中";
        _progressLabel.Dock = DockStyle.Fill;
        _progressLabel.TextAlign = ContentAlignment.MiddleLeft;
        fileLayout.Controls.Add(_progressLabel, 0, 3);
        fileLayout.SetColumnSpan(_progressLabel, 2);

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
        if (_cts is not null)
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
            _cts = new CancellationTokenSource();

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();

            _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));

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

    private void BrowseFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "送信するファイルを選択",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _filePathBox.Text = dialog.FileName;
            AddLog($"ファイル選択: {dialog.FileName}");
        }
    }

    private async Task SendFileToAllAsync()
    {
        List<ClientConnection> snapshot;

        lock (_clients)
        {
            snapshot = _clients.ToList();
        }

        await SendFileToTargetsAsync(snapshot);
    }

    private async Task SendFileToSelectedAsync()
    {
        if (_clientList.SelectedItem is not ClientConnection connection)
        {
            AddLog("送信先クライアントを選択してください。");
            return;
        }

        await SendFileToTargetsAsync(new List<ClientConnection> { connection });
    }

    private async Task SendFileToTargetsAsync(List<ClientConnection> targets)
    {
        if (_isSendingFile)
        {
            AddLog("すでにファイル送信中です。");
            return;
        }

        string filePath = _filePathBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            AddLog("送信するファイルを選択してください。");
            return;
        }

        if (targets.Count == 0)
        {
            AddLog("接続中のクライアントがありません。");
            return;
        }

        var fileInfo = new FileInfo(filePath);

        if (fileInfo.Length < 0)
        {
            AddLog("ファイルサイズが不正です。");
            return;
        }

        _isSendingFile = true;
        SetSendButtonsEnabled(false);
        UpdateProgress(0, 1, "送信準備中");

        try
        {
            long totalBytes = fileInfo.Length * targets.Count;
            long totalSentBytes = 0;
            int successCount = 0;

            foreach (var connection in targets)
            {
                bool ok = await SendFileToClientAsync(
                    connection,
                    fileInfo,
                    sentBytes =>
                    {
                        totalSentBytes += sentBytes;
                        UpdateProgress(
                            totalSentBytes,
                            totalBytes,
                            $"送信中: {fileInfo.Name} / {FormatBytes(totalSentBytes)} / {FormatBytes(totalBytes)}");
                    });

                if (ok)
                {
                    successCount++;
                }
            }

            UpdateProgress(1000, 1000, $"送信完了: {successCount}/{targets.Count} 台");
            AddLog($"ファイル送信完了: {fileInfo.Name} / {successCount}/{targets.Count} 台");
        }
        finally
        {
            _isSendingFile = false;
            SetSendButtonsEnabled(_cts is not null);
        }
    }

    private async Task<bool> SendFileToClientAsync(
        ClientConnection connection,
        FileInfo fileInfo,
        Action<int> onChunkSent)
    {
        try
        {
            var startInfo = new FileStartInfo
            {
                FileName = fileInfo.Name,
                FileSize = fileInfo.Length,
            };

            byte[] startPayload = JsonSerializer.SerializeToUtf8Bytes(startInfo);

            await connection.WriteLock.WaitAsync();

            try
            {
                NetworkStream stream = connection.Client.GetStream();

                await NetPacket.WriteAsync(stream, PacketType.FileStart, startPayload, CancellationToken.None);

                byte[] buffer = new byte[FileChunkSize];

                await using var fileStream = new FileStream(
                    fileInfo.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: FileChunkSize,
                    useAsync: true);

                while (true)
                {
                    int read = await fileStream.ReadAsync(buffer.AsMemory(0, buffer.Length));

                    if (read == 0)
                    {
                        break;
                    }

                    byte[] chunk = buffer.AsSpan(0, read).ToArray();

                    await NetPacket.WriteAsync(stream, PacketType.FileChunk, chunk, CancellationToken.None);
                    onChunkSent(read);
                }

                await NetPacket.WriteAsync(stream, PacketType.FileEnd, Array.Empty<byte>(), CancellationToken.None);
            }
            finally
            {
                connection.WriteLock.Release();
            }

            AddLog($"ファイル送信成功: {connection.DisplayName} / {fileInfo.Name}");
            return true;
        }
        catch (Exception ex)
        {
            AddLog($"ファイル送信失敗: {connection.DisplayName} / {ex.Message}");
            RemoveClient(connection);
            return false;
        }
    }

    private void StopServer()
    {
        try
        {
            _cts?.Cancel();

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

            _cts?.Dispose();
        }
        finally
        {
            _cts = null;
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
        _browseFileButton.Enabled = !_isSendingFile;
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

public sealed class FileStartInfo
{
    public string FileName { get; set; } = "";

    public long FileSize { get; set; }
}

public static class PacketType
{
    public const byte TextMessage = 1;
    public const byte FileStart = 2;
    public const byte FileChunk = 3;
    public const byte FileEnd = 4;

    public const byte ScreenFrame = 5;
    public const byte WebOpen = 6;
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