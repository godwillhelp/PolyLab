using System.Diagnostics;
using System.IO;
using System.Text;

namespace PolyLab.PBOGPacker.Core;

// Streams a child process line by line to a callback. stdout and stderr are read concurrently to
// avoid the classic pipe-buffer deadlock; both feed the same sink so the UI log stays in order.
public static class ProcRunner
{
    public sealed record ProcResult(int ExitCode, IReadOnlyList<string> Tail);

    public static async Task<ProcResult> RunAsync(
        string exe, IReadOnlyList<string> args, string? workDir,
        Action<string, bool> onLine, CancellationToken ct, int tailSize = 12)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (workDir != null) psi.WorkingDirectory = workDir;

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var tail = new Queue<string>(tailSize + 1);
        var gate = new object();

        void Sink(string? line, bool err)
        {
            if (line == null) return;
            lock (gate)
            {
                onLine(line, err);
                tail.Enqueue(line);
                while (tail.Count > tailSize) tail.Dequeue();
            }
        }

        if (!proc.Start()) return new ProcResult(-1, new[] { "не удалось запустить: " + exe });

        var pump = new[]
        {
            PumpAsync(proc.StandardOutput, l => Sink(l, false)),
            PumpAsync(proc.StandardError, l => Sink(l, true)),
        };

        using (ct.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch (Exception) { } }))
        {
            try { await proc.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                // Let the killed tree release its files before the caller cleans up after it.
                try { await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
                throw;
            }
            await Task.WhenAll(pump);
        }
        return new ProcResult(proc.ExitCode, tail.ToArray());
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
            if (line.Length > 0) onLine(line);
    }
}
