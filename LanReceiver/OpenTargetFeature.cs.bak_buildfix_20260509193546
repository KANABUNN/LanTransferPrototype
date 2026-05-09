using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace LanReceiver;

public sealed partial class ReceiverForm
{
    private readonly SemaphoreSlim _openTargetWriteLock = new(1, 1);

    private void InitializeOpenTargetFeature()
    {
        AddLog("Open target feature ready.");
    }

    private async Task HandleOpenTargetCommandAsync(byte[] payload)
    {
        OpenTargetCommand? command = null;

        try
        {
            command = JsonSerializer.Deserialize<OpenTargetCommand>(payload);

            if (command is null)
            {
                await SendOpenTargetResultAsync(new OpenTargetResult
                {
                    Success = false,
                    Message = "Command parse failed.",
                });

                return;
            }

            OpenTargetResult result = ExecuteOpenTarget(command);
            await SendOpenTargetResultAsync(result);
        }
        catch (Exception ex)
        {
            await SendOpenTargetResultAsync(new OpenTargetResult
            {
                RequestId = command?.RequestId ?? "",
                TargetType = command?.TargetType ?? OpenTargetType.ExistingPath,
                DisplayName = command?.DisplayName ?? "",
                Success = false,
                Message = ex.Message,
            });
        }
    }

    private OpenTargetResult ExecuteOpenTarget(OpenTargetCommand command)
    {
        try
        {
            switch (command.TargetType)
            {
                case OpenTargetType.Url:
                    return OpenUrl(command);

                case OpenTargetType.FolderPath:
                    return OpenFolderPath(command);

                case OpenTargetType.ExistingPath:
                    return OpenExistingPath(command);

                default:
                    return new OpenTargetResult
                    {
                        RequestId = command.RequestId,
                        TargetType = command.TargetType,
                        DisplayName = command.DisplayName,
                        Success = false,
                        Message = "Unsupported open target type.",
                    };
            }
        }
        catch (Exception ex)
        {
            return new OpenTargetResult
            {
                RequestId = command.RequestId,
                TargetType = command.TargetType,
                DisplayName = command.DisplayName,
                Success = false,
                Message = ex.Message,
            };
        }
    }

    private static OpenTargetResult OpenUrl(OpenTargetCommand command)
    {
        if (!Uri.TryCreate(command.Value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Failure(command, "Invalid URL.");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = command.Value,
            UseShellExecute = true,
        });

        return Success(command, "URL opened.");
    }

    private static OpenTargetResult OpenFolderPath(OpenTargetCommand command)
    {
        if (!Directory.Exists(command.Value))
        {
            return Failure(command, "Folder does not exist on receiver.");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = QuoteArgument(command.Value),
            UseShellExecute = true,
        });

        return Success(command, "Folder opened.");
    }

    private static OpenTargetResult OpenExistingPath(OpenTargetCommand command)
    {
        if (!File.Exists(command.Value) && !Directory.Exists(command.Value))
        {
            return Failure(command, "Path does not exist on receiver.");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = command.Value,
            UseShellExecute = true,
        });

        return Success(command, "Path opened.");
    }

    private async Task OpenReceivedFileAndReportAsync(string finalPath, string displayName, string requestId)
    {
        var result = new OpenTargetResult
        {
            RequestId = requestId,
            TargetType = OpenTargetType.ReceivedFile,
            DisplayName = displayName,
        };

        try
        {
            if (!File.Exists(finalPath))
            {
                result.Success = false;
                result.Message = "Received file does not exist.";
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = finalPath,
                    UseShellExecute = true,
                });

                result.Success = true;
                result.Message = "Received file opened.";
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = ex.Message;
        }

        await SendOpenTargetResultAsync(result);
    }

    private async Task SendOpenTargetResultAsync(OpenTargetResult result)
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(result);

            await _openTargetWriteLock.WaitAsync();

            try
            {
                await ReceiverPacketWriter.WriteAsync(_client.GetStream(), PacketType.OpenTargetResult, payload, CancellationToken.None);
            }
            finally
            {
                _openTargetWriteLock.Release();
            }
        }
        catch (Exception ex)
        {
            AddLog($"Failed to send open result: {ex.Message}");
        }
    }

    private static OpenTargetResult Success(OpenTargetCommand command, string message)
    {
        return new OpenTargetResult
        {
            RequestId = command.RequestId,
            TargetType = command.TargetType,
            DisplayName = command.DisplayName,
            Success = true,
            Message = message,
        };
    }

    private static OpenTargetResult Failure(OpenTargetCommand command, string message)
    {
        return new OpenTargetResult
        {
            RequestId = command.RequestId,
            TargetType = command.TargetType,
            DisplayName = command.DisplayName,
            Success = false,
            Message = message,
        };
    }

    private static string QuoteArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
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

public static class ReceiverPacketWriter
{
    private const int HeaderSize = 5;
    private const int MaxPayloadSize = 4 * 1024 * 1024;

    public static async Task WriteAsync(NetworkStream stream, byte packetType, byte[] payload, CancellationToken token)
    {
        if (payload.Length > MaxPayloadSize)
        {
            throw new InvalidOperationException("Payload too large.");
        }

        byte[] header = new byte[HeaderSize];

        header[0] = packetType;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1, 4), payload.Length);

        await stream.WriteAsync(header.AsMemory(0, header.Length), token);
        await stream.WriteAsync(payload.AsMemory(0, payload.Length), token);
        await stream.FlushAsync(token);
    }
}