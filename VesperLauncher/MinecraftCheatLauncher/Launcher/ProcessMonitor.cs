using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace VesperLauncher.Launcher;

public sealed class ProcessMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private readonly object _syncRoot = new();
    private readonly Timer _pollTimer;
    private Process? _process;
    private bool _disposed;
    private bool _exitRaised;

    public ProcessMonitor()
    {
        _pollTimer = new Timer(PollProcessState, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event EventHandler? ProcessExited;

    public Process? CurrentProcess
    {
        get
        {
            lock (_syncRoot)
            {
                return _process;
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_syncRoot)
            {
                return IsProcessRunningNoLock();
            }
        }
    }

    public void Attach(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        lock (_syncRoot)
        {
            DetachNoLock(disposeProcess: false);
            _process = process;
            _exitRaised = false;

            try
            {
                process.EnableRaisingEvents = true;
                process.Exited += Process_OnExited;
            }
            catch
            {
                // The process can exit before event wiring finishes; polling will catch it.
            }

            _pollTimer.Change(PollInterval, PollInterval);
        }

        if (!IsRunning)
        {
            RaiseExitedOnce();
        }
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            DetachNoLock(disposeProcess: false);
        }
    }

    public async Task<bool> StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
    {
        Process? process;
        lock (_syncRoot)
        {
            process = _process;
        }

        if (process is null || HasExited(process))
        {
            RaiseExitedOnce();
            return true;
        }

        try
        {
            if (process.CloseMainWindow())
            {
                if (await WaitForExitAsync(process, gracefulTimeout, cancellationToken).ConfigureAwait(false))
                {
                    RaiseExitedOnce();
                    return true;
                }
            }

            if (!HasExited(process))
            {
                process.Kill(entireProcessTree: true);
            }

            await WaitForExitAsync(process, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            RaiseExitedOnce();
            return true;
        }
        catch (InvalidOperationException)
        {
            RaiseExitedOnce();
            return true;
        }
        catch
        {
            if (HasExited(process))
            {
                RaiseExitedOnce();
                return true;
            }

            throw;
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DetachNoLock(disposeProcess: false);
            _pollTimer.Dispose();
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static async Task<bool> WaitForExitAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var waitTask = process.WaitForExitAsync(cancellationToken);
        var delayTask = Task.Delay(timeout, cancellationToken);
        return await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false) == waitTask;
    }

    private void Process_OnExited(object? sender, EventArgs e)
    {
        RaiseExitedOnce();
    }

    private void PollProcessState(object? state)
    {
        if (!IsRunning)
        {
            RaiseExitedOnce();
        }
    }

    private void RaiseExitedOnce()
    {
        var shouldRaise = false;
        lock (_syncRoot)
        {
            if (!_exitRaised)
            {
                _exitRaised = true;
                shouldRaise = true;
            }

            DetachNoLock(disposeProcess: false);
        }

        if (shouldRaise)
        {
            ProcessExited?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool IsProcessRunningNoLock()
    {
        return _process is not null && !HasExited(_process);
    }

    private void DetachNoLock(bool disposeProcess)
    {
        _pollTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        if (_process is null)
        {
            return;
        }

        try
        {
            _process.Exited -= Process_OnExited;
        }
        catch
        {
            // The process object can already be in a terminal state.
        }

        if (disposeProcess)
        {
            _process.Dispose();
        }

        _process = null;
    }
}

