using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;

namespace VpnSafeNotify.Infrastructure;

/// <summary>
/// Keeps one application instance per Windows user and interactive session and
/// forwards subsequent launches to the original instance.
/// </summary>
/// <remarks>
/// Construct and dispose this service on the same thread: Windows mutex
/// ownership belongs to the acquiring thread. Activation callbacks run on a
/// ThreadPool thread and must marshal window operations to the UI dispatcher.
/// </remarks>
public sealed class SingleInstanceService : IDisposable
{
    private readonly object _sync = new();
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly int _ownerThreadId;
    private RegisteredWaitHandle? _waitRegistration;
    private Action? _activate;
    private bool _disposed;

    public SingleInstanceService(string? namespaceSuffix = null)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string userSid = identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows user could not be identified.");
        using Process currentProcess = Process.GetCurrentProcess();
        string namePrefix = $@"Local\VpnSafeNotify.{userSid}.{currentProcess.SessionId}";
        if (namespaceSuffix is not null)
        {
            if (!Guid.TryParseExact(namespaceSuffix, "N", out _))
                throw new ArgumentException("An isolated instance namespace must be a GUID.", nameof(namespaceSuffix));
            namePrefix += ".Smoke." + namespaceSuffix;
        }

        _ownerThreadId = Environment.CurrentManagedThreadId;
        _mutex = new Mutex(initiallyOwned: false, namePrefix + ".Mutex");
        try
        {
            // Create/open the event before claiming the mutex so a second
            // instance can signal immediately. AutoReset retains an early
            // signal until the first instance starts listening.
            _activationEvent = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                namePrefix + ".Activate");
            try
            {
                IsFirstInstance = _mutex.WaitOne(millisecondsTimeout: 0);
            }
            catch (AbandonedMutexException)
            {
                // WaitOne grants ownership when a previous owner crashed.
                IsFirstInstance = true;
            }
            catch
            {
                _activationEvent.Dispose();
                throw;
            }
        }
        catch
        {
            _mutex.Dispose();
            throw;
        }
    }

    public bool IsFirstInstance { get; }

    public void SignalExistingInstance()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activationEvent.Set();
        }
    }

    public void StartListening(Action activate)
    {
        ArgumentNullException.ThrowIfNull(activate);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsFirstInstance)
            {
                throw new InvalidOperationException("Only the first instance can listen for activation.");
            }

            if (_waitRegistration is not null)
            {
                throw new InvalidOperationException("Activation listening has already started.");
            }

            _activate = activate;
            _waitRegistration = ThreadPool.RegisterWaitForSingleObject(
                _activationEvent,
                static (state, _) => ((SingleInstanceService)state!).OnActivation(),
                this,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
    }

    private void OnActivation()
    {
        Action? activate;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            activate = _activate;
        }

        try
        {
            activate?.Invoke();
        }
        catch (Exception exception)
        {
            // A dispatcher may be shutting down while a callback is in flight.
            // Never let a ThreadPool callback exception terminate the process.
            Debug.WriteLine($"Single-instance activation failed: {exception}");
        }
    }

    public void Dispose()
    {
        RegisteredWaitHandle? registration;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (IsFirstInstance && Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                throw new InvalidOperationException(
                    "SingleInstanceService must be disposed on the thread that created it.");
            }

            _disposed = true;
            _activate = null;
            registration = _waitRegistration;
            _waitRegistration = null;
        }

        // Do not wait synchronously for callbacks: a callback can already be
        // posting work to the dispatcher that is currently shutting down.
        registration?.Unregister(waitObject: null);
        _activationEvent.Dispose();
        try
        {
            if (IsFirstInstance)
            {
                _mutex.ReleaseMutex();
            }
        }
        finally
        {
            _mutex.Dispose();
        }
    }
}
