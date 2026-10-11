using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace JensTV;

/// <summary>
/// Network control of Samsung (Tizen, 2016+) TVs: Wake-on-LAN to switch the TV on, and the TV's remote-control
/// WebSocket API to send keys (power). Requires "Power On with Mobile" / network standby on the TV.
/// </summary>
public static class SamsungTv
{
    const string AppName = "Jens-TV";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    /// <summary>"on", "standby", or null when the TV doesn't answer (off, or not at that address).</summary>
    public static async Task<string?> GetPowerStateAsync(string host)
    {
        try
        {
            using var info = JsonDocument.Parse(await Http.GetStringAsync($"http://{host}:8001/api/v2/"));
            var device = info.RootElement.GetProperty("device");
            // Older models don't report PowerState; answering at all means they're on.
            return device.TryGetProperty("PowerState", out var state) ? state.GetString() : "on";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Magic packet to the TV's MAC: broadcast on every IPv4 network this PC is on, and sent straight to its last known
    /// address too (many routers don't pass broadcasts on to sleeping Wi-Fi devices).
    /// </summary>
    public static void SendWakeOnLan(string mac, string? host = null)
    {
        var macBytes = Convert.FromHexString(mac.Replace(":", "").Replace("-", ""));
        var packet = new byte[6 + 16 * 6];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (var i = 0; i < 16; i++)
            macBytes.CopyTo(packet, 6 + i * 6);

        var targets = new List<IPAddress> { IPAddress.Broadcast };
        if (host != null && IPAddress.TryParse(host, out var direct))
            targets.Add(direct);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            foreach (var address in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                var ip = address.Address.GetAddressBytes();
                var mask = address.IPv4Mask.GetAddressBytes();
                targets.Add(new IPAddress(ip.Select((b, i) => (byte)(b | ~mask[i])).ToArray()));
            }
        }

        using var udp = new UdpClient { EnableBroadcast = true };
        for (var attempt = 0; attempt < 3; attempt++)
            foreach (var target in targets.Distinct())
                foreach (var port in new[] { 9, 7 })
                    try { udp.Send(packet, packet.Length, new IPEndPoint(target, port)); } catch { /* unreachable network */ }
    }

    /// <summary>
    /// Sends remote keys (e.g. KEY_HDMI2, KEY_POWER). Without a token the TV shows an "Allow Jens-TV?" prompt; the
    /// token it hands out is returned so it can be saved. Throws if the TV refuses or doesn't answer.
    /// </summary>
    public static async Task<string?> SendKeysAsync(string host, string? token, IEnumerable<string> keys, CancellationToken cancel = default)
    {
        using var socket = new ClientWebSocket();
        // The TV uses a self-signed certificate; accept it for this one connection to the TV only.
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        var name = Convert.ToBase64String(Encoding.UTF8.GetBytes(AppName));
        var uri = new Uri($"wss://{host}:8002/api/v2/channels/samsung.remote.control?name={name}" +
            (string.IsNullOrEmpty(token) ? "" : $"&token={token}"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(45)); // time to press Allow on the TV the first time
        await socket.ConnectAsync(uri, timeout.Token);

        // Wait for the TV to accept the connection (and maybe hand out a new token).
        string? newToken = null;
        while (true)
        {
            using var message = JsonDocument.Parse(await ReceiveAsync(socket, timeout.Token));
            var evt = message.RootElement.TryGetProperty("event", out var e) ? e.GetString() : null;
            if (evt == "ms.channel.connect")
            {
                if (message.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("token", out var t))
                    newToken = t.GetString();
                break;
            }
            if (evt is "ms.channel.unauthorized" or "ms.channel.timeOut")
                throw new UnauthorizedAccessException("The TV didn't allow Jens-TV (press Allow on the TV's prompt)");
        }

        foreach (var key in keys)
        {
            var command = JsonSerializer.Serialize(new
            {
                method = "ms.remote.control",
                @params = new { Cmd = "Click", DataOfCmd = key, Option = "false", TypeOfRemote = "SendRemoteKey" },
            });
            await socket.SendAsync(Encoding.UTF8.GetBytes(command), WebSocketMessageType.Text, true, timeout.Token);
            await Task.Delay(400, timeout.Token);
        }

        try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { /* TV closed first */ }
        return newToken;
    }

    static async Task<string> ReceiveAsync(ClientWebSocket socket, CancellationToken cancel)
    {
        var buffer = new byte[16 * 1024];
        using var text = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancel);
            text.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(text.ToArray());
    }

    /// <summary>Finds the TV again via SSDP after its IP address changed, by matching its MAC address.</summary>
    public static async Task<string?> DiscoverAsync(string mac)
    {
        var wanted = mac.Replace("-", ":").ToUpperInvariant();
        var hosts = new HashSet<string>();
        using (var udp = new UdpClient(0))
        {
            var search = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n" +
                "ST: urn:samsung.com:device:RemoteControlReceiver:1\r\n\r\n");
            await udp.SendAsync(search, search.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));
            var until = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < until)
            {
                var receive = udp.ReceiveAsync();
                if (await Task.WhenAny(receive, Task.Delay(until - DateTime.UtcNow)) != receive)
                    break;
                hosts.Add(receive.Result.RemoteEndPoint.Address.ToString());
            }
        }

        foreach (var host in hosts)
        {
            try
            {
                using var info = JsonDocument.Parse(await Http.GetStringAsync($"http://{host}:8001/api/v2/"));
                if (info.RootElement.GetProperty("device").GetProperty("wifiMac").GetString()?.ToUpperInvariant() == wanted)
                    return host;
            }
            catch { /* not it */ }
        }
        return null;
    }
}
