using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace AcerBatteryControl
{
    sealed class Deadline
    {
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly int milliseconds;
        public Deadline(int timeout) { milliseconds = timeout; }
        public int Remaining { get { return (int)Math.Max(0L, milliseconds - clock.ElapsedMilliseconds); } }
    }

    static class PipeIo
    {
        // APM on overlapped pipes: no extra thread blocked in Read/Write and no WaitForPipeDrain.
        public static int Transfer(Stream stream, byte[] buffer, int count, bool write, Deadline deadline)
        {
            var gate = new object();
            bool done = false;
            int result = 0;
            Exception error = null;
            AsyncCallback completed = delegate(IAsyncResult ar)
            {
                try
                {
                    if (write) stream.EndWrite(ar);
                    else result = stream.EndRead(ar);
                }
                catch (Exception ex) { error = ex; }
                finally { lock (gate) { done = true; Monitor.PulseAll(gate); } }
            };
            if (deadline.Remaining == 0) throw new IOException("IPC timed out.");
            if (write) stream.BeginWrite(buffer, 0, count, completed, null);
            else stream.BeginRead(buffer, 0, count, completed, null);
            lock (gate)
            {
                while (!done)
                {
                    int left = deadline.Remaining;
                    if (left == 0) throw new IOException("IPC timed out; the command outcome may be uncertain.");
                    Monitor.Wait(gate, left);
                }
                if (error != null) throw new IOException("IPC connection interrupted.", error);
                return result;
            }
        }

        public static string ReadResponse(Stream stream, Deadline deadline)
        {
            using (var body = new MemoryStream())
            {
                byte[] buffer = new byte[4096];
                int count;
                while ((count = Transfer(stream, buffer, buffer.Length, false, deadline)) != 0)
                {
                    if (body.Length + count > 256 * 1024) throw new IOException("IPC response too long.");
                    body.Write(buffer, 0, count);
                }
                return new System.Text.UTF8Encoding(false, true).GetString(body.ToArray());
            }
        }
    }
}
