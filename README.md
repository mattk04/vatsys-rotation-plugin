# Ground Display Rotation Plugin for vatSys

## What it does

This plugin adds a `Rotate View` submenu under `Tools` in Ground (ASMGCS) windows, allowing controllers to dynamically rotate each ground display as required.

From the menu, controllers can:

- Enter a magnetic heading from `000` to `359` to orient the ground window.
- Use `Save Current Orientation` to save the current display orientation for the current aerodrome, with an optional label.
- Use `Saved Orientations` to load saved orientations for the current aerodrome.
- Set a particular orientation to load automatically every time an aerodrome is selected, through the `auto-load` system.
- Select `Reset to Original` to return to the initially loaded orientation for the window.

When a saved orientation has a label, it is shown beside the heading in the list.

Saved orientations and auto-load values are stored in:

- `%USERPROFILE%\Documents\vatSys Rotation Plugin\rotations.json`

### Why use it?
The default rotations of the ground map at each location are generally sufficient for most needs. At some locations, however, it may be beneficial to rotate the display to better fit the available space (for controllers with limited screen real estate) or to better align the aerodrome with any surrounding windows (for controllers who are top down to aerodromoes like YBRM, where the default rotation is 180 degrees offset from the corresponding air display).

<img width="600" alt="image" src="https://github.com/user-attachments/assets/69459ac7-39bf-4086-8d10-d6cba8f860f4" />
<img width="600" alt="image" src="https://github.com/user-attachments/assets/0f3d6f93-28e8-459e-a2e4-ddc497a9315d" />
<img width="600" alt="image" src="https://github.com/user-attachments/assets/75342b84-f817-4af6-ad5f-67d8d5457f73" />

## Install

1. Copy `bin\Release\GroundDisplayRotationPlugin.dll` to your vatSys plugin folder.
   - Usually: `C:\Program Files (x86)\vatSys\bin\Plugins`
2. Restart vatSys.
3. Open a Ground Display (ASD) window.
4. Open `Tools > Rotate View`.
   
## Build

1. Open `GroundDisplayRotationPlugin.csproj` in Visual Studio or MSBuild.
2. Build the project in `Release` mode.

If you are using the local MSBuild on Windows, run:

```powershell
& 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe' .\GroundDisplayRotationPlugin.csproj /p:Configuration=Release
```
- The project currently references `C:\Program Files (x86)\vatSys\bin\vatSys.exe`.
- If your vatSys installation path is different, update `GroundDisplayRotationPlugin.csproj`.
- The plugin targets .NET Framework 4.7.2 and is built as `x86`.
