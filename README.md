# Ground Display Rotation Plugin for vatSys

## What it does

This plugin adds a `Rotate View` submenu under `Tools` in Ground (ASMGCS) windows, allowing controllers to dynamically rotate each ground display as required.

From the menu, controllers can:

- Enter a magnetic heading from `000` to `359` (using a text input or slider control) to orient the ground window.
- Use `Save Current Orientation` to save the current display orientation for the current aerodrome, with an optional label.
- Use `Saved Orientations` to load saved orientations for the current aerodrome.
- Set a particular orientation to load automatically every time an aerodrome is selected, through the `auto-load` system.
- Select `Reset to Original` to return to the initially loaded orientation for the window.

When a saved orientation has a label, it is shown beside the heading in the list.

Saved orientations and auto-load values are stored in:

- `%USERPROFILE%\Documents\vatSys Rotation Plugin\rotations.json`

### Why use it?
The default rotations of the ground map at each location are generally sufficient for most needs. At some locations, however, it may be beneficial to rotate the display to better fit the available space (for controllers with limited screen real estate) or to better align the aerodrome with any surrounding windows (for controllers when top down.

#### Maximise Screen Real-estate
<img width="600" alt="sy1" src="https://github.com/user-attachments/assets/5c30f115-7b89-47b6-aeb5-1c1eadc22e19" />
<img width="600" alt="sy2" src="https://github.com/user-attachments/assets/be7967cb-6810-46be-b980-74e75686cf2b" />
<img width="600" alt="sy3" src="https://github.com/user-attachments/assets/e22a024d-268b-4921-938d-0ed20474bcee" />


#### Align Ground Windows with the Air Window while Top Down
<img width="700" alt="cb" src="https://github.com/user-attachments/assets/df6c1d43-7452-4e58-aed3-c8c560b6a042" />

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
