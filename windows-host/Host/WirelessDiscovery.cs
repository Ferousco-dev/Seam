using System;
using System.Collections.Generic;
using Seam.WindowsHost.Bootstrap;

namespace Seam.WindowsHost.Host
{
    /// <summary>One service advertised through adb's mDNS discovery.</summary>
    public readonly record struct WirelessService(bool CanConnect, bool CanPair, string Address);

    /// <summary>What the autopilot should do on this tick.</summary>
    public enum WirelessActionKind
    {
        None,
        ConnectDevice,   // an authorised device is already visible to adb: start a session on it
        AdbConnect,      // ask adb to attach a Wi-Fi endpoint, then re-list on the next tick
        NeedPairing      // the phone advertises pairing only: Android needs a one-time code
    }

    /// <summary>A decision plus the value it applies to.</summary>
    public readonly record struct WirelessAction(WirelessActionKind Kind, string Target);

    /// <summary>Everything the decision depends on, so the rule can be tested without adb.</summary>
    public sealed class WirelessContext
    {
        public bool AutoConnectEnabled { get; init; }
        public bool UserDisconnected { get; init; }
        public bool Connected { get; init; }
        public bool Busy { get; init; }
        public bool MdnsDue { get; init; }
        public IReadOnlyList<AdbDevice> Devices { get; init; } = Array.Empty<AdbDevice>();
        public IReadOnlyList<WirelessService> Services { get; init; } = Array.Empty<WirelessService>();
    }

    /// <summary>
    /// Windows port of the macOS autopilot's wireless half.
    ///
    /// macOS (<c>SeamAppViewModel.runAutoPilotTick</c>) lists devices, and every third tick runs
    /// <c>adb mdns services</c> and <c>adb connect</c>s any advertised <c>_adb-tls-connect._tcp</c>
    /// endpoint that is not connected yet, then bootstraps the agent over that transport. It never
    /// pairs on its own: <c>_adb-tls-pairing._tcp</c> means the phone wants a one-time code, which
    /// only the person holding the phone can supply.
    ///
    /// The rule is reproduced here as a pure function so it can be tested with no phone, no adb
    /// and no network. The transport difference is deliberate: macOS has its own UDP/TCP Wi-Fi
    /// channel, while Windows keeps using the adb forward it already uses for USB, so a Wi-Fi
    /// device needs no new wire protocol.
    /// </summary>
    public static class WirelessDiscovery
    {
        public const string ConnectService = "_adb-tls-connect._tcp";
        public const string PairingService = "_adb-tls-pairing._tcp";

        /// <summary>
        /// Parses `adb mdns services`. Lines look like
        /// <c>adb-R58M12ABCDE-XyZ\t_adb-tls-connect._tcp\t192.168.1.42:37231</c>.
        /// The header line and IPv6 advertisements (which this host does not dial) are skipped.
        /// </summary>
        public static List<WirelessService> ParseMdnsServices(string? output)
        {
            var services = new List<WirelessService>();
            if (string.IsNullOrWhiteSpace(output))
            {
                return services;
            }

            foreach (string rawLine in output.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 ||
                    line.StartsWith("List of discovered", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool canConnect = line.Contains(ConnectService, StringComparison.OrdinalIgnoreCase);
                bool canPair = line.Contains(PairingService, StringComparison.OrdinalIgnoreCase);
                if (!canConnect && !canPair)
                {
                    continue;
                }

                string[] tokens = line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                string address = string.Empty;
                for (int i = tokens.Length - 1; i >= 0; i--)
                {
                    string token = tokens[i];
                    if (token.Contains(ConnectService, StringComparison.OrdinalIgnoreCase) ||
                        token.Contains(PairingService, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // IPv6 endpoints arrive as [fe80::1]:40000; adb connect would need a
                    // bracketed, scoped address, so they are reported but not dialled.
                    if (token.Contains(':') && !token.StartsWith("[", StringComparison.Ordinal))
                    {
                        address = token;
                        break;
                    }
                }

                if (address.Length == 0)
                {
                    continue;
                }

                services.Add(new WirelessService(canConnect, canPair, address));
            }

            return services;
        }

        /// <summary>
        /// The autopilot rule, in the order macOS applies it: never duplicate a session, never
        /// fight the user, prefer a device that is already there, then let mDNS supply one.
        /// </summary>
        public static WirelessAction Decide(WirelessContext context)
        {
            if (context == null)
            {
                return new WirelessAction(WirelessActionKind.None, string.Empty);
            }

            // Already busy or connected: a second session would duplicate hooks and deploy the
            // agent again for nothing.
            if (context.Connected || context.Busy)
            {
                return new WirelessAction(WirelessActionKind.None, string.Empty);
            }

            // "Disconnect" is an instruction, not a suggestion: stay put until Connect is pressed.
            if (context.UserDisconnected || !context.AutoConnectEnabled)
            {
                return new WirelessAction(WirelessActionKind.None, string.Empty);
            }

            AdbDevice? usb = null;
            AdbDevice? wifi = null;
            AdbDevice? staleWifi = null;

            foreach (AdbDevice device in context.Devices)
            {
                if (device.IsAuthorized)
                {
                    if (device.IsWirelessAdb)
                    {
                        wifi ??= device;
                    }
                    else
                    {
                        usb ??= device;
                    }
                }
                else if (device.IsWirelessAdb &&
                         string.Equals(device.State, "offline", StringComparison.OrdinalIgnoreCase))
                {
                    // adb still holds a Wi-Fi transport whose socket died: re-dial it.
                    staleWifi ??= device;
                }
            }

            if (usb != null)
            {
                return new WirelessAction(WirelessActionKind.ConnectDevice, usb.Serial);
            }

            if (wifi != null)
            {
                return new WirelessAction(WirelessActionKind.ConnectDevice, wifi.Serial);
            }

            if (staleWifi != null)
            {
                return new WirelessAction(WirelessActionKind.AdbConnect, staleWifi.Serial);
            }

            if (!context.MdnsDue)
            {
                return new WirelessAction(WirelessActionKind.None, string.Empty);
            }

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AdbDevice device in context.Devices)
            {
                known.Add(device.Serial);
            }

            foreach (WirelessService service in context.Services)
            {
                if (service.CanConnect && !known.Contains(service.Address))
                {
                    return new WirelessAction(WirelessActionKind.AdbConnect, service.Address);
                }
            }

            foreach (WirelessService service in context.Services)
            {
                if (service.CanPair)
                {
                    return new WirelessAction(WirelessActionKind.NeedPairing, service.Address);
                }
            }

            return new WirelessAction(WirelessActionKind.None, string.Empty);
        }
    }
}
