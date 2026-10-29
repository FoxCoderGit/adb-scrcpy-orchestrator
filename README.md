# ADB / scrcpy Orchestrator

A small .NET example of monitoring several Android/VR devices, reconnecting TCP/IP
ADB sessions and arranging scrcpy windows in a grid.

This publishes the implementation approach developed in VrFoxVideo as a new,
minimal standalone example. It does **not** contain the private club application,
device inventory, game catalog, installer or driver-management scripts.

## Preview without devices

Install .NET SDK 8+:

```sh
dotnet build
dotnet run -- --config config.example.json --dry-run
```

The dry run only prints the command plan. It never invokes ADB or scrcpy.

## Run with your own devices

Install [Android Platform Tools](https://developer.android.com/tools/releases/platform-tools)
and [scrcpy](https://github.com/Genymobile/scrcpy). Put their executable directories
on PATH, or set absolute executable paths in a private copy of the JSON config.
Authorize/pair the devices yourself, then replace the example serials:

```sh
dotnet run -- --config config.json
```

Both USB serials and `IP:PORT` serials can be monitored. TCP/IP endpoints must already
be enabled and authorized; this program does not bypass pairing or enable debugging.
Use a trusted network. Ctrl+C stops the scrcpy processes started by this instance.
`--once` performs one reconciliation and then stops those owned processes; use it
for connection diagnostics, not persistent viewing.

## The approach

1. Parse `adb devices -l` and distinguish online, offline and unauthorized devices.
2. Reconnect only configured TCP/IP endpoints, with bounded retries.
3. Start one owned scrcpy process per online device.
4. Derive window positions from a configurable grid using scrcpy's window options.
5. Restart exited mirrors with exponential backoff; bound ADB commands with timeouts.

Arguments use `ProcessStartInfo.ArgumentList` instead of shell command interpolation.
The example does not restart the global ADB server or stop unrelated scrcpy processes.
Window placement depends on the operating system/window manager and scrcpy version;
on some Wayland desktops explicit placement is not supported.

## Offline verification

```sh
dotnet run --project tests/Orchestrator.Checks.csproj
```

These checks cover parsing, grid positions and configuration errors. The process-free
dry-run command above was also checked. Live multi-headset behavior requires testing on your own hardware.

Русский: минимальный пример реализации из VrFoxVideo — ADB-переподключение,
контроль процессов scrcpy и размещение окон, без публикации личного проекта.
