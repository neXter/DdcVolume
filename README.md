# DdcVolume

A small Windows tray app that controls your monitor's built-in speaker volume over DDC/CI (VCP code `0x62`).

## Usage

- **Left-click** the tray icon: slider popup (mouse wheel and arrow keys work, `Esc` closes).
- **Right-click** the tray icon: select monitor, mute, set a startup volume, start with Windows, refresh monitors, about, exit.
- **Hotkeys**:
  - `Ctrl+Alt+Up`: volume up
  - `Ctrl+Alt+Down`: volume down
  - `Ctrl+Alt+M`: mute / unmute

The tray icon shows the current volume percentage. Hotkeys and step size can be changed in `Config` in [Program.cs](Program.cs).

## Requirements

- Windows 10 or 11
- A monitor with speakers that supports DDC/CI (enable it in the monitor's on-screen menu if needed)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build, or the .NET 10 Desktop Runtime to run a published exe (the build is framework-dependent, not self-contained)

## Build

```
dotnet build -c Release
```

The executable ends up in `bin\Release\net10.0-windows\win-x64\DdcVolume.exe`.

To produce a single-file exe:

```
dotnet publish -c Release
```

The result is in `bin\Release\net10.0-windows\win-x64\publish\DdcVolume.exe`.

## Notes

- Settings (selected monitor, startup volume) are stored in `HKCU\Software\DdcVolume`.
- "Start with Windows" adds an entry under `HKCU\...\Run`.
- Only monitors that answer a volume query are listed. If none are found, the app retries every 10 seconds.
- Only one instance runs at a time.

## About

Built by Pieter Walsweer (2026) with the help of Claude by Anthropic.
