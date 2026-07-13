using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class Setup
{
    private static readonly Dictionary<string, string> Payload = new Dictionary<string, string>
    {
        { "VMixMidiSurfaceBridge.app.py", "app.py" },
        { "VMixMidiSurfaceBridge.bridge_core.py", "bridge_core.py" },
        { "VMixMidiSurfaceBridge.config.example.json", "config.example.json" },
        { "VMixMidiSurfaceBridge.requirements.txt", "requirements.txt" },
        { "VMixMidiSurfaceBridge.install.ps1", "install_on_windows.ps1" }
    };

    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        string temp = Path.Combine(Path.GetTempPath(), "VMixMidiSurfaceBridge-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            Assembly assembly = Assembly.GetExecutingAssembly();
            foreach (KeyValuePair<string, string> item in Payload)
            {
                using (Stream input = assembly.GetManifestResourceStream(item.Key))
                {
                    if (input == null)
                        throw new InvalidOperationException("Installer payload is incomplete: " + item.Value);
                    using (FileStream output = File.Create(Path.Combine(temp, item.Value)))
                        input.CopyTo(output);
                }
            }

            Process process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(temp, "install_on_windows.ps1") + "\"",
                UseShellExecute = true,
                WorkingDirectory = temp
            });
            if (process == null)
                throw new InvalidOperationException("Windows could not start the installer.");
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Setup did not complete. Review the PowerShell window for details.");

            MessageBox.Show(
                "vMix MIDI Surface Bridge is installed and running.\n\nThe browser interface is available at http://localhost:8091/.",
                "Setup complete",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Setup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
            catch { }
        }
    }
}
