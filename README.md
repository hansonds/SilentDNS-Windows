# SilentDNS for Windows

A tiny, silent DNS changer that lives in the system tray. About 60 KB, no installer, nothing extra to download. It uses the .NET Framework that already ships with Windows 10 and 11.

Companion to the SilentDNS Android app.

## Features

- **Cloudflare + Google by default**, for both IPv4 and IPv6:

  |  | IPv4 | IPv6 |
  |---|---|---|
  | Primary (Cloudflare) | 1.1.1.1 | 2606:4700:4700::1111 |
  | Secondary (Google) | 8.8.8.8 | 2001:4860:4860::8888 |

  Setting IPv6 DNS too matters on networks like Jio and Airtel, where Windows would otherwise keep using the ISP's IPv6 DNS.
- **Encrypted DNS (DoH)** on Windows 11
- **Starts with Windows** straight into the tray, no window, no UAC prompt at every boot
- **Stays on**: re-applies automatically when you change Wi-Fi, plug in Ethernet or USB tethering, or wake from sleep
- **Quick switch** presets: Cloudflare, Google, Quad9, AdGuard (blocks ads), or your own custom DNS
- **Turned off only from the app**: "Turn off" restores your normal automatic DNS
- Tray icon: green shield = on, grey shield = off

## Download

Get the latest **SilentDNS.exe** from [Releases](../../releases/latest).

## How to use

1. Make a permanent folder, for example `C:\Tools\SilentDNS\`, and put `SilentDNS.exe` in it.
2. Double-click it and click **Yes** on the admin prompt (needed once, because changing DNS requires admin rights).
3. A green shield appears in the system tray. Right-click it for the menu.

If Windows shows "Windows protected your PC", click **More info → Run anyway** (the app is not code-signed).

### Tray menu

| Item | What it does |
|---|---|
| (top two lines) | Live DNS your PC is using right now, IPv4 and IPv6 |
| Turn off / Turn on | Switch between SilentDNS and your normal automatic DNS |
| Quick switch | Pick a DNS provider in one click |
| Settings… | Custom primary/secondary DNS for IPv4 and IPv6, encrypted DNS, start with Windows |
| Flush DNS cache | Clears Windows' DNS cache |
| Start with Windows | Toggle auto-start |
| Exit | Closes the tray icon (your DNS stays set) |

## Check that it works

Open **https://1.1.1.1/help** in your browser. You should see *Connected to 1.1.1.1: Yes* and, on Windows 11, *Using DNS over HTTPS (DoH): Yes* (may need one restart).

To check IPv6, open **https://test-ipv6.com**. If your network has IPv6, the DNS shown should be Cloudflare/Google, not your ISP.

If you use Chrome or Edge, set its **Use secure DNS** option to *With your current service provider* so the browser follows SilentDNS.

## Uninstall

1. Right-click the tray icon → **Turn off** (restores automatic DNS).
2. Untick **Start with Windows**, then **Exit**.
3. Delete `SilentDNS.exe` and the folder `%LOCALAPPDATA%\SilentDNS`.

## Building

Every push to `main` builds the EXE with GitHub Actions and publishes it as a new Release. See `.github/workflows/build.yml`.

To build by hand on Windows:

```
csc -target:winexe -win32icon:src\app.ico -win32manifest:src\app.manifest ^
    -r:System.Drawing.dll -r:System.Windows.Forms.dll -out:SilentDNS.exe src\SilentDNS.cs
```

## License

MIT
