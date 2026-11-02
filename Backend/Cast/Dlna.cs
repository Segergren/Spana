using System.Net;
using System.Net.NetworkInformation;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Xml.Linq;
using Serilog;

namespace Spana.Backend.Cast;

internal sealed record DlnaRenderer(string Id, string Name, string? Model, IPAddress Address, Uri Control, string ServiceType);

// UPnP AV media renderers (most smart TVs): found over SSDP and told to play a URL through AVTransport.
internal static class Dlna
{
    private static readonly IPEndPoint Group = new(IPAddress.Parse("239.255.255.250"), 1900);
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task<List<DlnaRenderer>> Discover(TimeSpan duration)
    {
        var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sockets = OpenSockets();
        try
        {
            byte[] search = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n" +
                "ST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n\r\n");
            var deadline = DateTime.UtcNow + duration;
            var receivers = sockets.Select(s => Receive(s, locations, deadline)).ToList();
            for (int round = 0; round < 2; round++)
            {
                foreach (var s in sockets)
                {
                    try { await s.SendAsync(search, Group); } catch (SocketException) { }
                }
                await Task.Delay(600);
            }
            await Task.WhenAll(receivers);
        }
        finally
        {
            foreach (var s in sockets) s.Dispose();
        }

        List<string> list;
        lock (locations) list = locations.ToList();
        var found = await Task.WhenAll(list.Select(Describe));
        return found.Where(r => r != null).Select(r => r!).GroupBy(r => r.Id).Select(g => g.First()).ToList();
    }

    // One socket per IPv4 interface, so the search reaches every network this PC is on.
    private static List<UdpClient> OpenSockets()
    {
        var list = new List<UdpClient>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || !ni.SupportsMulticast) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                try
                {
                    var client = new UdpClient(new IPEndPoint(addr.Address, 0));
                    client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
                    list.Add(client);
                }
                catch (SocketException) { }
            }
        }
        return list;
    }

    private static async Task Receive(UdpClient socket, HashSet<string> locations, DateTime deadline)
    {
        while (true)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return;
            using var timeout = new CancellationTokenSource(left);
            UdpReceiveResult result;
            try { result = await socket.ReceiveAsync(timeout.Token); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }
            foreach (var line in Encoding.UTF8.GetString(result.Buffer).Split("\r\n"))
            {
                if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                    lock (locations) locations.Add(line[9..].Trim());
            }
        }
    }

    // Reads the device description; keeps renderers with AVTransport that accept video.
    private static async Task<DlnaRenderer?> Describe(string location)
    {
        try
        {
            var baseUri = new Uri(location);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var doc = XDocument.Parse(await Http.GetStringAsync(baseUri, timeout.Token));
            var root = doc.Root!;
            if (Child(root, "URLBase") is { Length: > 0 } urlBase && Uri.TryCreate(urlBase, UriKind.Absolute, out var b)) baseUri = b;

            foreach (var device in root.Descendants().Where(e => e.Name.LocalName == "device"))
            {
                var services = device.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceList")?.Elements().ToList() ?? [];
                var transport = services.FirstOrDefault(s => Child(s, "serviceType")?.Contains(":AVTransport:") == true);
                if (transport == null) continue;
                var control = new Uri(baseUri, Child(transport, "controlURL") ?? "");
                var connection = services.FirstOrDefault(s => Child(s, "serviceType")?.Contains(":ConnectionManager:") == true);
                if (connection != null && !await AcceptsVideo(new Uri(baseUri, Child(connection, "controlURL") ?? ""), Child(connection, "serviceType")!))
                    continue;

                string udn = Child(device, "UDN") ?? location;
                string name = Child(device, "friendlyName") ?? baseUri.Host;
                if (!IPAddress.TryParse(control.Host, out var address)) address = (await Dns.GetHostAddressesAsync(control.Host)).First();
                return new DlnaRenderer($"dlna:{udn}".ToLowerInvariant(), name, Child(device, "modelName"), address, control, Child(transport, "serviceType")!);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("DLNA description {Location} failed: {Message}", location, ex.Message);
        }
        return null;
    }

    // Speakers are media renderers too; their sink protocols list no video types.
    private static async Task<bool> AcceptsVideo(Uri control, string serviceType)
    {
        try
        {
            var res = await Soap(control, serviceType, "GetProtocolInfo");
            return Value(res, "Sink")?.Contains("video/", StringComparison.OrdinalIgnoreCase) ?? true;
        }
        catch (Exception)
        {
            return true;
        }
    }

    public static async Task Play(DlnaRenderer r, string url, string title, CancellationToken ct)
    {
        string meta = "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
            "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\"><item id=\"0\" parentID=\"-1\" restricted=\"1\">" +
            $"<dc:title>{SecurityElement.Escape(title)}</dc:title><upnp:class>object.item.videoItem</upnp:class>" +
            $"<res protocolInfo=\"{StreamServer.DlnaProtocolInfo}\">{SecurityElement.Escape(url)}</res></item></DIDL-Lite>";
        var args = new[] { ("InstanceID", "0"), ("CurrentURI", url), ("CurrentURIMetaData", meta) };
        try
        {
            await Soap(r.Control, r.ServiceType, "SetAVTransportURI", ct, args);
        }
        catch (DlnaException)
        {
            // Some renderers refuse a new address while something else plays.
            try { await Soap(r.Control, r.ServiceType, "Stop", ct, ("InstanceID", "0")); } catch (DlnaException) { }
            await Soap(r.Control, r.ServiceType, "SetAVTransportURI", ct, args);
        }
        // Some TVs (LG) start on their own once they have the address, and then fail an explicit Play.
        if (await StartedWithin(r, TimeSpan.FromSeconds(4), ct)) return;
        try
        {
            await Soap(r.Control, r.ServiceType, "Play", ct, ("InstanceID", "0"), ("Speed", "1"));
        }
        catch (Exception ex) when (ex is DlnaException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            if (!await StartedWithin(r, TimeSpan.FromSeconds(2), ct)) throw;
        }
    }

    private static async Task<bool> StartedWithin(DlnaRenderer r, TimeSpan wait, CancellationToken ct)
    {
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                if (await State(r, ct) is "PLAYING" or "TRANSITIONING") return true;
            }
            catch (Exception ex) when (ex is DlnaException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { }
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(500, ct);
        }
    }

    public static Task Stop(DlnaRenderer r, CancellationToken ct) => Soap(r.Control, r.ServiceType, "Stop", ct, ("InstanceID", "0"));

    // PLAYING, PAUSED_PLAYBACK, TRANSITIONING, STOPPED or NO_MEDIA_PRESENT.
    public static async Task<string> State(DlnaRenderer r, CancellationToken ct) =>
        Value(await Soap(r.Control, r.ServiceType, "GetTransportInfo", ct, ("InstanceID", "0")), "CurrentTransportState") ?? "";

    private static async Task<XDocument> Soap(Uri control, string serviceType, string action, params (string Name, string Value)[] args)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await Soap(control, serviceType, action, timeout.Token, args);
    }

    private static async Task<XDocument> Soap(Uri control, string serviceType, string action, CancellationToken ct, params (string Name, string Value)[] args)
    {
        var body = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            $"s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:{action} xmlns:u=\"{serviceType}\">");
        foreach (var (name, value) in args) body.Append($"<{name}>{SecurityElement.Escape(value)}</{name}>");
        body.Append($"</u:{action}></s:Body></s:Envelope>");

        using var req = new HttpRequestMessage(HttpMethod.Post, control) { Content = new StringContent(body.ToString(), Encoding.UTF8) };
        req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=\"utf-8\"");
        req.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{serviceType}#{action}\"");
        // TVs often answer Play only once the stream is open and playing, which takes a while.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(action switch { "SetAVTransportURI" => 45, "Play" => 20, _ => 10 }));
        using var res = await Http.SendAsync(req, timeout.Token);
        string text = await res.Content.ReadAsStringAsync(timeout.Token);
        XDocument? doc = null;
        try { doc = XDocument.Parse(text); } catch (Exception) { }
        if (!res.IsSuccessStatusCode || doc == null)
        {
            string detail = doc != null ? $"{Value(doc, "errorCode")} {Value(doc, "errorDescription")}".Trim() : "";
            throw new DlnaException($"{action} failed ({(int)res.StatusCode}{(detail.Length > 0 ? ", " + detail : "")})");
        }
        return doc;
    }

    private static string? Child(XElement e, string name) => e.Elements().FirstOrDefault(c => c.Name.LocalName == name)?.Value.Trim();

    private static string? Value(XDocument doc, string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
}

internal sealed class DlnaException(string message) : CastException(message);

public class CastException(string message) : Exception(message);
