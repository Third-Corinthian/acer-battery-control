using System;
using System.Diagnostics;
using System.Threading;

namespace AcerBatteryControl
{
    // A timeout does not cancel a native call. Until the timed-out call returns, no new
    // work is admitted: at most ONE background thread remains in the driver, with no late
    // write continuations. When that thread returns, the channel reopens automatically.
    sealed class CallPermit
    {
        internal volatile bool Cancelled;
        readonly Stopwatch clock;
        readonly int timeoutMs;
        public CallPermit(Stopwatch elapsed, int timeout) { clock = elapsed; timeoutMs = timeout; }
        public void Check()
        {
            if (Cancelled || clock.ElapsedMilliseconds >= timeoutMs)
                throw new AcerException("Operation timed out: no further device access.");
        }
    }

    sealed class BoundedCall
    {
        readonly object gate = new object();
        readonly int timeoutMs;
        bool busy, stuck;

        public BoundedCall(int timeout) { timeoutMs = timeout; }

        // true while a timed-out call is still inside the driver.
        public bool Stuck { get { lock (gate) { return stuck; } } }

        public T Run<T>(Func<CallPermit, T> action)
        {
            lock (gate)
            {
                if (stuck) throw new AcerException("WMI driver still busy with a timed-out call: retry at the next check.");
                if (busy) throw new AcerException("Device access already in progress.");
                busy = true;
                var watch = Stopwatch.StartNew();
                var permit = new CallPermit(watch, timeoutMs);
                T value = default(T);
                Exception error = null;
                bool done = false;
                var worker = new Thread(delegate()
                {
                    try { permit.Check(); value = action(permit); }
                    catch (Exception ex) { error = ex; }
                    finally
                    {
                        // The thread returning from the driver reopens the channel, even after a timeout.
                        lock (gate) { done = true; busy = false; stuck = false; Monitor.PulseAll(gate); }
                    }
                });
                worker.IsBackground = true;
                worker.Name = "WMI with deadline";
                try { worker.Start(); }
                catch { busy = false; throw; }
                while (!done)
                {
                    int left = timeoutMs - (int)Math.Min(int.MaxValue, watch.ElapsedMilliseconds);
                    if (left <= 0)
                    {
                        stuck = true;
                        permit.Cancelled = true;
                        throw new AcerException("WMI timed out: outcome uncertain, new access suspended until the driver responds.");
                    }
                    Monitor.Wait(gate, left);
                }
                // The worker has already exited: the channel stays open, but a late result is not used.
                if (watch.ElapsedMilliseconds >= timeoutMs)
                    throw new AcerException("WMI result arrived after the deadline: discarded, state must be read again.");
                if (error != null) throw new AcerException(error.Message);
                return value;
            }
        }
    }
}
