using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;

namespace CS2ReplayRequest;

public partial class CS2ReplayRequest : BasePlugin
{
    public override string ModuleName => "CS2 Replay Request";
    public override string ModuleVersion => "1.0.1";
    public override string ModuleAuthor => "jvnipers";
    public override string ModuleDescription => "Allows players to request replay files from the FKZ filehost.";
    private static readonly string BaseUrl = "https://files.femboy.kz/fastdl/cs2/kzreplays/";
    private static readonly string? BackupUrl = "https://files-na.femboy.kz/fastdl/cs2/kzreplays/";
    private static readonly HttpClient HttpClient = new();

    private const int CooldownSeconds = 10;

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase)]
    private static partial Regex UuidRegex();

    private readonly ConcurrentDictionary<ulong, DateTime> _cooldowns = new();
    private readonly HashSet<string> _downloadedReplays = new();
    private string _replayDir = string.Empty;
    private string _trackingFile = string.Empty;

    public override void Load(bool hotReload)
    {
        _replayDir = Path.Combine(Server.GameDirectory, "csgo", "kzreplays");
        Directory.CreateDirectory(_replayDir);

        _trackingFile = Path.Combine(_replayDir, "downloaded.txt");
        CleanupTrackedReplays();

        RegisterListener<Listeners.OnMapStart>(name =>
        {
            CleanupTrackedReplays();
            _cooldowns.Clear();
        });

        Logger.LogInformation("[ReplayRequest] Plugin loaded. Replay directory: {Dir}", _replayDir);
    }

    private void CleanupTrackedReplays()
    {
        if (!File.Exists(_trackingFile)) return;

        var lines = File.ReadAllLines(_trackingFile);
        var count = 0;
        foreach (var uuid in lines)
        {
            if (string.IsNullOrWhiteSpace(uuid) || !UuidRegex().IsMatch(uuid.Trim())) continue;
            var path = Path.Combine(_replayDir, $"{uuid.Trim()}.replay");
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    count++;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[ReplayRequest] Failed to delete tracked replay {Uuid}", uuid);
            }
        }

        File.Delete(_trackingFile);
        _downloadedReplays.Clear();
        Logger.LogInformation("[ReplayRequest] Cleaned up {Count} tracked replays.", count);
    }

    [ConsoleCommand("css_reqreplay", "Request a replay file by UUID")]
    [CommandHelper(minArgs: 1, usage: "<uuid>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnReplayReqCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null) return;

        var steamId = player.SteamID;
        if (_cooldowns.TryGetValue(steamId, out var lastUse))
        {
            var remaining = CooldownSeconds - (DateTime.UtcNow - lastUse).TotalSeconds;
            if (remaining > 0)
            {
                command.ReplyToCommand($" \u000EFKZ \x01| Please wait \x06{remaining:F0}s\x01 before requesting another replay.");
                return;
            }
        }
        _cooldowns[steamId] = DateTime.UtcNow;

        var uuid = command.GetArg(1).Trim();

        if (!UuidRegex().IsMatch(uuid))
        {
            command.ReplyToCommand($" \u000EFKZ \x01| Invalid UUID format. Example: !replayreq 019a992d-749d-70f5-b09e-ee77653aeafb");
            return;
        }

        var destPath = Path.Combine(_replayDir, $"{uuid}.replay");

        if (_downloadedReplays.Contains(uuid) || File.Exists(destPath))
        {
            command.ReplyToCommand($" \u000EFKZ \x01| Replay \x06{uuid}\x01 already exists on the server.");
            return;
        }

        command.ReplyToCommand($" \u000EFKZ \x01| Downloading replay \x06{uuid}\x01...");

        var playerName = player.PlayerName;

        _ = Task.Run(async () =>
        {
            try
            {
                byte[]? bytes = null;

                var url = $"{BaseUrl}{uuid}.replay";
                using (var response = await HttpClient.GetAsync(url))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        bytes = await response.Content.ReadAsByteArrayAsync();
                    }
                }

                if (bytes == null && !string.IsNullOrEmpty(BackupUrl))
                {
                    Logger.LogInformation("[ReplayRequest] Primary URL failed for {Uuid}, trying backup...", uuid);
                    var backupUrlFull = $"{BackupUrl}{uuid}.replay";
                    using var backupResponse = await HttpClient.GetAsync(backupUrlFull);
                    if (backupResponse.IsSuccessStatusCode)
                    {
                        bytes = await backupResponse.Content.ReadAsByteArrayAsync();
                    }
                }

                if (bytes == null)
                {
                    Server.NextFrame(() =>
                    {
                        player.PrintToChat($" \u000EFKZ \x01| Replay \x06{uuid}\x01 not found on any server.");
                    });
                    return;
                }

                await File.WriteAllBytesAsync(destPath, bytes);
                _downloadedReplays.Add(uuid);
                await File.AppendAllTextAsync(_trackingFile, uuid + Environment.NewLine);

                Logger.LogInformation("[ReplayRequest] Player {Player} downloaded replay {Uuid} ({Size} bytes)",
                    playerName, uuid, bytes.Length);

                Server.NextFrame(() =>
                {
                    player.PrintToChat($" \u000EFKZ \x01| Replay \x06{uuid}\x01 downloaded successfully ({bytes.Length:N0} bytes).");
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[ReplayRequest] Error downloading replay {Uuid}", uuid);

                Server.NextFrame(() =>
                {
                    player.PrintToChat($" \u000EFKZ \x01| An error occurred downloading replay \x06{uuid}\x01.");
                });
            }
        });
    }
}
