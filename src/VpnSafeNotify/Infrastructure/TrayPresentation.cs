using System;
using VpnSafeNotify.Models;

namespace VpnSafeNotify.Infrastructure;

public enum TrayTone { Gray, Green, Amber, Red }

public sealed record TrayPresentation(string Tooltip, TrayTone Tone)
{
    public static TrayPresentation Create(bool running, MonitoringSnapshot? snapshot, DateTimeOffset? resumeAt = null)
    {
        const string prefix = "VPN Safe Notify — ";
        if (!running)
            return new(prefix + (resumeAt is { } until ? $"Paused until {until.ToLocalTime():HH:mm}" : "Paused"), TrayTone.Gray);
        if (snapshot is null) return new(prefix + "Checking", TrayTone.Gray);
        if (snapshot.Internet.State == InternetState.Offline) return new(prefix + "Offline", TrayTone.Gray);
        string internet = snapshot.Internet.State == InternetState.Online ? "Online" : "Internet unknown";
        return snapshot.Vpn.State switch
        {
            VpnState.Connected => new(prefix + internet + " · VPN detected", TrayTone.Green),
            VpnState.Unverified => new(prefix + internet + " · VPN unverified", TrayTone.Amber),
            _ => new(prefix + internet + " · VPN disconnected",
                snapshot.Internet.State == InternetState.Online ? TrayTone.Red : TrayTone.Gray)
        };
    }
}
