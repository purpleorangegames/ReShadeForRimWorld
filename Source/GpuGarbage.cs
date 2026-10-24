using System;
using System.Collections.Generic;

namespace ReShadeForRimWorld;

// GPU objects can still be in use by frames in flight, so destroying them waits a few frames
public static class GpuGarbage
{
    private static readonly List<(long frame, Action destroy)> pending = new();
    private static long frame;
    private static int delay = 4;

    public static void Add(Action destroy)
    {
        pending.Add((frame, destroy));
    }

    // render thread, once per recorded frame
    public static void Tick(int framesInFlight)
    {
        frame++;
        delay = framesInFlight + 2;
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            if (frame - pending[i].frame >= delay)
            {
                pending[i].destroy();
                pending.RemoveAt(i);
            }
        }
    }
}
