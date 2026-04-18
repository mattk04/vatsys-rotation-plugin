# Ground Display Rotation Plugin for vatSys

## What it does

This plugin adds a new menu item in the ground window, allowing controllers to enter a rotation angle and rotate the display.

## Build

1. Open `GroundDisplayRotationPlugin.csproj` in Visual Studio or MSBuild.
2. Build the project in `Release` mode.

If you are using the local MSBuild on Windows, run:

```powershell
& 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe' .\GroundDisplayRotationPlugin.csproj /p:Configuration=Release
```

## Install

1. Copy `bin\Release\GroundDisplayRotationPlugin.dll` to your vatSys plugin folder.
   - Usually: `C:\Program Files (x86)\vatSys\bin\Plugins`
2. Restart vatSys.
3. Open a Ground Display (ASD) window.
4. Open `Tools > Ground Rotation` and choose an angle.

## Notes

- The project currently references `C:\Program Files (x86)\vatSys\bin\vatSys.exe`.
- If your vatSys installation path is different, update `GroundDisplayRotationPlugin.csproj`.
- The plugin uses the `ASD` window menu registration API from vatSys.
