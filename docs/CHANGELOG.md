# Changelog

## 2.0.0

- **Live list.** Access points are found automatically when the app opens and sorted into Ready,
  Setting up, Waiting for approval, Needs a look, Checking and Already adopted. Adopted ones are
  folded away.
- **Adoption rule (agreed with the ITO Networks Team):** SSH open and the factory password refused
  means the access point is adopted. It is used both to fold away adopted access points and to detect
  approval after a setup.
- **Set up several at once.** Ready access points are ticked automatically. Three run at a time, with
  re-validation before each, two automatic retries for temporary failures, and a clean Stop.
- **Approval hand-off.** One click copies a message for the ITO Networks Team listing the access points
  to approve. The summary updates by itself as they are approved.
- **Search by MAC.** The search box filters the list (2–12 characters). It explains no-match and
  several-matches cases in plain words.
- **Small download.** The app targets .NET Framework 4.8 (built into Windows 10 1903+ and 11), so the
  exe is a few MB instead of 48 MB. SSH.NET is packed inside with Costura.
- **Fingerprint rule.** A changed SSH fingerprint (normal after a factory reset) no longer blocks the
  public factory login; it still blocks IT-entered logins and IT-pinned keys.
- New outcome **Already adopted** replaces "login refused" when the factory password is refused.
- The engine is split into reusable steps (`DeviceAdopter`) used by checks, batches and the
  single-device flow.

## 1.0.0

- Single-device wizard: type 4 characters, find, check, `set-inform`, read back the status.
