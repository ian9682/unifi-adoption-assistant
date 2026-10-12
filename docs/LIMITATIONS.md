# Known limitations and firmware assumptions

## The adoption rule (version 2)

The app treats "SSH open and factory password refused" as adopted. That is the ITO Networks Team's
rule, and it fits how the controller behaves: adoption replaces the factory password. Two consequences
to keep in mind:

* An access point still configured by an old or different controller also refuses the factory
  password, so it is shown as adopted. If an access point in **Already adopted** has no Wi-Fi, it may
  need a factory reset by the ITO Networks Team.
* An access point with SSH closed is never counted as adopted. It is shown under **Needs a look**.

## Firmware assumptions (to confirm in the lab, see TEST-PLAN.md part B)

* The access point runs UniFi AP firmware with an SSH server (dropbear) on port 22 and a
  root-equivalent login shell (BusyBox `ash`).
* A factory-reset device accepts the documented default login `ubnt` / `ubnt`. An adopted device
  uses the device credentials set by its controller instead.
* `mca-cli-op set-inform <url>` and `mca-cli-op info` exist (the non-interactive forms of the
  `set-inform` and `info` commands). Without `mca-cli-op`, the interactive `mca-cli` is used, if
  present, and the status cannot be read back.
* `mca-cli-op info` prints `Key: value` lines including `Model`, `Version`, `MAC Address` and
  `Status: <state> (<inform url>)`. States are classified by keyword: connected, timeout, reject,
  not adopted, unknown or adopting. Unrecognised wording is reported as pending, never as success.
* BusyBox `ping` prints `PING <host> (<ip>)` when the name resolves and `bad address` when it does not.
* The device answers Ubiquiti discovery on UDP 10001. If it does not, a Ubiquiti MAC prefix (OUI) is
  required instead. The built-in OUI list covers common prefixes; add others with `ExtraUbiquitiOuis`.
* The model reported by the device starts with `UAP` (AC Pro: `UAP-AC-Pro…`).

None of these have been checked against a physical UAP-AC-Pro during development; they were exercised
against a simulated device only.

## Network limitations

* The PC and the access point must be on the same local network segment (same VLAN/subnet). Discovery
  does not cross routers. Guest Wi-Fi with client isolation will not work.
* The access point needs a DHCP address. Without one it uses 192.168.1.20. The assistant does not
  change the PC's network settings to reach that address.
* On networks larger than 1,024 addresses, only the 1,024 addresses around the PC are swept by ARP.
  Ubiquiti discovery still covers the whole broadcast domain.
* Windows Firewall normally allows the replies to the discovery broadcast. If security software blocks
  it, the ARP sweep still works. If both are blocked, the assistant reports that the search was
  blocked.
* The controller check from the PC is a hint only; what counts is the status the access point reports.

## Behaviour limitations

* "Connected" relies on the device's own status line. Adoption still has to be approved in the
  controller unless the controller adopts automatically.
* After adoption the factory login stops working, so running the assistant again on an adopted
  device needs an IT-entered login.
* An `/etc/hosts` entry (only with `HostsFallbackControllerIp`) is lost when the access point
  restarts. Fix DNS at the site for a lasting solution.
* Trust on first use cannot protect the very first SSH connection to a device. Identity is then
  confirmed by the MAC address the device reports, which a malicious device could fake. Use
  `PinnedHostKeys` if this matters.
* A changed host key (for example after a factory reset) blocks the run until IT clears the saved
  fingerprints on that PC.
* Only IPv4.
* English only.
* The installer and exe are unsigned unless a certificate is provided at build time.
* Version 2 needs .NET Framework 4.8. It is built into Windows 10 version 1903 and later and into
  Windows 11. Older Windows 10 builds (for example LTSC 2019) need it installed once from Microsoft.
* The blue SmartScreen warning cannot be removed from inside the program; see DEPLOYMENT.md.
