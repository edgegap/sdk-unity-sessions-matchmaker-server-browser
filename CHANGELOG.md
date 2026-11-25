# Edgegap Matchmaking SDK Changelog

Please refer to our [Github repository](https://github.com/edgegap/edgegap-unity-sdk/releases) for up-to-date changelog.

## 3.7.2 - 2026-11-25

- Matchmaking `GroupClient.ResumeMatchmaking` takes an `owner` parameter (default `false`) and restores `Owner`, so a resumed owner deletes the group on `StopMatchmaking` instead of leaving it, and a member never deletes a group it only joined.
- Matchmaking backfill `ServerAgent` creates backfills only between `AddBackfills` and `AbandonAllBackfills`: players leaving after `AbandonAllBackfills` no longer open new backfills. `AbandonAllBackfills` waits for backfill creations in flight and deletes them too, so none is left behind polling.
- Beacons that never answer are left out of `MeasureBeaconsRoundTripTime` results instead of reported as 0 (WebGL builds return no latencies). Beacons not answering ICMP are measured over UDP echo, then TCP connect time (`Ping.Protocols`, or the new `protocols` parameter).
