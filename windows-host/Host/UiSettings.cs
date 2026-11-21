using System;
using System.IO;
using System.Text.Json;

namespace Seam.WindowsHost.Host
{
    /// <summary>
    /// User preferences for the desktop application.
    ///
    /// Deliberately separate from the pairing material: the authentication key lives in
    /// <c>%USERPROFILE%\.seam\auth_key</c> (shared with the macOS host) and is never read,
    /// copied or logged here. These settings are pure UI/behaviour preferences and live in
    /// <c>%APPDATA%\SEAM\ui-settings.json</c>, so they survive upgrades and are removed with
    /// the user profile rather than with the installation.
    /// </summary>
    public sealed class UiSettings
    {
        /// <summary>Screen edge that hands the pointer to the phone: right (default), left, top, bottom.</summary>
        public string Edge { get; set; } = "right";

        /// <summary>Install the input hooks as soon as a session connects.</summary>
        public bool RoutingOnConnect { get; set; } = true;

        /// <summary>
        /// Connect automatically when an authorised device appears - over USB or over Wi-Fi, once
        /// adb's mDNS discovery has attached an already-paired Wireless debugging endpoint. On by
        /// default, matching the macOS autopilot; Disconnect is still an explicit instruction.
        /// </summary>
        public bool AutoConnect { get; set; } = true;

        /// <summary>Lines kept in the activity panel before the oldest are dropped.</summary>
        public int MaxLogLines { get; set; } = 500;

        /// <summary>Last device used, so a re-plug can preselect it. Not sensitive.</summary>
        public string? LastDeviceSerial { get; set; }

        public static UiSettings Load() => Load(DefaultPath);

        public static UiSettings Load(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return new UiSettings();
                }

                string json = File.ReadAllText(path);
                UiSettings? parsed = JsonSerializer.Deserialize<UiSettings>(json);
                if (parsed == null)
                {
                    return new UiSettings();
                }

                parsed.Normalize();
                return parsed;
            }
            catch
            {
                // A corrupt or unreadable preferences file must never stop SEAM from starting.
                return new UiSettings();
            }
        }

        public void Save() => Save(DefaultPath);

        public void Save(string path)
        {
            try
            {
                Normalize();
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir!);
                }

                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch
            {
                // Preferences are a convenience: failing to persist them is not an error worth
                // interrupting a control session for.
            }
        }

        /// <summary>Clamps anything a hand-edited file could contain into a usable state.</summary>
        public void Normalize()
        {
            switch ((Edge ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "left":
                case "top":
                case "bottom":
                case "right":
                    Edge = Edge!.Trim().ToLowerInvariant();
                    break;
                default:
                    Edge = "right";
                    break;
            }

            if (MaxLogLines < 50) MaxLogLines = 50;
            if (MaxLogLines > 5000) MaxLogLines = 5000;
        }

        /// <summary>Wire value for the selected edge (SEAM-Wire edge constants).</summary>
        public byte EdgeCode => Edge switch
        {
            "left" => Protocol.SeamWireProtocol.EdgeLeft,
            "top" => Protocol.SeamWireProtocol.EdgeTop,
            "bottom" => Protocol.SeamWireProtocol.EdgeBottom,
            _ => Protocol.SeamWireProtocol.EdgeRight
        };

        /// <summary>Human name used in prompts such as "move the pointer to the right edge".</summary>
        public string EdgeName => Edge;

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SEAM",
            "ui-settings.json");
    }
}
