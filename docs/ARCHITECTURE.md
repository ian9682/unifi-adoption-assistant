# Architecture and technical risks

## Version 2 in one paragraph

`AssistantHost` wires together a `DeviceInventory` (one record per MAC address), a `NetworkMonitor`
(discovery broadcast, ARP sweep, controller check, and read-only checks on access points that are due),
a `BatchRunner` (concurrency, re-validation, retries, cancellation) and a `DeviceAdopter` (the
per-device steps, shared by every path). The `Classifier` turns the facts about each access point into
one of six plain-language groups. The window only reads the inventory's snapshots on a timer and
starts batches.

## Shape

```
UniFiAdoption.App (WinForms, net10.0-windows)          presentation only
        │ AdoptionWorkflow.RunAsync(request, progress, cancel)
        ▼
UniFiAdoption.Core (net10.0)
  Networking   MacAddress, MacSuffix, Ipv4Subnet, SystemLocalNetworkProvider
  Discovery    UbntDiscoveryScanner (UDP 10001), WindowsArpScanner (SendARP), CandidateResolver
  Ssh          TcpSshBannerProbe, SshNetConnector (SSH.NET)
  Device       ControllerTarget (validated URL), DeviceCommands (fixed command set), parsers
  Security     JsonHostKeyStore (TOFU per MAC + pinning), DpapiCredentialStore
  Workflow     AdoptionWorkflow, outcomes and plain-language messages, ControllerProbe
  Logging      DiagnosticLog (redacting, daily file)
```

Every external effect sits behind an interface (`ILocalNetworkProvider`, `IUbntDiscovery`,
`INeighborScanner`, `IControllerProbe`, `ISshBannerProbe`, `ISshConnector`, `IHostKeyStore`,
`ICredentialStore`). The workflow is therefore tested end to end with a simulated access point, and
with the real SSH and UDP implementations against a simulated device.

## Technology choices

* **C# / .NET 10, Windows Forms.** Native, small surface, no runtime install when published
  self-contained. .NET 10 rather than 8 because .NET 8 support ends in November 2026.
* **Single-file, self-contained, win-x86.** One exe, no prerequisites, no admin rights; x86 runs on
  32- and 64-bit Windows. The trade-off is size (tens of MB, compressed), accepted for zero setup.
* **SSH.NET** (MIT) rather than Windows' `ssh.exe`: password login without a terminal, host-key
  callback for verification, and support for the older key-exchange (`diffie-hellman-group1-sha1`)
  and host-key (`ssh-rsa`) algorithms that older UniFi firmware may still offer.
* **Two discovery methods.** Ubiquiti discovery identifies Ubiquiti firmware and reports MAC/IP before
  any login. The ARP sweep finds devices that ignore ping or whose discovery reply is blocked. Both are
  available to a standard user; neither needs firewall changes.

## Safety rules enforced in code

* Nothing is sent to a device unless exactly one MAC matches and it looks like a Ubiquiti device.
* Credentials are only tried against that one verified address, at most three logins.
* After login, the device must report the expected MAC itself before anything is changed, and only
  then is its host key recorded.
* A changed host key, a different MAC, or an unsupported model or firmware stops the run.
* Commands are fixed strings. The only variable parts come from validated settings: the inform URL
  and host must pass strict validation, and the hosts IP must be an IPv4 address. They are also
  single-quoted. User input never reaches the device. The Windows side starts no processes except
  opening the log folder.
* Test mode stops before the first write.
* `/etc/hosts` is only touched when configured, only when the name does not resolve, and never
  duplicated or overwritten.
* Adoption is reported only from the device's own status, never from an SSH exit code.

## Main technical risks

| Risk | Mitigation |
|---|---|
| Firmware output differs from expectations (`mca-cli-op` path, `info` wording) | Keyword-based parsing, raw output logged, unknown wording reported as "pending" not "success"; lab checklist |
| `mca-cli-op` missing on some firmware | Interactive `mca-cli` fallback in a pseudo-terminal (tested against a simulated CLI); can be disabled |
| Factory login only works on reset devices | Clear message; IT can enter a login; discovery's "default" flag is logged |
| Controller pushes a different inform host after adoption | Deployment guide: set the controller's Inform Host override |
| PC and access point on different VLANs / guest Wi-Fi isolation | Detected as "not found" or "different network" with plain advice |
| Access point without DHCP lease (192.168.1.20) | Reported as "on a different network"; no changes to the PC's network settings |
| Large subnets | ARP sweep limited to 1,024 addresses around the PC (configurable) |
| Unsigned exe triggers SmartScreen | Signing supported in build script and CI |
| Trust on first use cannot protect the very first connection | Device identity confirmed by MAC from inside; optional pinned fingerprints |
