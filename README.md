# MediaPager Desktop

Avalonia native window shell for the shared Quasar/Vue SPA. The desktop app starts a
local MediaPager API host and displays it in Avalonia's platform-native WebView.

For development, set `MEDIAPAGER_DESKTOP_URL` to a running Vite URL. `dotnet publish`
builds the Quasar SPA, publishes the API and its official plugin set under `host/`, and
copies the SPA into `host/wwwroot/`. A self-contained publish launches the API apphost;
a framework-dependent publish uses `MEDIAPAGER_DOTNET_HOST`, `MEDIAPAGER_TOOLCHAIN_ROOT`,
or the global `dotnet` development fallback.

Initial desktop publish targets:

```sh
dotnet publish MediaPager.App.Desktop.csproj -c Release -r win-x64 --self-contained true
dotnet publish MediaPager.App.Desktop.csproj -c Release -r osx-x64 --self-contained true
dotnet publish MediaPager.App.Desktop.csproj -c Release -r osx-arm64 --self-contained true
dotnet publish MediaPager.App.Desktop.csproj -c Release -r linux-x64 --self-contained true
```

The host binds to loopback. LAN exposure is a separate application setting and must not
be enabled implicitly by the desktop shell.

Bundled official plugin folders are synchronized into the per-user writable
`.MediaPager/plugins/official` directory before the API starts; community installs and
their state use the adjacent `community` directory. `MEDIAPAGER_Plugins__Directory` can
override that root for local development.

FFmpeg 9.0.2 and Playwright 1.63.0 tools are bootstrapped in the background into `tools/`
beside the bundled API binary (override with `MEDIAPAGER_Tools__Directory`). The UI and API
start while setup runs, and progress appears in Activity → Notifications. Downloads are
versioned and reused on later launches; neither toolchain is installed into the host's
system directories. If that directory needs administrator rights, the standalone installer
requests OS authorization; the API continues running as the logged-in user.

Avalonia NativeWebView uses WebView2 on Windows, WKWebView on macOS, and WPE WebKit or
WebKitGTK on Linux. The desktop installer/bootstrapper must verify the platform runtime
dependencies before launching the UI.
