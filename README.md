# Ground Display Rotation Plugin for vatSys

## What it does

This plugin adds a `Rotate View` submenu under `Tools` in Ground (ASMGCS) windows.

From that submenu controllers can:

- Select `Set Rotation Heading` and enter a heading from `000` to `359`.
- Select `Reset to Original` to return to the initially loaded heading for the active position.

Rotation changes are applied to the specific ground window/control that invoked the command.

## Build

1. Open `GroundDisplayRotationPlugin.csproj` in Visual Studio or MSBuild.
2. Build the project in `Release` mode.

If you are using the local MSBuild on Windows, run:

```powershell
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe' .\GroundDisplayRotationPlugin.csproj /p:Configuration=Release
```

## Install

1. Copy `bin\Release\GroundDisplayRotationPlugin.dll` to your vatSys plugin folder.
   - Usually: `C:\Program Files (x86)\vatSys\bin\Plugins`
2. Restart vatSys.
3. Open a Ground Display (ASD) window.
4. Open `Tools > Rotate View`.

## Notes

- The project currently references `C:\Program Files (x86)\vatSys\bin\vatSys.exe`.
- If your vatSys installation path is different, update `GroundDisplayRotationPlugin.csproj`.
- The plugin targets .NET Framework 4.7.2 and is built as `x86`.
