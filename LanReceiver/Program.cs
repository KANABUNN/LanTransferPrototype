using System.Buffers.Binary;
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

    private readonly Label _statusLabel = new();
    private readonly Label _progressLabel = new();
    private readonly ProgressBar _progressBar = new();

    private readonly ListBox _messageList = new();
    private readonly ListBox _logList = new();

    private TcpClient? _client;
    private CancellationTokenSource? _cts;
    private bool _manualDisconnect;

    private FileReceiveSession? _fileSession;

    public ReceiverForm()
    {
        Text = "LAN Receiver - Step 3 File Transfer";
        Width = 880;
        Height = 680;
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();

        _connectButton.Click += async (_, _) => await ConnectAsync();
        _disconnectButton.Click += (_, _) => DisconnectByUser();
        _clearButton.Click += (_, _) => _messageList.Items.Clear();
        _chooseFolderButton.Click += (_, _) => ChooseSaveFolder();

        FormClosing += (_, _) => DisconnectSilent();
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
            RowCount = 6,
            ColumnCount = 1,
            Padding = new Padding(12),
        };

        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 38));

        var connectPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        connectPanel.Controls.Add(new Label
        {
            Text = "サーバーIP:",
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
        });

        _hostBox.Text = "127.0.0.1";
        _hostBox.Width = 160;
        connectPanel.Controls.Add(_hostBox);

        connectPanel.Controls.Add(new Label
        {
            Text = "ポート:",
            AutoSize = true,
            Padding = new Padding(12, 8, 0, 0),
        });

        _portBox.Text = "50000";
        _portBox.Width = 100;
        connectPanel.Controls.Add(_portBox);

        _connectButton.Text = "接続";
        _connectButton.Width = 90;
        connectPanel.Controls.Add(_connectButton);

        _disconnectButton.Text = "切断";
        _disconnectButton.Width = 90;
        _disconnectButton.Enabled = false;
        connectPanel.Controls.Add(_disconnectButton);

        _clearButton.Text = "表示クリア";
        _clearButton.Width = 100;
        connectPanel.Controls.Add(_clearButton);

        root.Controls.Add(connectPanel, 0, 0);

        var savePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
        };

        savePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        savePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        savePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));

        savePanel.Controls.Add(new Label
        {
            Text = "保存先:",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        _saveFolderBox.Text = defaultFolder;
        _saveFolderBox.Dock = DockStyle.Fill;
        _saveFolderBox.ReadOnly = true;
        savePanel.Controls.Add(_saveFolderBox, 1, 0);

        _chooseFolderButton.Text = "保存先選択";
        _chooseFolderButton.Dock = DockStyle.Fill;
        savePanel.Controls.Add(_chooseFolderButton, 2, 0);

        root.Controls.Add(savePanel, 0, 1);

        _statusLabel.Text = "未接続";
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(_statusLabel, 0, 2);

        var progressPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
        };

        progressPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        progressPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _progressLabel.Text = "待機中";
        _progressLabel.Dock = DockStyle.Fill;
        _progressLabel.TextAlign = ContentAlignment.MiddleLeft;
        progressPanel.Controls.Add(_progressLabel, 0, 0);

        _progressBar.Dock = DockStyle.Fill;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 1000;
        progressPanel.Controls.Add(_progressBar, 0, 1);

        root.Controls.Add(progressPanel, 0, 3);

        var messageGroup = new GroupBox
        {
            Text = "受信メッセージ・ファイル",
            Dock = DockStyle.Fill,
        };

        _messageList.Dock = DockStyle.Fill;
        messageGroup.Controls.Add(_messageList);

        root.Controls.Add(messageGroup, 0, 4);

        var logGroup = new GroupBox
        {
            Text = "ログ",
            Dock = DockStyle.Fill,
        };

        _logList.Dock = DockStyle.Fill;
        logGroup.Controls.Add(_logList);

        root.Controls.Add(logGroup, 0, 5);

        Controls.Add(root);
    }

    private async Task ConnectAsync()
    {
        if (_client is not null)
        {
            AddLog("すでに接続しています。");
            return;
        }

        string host = _hostBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(host))
        {
            AddLog("サーバーIPを入力してください。");
            return;
        }

        if (!int.TryParse(_portBox.Text, out int port) || port < 1 || port > 65535)
        {
            AddLog("ポート番号が不正です。");
            return;
        }

        try
        {
            _manualDisconnect = false;

            _client = new TcpClient();
            _client.NoDelay = true;

            await _client.ConnectAsync(host, port);

            _cts = new CancellationTokenSource();

            SetConnectedUi(true, $"{host}:{port} に接続中");
            AddLog($"接続成功: {host}:{port}");

            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            AddLog($"接続失敗: {ex.Message}");
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
                        AddLog("サーバーから切断されました。");
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
                AddLog("サーバーとの通信が終了しました。");
            }
        }
        catch (SocketException)
        {
            if (!_manualDisconnect)
            {
                AddLog("サーバーとの通信が終了しました。");
            }
        }
        catch (Exception ex)
        {
            if (!_manualDisconnect)
            {
                AddLog($"受信エラー: {ex.Message}");
            }
        }
        finally
        {
            CloseCurrentFileSession(deleteTempFile: true);
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
                    AddMessage($"メッセージ: {text}");
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

            default:
                {
                    AddLog($"未知のパケット種別: {packet.Type}");
                    break;
                }
        }
    }

    private void StartFileReceive(byte[] payload)
    {
        CloseCurrentFileSession(deleteTempFile: true);

        FileStartInfo? info = JsonSerializer.Deserialize<FileStartInfo>(payload);

        if (info is null || string.IsNullOrWhiteSpace(info.FileName) || info.FileSize < 0)
        {
            AddLog("ファイル開始情報が不正です。");
            return;
        }

        string saveFolder = GetSaveFolder();
        Directory.CreateDirectory(saveFolder);

        string safeFileName = SanitizeFileName(Path.GetFileName(info.FileName));
        string finalPath = CreateUniqueFilePath(saveFolder, safeFileName);
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
            FileName = safeFileName,
            FileSize = info.FileSize,
            FinalPath = finalPath,
            TempPath = tempPath,
            Stream = stream,
        };

        UpdateProgress(0, Math.Max(info.FileSize, 1), $"受信開始: {safeFileName}");
        AddLog($"ファイル受信開始: {safeFileName} / {FormatBytes(info.FileSize)}");
    }

    private async Task WriteFileChunkAsync(byte[] payload)
    {
        if (_fileSession is null)
        {
            AddLog("ファイル開始前のチャンクを受信したため破棄しました。");
            return;
        }

        await _fileSession.Stream.WriteAsync(payload.AsMemory(0, payload.Length));
        _fileSession.ReceivedBytes += payload.Length;

        if (_fileSession.ReceivedBytes > _fileSession.FileSize)
        {
            AddLog("受信サイズが予定サイズを超えたため、ファイル受信を中断しました。");
            CloseCurrentFileSession(deleteTempFile: true);
            UpdateProgress(0, 1, "受信失敗");
            return;
        }

        UpdateProgress(
            _fileSession.ReceivedBytes,
            Math.Max(_fileSession.FileSize, 1),
            $"受信中: {_fileSession.FileName} / {FormatBytes(_fileSession.ReceivedBytes)} / {FormatBytes(_fileSession.FileSize)}");
    }

    private void FinishFileReceive()
    {
        if (_fileSession is null)
        {
            AddLog("ファイル受信状態がないまま完了通知を受け取りました。");
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
                AddLog($"ファイルサイズ不一致: {fileName} / {FormatBytes(receivedBytes)} / {FormatBytes(fileSize)}");

                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                }

                UpdateProgress(0, 1, "受信失敗");
                return;
            }

            File.Move(tempPath, finalPath);

            AddMessage($"ファイル受信完了: {fileName}");
            AddLog($"保存完了: {finalPath}");
            UpdateProgress(1000, 1000, $"受信完了: {fileName}");
        }
        finally
        {
            _fileSession.Dispose();
            _fileSession = null;
        }
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

    private void ChooseSaveFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "受信ファイルの保存先を選択してください",
            UseDescriptionForTitle = true,
            SelectedPath = _saveFolderBox.Text,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _saveFolderBox.Text = dialog.SelectedPath;
            AddLog($"保存先変更: {dialog.SelectedPath}");
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

        return folder;
    }

    private void DisconnectByUser()
    {
        _manualDisconnect = true;
        DisconnectSilent();
        AddLog("切断しました。");
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

            _cts = null;
            _client = null;

            if (!IsDisposed)
            {
                SetConnectedUi(false, "未接続");
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

        SetConnectedUi(false, "未接続");
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

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "received_file";
        }

        foreach (char c in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(c, '_');
        }

        return fileName;
    }

    private static string CreateUniqueFilePath(string folder, string fileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);

        string path = Path.Combine(folder, fileName);

        int index = 1;

        while (File.Exists(path) || File.Exists(path + ".part"))
        {
            string newFileName = $"{baseName} ({index}){extension}";
            path = Path.Combine(folder, newFileName);
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

public sealed class ReceivedPacket
{
    public required byte Type { get; init; }

    public required byte[] Payload { get; init; }
}

public static class NetPacket
{
    private const int HeaderSize = 5;
    private const int MaxPayloadSize = 4 * 1024 * 1024;

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
            throw new InvalidOperationException("受信データサイズが不正です。");
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