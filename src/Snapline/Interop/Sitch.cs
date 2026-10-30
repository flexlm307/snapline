using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Snapline.Core;

namespace Snapline.Interop;

/// <summary>
/// Sitch, the screenshot editor (github.com/flexlm307/Sitch). "Edit in Sitch" hands it a photo
/// with <c>--edit</c>: Save in Sitch writes back into the same file and the watcher reloads the
/// card. A running Sitch gets the path over its named pipe; otherwise the exe is launched.
/// </summary>
public static class Sitch
{
    private static string PipeName => $"Sitch-pipe-{Environment.UserName}";
    private static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sitch", "settings.json");

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int dwProcessId);
    private const int AsfwAny = -1;

    /// <summary>Running, or found on disk: the menu offers "Edit in Sitch".</summary>
    public static bool Available => IsRunning || ExePath() is not null;

    /// <summary>Sitch has been run on this PC at least once.</summary>
    public static bool Installed => File.Exists(SettingsFile) || ExePath() is not null;

    private static bool IsRunning
    {
        get
        {
            // The release is also downloaded as WinSitch.exe, its old name.
            var running = Process.GetProcessesByName("Sitch").Concat(Process.GetProcessesByName("WinSitch")).ToList();
            foreach (var p in running) p.Dispose();
            return running.Count > 0;
        }
    }

    public static bool Edit(string path)
    {
        var full = Path.GetFullPath(path);
        if (IsRunning && SendToRunning("EDIT " + full)) return true;
        if (ExePath() is not { } exe) return false;
        try
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false };
            info.ArgumentList.Add("--edit=" + full);
            Process.Start(info)?.Dispose();
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"Could not start Sitch: {e.Message}");
            return false;
        }
    }

    private static bool SendToRunning(string line)
    {
        try
        {
            // Let Sitch bring its editor to the front.
            AllowSetForegroundWindow(AsfwAny);
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(800);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.WriteLine(line);
            writer.Flush();
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"Could not reach Sitch: {e.Message}");
            return false;
        }
    }

    /// <summary>Sitch registers itself under App Paths each start; older builds only in the Run key or winget's links.</summary>
    public static string? ExePath()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\Sitch.exe"))
                if (key?.GetValue(null) is string p && File.Exists(p)) return p;
            using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                if (run?.GetValue("Sitch") is string r && r.Trim('"') is var exe && File.Exists(exe)) return exe;
        }
        catch { }
        var link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "Sitch.exe");
        return File.Exists(link) ? link : null;
    }

    /// <summary>Where Sitch saves captures: its chosen folder, or Pictures\Sitch.</summary>
    public static string SaveFolder()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(SettingsFile));
                if (doc.RootElement.TryGetProperty("DefaultSaveFolder", out var f) && f.GetString() is { Length: > 0 } folder && Directory.Exists(folder))
                    return folder;
            }
        }
        catch (Exception e)
        {
            Log.Error($"Could not read Sitch settings: {e.Message}");
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Sitch");
    }
}
