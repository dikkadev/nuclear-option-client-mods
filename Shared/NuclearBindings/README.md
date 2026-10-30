# NuclearBindings

One binding concept for every key and button setting in these mods. It is source only: `NuclearBindings.props` compiles it into each consuming plugin, so there is no helper DLL and no plugin dependency.

## Why

BepInEx `KeyboardShortcut` reads Unity's legacy input. The game reads controllers through Rewired, and Unity's `JoystickButtonN` codes do not see them: a gamepad button bound to a `KeyboardShortcut` does nothing. This helper reads keys and mouse buttons the way BepInEx does (Unity input) and every controller through Rewired. It grew out of NuclearMinimap 0.3's "press the control to bind" device rows.

## In F1

Each action is one setting. It lists the bound controls by name, each with **Remove**, and an **Add binding** button. The manager's normal reset button restores the default.

**Add binding** listens to every device at once for 15 seconds. Escape, closing F1 or losing focus cancels it.

| You press | Stored as | Shown as |
| --- | --- | --- |
| A key, with Ctrl, Shift or Alt held for a combination | `Ctrl+F7` | Ctrl + F7 |
| A modifier on its own (release it without pressing another key) | `RightControl` | RightControl |
| Mouse button 3 to 7 (left and right click operate F1) | `Mouse3` | Mouse button 4 |
| A button, bumper, trigger, D-pad direction, stick click, Start or Back on a gamepad Rewired recognizes | `Gamepad:ButtonSouth` | Gamepad A / Cross |
| A button, POV hat direction or axis direction past halfway on any other device | `Device:Joystick\|guid\|...` | VKBsim Gladiator EVO R: Button 24 |

Capture waits until the device's axes have settled for 0.3 seconds and only takes a fresh press. A button or switch that is already held must be released first. A control that another binding of the same mod already uses is rejected.

A setting holds its rows separated by `;`, so it can also be edited in the `.cfg` file. Old BepInEx values convert once when the mod loads: `F7 + LeftControl` becomes `Ctrl+F7`, `None` becomes unbound, and `JoystickButton0` to `JoystickButton9` become the gamepad buttons in Unity's XInput order (A, B, X, Y, LB, RB, Back, Start, LS, RS). Unreadable rows stay visible as "Unreadable" and are ignored.

## Behavior

- **Additive.** A binding never consumes the game's own binding for the same control. NuclearMinimap's zoom is the one exception: its existing **Consume Zoom Bindings** option replays a short tap as the native action and takes a hold for zoom.
- **Ignored** without game focus, while a text field or chat has focus, while F1 or the pause menu is open, while loading, and while the full map is open unless the mod allows it. Mods add their own conditions, for example "only while spectating".
- **Modifiers are exact.** `Ctrl+F7` needs Ctrl (either side) and no Shift or Alt; `F7` does not fire while Ctrl, Shift or Alt is held. Other keys may be held, so flight keys do not block a binding.
- **Gamepad rows** apply to every gamepad assigned to the player. On an assigned controller without a gamepad layout, D-pad rows use its directional pad or POV hat (diagonals count for both directions).
- **Device rows** belong to one device: Rewired's persistent device GUID, or a unique hardware match when there is no GUID. A disconnected device's rows stay saved and show "(not connected)"; they work again when it reconnects. Identical devices that cannot be told apart stay inactive. A device row on a joystick replaces the gamepad rows of the same binding on that joystick.
- **Release first.** After a device change, a settings change, a blocked moment or a frame in which the mod did not read the binding, a held control must be released before it fires again.
- **Log.** The first time each control fires, the mod logs `Binding '<name>' fired from <control> (device <name>)`. When a binding is first read and whenever controllers change, it logs the controllers it can see (bounded).

## Use in a mod

```csharp
using NuclearBindings;

toggle = InputBinding.Bind(Config, Logger, "Controls", "Toggle Cockpit Page", "F8", "Switch custom/stock cockpit page.");
// once per frame, in the mod's own per-frame code:
if (toggle.IsDown()) custom = !custom;
```

`IsDown()` is true on the frame the binding goes from released to held; `IsHeld()` is the held state. Call them every frame you care about. `allowFullMap: true` keeps a binding active on the full map. `Rows` exposes the parsed rows for mods that need their own per-control logic (NuclearMinimap tap/hold, NuclearControllerGestures double presses); call `LogFired` from there. Import the props file and reference `Rewired_Core`, `Assembly-CSharp-firstpass`, `Unity.TextMeshPro`, `UnityEngine.UI`, `UnityEngine.InputLegacyModule` and `UnityEngine.IMGUIModule`.

When a setting changes type from `KeyboardShortcut`, keep its section and key: `Bind` converts the saved text in place. To move values from settings that no longer exist, use `BindingMigration.TakeLegacy` (reads and removes an old key) and `BindingMigration.IsSaved`, then save once.

## Checks

`tests/BindingChecks.csproj` runs the row parser, the legacy conversions, the Minimap and ControllerGestures migrations and real BepInEx configuration files. The modpack builder runs it. `tests/ClientApiCheck` fails if an active mod references `KeyboardShortcut` or if the consumer set changes. These checks do not establish that a controller press reaches a mod in the game; the fired log line shows that.
