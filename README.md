# Dimmer

**Make your screen darker than its lowest brightness setting.**

Dimmer is a tiny Windows tray app for late nights and dark rooms, when your screen's brightness is already at
its minimum and it's still too bright. It darkens the picture on top of your hardware brightness, so the two add
up. It works on laptop screens and desktop monitors alike.

- **Tiny:** a single 15 KB exe with nothing to install.
- **Light on your PC:** it uses no CPU while idle and about 3 MB of RAM.
- **Safe:** if it closes or crashes, your screen goes straight back to normal.
- **Out of the way:** it lives in the tray, and hotkeys let you change the level without opening anything.

## Download

1. Download `Dimmer.exe` from the [latest release](https://github.com/dapss/Dimmer/releases/latest).
2. Put it in any folder, such as `Documents\Dimmer`, and run it.

Requires 64-bit Windows 10 or 11.

> **"Windows protected your PC"?** Dimmer isn't code-signed, so Windows SmartScreen may warn you the first time.
> Click **More info**, then **Run anyway**. If you'd rather not trust a downloaded exe, you can
> [build it yourself](#build-from-source) from the source in this repo.

## Usage

Dimmer appears as an amber ring in the system tray. The ring fills up as the screen gets brighter. If you don't
see it, click the `^` arrow on the taskbar and drag the icon onto the taskbar to keep it visible.

| To | Do this |
|---|---|
| Turn dimming on or off | Left-click the icon, or press `Ctrl+Alt+Home` |
| Make the screen darker | Press `Ctrl+Alt+PgDn` |
| Make the screen brighter | Press `Ctrl+Alt+PgUp` |
| Pick a level (100% to 5%) | Right-click the icon |
| Start Dimmer when you sign in | Right-click the icon and choose **Start with Windows** |
| Quit | Right-click the icon and choose **Exit** |

**Tip:** for the darkest result, turn your screen's own brightness to its minimum first, then use Dimmer to go
further. On a laptop that's the Windows brightness slider; on a desktop monitor it's usually the buttons or
on-screen menu on the monitor itself.

Levels go down in 10% steps to 30%, then in 5% steps to 5%, because small changes are easier to see at the dark
end. If you go too dark to see anything, press `Ctrl+Alt+Home` to turn dimming off.

Dimmer remembers your level and restores it the next time it starts. If you move the exe to another folder,
**Start with Windows** follows it the next time you run it.

## How it works

Dimmer uses a colour filter that's built into Windows (`MagSetFullscreenColorEffect`, part of the same area of
Windows as the Magnifier app). It asks Windows to multiply every pixel's colour by your chosen level, so at 30%
a white pixel is shown at about 30% of its usual value.

Because Windows applies the filter to the final screen image, there's no overlay window covering the screen:
the Start menu dims too, and clicks work as normal. Dimmer sets the filter once and then sleeps until you click
the icon or press a hotkey. It wakes briefly after sleep, unlock or a display change to reapply the filter.

It needs no admin rights and makes no lasting changes to your system.

## Limitations

- **No power savings:** Dimmer darkens the picture but can't lower your screen's hardware brightness further,
  so the screen uses the same power as before.
- **Some things stay bright:**
  - The mouse cursor, which Windows usually draws after the filter.
  - Administrator (UAC) prompts, which appear on a separate secure desktop.
  - The lock and sign-in screens.
- **Dark detail is harder to see** at low levels.
- **The percentage isn't perceived brightness.** 50% looks darker than half as bright.
- **All monitors share one level.**
- **Screenshots come out dimmed too,** and screen recordings may as well.
- **Taken hotkeys don't work:** if another app already uses one of the hotkeys, use the tray icon for that action.
- **The tray menu is always light,** even in dark mode.

## Build from source

You don't need Visual Studio or any SDK. Dimmer compiles with the C# compiler that comes with Windows
(.NET Framework 4).

```
git clone https://github.com/dapss/Dimmer.git
cd Dimmer
build.bat
```

This produces `Dimmer.exe` in the same folder. All of the code is in [`Dimmer.cs`](Dimmer.cs).

## Uninstall

1. If you turned on **Start with Windows**, right-click the tray icon and turn it off.
2. Choose **Exit**.
3. Delete the folder containing `Dimmer.exe`.

Dimmer only writes two things:

- `settings.txt`, next to the exe. If that folder is read-only, such as `Program Files`, it goes in
  `%APPDATA%\Dimmer` instead, so delete that folder too if it exists.
- The **Start with Windows** entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, which step 1
  removes.

## License

[MIT](LICENSE)
