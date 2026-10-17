using System;
using VpnSafeNotify.Models;

namespace VpnSafeNotify.Services;

public interface IOverlayPresenter : IDisposable
{
    void Configure(AppSettings settings) { }
    void Show(EdgeEffect effect, double intensity);
    void Hide();
}

/// <summary>One reusable, one-shot scheduler owned by the alert controller.</summary>
public interface IAlertTimer : IDisposable
{
    void Schedule(TimeSpan delay, Action callback);
    void Cancel();
}
