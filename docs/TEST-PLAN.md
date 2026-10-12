# Test plan

## A. Automated tests (run on every build)

`dotnet run --project tests/UniFiAdoption.Tests -c Release` — 64 tests, no network or device needed.

| Required scenario | Covered by |
|---|---|
| Valid and invalid MAC suffixes | `InputTests.*`, `WorkflowTests.InvalidInputStopsBeforeAnyNetworkActivity` |
| Successful discovery and configuration | `WorkflowTests.FactoryResetDeviceIsPointedAtControllerAndReportedAsPending`, `ConnectedStatusIsReportedAsSuccess`, `ArpAloneIsEnoughForAUbiquitiOui` |
| No matching device | `WorkflowTests.NoMatchingDevice`, `CandidateResolverTests.NothingMatches` |
| Multiple matching devices | `WorkflowTests.MultipleMatchingDevicesAreNotTouched`, `CandidateResolverTests.TwoDevicesWithTheSameSuffixAreRefused` |
| Wrong credentials | `WorkflowTests.WrongCredentialsAreReportedAndOnlyTheVerifiedDeviceIsTried`, `NoLoginAllowedMeansNoAttempt` |
| SSH connection failures | `WorkflowTests.SshUnavailable` |
| Controller DNS and connectivity failures | `WorkflowTests.ControllerNameNotResolvableOnTheDevice`, `ControllerUnreachableFromTheDevice`, `ControllerRejection` |
| Duplicate hostname entries | `WorkflowTests.HostsFallbackAddsOneCorrectLineAndNeverDuplicates`, `ConflictingHostsEntryIsNotOverwritten`, `DeviceCommandTests.FindsHostsEntriesAndFlagsTheLegacyMalformedLine` |
| Unsupported firmware | `WorkflowTests.UnsupportedFirmwareWithoutCliIsNotChanged`, `UnsupportedModelIsNotChanged`, `LegacyInteractiveFallbackIsUsedOnlyWithoutMcaCliOp` |
| Inform accepted, adoption pending | `WorkflowTests.FactoryResetDeviceIsPointedAtControllerAndReportedAsPending`, `VerificationPollsUntilConnected` |
| Test mode changes nothing | `WorkflowTests.TestModeNeverChangesTheDevice` |
| Security | host-key TOFU, mismatch and pinning; identity mismatch; URL injection attempts; shell quoting; password never in log or support text; settings validation |
| Robustness | discovery parser fuzzing (2,000 random packets), truncated packets, cancellation, non-contiguous masks, large-subnet narrowing, other-subnet and multi-address devices |

### Integration tests against a simulated access point

On a disposable Linux machine or container with `dropbear-bin` and `iputils-ping`:

```
sudo tests/simulated-device/setup.sh
UNIFI_IT_SSH_PORT=2222 dotnet run --project tests/UniFiAdoption.Tests -c Release -- --integration
```

These 7 tests use the real SSH.NET connector, real UDP discovery and real TCP probes. The simulated
device's output mimics UniFi firmware but is not UniFi firmware.

## B. Lab checklist on real hardware (before rollout)

Use a Windows 10 and a Windows 11 PC, a switch, a PoE injector, and at least one UAP-AC-Pro per
firmware line in the field. Record the assistant log for each step.

| # | Setup | Action | Expected |
|---|---|---|---|
| 1 | Factory-reset AC Pro, DHCP, PC on same LAN | Admin settings → test mode, run | "Test finished"; model/firmware shown; planned command is `mca-cli-op set-inform '…'`. **Check the log:** `command -v mca-cli-op` found it, and `mca-cli-op info` shows *MAC Address* and *Status* lines |
| 2 | Same | Real run | "Waiting for IT to approve it"; device appears as pending in the controller |
| 3 | Same | Adopt in controller, wait for provisioning, run again | Login refused (device now uses controller credentials). This is expected; with IT login entered: "Already connected" |
| 4 | Wrong last 4 characters | Run | "Access point not found"; no SSH in log |
| 5 | Two devices ending in the same 4 characters (spoof the MAC on a laptop if needed) | Run | "More than one device matches"; no SSH |
| 6 | AP adopted elsewhere (non-default login) | Run | "Didn't accept the login"; then enter login in Admin settings → success |
| 7 | Block TCP 8080 to the controller at the router | Real run | "Can't reach the controller yet" |
| 8 | Router DNS broken for the controller name | Real run | "Can't find the controller yet"; with `HostsFallbackControllerIp` set: one correct `/etc/hosts` line, none added on repeat runs |
| 9 | AP without DHCP (192.168.1.20) | Run | "Not found" or "on a different network"; no changes |
| 10 | PC on guest Wi-Fi | Run | "Not found", with the guest-network advice |
| 11 | Factory-reset an AP that this PC configured before | Run | "Security check failed"; after *Forget saved fingerprints*: success |
| 12 | Oldest firmware in the field | Test mode | Works, or "can't be set up automatically" with the reason in the log |
| 13 | Click Stop during search | Stop | "Stopped. Nothing was changed" |
| 14 | 125%/150% display scaling, small laptop screen | Open | Text wraps; window fits; scrolls if needed |
| 15 | Unsigned vs signed download | Download and open | Note SmartScreen behaviour for the staff instructions |

Firmware status wording seen in step 1–3 and 7 should be compared with `DeviceInfoParser.Classify`;
add any new wording there and as a unit test.
