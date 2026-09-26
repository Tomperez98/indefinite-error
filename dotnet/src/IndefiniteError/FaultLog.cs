using System.Text;

namespace IndefiniteError;

/// <summary>Where fault lines go: stderr, outside tests. Each call is one whole line.</summary>
internal delegate void FaultLog(string line);

internal static class FaultLogs
{
    /// <summary>
    /// The process's stderr, written without <see cref="Console.Error"/>: that
    /// is a buffered writer anyone can replace.
    /// </summary>
    public static readonly FaultLog Stderr = To(Console.OpenStandardError());

    /// <summary>
    /// Writes each line to <paramref name="stream"/> as UTF-8, in one write, so
    /// lines from concurrent requests don't interleave or wait on a flush.
    /// </summary>
    public static FaultLog To(Stream stream) => line =>
    {
        try
        {
            stream.Write(Encoding.UTF8.GetBytes(line));
        }
        catch (IOException)
        {
            // stderr is closed: the response still names the fault.
        }
    };
}
