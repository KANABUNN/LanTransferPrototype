using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace LanSender;

public sealed partial class SenderForm
{
    private readonly Button _refreshOpenTargetsButton = new();
    private readonly Button _sendOpenTargetAllButton = new();
    private readonly Button _sendOpenTargetSelectedButton = new();
    private readonly Button _sendFileAndOpenAllButton = new();
    private readonly Button _sendFileAndOpenSelectedButton = new();
    private readonly ListBox _openTargetList = new();
    private readonly Label _openTargetStatusLabel = new();

    private void InitializeOpenTargetFeature()
    {
        AddOpenTargetPanel();

        _refreshOpenTargetsButton.Click += (_, _) => RefreshOpenTargetList();
        _sendOpenTargetAllButton.Click += async (_, _) => await SendOpenTargetToAllAsync();
        _sendOpenTargetSelectedButton.Click += async (_, _) => await SendOpenTargetToSelectedAsync();
        _sendFileAndOpenAllButton.Click += async (_, _) => await SendFileAndOpenToAllAsync();
        _sendFileAndOpenSelectedButton.Click += async (_, _) => await SendFileAndOpenToSelectedAsync();

        RefreshOpenTargetList();
    }

    private void AddOpenTargetPanel()
    {
        var group = new GroupBox
        {
            Text = "Open target replication",
            Dock = DockStyle.Bottom,
            Height = 170,
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            Padding = new Padding(8),
        };

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        _refreshOpenTargetsButton.Text = "Refresh windows";
        _refreshOpenTargetsButton.Width = 130;
        topPanel.Controls.Add(_refreshOpenTargetsButton);

        _sendOpenTargetAllButton.Text = "Open target all";
        _sendOpenTargetAllButton.Width = 130;
        _sendOpenTargetAllButton.Enabled = false;
        topPanel.Controls.Add(_sendOpenTargetAllButton);

        _sendOpenTargetSelectedButton.Text = "Open target selected";
        _sendOpenTargetSelectedButton.Width = 155;
        _sendOpenTargetSelectedButton.Enabled = false;
        topPanel.Controls.Add(_sendOpenTargetSelectedButton);

        layout.Controls.Add(topPanel, 0, 0);

        _openTargetList.Dock = DockStyle.Fill;
        layout.Controls.Add(_openTargetList, 0, 1);

        var filePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        _sendFileAndOpenAllButton.Text = "Send file and open all";
        _sendFileAndOpenAllButton.Width = 170;
        _sendFileAndOpenAllButton.Enabled = false;
        filePanel.Controls.Add(_sendFileAndOpenAllButton);

        _sendFileAndOpenSelectedButton.Text = "Send file and open selected";
        _sendFileAndOpenSelectedButton.Width = 195;
        _sendFileAndOpenSelectedButton.Enabled = false;
        filePanel.Controls.Add(_sendFileAndOpenSelectedButton);

        layout.Controls.Add(filePanel, 0, 2);

        _openTargetStatusLabel.Text = "URL, Explorer folder, and selected local file are supported.";
        _openTargetStatusLabel.Dock = DockStyle.Fill;
        _openTargetStatusLabel.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(_openTargetStatusLabel, 0, 3);

        group.Controls.Add(layout);
        Controls.Add(group);
        group.BringToFront();
    }

    private void RefreshOpenTargetList()
    {
        _openTargetList.Items.Clear();

        foreach (var candidate in WindowTargetEnumerator.GetOpenWindows())
        {
            _openTargetList.Items.Add(candidate);
        }

        _openTargetStatusLabel.Text = $"Open windows: {_openTargetList.Items.Count}";
    }

    private async Task SendOpenTargetToAllAsync()
    {
        await SendOpenTargetToTargetsAsync(GetClientSnapshot());
    }

    private async Task SendOpenTargetToSelectedAsync()
    {
        if (_clientList.SelectedItem is not ClientConnection connection)
        {
            AddLog("Select a client first.");
            return;
        }

        await SendOpenTargetToTargetsAsync(new List<ClientConnection> { connection });
    }

    private async Task SendOpenTargetToTargetsAsync(List<ClientConnection> targets)
    {
        if (targets.Count == 0)
        {
            AddLog("No connected clients.");
            return;
        }

        if (_openTargetList.SelectedItem is not WindowTargetCandidate candidate)
        {
            AddLog("Select an open window first.");
            return;
        }

        OpenTargetCommand? command = await ResolveWindowTargetAsync(candidate);

        if (command is null)
        {
            AddLog($"Open target could not be resolved: {candidate}");
            return;
        }

        await SendOpenTargetCommandToTargetsAsync(targets, command);
    }

    private async Task<OpenTargetCommand?> ResolveWindowTargetAsync(WindowTargetCandidate candidate)
    {
        string process = candidate.ProcessName.ToLowerInvariant();

        if (process == "explorer")
        {
            if (TryResolveExplorerFolder(candidate.Hwnd, out string folder))
            {
                return new OpenTargetCommand
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    TargetType = OpenTargetType.FolderPath,
                    Value = folder,
                    DisplayName = folder,
                };
            }
        }

        if (IsBrowserProcess(process))
        {
            string? url = await TryReadBrowserUrlByClipboardAsync(candidate.Hwnd);

            if (IsHttpUrl(url))
            {
                return new OpenTargetCommand
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    TargetType = OpenTargetType.Url,
                    Value = url!,
                    DisplayName = candidate.Title,
                };
            }
        }

        if (TryExtractExistingPathFromTitle(candidate.Title, out string existingPath))
        {
            return new OpenTargetCommand
            {
                RequestId = Guid.NewGuid().ToString("N"),
                TargetType = Directory.Exists(existingPath) ? OpenTargetType.FolderPath : OpenTargetType.ExistingPath,
                Value = existingPath,
                DisplayName = existingPath,
            };
        }

        return null;
    }

    private static bool IsBrowserProcess(string processName)
    {
        return processName is "chrome" or "msedge" or "firefox" or "brave" or "vivaldi" or "opera" or "iexplore";
    }

    private static bool IsHttpUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private async Task<string?> TryReadBrowserUrlByClipboardAsync(IntPtr hwnd)
    {
        IDataObject? oldClipboard = null;

        try
        {
            if (Clipboard.ContainsData(DataFormats.Text) || Clipboard.ContainsData(DataFormats.UnicodeText))
            {
                oldClipboard = Clipboard.GetDataObject();
            }

            SetForegroundWindow(hwnd);
            await Task.Delay(150);

            SendKeys.SendWait("^l");
            await Task.Delay(120);

            SendKeys.SendWait("^c");
            await Task.Delay(150);

            string text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";

            if (oldClipboard is not null)
            {
                Clipboard.SetDataObject(oldClipboard, true);
            }

            return text;
        }
        catch (Exception ex)
        {
            AddLog($"Browser URL read failed: {ex.Message}");

            try
            {
                if (oldClipboard is not null)
                {
                    Clipboard.SetDataObject(oldClipboard, true);
                }
            }
            catch
            {
            }

            return null;
        }
    }

    private static bool TryResolveExplorerFolder(IntPtr hwnd, out string folder)
    {
        folder = "";

        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");

            if (shellType is null)
            {
                return false;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic windows = shell.Windows();

            foreach (dynamic window in windows)
            {
                try
                {
                    int windowHwnd = (int)window.HWND;

                    if (windowHwnd != hwnd.ToInt32())
                    {
                        continue;
                    }

                    string locationUrl = (string)window.LocationURL;

                    if (string.IsNullOrWhiteSpace(locationUrl))
                    {
                        return false;
                    }

                    var uri = new Uri(locationUrl);
                    folder = uri.LocalPath;

                    return Directory.Exists(folder);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryExtractExistingPathFromTitle(string title, out string path)
    {
        path = "";

        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        string cleaned = title.Trim().Trim('"');

        if (File.Exists(cleaned) || Directory.Exists(cleaned))
        {
            path = cleaned;
            return true;
        }

        int marker = cleaned.IndexOf(" - ", StringComparison.Ordinal);

        if (marker > 0)
        {
            string left = cleaned[..marker].Trim().Trim('"');

            if (File.Exists(left) || Directory.Exists(left))
            {
                path = left;
                return true;
            }
        }

        return false;
    }

    private async Task SendOpenTargetCommandToTargetsAsync(List<ClientConnection> targets, OpenTargetCommand command)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(command);
        int success = 0;

        foreach (ClientConnection connection in targets)
        {
            try
            {
                await connection.WriteLock.WaitAsync();

                try
                {
                    await NetPacket.WriteAsync(connection.Client.GetStream(), PacketType.OpenTarget, payload, CancellationToken.None);
                }
                finally
                {
                    connection.WriteLock.Release();
                }

                success++;
            }
            catch (Exception ex)
            {
                AddLog($"Open target send failed: {connection.DisplayName} / {ex.Message}");
                RemoveClient(connection);
            }
        }

        AddLog($"Open target command sent: {success}/{targets.Count} / {command.TargetType} / {command.Value}");
    }

    private async Task SendFileAndOpenToAllAsync()
    {
        await SendFileAndOpenToTargetsAsync(GetClientSnapshot());
    }

    private async Task SendFileAndOpenToSelectedAsync()
    {
        if (_clientList.SelectedItem is not ClientConnection connection)
        {
            AddLog("Select a client first.");
            return;
        }

        await SendFileAndOpenToTargetsAsync(new List<ClientConnection> { connection });
    }

    private async Task SendFileAndOpenToTargetsAsync(List<ClientConnection> targets)
    {
        if (targets.Count == 0)
        {
            AddLog("No connected clients.");
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "Select a file to send and open",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        string path = dialog.FileName;
        var info = new FileInfo(path);

        int success = 0;

        foreach (ClientConnection connection in targets)
        {
            bool ok = await SendSingleFileAndOpenAsync(connection, info);

            if (ok)
            {
                success++;
            }
        }

        AddLog($"Send file and open request sent: {success}/{targets.Count} / {info.Name}");
    }

    private async Task<bool> SendSingleFileAndOpenAsync(ClientConnection connection, FileInfo fileInfo)
    {
        string requestId = Guid.NewGuid().ToString("N");

        try
        {
            await connection.WriteLock.WaitAsync();

            try
            {
                NetworkStream stream = connection.Client.GetStream();

                byte[] startPayload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    FileName = fileInfo.Name,
                    FileSize = fileInfo.Length,
                    OpenAfterReceive = true,
                    OpenRequestId = requestId,
                });

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

                    await NetPacket.WriteAsync(stream, PacketType.FileChunk, buffer.AsSpan(0, read).ToArray(), CancellationToken.None);
                }

                await NetPacket.WriteAsync(stream, PacketType.FileEnd, Array.Empty<byte>(), CancellationToken.None);
            }
            finally
            {
                connection.WriteLock.Release();
            }

            return true;
        }
        catch (Exception ex)
        {
            AddLog($"Send file and open failed: {connection.DisplayName} / {ex.Message}");
            RemoveClient(connection);
            return false;
        }
    }

    private async Task MonitorClientPacketsAsync(ClientConnection connection, CancellationToken token)
    {
        try
        {
            NetworkStream stream = connection.Client.GetStream();

            while (!token.IsCancellationRequested)
            {
                InboundPacket? packet = await OpenTargetPacketReader.ReadAsync(stream, token);

                if (packet is null)
                {
                    AddLog($"Client disconnected: {connection.DisplayName}");
                    break;
                }

                if (packet.Type == PacketType.OpenTargetResult)
                {
                    OpenTargetResult? result = JsonSerializer.Deserialize<OpenTargetResult>(packet.Payload);

                    if (result is not null)
                    {
                        string status = result.Success ? "OK" : "FAIL";
                        AddLog($"Client open result [{status}]: {connection.DisplayName} / {result.TargetType} / {result.DisplayName} / {result.Message}");
                    }
                    else
                    {
                        AddLog($"Open target result parse failed: {connection.DisplayName}");
                    }
                }
                else
                {
                    AddLog($"Unexpected inbound packet type {packet.Type} from {connection.DisplayName}");
                }
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

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}

public enum OpenTargetType
{
    Url,
    FolderPath,
    ExistingPath,
    ReceivedFile,
}

public sealed class OpenTargetCommand
{
    public string RequestId { get; set; } = "";

    public OpenTargetType TargetType { get; set; }

    public string Value { get; set; } = "";

    public string DisplayName { get; set; } = "";
}

public sealed class OpenTargetResult
{
    public string RequestId { get; set; } = "";

    public OpenTargetType TargetType { get; set; }

    public string DisplayName { get; set; } = "";

    public bool Success { get; set; }

    public string Message { get; set; } = "";
}

public sealed class WindowTargetCandidate
{
    public IntPtr Hwnd { get; init; }

    public string Title { get; init; } = "";

    public string ProcessName { get; init; } = "";

    public int ProcessId { get; init; }

    public override string ToString()
    {
        return $"{ProcessName} [{ProcessId}] - {Title}";
    }
}

public static class WindowTargetEnumerator
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public static List<WindowTargetCandidate> GetOpenWindows()
    {
        var result = new List<WindowTargetCandidate>();
        IntPtr shellWindow = GetShellWindow();
        int currentProcessId = Environment.ProcessId;

        EnumWindows((hWnd, _) =>
        {
            if (hWnd == shellWindow || !IsWindowVisible(hWnd))
            {
                return true;
            }

            string title = GetWindowTitle(hWnd);

            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            GetWindowThreadProcessId(hWnd, out uint processId);

            if ((int)processId == currentProcessId)
            {
                return true;
            }

            string processName = "unknown";

            try
            {
                processName = Process.GetProcessById((int)processId).ProcessName;
            }
            catch
            {
            }

            result.Add(new WindowTargetCandidate
            {
                Hwnd = hWnd,
                Title = title,
                ProcessId = (int)processId,
                ProcessName = processName,
            });

            return true;
        }, IntPtr.Zero);

        return result
            .OrderBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        int length = GetWindowTextLength(hWnd);

        if (length <= 0)
        {
            return "";
        }

        var builder = new StringBuilder(length + 1);
        GetWindowText(hWnd, builder, builder.Capacity);
        return builder.ToString();
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}

public sealed class InboundPacket
{
    public required byte Type { get; init; }

    public required byte[] Payload { get; init; }
}

public static class OpenTargetPacketReader
{
    private const int HeaderSize = 5;
    private const int MaxPayloadSize = 4 * 1024 * 1024;

    public static async Task<InboundPacket?> ReadAsync(NetworkStream stream, CancellationToken token)
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
            throw new InvalidOperationException("Invalid inbound payload size.");
        }

        byte[] payload = new byte[payloadLength];

        bool payloadRead = await ReadExactAsync(stream, payload, token);

        if (!payloadRead)
        {
            return null;
        }

        return new InboundPacket
        {
            Type = packetType,
            Payload = payload,
        };
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken token)
    {
        int offset = 0;

        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token);

            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}