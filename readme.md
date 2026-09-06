<h3>
    <p align="center">COMPEL</p>
    <p>Heroes Of Newerth match server launcher to connect to the Project KONGOR services.</p>
    <p>If you would like to support the development of this project and buy me a coffee, please consider one of the following options: <a href="https://github.com/sponsors/K-O-N-G-O-R">GitHub Sponsors</a>, <a href="https://www.patreon.com/newerth">Patreon</a>, <a href="https://paypal.me/MissingLinkMedia">PayPal</a>. 💚</p>
</h3>

<hr/>

<br/>

## Overview

COMPEL is a cross-platform, Native-AOT ASP.NET Core application that launches and supervises Heroes Of Newerth match servers on a host machine. Some of its functions are the following:

- synchronises the match server distribution from the CDN (incremental, hash-verified, atomic) into its own directory, then launches and supervises the Heroes Of Newerth manager process, restarting it if it exits unexpectedly
- runs a managed, cross-platform UDP proxy when enabled, forwarding the public game and voice ports to the local server ports and authenticating clients with the challenge protocol they require on that port range
- answers the master server's UDP latency pings
- exposes an HTTP control plane so it can be pinged for latency, queried by the master server, and managed remotely by the host operator

It ships as a single self-contained binary plus a self-describing `COMPEL.json`. The match server distribution is synchronised into the same directory as the executable.

On Linux, a compatible LAS distribution may provide `compatibility/libhon-rng-forksafe.so`. COMPEL loads this ABI-specific compatibility library into CowMaster's child process through `LD_PRELOAD`, and CowMaster's forked slaves inherit it. The library is never loaded into COMPEL itself. Its presence in the LAS manifest enables the workaround; omitting it leaves the Linux launch environment unchanged.

## Configuration

All host-facing configuration lives in a single `COMPEL.json` beside the executable, in the self-documenting `{ "Value": …, "Description": … }` format. On first run it is generated with defaults and descriptions, and COMPEL stops so it can be edited. Every value is validated at startup, and all startup problems are reported together.

| Key                     | Purpose                                                                                                                                                                      |
| ----------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `UserName` / `Password` | The Project KONGOR host account credentials.                                                                                                                                 |
| `Instances`             | Number of match server instances (1 … logical processor count).                                                                                                              |
| `Gateway`               | Address advertised by the match servers: `localhost`, `PUBLIC` (auto-detect public IP), an IPv4 address, or a host name.                                                    |
| `MasterServer`          | Master server endpoint used for authentication and registration, such as `api.kongor.net` or `192.168.0.186:5555`.                                                          |
| `Location`              | TMM region: `USW`, `USE`, `EU`, `AU`, `BR`, `RU`, `SEA`, or `NEWERTH`.                                                                                                       |
| `ServerNamePrefix`      | The base match server name. The instance index is appended.                                                                                                                  |
| `UseProxy`              | Whether to run the proxy (public port remapping + client challenge authentication. Defaults to `true`).                                                                      |
| `PortRangeOffset`       | Offset into the game/voice port windows. `base + offset + instances` must stay within the 100-port window.                                                                   |
| `RuntimeArtefactsPath`  | `DEFAULT` (the host account's profile) or a fully qualified path. Windows only, as the runtime artefacts path is hard-coded for the Linux server distribution.               |
| `CDNHost`               | Base URL containing the `las` and `was` match-server distributions. This must be set explicitly.                                                                            |
| `CDNSynchronisation`    | Whether to synchronise the distribution from the CDN on startup. Set `false` to skip the initial synchronisation for development/testing (the `/sync` endpoint still works). |
| `AuthenticationToken`   | Bearer token gating the management endpoints. Leave as `...` to disable remote management.                                                                                   |
| `ControlPlanePort`      | TCP port for the HTTP control plane (default `8080`).                                                                                                                        |

## Running

```
dotnet run --project source/COMPEL
```

On first run COMPEL writes a default `COMPEL.json` next to the executable and exits. Set `UserName`, `Password`, and `CDNHost`; set `Gateway` to the address clients use for the match-server host and `MasterServer` to the NEXUS master-server endpoint. Set `AuthenticationToken` to enable remote management. Logs are written to the console and to a single `COMPEL.log` beside the executable.

### Debian 13 x64 dependencies

On a fresh **Debian 13 amd64** VPS, run this once as root from the directory containing COMPEL, before starting the servers:

```sh
sudo ./COMPEL --install-dependencies
```

This standalone setup command works without `COMPEL.json`, CDN access or a master-server connection. It exits after setup and does not start COMPEL's services. Stop any COMPEL process using the same installation first; setup uses the existing installation lock.

Setup installs missing `libfontconfig1` and `libfreetype6` packages through the configured APT repositories, plus the legacy `libncurses5`/`libtinfo5` **6.4-4 amd64** compatibility packages from Debian's official HTTPS mirror. Both legacy downloads must match the SHA-256 checksums published on Debian's [libncurses5](https://packages.debian.org/bookworm/amd64/libncurses5/download) and [libtinfo5](https://packages.debian.org/bookworm/amd64/libtinfo5/download) download pages before APT runs. It does not add a Bookworm repository, substitute ABI-6 symlinks, or request package removals or downgrades. Already installed packages are left alone; if everything is installed, no downloads or APT operations are needed. APT errors, unavailable downloads and checksum failures return a non-zero exit code. Review APT's output if installation fails; package installation is not an atomic transaction.

**Normal startup never installs packages.** After distribution preparation (including the existing bundled-FreeType migration), Debian 13 x64 launches run a read-only loader check against the manager and available x64 server libraries/HCon helper. Missing dependencies or a failed probe pause manager launches and produce an actionable console/log error rather than repeatedly spawning a binary that cannot load. COMPEL's control plane stays available, with `ManagerRunning` false in `/status`. Stop COMPEL, run setup, then start it normally; after repairing dependencies manually, `/instances/start` or `/instances/restart` also retries the check. The preflight checks library loading, not complete runtime or gameplay compatibility.

The **new installer and preflight** are restricted to Linux with exact `ID=debian`, `VERSION_ID=13`, and native x64 OS/process architecture. The installer additionally requires root and `dpkg` architecture `amd64`. Windows, ARM, other Debian releases, Ubuntu and other derivatives keep their existing startup behaviour; explicitly requesting setup on an unsupported platform fails without making changes. Existing Linux FreeType and RNG workarounds are unchanged.

## Control Plane

| Method | Route                                                       | Authentication | Purpose                                                                              |
| ------ | ----------------------------------------------------------- | -------------- | ------------------------------------------------------------------------------------ |
| `GET`  | `/ping`                                                     | none           | Latency probe.                                                                       |
| `GET`  | `/health`, `/alive`                                         | none           | Readiness and liveness.                                                              |
| `GET`  | `/status`                                                   | bearer         | Configuration, ports, distribution version, sync state, manager/proxy state, uptime. |
| `POST` | `/sync`                                                     | bearer         | Trigger a CDN re-synchronisation.                                                    |
| `POST` | `/instances/start`, `/instances/stop`, `/instances/restart` | bearer         | Manage match server lifecycles.                                                      |

Authenticate management requests with `Authorization: Bearer <AuthenticationToken>`, using the `AuthenticationToken` from `COMPEL.json`.

## Building & Publishing

Requires the .NET 10 SDK. A native, self-contained release is produced per platform via the publish profiles, or locally with the helper script (PowerShell 7+; on Windows, the Visual Studio C++ build tools are required for the Native AOT link step):

```
pwsh scripts/Publish-Native-AOT-Release.ps1
```

Tagged pushes (`vX.Y.Z`) trigger `.github/workflows/publish-release.yml`, which builds all four platform targets (Windows and Linux, each × x64 and arm64) and attaches one zip per platform to a single GitHub Release.

## Testing

```
dotnet test --solution source/COMPEL.slnx
```

The `COMPEL.Tests` project (TUnit) covers the port arithmetic, manager arguments, configuration validation and loading, the ping and proxy-challenge wire formats, and the proxy relay over loopback. The `global.json` at the repository root selects the Microsoft Testing Platform runner that TUnit requires. `.github/workflows/run-unit-tests.yml` runs the suite on Windows and Linux for every pull request to `main`.

## Solution Layout

```
scripts/    Native AOT release helper
native/     Reproducible native compatibility libraries and their isolated test harnesses
source/     COMPEL.slnx, Directory.Build.props/.targets, .editorconfig, the COMPEL project, and the COMPEL.Tests project
```

<br/>
