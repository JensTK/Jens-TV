# Jens-TV

A living-room launcher for a Windows PC with a TV attached as a second display, in the spirit of Apple TV / Google TV.
It runs fullscreen on the TV, is driven by keyboard, remote or gamepad, and launches:

- **Jellyfin** inside the TV window (Jellyfin's web client, forced into its TV layout)
- **YouTube** via `youtube.com/tv` in a dedicated Firefox kiosk window placed on the TV
- **Spotify** desktop app, maximized on the TV
- **F1 TV** website inside the TV window (WebView2 has the Widevine/PlayReady DRM it needs; F1 blocks Firefox),
  navigable with arrow keys
- anything else you add (programs, shortcuts, `steam://` URIs, other web apps)

## Run

```powershell
dotnet run
# or build once and start bin\Debug\net10.0-windows\JensTV.exe
dotnet publish -c Release -o publish   # self-contained folder you can point "Start with Windows" at
```

Requires Windows 10/11, the .NET 10 Desktop Runtime, the Edge WebView2 Runtime (preinstalled on Windows 11) and Firefox.

First run: open **Settings** (the gear in the top right: press Up from the tiles) and pick the TV display and sound
output, then set the Jellyfin address.

**Launch hotkey:** press **Ctrl+Alt+T** anywhere to start Jens-TV, or to bring it to the front on the home screen if
it's already running. Jens-TV puts a "Jens-TV" shortcut in the Start Menu with that Windows shortcut key, so Windows
itself handles the key and nothing runs in the background while Jens-TV is closed. The shortcut points at whichever
JensTV.exe ran last (start the published exe once after `dotnet publish`). Change the key with `launchHotkey`
(Ctrl and/or Alt plus a key) or set it to `""` to turn it off.

## Controls

| Action | Keyboard / remote | Gamepad |
| --- | --- | --- |
| Move | Arrow keys | D-pad / left stick |
| Open / select | Enter | A |
| Back | Esc / Backspace | B |
| Settings | Up to the gear, then Enter | Up, then A |
| Turn off Jens-TV | Up, Right to the power button, then Enter | Up, Right, then A |
| **Return home from any app** | Home, media-keyboard Home, or Ctrl+Alt+Home | hold **Back + Start** |

**One controller only:** Settings → Controller → press OK, then any button on the controller you want. After that only
that controller drives the launcher (matched by name, so dongle, Bluetooth and cable all work); others, like a racing
wheel or a second pad, are ignored there. Apps such as YouTube or Jellyfin still read every controller themselves.
The built-in hold-Back+Start chord uses XInput, which only sees Xbox-style controllers and can't tell them apart, so
it's off when a specific non-XInput (e.g. DirectInput) controller is chosen.

**Home shortcut for any controller (incl. DirectInput):** Settings → Home shortcut on controller → OK, then hold the
buttons you want (e.g. Select + Start) and let go. Holding them for half a second returns home from any app. It only
reacts to the controller you set it with, reads HID input in the background (Raw Input), and only listens while a
shortcut is set. Left/Right on the row switches it off.

Returning home closes the running app (e.g. YouTube's Firefox) and brings the launcher back. The plain Home key
(`tvHotkeys`) only does this while Jens-TV or the app it launched is in front, so it keeps working normally in
other programs on the PC. The media-keyboard Home key and Ctrl+Alt+Home (`homeHotkeys`) work from anywhere.
Pressing Back on Jellyfin's home screen also returns to the launcher.

## How it works

- **Display**: the TV is remembered by its EDID name (e.g. `LG TV SSCR2`), not by `DISPLAY#`, because Windows renumbers
  displays. If the TV is off at startup Jens-TV uses another screen, then moves to the TV as soon as it appears.
  If the TV is connected but not part of the desktop (Win+P "PC screen only", or "Disconnect this display"),
  Settings lists it as "connected, not extended". Once it's chosen, Jens-TV switches Windows to Extend, keeping your
  current main display as the main one. That's tried once each time the TV connects; set `display.autoExtend` to
  `false` to turn it off. When you quit, the display setup from before is put back (`display.restoreOnExit`); this
  only happens if Jens-TV did the extending, so a desktop you extended yourself is left alone.
  "Turn off other screens while Jens-TV runs" (Settings, or `display.turnOffOtherScreens`) takes every other screen
  off the desktop once the TV is on, like Win+P "Second screen only", so they go to standby. They always come back when
  Jens-TV quits or the option is switched off. If Jens-TV crashes with screens off, press Win+P and pick a mode.
- **Sound**: while Jens-TV runs, the chosen device becomes the Windows default output (so Jellyfin, Firefox and
  everything else play through the TV). The previous default comes back when Jens-TV exits. You can turn this off in Settings.
- **YouTube**: Firefox starts with its own profile in `%LOCALAPPDATA%\JensTV\firefox-profile` and a TV user agent, so
  your everyday Firefox and its settings stay untouched.
- **Firefox add-ons**: list them in `firefox.extensions` by their addons.mozilla.org name (the end of
  `addons.mozilla.org/firefox/addon/<name>`), e.g. `["sponsorblock", "ublock-origin"]`. Jens-TV downloads them into
  the YouTube profile only, enables them without a prompt, and lets Firefox start once invisibly so welcome pages don't
  cover YouTube. Firefox keeps them updated; removing a name uninstalls it. SponsorBlock is tested working on
  youtube.com/tv. To change an add-on's own settings, set `firefox.kiosk` to `false` temporarily to get the toolbar.
- **Jellyfin**: runs in WebView2; logins persist in `%LOCALAPPDATA%\JensTV\WebView2`.
- **Samsung TV (wake + standby)**: with a `samsungTv` section (Samsung Tizen TVs, 2016+), Jens-TV wakes the TV over
  the network when it starts (or is brought forward with the launch hotkey), and can put it in standby when it quits
  (both switches are in Settings). The TV needs to be on the network with "Power On with Mobile" (Settings → General →
  Network → Expert Settings) switched on. The first time, the TV asks to allow "Jens-TV"; the token it hands out is
  saved. The TV is found again by MAC address if its IP changes. Right after being switched off the TV needs a minute or
  two before it reacts to wake packets. For diagnostics, `JensTV.exe --tv-key KEY_POWER` sends a remote key.
- **Background slideshow**: choose a photo folder in Settings (the picker opens on the TV; use a mouse). Photos
  (jpg, png, webp, gif, bmp, avif, including subfolders) crossfade behind the menu with a slow zoom, darkened so
  the menu stays readable. The folder is re-scanned whenever the launcher reloads or settings change.

## Config

`%APPDATA%\JensTV\config.json` is created on first run. Settings on the TV cover the common options. You can also
edit the file by hand (the "Edit config file" button opens it on the PC); saved changes are picked up immediately.

```jsonc
{
  "display": { "monitorName": "LG TV SSCR2", "monitorDevice": "\\\\.\\DISPLAY2", "preferNonPrimary": true,
               "autoExtend": true, "restoreOnExit": true,
               "turnOffOtherScreens": false },
  "audio":   { "deviceId": "{0.0.0.00000000}.{â€¦}", "deviceName": "LG TV (NVIDIA High Definition Audio)",
               "switchSystemDefault": true, "restoreOnExit": true },
  "firefox": {
    "path": null,                 // auto-detected
    "kiosk": true,
    "extensions": ["sponsorblock"],   // addons.mozilla.org names, YouTube profile only
    "userAgent": "â€¦",             // must look like a TV/console for youtube.com/tv
    "extraPrefs": { "media.hardware-video-decoding.force-enabled": true }
  },
  "samsungTv": {                  // optional: wake the TV over the network, standby on quit
    "host": "192.168.1.109", "mac": "A4:30:7A:73:16:85", "token": null,
    "wakeOnStart": true, "turnOffOnExit": false
  },
  "slideshow": {                  // photos behind the launcher; folder can also be picked in Settings
    "enabled": true, "folder": "D:\\Photos\\Favourites", "intervalSeconds": 30,
    "shuffle": true, "includeSubfolders": true,
    "dim": 0.45                   // 0 = photos at full brightness, 1 = black
  },
  "homeHotkeys": ["BrowserHome", "Ctrl+Alt+Home"],   // global; WPF key names, modifiers Ctrl/Alt/Shift/Win
  "tvHotkeys": ["Home"],                             // only while Jens-TV or its app is in front
  "gamepadHome": true,
  "controller": null,             // only this controller drives the launcher (Settings → Controller)
  "homeChord": null,              // e.g. { "device": "8BitDo Ultimate 2 Wireless", "buttons": [11, 12] } (learned in Settings)
  "startWithWindows": false,
  "launchHotkey": "Ctrl+Alt+T",   // Windows shortcut key on the Start Menu shortcut; "" = none
  "transparentTiles": false,      // logos without tile backgrounds; also a switch in Settings
  "apps": [
    { "id": "jellyfin", "name": "Jellyfin", "type": "web", "url": "http://localhost:8096", "icon": "jellyfin" },
    { "id": "youtube",  "name": "YouTube",  "type": "firefox", "url": "https://www.youtube.com/tv", "icon": "youtube" },

    { "id": "spotify",  "name": "Spotify",  "type": "exe", "processName": "Spotify",
      "path": "shell:AppsFolder\\SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",   // Store app; or the classic Spotify.exe path
      "maximize": true, "zoom": 1.5, "icon": "spotify" },

    // more examples:
    { "id": "steam", "name": "Steam", "type": "exe", "path": "steam://open/bigpicture",
      "background": "linear-gradient(160deg,#1b2838,#2a475e)", "accent": "#1b3a5c" },
    { "id": "kodi", "name": "Kodi", "type": "exe", "path": "C:\\Program Files\\Kodi\\kodi.exe", "moveToTv": true },
    { "id": "f1tv", "name": "F1 TV", "type": "web", "url": "https://f1tv.formula1.com/", "forceTvLayout": false,
      "spatialNavigation": true, "icon": "f1tv" },
    { "id": "nrk", "name": "NRK TV", "type": "firefox", "url": "https://tv.nrk.no" }
  ]
}
```

App types:

- `web`: opens inside the TV window. `forceTvLayout` (default true) sets Jellyfin's TV layout. `spatialNavigation`
  makes a mouse-oriented site usable with a remote: arrow keys move a white highlight to the nearest link or button,
  Enter clicks, Back goes back, Right/Left at the end of a carousel row presses its Next/Previous arrow, and Down
  keeps going into rows that load as you scroll. Arrows are left to the site in fullscreen video and text fields.
- `firefox`: opens the URL in the Jens-TV Firefox profile, fullscreen on the TV. `profile` gives the app its own
  Firefox profile (own logins, no TV user agent, no `firefox.extensions`); `userAgent` optionally overrides the user
  agent there. New profiles get an invisible first run in the background so Firefox can download Widevine (DRM).
- `exe`: starts a program (`path` + `args`). Non-`.exe` paths (shortcuts, Store apps, URIs) go through the shell; set
  `processName` (without `.exe`) so Jens-TV can find the real process, move it and close it. `moveToTv` moves the
  window to the TV, `maximize` maximizes it there. `path` can also be `shell:AppsFolder\<AppID>` for a Store app
  (find the ID with PowerShell `Get-StartApps`), which, unlike a URI, passes `args` through. `zoom` (e.g. `1.5`)
  enlarges Chromium/Electron apps such as Spotify for that launch only, on top of the TV's Windows scaling.

Any app: `closeOnHome: false` leaves it running when you go home (the tile shows "Running"; opening it again brings
the same window back). Opening a different app still closes it.

Spotify notes: the desktop app is built for mouse and keyboard rather than a remote (Space plays/pauses, Ctrl+L
searches). The web player isn't used because the Firefox profile presents a TV user agent and Spotify needs DRM.

Tile look: `icon` (`jellyfin`, `youtube`, `spotify`, `f1tv`, an image URL, or empty for a letter), `background` (any CSS
background), `accent` (glow colour) and `description`. With `transparentTiles` on, `background` isn't drawn: the logos
sit directly on the photo/background with a soft shadow, and the focused app gets a frosted-glass panel. A custom
icon should then be a logo with a transparent background (PNG, SVG or WebP) to look right.

## Files

| Path | Purpose |
| --- | --- |
| `MainWindow.cs` | TV window, WebView2 host, messaging with the UI, settings, Home handling |
| `ExternalAppRunner.cs` | Launches/stops Firefox and other programs, keeps their windows on the TV |
| `SamsungTv.cs` | Wake-on-LAN and the Samsung remote API (keys, pairing) |
| `Native/Monitors.cs` | Display enumeration with EDID names |
| `Audio/AudioDevices.cs` | Output device listing and default-device switching |
| `Input/` | Global hotkeys, XInput "home" chord, HID/DirectInput home shortcut (Raw Input) |
| `wwwroot/` | Launcher UI (`index.html`, `app.css`, `app.js`) and `inject.js` (runs inside Jellyfin) |

Logs: `%LOCALAPPDATA%\JensTV\jenstv.log`.
