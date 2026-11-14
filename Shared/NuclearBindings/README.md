# NuclearBindings

One binding concept for every key and button setting in these mods: F1 rows bound by pressing a key, mouse button, gamepad control or any joystick button, hat or axis direction. Keys are read through Unity input, controllers through the game's Rewired.

The code is the public library [scramd/nuclear-option-bindings](https://github.com/scramd/nuclear-option-bindings) (MIT), published on nuget.org as the source-only package `NuclearOption.Bindings`. Its README describes the F1 rows, the stored row format, the behavior and the API. `NuclearBindings.props` adds the package; its files compile into each consuming plugin, so there is no helper DLL and no plugin dependency. Change the code in the library, release it, then raise the version in `NuclearBindings.props`.

## Use in a mod

```csharp
using NuclearBindings;

toggle = InputBinding.Bind(Config, Logger, "Controls", "Toggle Cockpit Page", "F8", "Switch custom/stock cockpit page.");
// once per frame, in the mod's own per-frame code:
if (toggle.IsDown()) custom = !custom;
```

Import the props file and reference `Rewired_Core`, `Assembly-CSharp-firstpass`, `Unity.TextMeshPro`, `UnityEngine.UI`, `UnityEngine.InputLegacyModule` and `UnityEngine.IMGUIModule`. NuclearMinimap (tap/hold zoom) and NuclearControllerGestures (double presses) read `Rows` for their own per-control logic.

## Checks

`tests/BindingChecks.csproj` runs the package's row parser and legacy conversions, the Minimap and ControllerGestures migrations and real BepInEx configuration files. The modpack builder runs it. `tests/ClientApiCheck` fails if an active mod references `KeyboardShortcut` or if the consumer set changes. These checks do not establish that a controller press reaches a mod in the game; the fired log line shows that.
