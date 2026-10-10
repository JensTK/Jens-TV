# Jens-TV

A living-room launcher for a Windows PC with a TV attached as a second display, in the spirit of Apple TV / Google TV.
It runs fullscreen on the TV, is driven by keyboard, remote or gamepad, and launches:

- **Jellyfin** inside the TV window (Jellyfin's web client, forced into its TV layout)
- **YouTube** via `youtube.com/tv` in a dedicated Firefox kiosk window placed on the TV
- **Spotify** desktop app, maximized on the TV
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

## Controls

| Action | Keyboard / remote | Gamepad |
| --- | --- | --- |
| Move | Arrow keys | D-pad / left stick |
| Open / select | Enter | A |
| Back | Esc / Backspace | B |
| Settings | Up to the gear, then Enter; or Menu key | Up, then A; or Start |
| **Return home from any app** | Home, media-keyboard Home, or Ctrl+Alt+Home | hold **Back + Start** |

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
- **Sound**: while Jens-TV runs, the chosen device becomes the Windows default output (so Jellyfin, Firefox and
  everything else play through the TV). The previous default comes back when Jens-TV exits. You can turn this off in Settings.
- **YouTube**: Firefox starts with its own profile in `%LOCALAPPDATA%\JensTV\firefox-profile` and a TV user agent, so
  your everyday Firefox and its settings stay untouched. To install add-ons like uBlock Origin or SponsorBlock into
  that profile, set `firefox.kiosk` to `false`, open YouTube from the launcher, install them, then set it back.
- **Jellyfin**: runs in WebView2; logins persist in `%LOCALAPPDATA%\JensTV\WebView2`.
- **Background slideshow**: choose a photo folder in Settings (the picker opens on the TV; use a mouse). Photos
  (jpg, png, webp, gif, bmp, avif, including subfolders) crossfade behind the menu with a slow zoom, darkened so
  the menu stays readable. The folder is re-scanned whenever the launcher reloads or settings change.

## Config

`%APPDATA%\JensTV\config.json` is created on first run. Settings on the TV cover the common options. You can also
edit the file by hand (the "Edit config file" button opens it on the PC); saved changes are picked up immediately.

```jsonc
{
  "display": { "monitorName": "LG TV SSCR2", "monitorDevice": "\\\\.\\DISPLAY2", "preferNonPrimary": true,
               "autoExtend": true, "restoreOnExit": true },
  "audio":   { "deviceId": "{0.0.0.00000000}.{…}", "deviceName": "LG TV (NVIDIA High Definition Audio)",
               "switchSystemDefault": true, "restoreOnExit": true },
  "firefox": {
    "path": null,                 // auto-detected
    "kiosk": true,
    "userAgent": "…",             // must look like a TV/console for youtube.com/tv
    "extraPrefs": { "media.hardware-video-decoding.force-enabled": true }
  },
  "slideshow": {                  // photos behind the launcher; folder can also be picked in Settings
    "enabled": true, "folder": "D:\\Photos\\Favourites", "intervalSeconds": 30,
    "shuffle": true, "includeSubfolders": true,
    "dim": 0.45                   // 0 = photos at full brightness, 1 = black
  },
  "homeHotkeys": ["BrowserHome", "Ctrl+Alt+Home"],   // global; WPF key names, modifiers Ctrl/Alt/Shift/Win
  "tvHotkeys": ["Home"],                             // only while Jens-TV or its app is in front
  "gamepadHome": true,
  "startWithWindows": false,
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
    { "id": "nrk", "name": "NRK TV", "type": "firefox", "url": "https://tv.nrk.no" }
  ]
}
```

App types:

- `web`: opens inside the TV window. `forceTvLayout` (default true) sets Jellyfin's TV layout.
- `firefox`: opens the URL in the Jens-TV Firefox profile, fullscreen on the TV.
- `exe`: starts a program (`path` + `args`). Non-`.exe` paths (shortcuts, Store apps, URIs) go through the shell; set
  `processName` (without `.exe`) so Jens-TV can find the real process, move it and close it. `moveToTv` moves the
  window to the TV, `maximize` maximizes it there. `path` can also be `shell:AppsFolder\<AppID>` for a Store app
  (find the ID with PowerShell `Get-StartApps`), which, unlike a URI, passes `args` through. `zoom` (e.g. `1.5`)
  enlarges Chromium/Electron apps such as Spotify for that launch only, on top of the TV's Windows scaling.

Any app: `closeOnHome: false` leaves it running when you go home (the tile shows "Running"; opening it again brings
the same window back). Opening a different app still closes it.

Spotify notes: the desktop app is built for mouse and keyboard rather than a remote (Space plays/pauses, Ctrl+L
searches). The web player isn't used because the Firefox profile presents a TV user agent and Spotify needs DRM.

Tile look: `icon` (`jellyfin`, `youtube`, `spotify`, an image URL, or empty for a letter), `background` (any CSS
background), `accent` (glow colour) and `description`. With `transparentTiles` on, `background` isn't drawn: the logos
sit directly on the photo/background with a soft shadow, and the focused app gets a frosted-glass panel. A custom
icon should then be a logo with a transparent background (PNG, SVG or WebP) to look right.

## Files

| Path | Purpose |
| --- | --- |
| `MainWindow.cs` | TV window, WebView2 host, messaging with the UI, settings, Home handling |
| `ExternalAppRunner.cs` | Launches/stops Firefox and other programs, keeps their windows on the TV |
| `Native/Monitors.cs` | Display enumeration with EDID names |
| `Audio/AudioDevices.cs` | Output device listing and default-device switching |
| `Input/` | Global hotkeys and system-wide XInput "home" chord |
| `wwwroot/` | Launcher UI (`index.html`, `app.css`, `app.js`) and `inject.js` (runs inside Jellyfin) |

Logs: `%LOCALAPPDATA%\JensTV\jenstv.log`.
