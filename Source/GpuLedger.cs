using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Verse;

namespace ReShadeForRimWorld;

// every GPU memory block the mod takes, by name, until it's freed. When the game closes anything still here makes the
// engine abort (and the system write a core dump, which is the long wait), so it's written to the log to find it
public static class GpuLedger
{
    private static readonly ConcurrentDictionary<long, string> taken = new();
    private static long next;

    public static long Take(string name)
    {
        long id = Interlocked.Increment(ref next);
        taken[id] = name;
        return id;
    }

    public static void Free(long id) => taken.TryRemove(id, out _);

    public static void Report(string when)
    {
        if (taken.IsEmpty)
        {
            return;
        }
        string list = string.Join(", ", taken.Values.GroupBy(n => n).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key));
        Log.Warning($"[ReShade] {when}: {taken.Count} GPU memory blocks still taken: {list}");
    }
}
