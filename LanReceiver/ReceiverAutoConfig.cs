using System.Text.Json;

namespace LanReceiver;

public sealed class ReceiverAutoConfig
{
    public bool AutoConnectEnabled { get; set; } = true;
    public bool AutoReconnectEnabled { get; set; } = true;
    public int RetryIntervalSeconds { get; set; } = 5;
    public bool CloseButtonToTray { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimizedToTray { get; set; } = true;
    public bool AutoFullScreenOnFirstFrame { get; set; } = true;
    public List<ServerEndpoint> Servers { get; set; } = new();

    public static ReceiverAutoConfig CreateDefault()
    {
        return new ReceiverAutoConfig
        {
            AutoConnectEnabled = true,
            AutoReconnectEnabled = true,
            RetryIntervalSeconds = 5,
            CloseButtonToTray = true,
            MinimizeToTray = true,
            StartMinimizedToTray = false,
            AutoFullScreenOnFirstFrame = true,
            Servers =
            [
                new ServerEndpoint
                {
                    Host = "127.0.0.1",
                    Port = 50000,
                },
            ],
        };
    }

    public void Normalize()
    {
        RetryIntervalSeconds = Math.Clamp(RetryIntervalSeconds, 1, 3600);
        Servers ??= new List<ServerEndpoint>();

        Servers = Servers
            .Where(x => !string.IsNullOrWhiteSpace(x.Host) && x.Port is >= 1 and <= 65535)
            .Select(x => new ServerEndpoint
            {
                Host = x.Host.Trim(),
                Port = x.Port,
            })
            .ToList();

        if (Servers.Count == 0)
        {
            Servers.Add(new ServerEndpoint
            {
                Host = "127.0.0.1",
                Port = 50000,
            });
        }
    }

    public static ReceiverAutoConfig LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            ReceiverAutoConfig created = CreateDefault();
            Save(path, created);
            return created;
        }

        string json = File.ReadAllText(path);
        ReceiverAutoConfig? loaded = JsonSerializer.Deserialize<ReceiverAutoConfig>(json);
        ReceiverAutoConfig config = loaded ?? CreateDefault();
        config.Normalize();
        return config;
    }

    public static void Save(string path, ReceiverAutoConfig config)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        config.Normalize();
        string json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true,
        });

        File.WriteAllText(path, json);
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
