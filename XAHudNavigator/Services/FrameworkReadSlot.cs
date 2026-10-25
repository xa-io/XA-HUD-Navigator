using System;

namespace XAHudNavigator.Services;

/// <summary>A bounded latest-request mailbox: capture on Framework, publish on the UI thread.</summary>
internal sealed class FrameworkReadSlot<T> : IDisposable
{
    private readonly object gate = new();
    private long generation;
    private bool disposed;
    private bool busy;
    private Func<T>? pending;
    private Action<T>? publish;
    private Action<Exception>? failure;
    private Action? completed;
    public bool IsPending { get { lock (gate) return busy; } }

    public void Request(Func<T> read, Action<T> apply, Action<Exception> onFailure)
    {
        lock (gate)
        {
            if (disposed) return;
            generation++;
            pending = read;
            publish = apply;
            failure = onFailure;
            completed = null;
            busy = true;
        }
    }

    public void Capture()
    {
        Func<T>? read;
        Action<T>? apply;
        Action<Exception>? onFailure;
        long version;
        lock (gate)
        {
            if (disposed || pending == null) return;
            read = pending; apply = publish; onFailure = failure; version = generation;
            pending = null;
        }
        Action result;
        try { var value = read(); result = () => apply!(value); }
        catch (Exception ex) { result = () => onFailure!(ex); }
        lock (gate)
            if (!disposed && generation == version) completed = result;
    }

    public void Apply()
    {
        lock (gate)
        {
            if (disposed || completed == null) return;
            var result = completed;
            completed = null;
            busy = false;
            result();
        }
    }

    public void Cancel()
    {
        lock (gate)
        {
            generation++;
            pending = null; completed = null; publish = null; failure = null;
            busy = false;
        }
    }

    public void Dispose()
    {
        lock (gate) { disposed = true; Cancel(); }
    }
}
