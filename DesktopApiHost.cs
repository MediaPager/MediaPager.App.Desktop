using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace MediaPager.App.Desktop;

/// <summary>
/// Starts the local API host for a packaged desktop app. Development can point the
/// WebView at a separately launched Vite/API pair with MEDIAPAGER_DESKTOP_URL.
/// </summary>
internal sealed class DesktopApiHost : IAsyncDisposable
{
    private readonly Process? _process;

    private DesktopApiHost(Process? process, Uri address)
    {
        _process = process;
        Address = address;
    }

    public Uri Address { get; }

    public static async Task<DesktopApiHost> StartAsync(CancellationToken cancellationToken = default)
    {
        if (Uri.TryCreate(Environment.GetEnvironmentVariable("MEDIAPAGER_DESKTOP_URL"), UriKind.Absolute, out var externalUrl))
            return new DesktopApiHost(null, externalUrl);

        var apiAssembly = ResolveApiAssembly();
        if (apiAssembly is null)
            throw new InvalidOperationException(
                "The local API host was not found. Set MEDIAPAGER_DESKTOP_API_DLL or MEDIAPAGER_DESKTOP_URL.");

        var pluginRoot = ResolvePluginRoot(apiAssembly);
        var configuredToolsRoot = Environment.GetEnvironmentVariable("MEDIAPAGER_Tools__Directory");
        var toolsRoot = Path.GetFullPath(
            string.IsNullOrWhiteSpace(configuredToolsRoot)
                ? Path.Combine(Path.GetDirectoryName(apiAssembly)!, "tools")
                : configuredToolsRoot,
            Path.GetDirectoryName(apiAssembly)!);
        CopyBundledOfficialPlugins(
            Path.Combine(Path.GetDirectoryName(apiAssembly)!, "plugins", "official"),
            Path.Combine(pluginRoot, "official"));

        var port = GetAvailableLoopbackPort();
        var address = new Uri($"http://127.0.0.1:{port}/");
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveApiExecutable(apiAssembly) ?? ResolveDotnetHost(),
            WorkingDirectory = Path.GetDirectoryName(apiAssembly)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (ResolveApiExecutable(apiAssembly) is null)
            startInfo.ArgumentList.Add(apiAssembly);
        startInfo.Environment["ASPNETCORE_URLS"] = address.GetLeftPart(UriPartial.Authority);
        startInfo.Environment["MEDIAPAGER_Desktop__Mode"] = "true";
        startInfo.Environment["MEDIAPAGER_Plugins__Directory"] = pluginRoot;
        startInfo.Environment["MEDIAPAGER_Tools__Directory"] = toolsRoot;
        startInfo.Environment["PLAYWRIGHT_BROWSERS_PATH"] = Path.Combine(toolsRoot, "playwright", "1.63.0");

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the local MediaPager API host.");
        _ = DrainAsync(process.StandardOutput, cancellationToken);
        _ = DrainAsync(process.StandardError, cancellationToken);

        try
        {
            await WaitUntilReadyAsync(address, process, cancellationToken);
            return new DesktopApiHost(process, address);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException)
        {
            // The host already exited.
        }
        catch (TimeoutException)
        {
            // The desktop shell must still close if a host shutdown hook is stuck.
        }
        finally
        {
            _process.Dispose();
        }
    }

    private static string? ResolveApiAssembly()
    {
        var configured = Environment.GetEnvironmentVariable("MEDIAPAGER_DESKTOP_API_DLL");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);

        var packaged = Path.Combine(AppContext.BaseDirectory, "host", "MediaPager.App.Api.dll");
        if (File.Exists(packaged)) return packaged;

        // Developer checkout layout: Official/MediaPager.App.Desktop/bin/... alongside
        // Official/MediaPager.App.Api/bin/Debug/net10.0.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "MediaPager.App.Api", "bin", "Debug", "net10.0", "MediaPager.App.Api.dll");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string ResolvePluginRoot(string apiAssembly)
    {
        var configured = Environment.GetEnvironmentVariable("MEDIAPAGER_Plugins__Directory");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured, Path.GetDirectoryName(apiAssembly)!);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".MediaPager", "plugins");
    }

    private static void CopyBundledOfficialPlugins(string sourceRoot, string destinationRoot)
    {
        if (!Directory.Exists(sourceRoot) ||
            string.Equals(Path.GetFullPath(sourceRoot), Path.GetFullPath(destinationRoot), StringComparison.Ordinal))
            return;

        foreach (var sourceDirectory in Directory.EnumerateDirectories(sourceRoot))
        {
            var destinationDirectory = Path.Combine(destinationRoot, Path.GetFileName(sourceDirectory));
            Directory.CreateDirectory(destinationDirectory);
            foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory))
            {
                // User-created disable markers are state, not part of the shipped package.
                if (string.Equals(Path.GetFileName(sourceFile), ".disabled", StringComparison.Ordinal))
                    continue;
                File.Copy(sourceFile, Path.Combine(destinationDirectory, Path.GetFileName(sourceFile)), overwrite: true);
            }
        }
    }

    private static string ResolveDotnetHost()
    {
        var configured = Environment.GetEnvironmentVariable("MEDIAPAGER_DOTNET_HOST");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);

        var toolRoot = Environment.GetEnvironmentVariable("MEDIAPAGER_TOOLCHAIN_ROOT");
        if (!string.IsNullOrWhiteSpace(toolRoot))
        {
            var managedHost = Path.Combine(toolRoot, "dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(managedHost)) return managedHost;
        }

        // Development fallback. A packaged installer should configure the managed
        // toolchain path instead of relying on a global dotnet installation.
        return OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    }

    private static string? ResolveApiExecutable(string apiAssembly)
    {
        var executable = Path.Combine(
            Path.GetDirectoryName(apiAssembly)!,
            OperatingSystem.IsWindows() ? "MediaPager.App.Api.exe" : "MediaPager.App.Api");
        return File.Exists(executable) ? executable : null;
    }

    private static int GetAvailableLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitUntilReadyAsync(Uri address, Process process, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException($"The local API host exited with code {process.ExitCode}.");
            try
            {
                using var response = await client.GetAsync(new Uri(address, "plugins"), cancellationToken);
                if ((int)response.StatusCode < 500) return;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                lastError = exception;
            }
            await Task.Delay(250, cancellationToken);
        }
        throw new InvalidOperationException("The local API host did not become ready in time.", lastError);
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is not null) { }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }
}
