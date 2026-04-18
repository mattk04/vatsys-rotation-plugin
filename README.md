# Ground Display Rotation Plugin for vatSys

## What it does

This plugin adds a `Rotate View` submenu under `Tools` in Ground (ASMGCS) windows.

From that submenu controllers can:

- Enter a heading from `000` to `359` in the inline textbox directly under `Rotate View`, then press `Enter` (or click away from the textbox) to apply it.
- Select `Reset to Original` to return to the initially loaded heading for the active position.

<img width="600" alt="image" src="https://github.com/user-attachments/assets/69459ac7-39bf-4086-8d10-d6cba8f860f4" />
<img width="600" alt="image" src="https://github.com/user-attachments/assets/0f3d6f93-28e8-459e-a2e4-ddc497a9315d" />
<img width="600" alt="image" src="https://github.com/user-attachments/assets/75342b84-f817-4af6-ad5f-67d8d5457f73" />


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
