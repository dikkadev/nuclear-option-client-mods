# NuclearControllerGestures

Independent controller double presses for Nuclear Option's regular BepInEx 5 client. No other Nuclear mod is required.

All sixteen controls start **Unbound**. In the mod's configuration, select a native button action for each named control. For example, set **DPadLeft** to **Eject**, then press and release left twice. The action runs after the second release. The dropdown comes from the game's current assignable button actions; axis actions are excluded.

Supports cardinal D-pad directions, four face buttons, shoulders, triggers, stick buttons, Select and Start on recognized gamepads. Other assigned controllers use their first declared D-pad, or their first POV hat when no D-pad is declared; additional hats do not contribute to the gesture. Diagonals cancel D-pad gestures. Each gesture belongs to one physical controller; presses on separate controllers cannot combine.

Normal game bindings still run on both presses. This mod adds an action and does not consume, delay or replace those bindings. Choose controls whose existing bindings are appropriate. Native action evaluation preserves the game's press/release behavior and restrictions; a double press sends a short action pulse, not a held action. Eject uses the game's native Eject action.

Defaults: maximum gap **0.30 seconds** from first release to second press, maximum duration **0.25 seconds** for each press, and **0.30 seconds** cooldown after success. These three timings are independently configurable. Holds cancel. Input pauses in menus, text entry, configuration windows, the expanded map, and when the game lacks focus or an active local aircraft. Context changes, disconnections and setting changes cancel pending gestures. Return the control to neutral before beginning again.

The separation of press duration and inter-press gap follows the useful distinction in [Unity's MultiTap interaction](https://docs.unity3d.com/Packages/com.unity.inputsystem@1.14/manual/Interactions.html#multitap). Windows' [SetDoubleClickTime documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setdoubleclicktime) describes a default 500 ms mouse double-click interval; this mod's shorter defaults are gameplay choices, independent of the Windows mouse setting.

Install the DLL under `BepInEx/plugins/NuclearControllerGestures/` using NOMM or your normal BepInEx installation. Configuration is `BepInEx/config/dev.dikka.nuclearcontrollergestures.cfg`. The master Enabled setting turns off recognition. Transient action maps are excluded from native saved controller settings.
