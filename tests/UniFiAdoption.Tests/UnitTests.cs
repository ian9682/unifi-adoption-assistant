using System.Net;
using System.Net.Sockets;
using UniFiAdoption.Core.Configuration;
using UniFiAdoption.Core.Device;
using UniFiAdoption.Core.Discovery;
using UniFiAdoption.Core.Logging;
using UniFiAdoption.Core.Networking;
using UniFiAdoption.Core.Security;
using UniFiAdoption.Core.Ssh;

namespace UniFiAdoption.Tests;

internal sealed class InputTests
{
    [Test]
    public void ValidSuffixesAreAcceptedAndNormalised()
    {
        foreach (var input in new[] { "abcd", "ABCD", " ab cd ", "AB:CD", "ab-cd", "0f9E" })
        {
            var result = MacSuffix.Validate(input);
            Assert.True(result.IsValid, $"'{input}' should be valid");
            Assert.Equal(input.Replace(" ", "").Replace(":", "").Replace("-", "").ToUpperInvariant(), result.Suffix!.Value);
        }
    }

    [Test]
    public void InvalidSuffixesGivePlainLanguageReasons()
    {
        Assert.Contains("Type the last 4", MacSuffix.Validate("").Problem);
        Assert.Contains("You entered 3", MacSuffix.Validate("ABC").Problem);
        Assert.Contains("You entered 5", MacSuffix.Validate("ABCDE").Problem);
        Assert.Contains("number 0 (zero)", MacSuffix.Validate("AB0O").Problem);
        Assert.Contains("number 1 (one)", MacSuffix.Validate("ABlI").Problem);
        Assert.Contains("0 to 9 and the letters A to F", MacSuffix.Validate("ABXZ").Problem);
        Assert.False(MacSuffix.Validate("AB;rm").IsValid);
        Assert.False(MacSuffix.Validate("$(ls)").IsValid);
    }
}

internal sealed class MacAddressTests
{
    [Test]
    public void ParsesCommonFormats()
    {
        foreach (var text in new[] { "80:2a:a8:12:ab:cd", "80-2A-A8-12-AB-CD", "802aa812abcd", "802a.a812.abcd" })
        {
            Assert.True(MacAddress.TryParse(text, out var mac), text);
            Assert.Equal("802AA812ABCD", mac.Hex);
            Assert.Equal("80:2A:A8:12:AB:CD", mac.ToString());
            Assert.Equal("802AA8", mac.Oui);
        }
    }

    [Test]
    public void RejectsMalformedAndFlagsUnusable()
    {
        Assert.False(MacAddress.TryParse("80:2a:a8:12:ab", out _));
        Assert.False(MacAddress.TryParse("80:2a:a8:12:ab:cd:ef", out _));
        Assert.False(MacAddress.TryParse("zz:2a:a8:12:ab:cd", out _));
        Assert.False(MacAddress.Parse("FF:FF:FF:FF:FF:FF").IsUsableUnicast);
        Assert.False(MacAddress.Parse("01:00:5E:00:00:01").IsUsableUnicast);
        Assert.False(MacAddress.Parse("00:00:00:00:00:00").IsUsableUnicast);
        Assert.True(MacAddress.Parse("80:2A:A8:12:AB:CD").IsUsableUnicast);
    }

    [Test]
    public void SuffixMatchUsesTheLastFourHexDigits()
    {
        var mac = MacAddress.Parse("80:2A:A8:12:AB:CD");
        Assert.True(mac.EndsWith(MacSuffix.Validate("abcd").Suffix!));
        Assert.False(mac.EndsWith(MacSuffix.Validate("12AB").Suffix!));
    }
}

internal sealed class SubnetTests
{
    [Test]
    public void ComputesNetworkBroadcastAndHosts()
    {
        var subnet = Ipv4Subnet.FromAddressAndMask(IPAddress.Parse("192.168.10.57"), IPAddress.Parse("255.255.255.0"));
        Assert.Equal("192.168.10.0/24", subnet.ToString());
        Assert.Equal(IPAddress.Parse("192.168.10.255"), subnet.BroadcastAddress);
        Assert.Equal(254L, subnet.HostCount);
        var hosts = subnet.Hosts().ToList();
        Assert.Equal(254, hosts.Count);
        Assert.Equal(IPAddress.Parse("192.168.10.1"), hosts[0]);
        Assert.Equal(IPAddress.Parse("192.168.10.254"), hosts[hosts.Count - 1]);
        Assert.True(subnet.Contains(IPAddress.Parse("192.168.10.200")));
        Assert.False(subnet.Contains(IPAddress.Parse("192.168.11.1")));
    }

    [Test]
    public void LargeNetworksAreNarrowedAroundThisComputer()
    {
        var big = Ipv4Subnet.FromAddressAndMask(IPAddress.Parse("10.20.130.7"), IPAddress.Parse("255.255.0.0"));
        var narrowed = big.NarrowAround(IPAddress.Parse("10.20.130.7"), 1024);
        Assert.Equal("10.20.128.0/22", narrowed.ToString());
        Assert.True(narrowed.HostCount <= 1024);
        Assert.True(narrowed.Contains(IPAddress.Parse("10.20.130.7")));
    }

    [Test]
    public void RejectsNonContiguousMasks()
    {
        var threw = false;
        try
        {
            Ipv4Subnet.FromAddressAndMask(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("255.0.255.0"));
        }
        catch (ArgumentException)
        {
            threw = true;
        }

        Assert.True(threw);
    }
}

internal sealed class DiscoveryProtocolTests
{
    internal static byte[] BuildReply(string mac, string ip, bool includeExtras = true, bool? isDefault = true)
    {
        var fields = new List<byte>();

        void Add(byte type, byte[] value)
        {
            fields.Add(type);
            fields.Add((byte)(value.Length >> 8));
            fields.Add((byte)value.Length);
            fields.AddRange(value);
        }

        var hex = MacAddress.Parse(mac).Hex;
        var macBytes = Enumerable.Range(0, 6).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();
        Add(0x02, macBytes.Concat(IPAddress.Parse(ip).GetAddressBytes()).ToArray());
        Add(0x01, macBytes);
        if (includeExtras)
        {
            Add(0x03, System.Text.Encoding.UTF8.GetBytes("BZ.qca956x.v6.6.77.15402.240924.1623"));
            Add(0x0B, System.Text.Encoding.UTF8.GetBytes("UAP-AC-Pro"));
            Add(0x0C, System.Text.Encoding.UTF8.GetBytes("U7PG2"));
            Add(0x14, System.Text.Encoding.UTF8.GetBytes("UAP-AC-Pro-Gen2"));
            Add(0x0A, new byte[] { 0, 0, 1, 0 });
        }

        if (isDefault is { } d)
        {
            Add(0x17, new[] { d ? (byte)1 : (byte)0 });
        }

        return new byte[] { 0x01, 0x00, (byte)(fields.Count >> 8), (byte)fields.Count }.Concat(fields).ToArray();
    }

    [Test]
    public void ParsesAWellFormedReply()
    {
        var reply = Assert.NotNull(UbntDiscoveryProtocol.TryParse(BuildReply("80:2A:A8:12:AB:CD", "192.168.10.23"), IPAddress.Parse("192.168.10.23")));
        Assert.Equal(MacAddress.Parse("802AA812ABCD"), reply.Mac);
        Assert.Equal(IPAddress.Parse("192.168.10.23"), reply.ReportedAddresses.Single());
        Assert.Equal("U7PG2", reply.Model);
        Assert.Equal("UAP-AC-Pro-Gen2", reply.ModelDisplay);
        Assert.Contains("6.6.77", reply.Firmware);
        Assert.Equal(true, reply.IsFactoryDefault);
    }

    [Test]
    public void IgnoresMalformedPacketsWithoutThrowing()
    {
        var source = IPAddress.Parse("192.168.10.23");
        Assert.Null(UbntDiscoveryProtocol.TryParse(new byte[0], source));
        Assert.Null(UbntDiscoveryProtocol.TryParse(new byte[] { 0x01, 0x00, 0x00, 0x00 }, source)); // our own request echoed back
        Assert.Null(UbntDiscoveryProtocol.TryParse(new byte[] { 0x02, 0x00, 0x00, 0x00 }, source));
        Assert.Null(UbntDiscoveryProtocol.TryParse(new byte[] { 0x01, 0x00, 0x00, 0x09, 0x01, 0x00, 0x06, 0xAA, 0xBB }, source)); // truncated MAC

        var good = BuildReply("80:2A:A8:12:AB:CD", "192.168.10.23");
        for (var cut = 0; cut < good.Length; cut++)
        {
            _ = UbntDiscoveryProtocol.TryParse(good, cut, source); // must never throw
        }

        var random = new Random(1234);
        for (var i = 0; i < 2000; i++)
        {
            var junk = new byte[random.Next(0, 80)];
            random.NextBytes(junk);
            if (junk.Length > 1)
            {
                junk[0] = 1;
                junk[1] = 0;
            }

            _ = UbntDiscoveryProtocol.TryParse(junk, source);
        }
    }

    [Test]
    public async Task ScannerFindsAResponderOverRealUdp()
    {
        var responder = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var port = ((IPEndPoint)responder.Client.LocalEndPoint!).Port;
        var serve = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var request = await responder.ReceiveAsync();
                    if (request.Buffer.SequenceEqual(UbntDiscoveryProtocol.CreateRequest()))
                    {
                        var reply = BuildReply("80:2A:A8:12:AB:CD", "127.0.0.1");
                        await responder.SendAsync(reply, reply.Length, request.RemoteEndPoint);
                    }
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
            {
            }
        });

        var loopback = new LocalInterfaceInfo("lo", "loopback", IPAddress.Loopback,
            Ipv4Subnet.FromAddressAndMask(IPAddress.Loopback, IPAddress.Parse("255.255.255.0")), null, false, false);
        var result = await new UbntDiscoveryScanner(port).DiscoverAsync(new[] { loopback }, TimeSpan.FromSeconds(1.5), CancellationToken.None);
        responder.Close();
        await serve;

        var reply = result.Replies.Single();
        Assert.Equal(MacAddress.Parse("802AA812ABCD"), reply.Mac);
        Assert.Equal(IPAddress.Loopback, reply.Source);
    }
}

internal sealed class CandidateResolverTests
{
    private static readonly LocalInterfaceInfo Lan = new("Ethernet", "Intel", IPAddress.Parse("192.168.10.5"),
        Ipv4Subnet.FromAddressAndMask(IPAddress.Parse("192.168.10.5"), IPAddress.Parse("255.255.255.0")),
        IPAddress.Parse("192.168.10.1"), false, false);

    private static UbntDiscoveryReply Reply(string mac, string ip) =>
        UbntDiscoveryProtocol.TryParse(DiscoveryProtocolTests.BuildReply(mac, ip), IPAddress.Parse(ip))!;

    private static LocateResult Resolve(string suffix, UbntDiscoveryReply[] replies, NeighborEntry[] arp) =>
        CandidateResolver.Resolve(MacSuffix.Validate(suffix).Suffix!, replies, arp, new[] { Lan }, new AssistantSettings().AllUbiquitiOuis, true);

    [Test]
    public void FindsTheSingleMatch()
    {
        var result = Resolve("ABCD",
            [Reply("80:2A:A8:12:AB:CD", "192.168.10.23")],
            [new(IPAddress.Parse("192.168.10.1"), MacAddress.Parse("001122334455")), new(IPAddress.Parse("192.168.10.23"), MacAddress.Parse("802AA812ABCD"))]);
        Assert.Equal(LocateStatus.Found, result.Status);
        Assert.Equal(IPAddress.Parse("192.168.10.23"), result.Address);
        Assert.Equal(2, result.DevicesSeen);
    }

    [Test]
    public void NothingMatches()
    {
        var result = Resolve("FFFF", [Reply("80:2A:A8:12:AB:CD", "192.168.10.23")], []);
        Assert.Equal(LocateStatus.NotFound, result.Status);
    }

    [Test]
    public void TwoDevicesWithTheSameSuffixAreRefused()
    {
        var result = Resolve("ABCD",
            [Reply("80:2A:A8:12:AB:CD", "192.168.10.23")],
            [new(IPAddress.Parse("192.168.10.40"), MacAddress.Parse("3C:52:82:99:AB:CD"))]);
        Assert.Equal(LocateStatus.MultipleMatches, result.Status);
        Assert.Equal(2, result.Matches.Count);
    }

    [Test]
    public void ArpOnlyDeviceWithUbiquitiOuiIsAccepted()
    {
        var result = Resolve("ABCD", [], [new(IPAddress.Parse("192.168.10.23"), MacAddress.Parse("802AA812ABCD"))]);
        Assert.Equal(LocateStatus.Found, result.Status);
    }

    [Test]
    public void ArpOnlyDeviceWithOtherOuiIsRefused()
    {
        var result = Resolve("ABCD", [], [new(IPAddress.Parse("192.168.10.23"), MacAddress.Parse("3C:52:82:12:AB:CD"))]);
        Assert.Equal(LocateStatus.NotUnifiDevice, result.Status);
    }

    [Test]
    public void SameMacOnTwoAddressesIsRefused()
    {
        var mac = MacAddress.Parse("802AA812ABCD");
        var result = Resolve("ABCD", [], [new(IPAddress.Parse("192.168.10.23"), mac), new(IPAddress.Parse("192.168.10.24"), mac)]);
        Assert.Equal(LocateStatus.AmbiguousAddress, result.Status);
    }

    [Test]
    public void DeviceOutsideLocalSubnetsIsReported()
    {
        var result = Resolve("ABCD", [Reply("80:2A:A8:12:AB:CD", "192.168.1.20")], []);
        Assert.Equal(LocateStatus.OnOtherNetwork, result.Status);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), result.Address);
    }
}

internal sealed class DeviceCommandTests
{
    [Test]
    public void AcceptsTheConfiguredInformUrl()
    {
        Assert.True(ControllerTarget.TryCreate("http://unifi.oneacrefund.org:8080/inform", out var target, out _));
        Assert.Equal("http://unifi.oneacrefund.org:8080/inform", target!.InformUrl);
        Assert.Equal("unifi.oneacrefund.org", target.Host);
        Assert.Equal(8080, target.Port);
        Assert.Equal("mca-cli-op set-inform 'http://unifi.oneacrefund.org:8080/inform'", DeviceCommands.SetInform(target));
        Assert.Equal("ping -c 1 -W 2 'unifi.oneacrefund.org' 2>&1", DeviceCommands.ResolveCheck(target));
    }

    [Test]
    public void RejectsUnsafeOrWrongInformUrls()
    {
        string[] bad =
        [
            "", "unifi.oneacrefund.org:8080/inform", "ftp://unifi.oneacrefund.org/inform",
            "http://unifi.oneacrefund.org:8080/inform;reboot", "http://unifi.oneacrefund.org:8080/inform?x=$(reboot)",
            "http://user:pw@unifi.oneacrefund.org:8080/inform", "http://unifi.oneacrefund.org:8080/other",
            "http://bad_host$(id).org:8080/inform", "http://unifi.oneacrefund.org:8080/inform#frag",
        ];
        foreach (var url in bad)
        {
            Assert.False(ControllerTarget.TryCreate(url, out _, out var problem), $"should reject '{url}'");
            Assert.True(problem is not null);
        }
    }

    [Test]
    public void ShellQuotingNeutralisesQuotes()
    {
        Assert.Equal("'abc'", DeviceCommands.ShellQuote("abc"));
        Assert.Equal("'a'\\''b'", DeviceCommands.ShellQuote("a'b"));
        Assert.Equal("'$(reboot)'", DeviceCommands.ShellQuote("$(reboot)"));
    }

    [Test]
    public void HostsEntryCommandIsWellFormed()
    {
        ControllerTarget.TryCreate("http://unifi.oneacrefund.org:8080/inform", out var target, out _);
        Assert.Equal("printf '%s\\n' '203.0.113.10 unifi.oneacrefund.org' >> /etc/hosts",
            DeviceCommands.AppendHostsEntry(IPAddress.Parse("203.0.113.10"), target!));
    }

    [Test]
    public void ParsesDeviceInfo()
    {
        const string output = """
            Model:       UAP-AC-Pro-Gen2
            Version:     6.6.77.15402
            MAC Address: 80:2a:a8:12:ab:cd
            IP Address:  192.168.10.23
            Hostname:    UAP-AC-Pro
            Uptime:      1234 seconds

            Status:      Not Adopted (http://unifi:8080/inform)
            """;
        var info = DeviceInfoParser.Parse(output);
        Assert.Equal("UAP-AC-Pro-Gen2", info.Model);
        Assert.Equal("6.6.77.15402", info.Version);
        Assert.Equal(MacAddress.Parse("802AA812ABCD"), info.Mac!.Value);
        Assert.Equal("Not Adopted", info.StatusText);
        Assert.Equal("http://unifi:8080/inform", info.StatusUrl);
        Assert.Equal(DeviceInformState.NotAdopted, info.State);
    }

    [Test]
    public void ClassifiesStatusWording()
    {
        Assert.Equal(DeviceInformState.Connected, DeviceInfoParser.Classify("Connected"));
        Assert.Equal(DeviceInformState.Disconnected, DeviceInfoParser.Classify("Disconnected"));
        Assert.Equal(DeviceInformState.Timeout, DeviceInfoParser.Classify("Timeout"));
        Assert.Equal(DeviceInformState.Rejected, DeviceInfoParser.Classify("Server Reject"));
        Assert.Equal(DeviceInformState.InProgress, DeviceInfoParser.Classify("Unknown[11]"));
        Assert.Equal(DeviceInformState.NotAdopted, DeviceInfoParser.Classify("Not Adopted"));
        Assert.Equal(DeviceInformState.Unrecognized, DeviceInfoParser.Classify("Something new"));
    }

    [Test]
    public void ParsesNameResolutionFromPingOutput()
    {
        var ok = ResolveOutputParser.Parse("PING unifi.oneacrefund.org (203.0.113.10): 56 data bytes\n--- ping statistics ---\n1 packets transmitted, 0 packets received, 100% packet loss");
        Assert.Equal(NameResolution.Resolved, ok.State);
        Assert.Equal(IPAddress.Parse("203.0.113.10"), ok.Address);
        Assert.Equal(NameResolution.NotResolved, ResolveOutputParser.Parse("ping: bad address 'unifi.oneacrefund.org'").State);
        Assert.Equal(NameResolution.NotResolved, ResolveOutputParser.Parse("ping: unifi.oneacrefund.org: Temporary failure in name resolution").State);
        Assert.Equal(NameResolution.Unknown, ResolveOutputParser.Parse("sh: ping: not found").State);
    }

    [Test]
    public void FindsHostsEntriesAndFlagsTheLegacyMalformedLine()
    {
        const string hosts = "127.0.0.1 localhost\nunifi.oneacrefund.org unifi\n203.0.113.10 unifi.oneacrefund.org # controller\n";
        var entries = HostsFile.FindEntriesFor(hosts, "unifi.oneacrefund.org");
        Assert.Equal(2, entries.Count);
        Assert.True(entries[0].IsMalformed);
        Assert.False(entries[1].IsMalformed);
        Assert.Equal(IPAddress.Parse("203.0.113.10"), entries[1].Address);
    }

    [Test]
    public void DecidesWhetherSetInformWasAccepted()
    {
        Assert.True(DeviceCommands.SetInformAccepted(new("x", 0, "Adoption request sent to 'http://u:8080/inform'.", "", false)));
        Assert.True(DeviceCommands.SetInformAccepted(new("x", 1, "Adoption request sent to 'http://u:8080/inform'.", "", false)));
        Assert.True(DeviceCommands.SetInformAccepted(new("x", 0, "", "", false)));
        Assert.False(DeviceCommands.SetInformAccepted(new("x", 0, "Error: invalid URL", "", false)));
        Assert.False(DeviceCommands.SetInformAccepted(new("x", 127, "", "sh: mca-cli-op: not found", false)));
        Assert.False(DeviceCommands.SetInformAccepted(new("x", null, "", "", true)));
    }
}

internal sealed class SecurityTests
{
    [Test]
    public void HostKeysAreTrustedOnFirstUseThenEnforced()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hostkeys-{Guid.NewGuid():N}.json");
        try
        {
            var mac = MacAddress.Parse("802AA812ABCD");
            var store = new JsonHostKeyStore(path);
            Assert.Equal(HostKeyCheck.TrustedFirstUse, store.Check(mac, "ssh-ed25519", "AAAA"));
            store.Remember(mac, "ssh-ed25519", "AAAA");

            var reloaded = new JsonHostKeyStore(path);
            Assert.Equal(HostKeyCheck.TrustedKnown, reloaded.Check(mac, "ssh-ed25519", "AAAA"));
            Assert.Equal(HostKeyCheck.Rejected, reloaded.Check(mac, "ssh-ed25519", "BBBB"));
            Assert.Equal(HostKeyCheck.TrustedFirstUse, reloaded.Check(MacAddress.Parse("802AA8000001"), "ssh-ed25519", "BBBB"));

            reloaded.Clear();
            Assert.Equal(HostKeyCheck.TrustedFirstUse, new JsonHostKeyStore(path).Check(mac, "ssh-ed25519", "BBBB"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void PinnedKeysOverrideFirstUse()
    {
        var store = new JsonHostKeyStore(Path.Combine(Path.GetTempPath(), $"hk-{Guid.NewGuid():N}.json"),
            new Dictionary<string, string> { ["80:2a:a8:12:ab:cd"] = "SHA256:PINNED=" });
        var mac = MacAddress.Parse("802AA812ABCD");
        Assert.Equal(HostKeyCheck.TrustedKnown, store.Check(mac, "ssh-rsa", "PINNED"));
        Assert.Equal(HostKeyCheck.Rejected, store.Check(mac, "ssh-rsa", "OTHER"));
    }

    [Test]
    public void CredentialsNeverPrintTheirPassword()
    {
        var credential = new SshCredential("admin", "S3cret-Passw0rd", CredentialSource.ItProvidedThisSession);
        Assert.DoesNotContain("S3cret", credential.ToString());
    }

    [Test]
    public void LogRedactsRegisteredSecrets()
    {
        var log = new DiagnosticLog(null);
        log.AddSecret("S3cret-Passw0rd");
        log.Info("something mentioning S3cret-Passw0rd by accident");
        Assert.DoesNotContain("S3cret", string.Join("\n", log.Snapshot()));
    }

    [Test]
    public void SettingsLoadFromJsonWithCommentsAndValidate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              // comment allowed
              "InformUrl": "http://controller.example.org:8080/inform",
              "SupportContact": "the IT helpdesk",
              "VerifySeconds": 99999,
              "ExtraUbiquitiOuis": ["aa:bb:cc"],
            }
            """);
        try
        {
            var result = SettingsLoader.Load([path]);
            Assert.Equal(0, result.Problems.Count);
            Assert.Equal("http://controller.example.org:8080/inform", result.Settings.InformUrl);
            Assert.Equal(600, result.Settings.VerifySeconds);
            Assert.True(result.Settings.AllUbiquitiOuis.Contains("AABBCC"));

            File.WriteAllText(path, """{ "InformUrl": "http://x.org/inform;reboot" }""");
            Assert.True(SettingsLoader.Load([path]).Problems.Count > 0);

            File.WriteAllText(path, "{ not json");
            Assert.True(SettingsLoader.Load([path]).Problems.Count > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void DefaultsAreValidWithoutASettingsFile()
    {
        var result = SettingsLoader.Load([Path.Combine(Path.GetTempPath(), "does-not-exist.json")]);
        Assert.Equal(0, result.Problems.Count);
        Assert.Null(result.SourcePath);
        Assert.Equal("http://unifi.oneacrefund.org:8080/inform", result.Settings.InformUrl);
    }
}
