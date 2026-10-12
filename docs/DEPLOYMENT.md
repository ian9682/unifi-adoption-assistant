# Deployment guide for IT

## 1. Before the first rollout

1. **Controller: set the Inform Host override.** In the UniFi Network application, open the system or
   advanced settings. The exact place depends on the version. Enable *Inform Host / Override Inform Host*
   and set it to `unifi.oneacrefund.org`.
   Without it, the controller may give the access point a different address (for example an internal
   IP) when you click **Adopt**, and the access point then loses contact. That is the usual cause of
   "it adopted, then went offline". It also explains the old advice to run `set-inform` twice.
2. **Controller reachability.** From a remote site, `http://unifi.oneacrefund.org:8080/inform` must be
   reachable on TCP 8080. DNS for `unifi.oneacrefund.org` must work through the sites' routers.
3. **Lab validation.** Run the lab checklist in [TEST-PLAN.md](TEST-PLAN.md) with at least one factory-reset AC Pro on the
   firmware versions you have in the field. Start with test mode, then do a real run.
4. **Optional settings.** Create a `settings.json` only if a default must change (see the README).
   Typical choices are `SupportContact`, `SupportedModelPrefixes: ["UAP-AC-Pro"]`, and for sites with
   broken DNS, `HostsFallbackControllerIp`.

## 2. Build a release

On a Windows build machine, or let GitHub Actions do it:

```powershell
./build/publish.ps1 -Version 1.0.0 -SettingsFile .\deploy\settings.json
```

Outputs in `artifacts\`:

* `UniFiAdoptionAssistant-Setup.exe` — per-user installer. It needs no admin rights, creates Start menu
  entries (normal and test mode) and an optional desktop shortcut.
* `UniFiAdoptionAssistant-win-x86.zip` — portable copy. Unzip it and run the exe.
* `SHA256SUMS.txt`

**Code signing is strongly recommended.** Unsigned downloads show *Windows protected your PC*
(SmartScreen), which remote staff may not get past. Sign with an organisation code-signing certificate
(`-CertificatePath`/`-CertificatePassword`, or the CI secrets `SIGNING_CERT_PFX_BASE64` and
`SIGNING_CERT_PASSWORD`). A cloud signing service such as Azure Artifact Signing also works; add its
step after publishing.

## 3. Host it at a stable link

Keep the file names constant (`UniFiAdoptionAssistant-Setup.exe`), so one link always serves the newest
version. Pick one option:

* **GitHub Releases (public repository).** Push a tag (`git tag v1.0.0 && git push --tags`). The workflow
  creates the release. This link always points to the newest release:
  `https://github.com/<org>/<repo>/releases/latest/download/UniFiAdoptionAssistant-Setup.exe`
  It does not work for private repositories, because staff would need a GitHub login.
* **SharePoint / OneDrive.** Upload the installer to a library, create an *Anyone with the link* or
  *People in your organisation* link, and replace the file in place for new versions. Use *Manage
  versions → Replace* or upload with the same name; the link stays the same. Organisation-only links
  require staff to sign in.
* **Azure Blob Storage or any web server.** Upload to a fixed path such as
  `https://<account>.blob.core.windows.net/tools/UniFiAdoptionAssistant-Setup.exe` with public read on
  that container. Overwrite the file for new versions. Publish `SHA256SUMS.txt` next to it.

Share the link with a short instruction ("Download, open, follow the screen"). For sites with slow
connections, the portable zip can be copied by USB stick.

## 4. Supporting a site remotely

* Ask the person to click **Copy details for IT** and paste the result into chat or email. It contains
  the outcome, the device MAC/IP/model/firmware/status, notes, and the recent log, and never a password.
* If the factory login is refused, the device was set up before. Either open **IT settings** with the
  person (screen share or dictation), enter the device login, and choose *Use this login*; or have the
  device factory-reset (hold the reset button about 10 seconds while powered).
* After a factory reset, the device's SSH fingerprint changes. If the same PC configured it before,
  use **IT settings → Forget saved fingerprints**.
* Logs are in `%LOCALAPPDATA%\UniFiAdoptionAssistant\logs` (**Open log folder**).

## 5. Updating

Build with a new `-Version`, sign, and replace the hosted file. Installing a new version over an old one
keeps logs, saved fingerprints and any saved login.
