using System;
using System.Collections.Generic;

namespace ReShadeForRimWorld;

// GPU objects can still be in use by frames in flight, so destroying them waits a few frames. When the game closes
// there are no more frames: everything goes right away, or the engine finds our memory still taken and aborts
public static class GpuGarbage
{
    private static readonly List<(long frame, Action destroy)> pending = new();
    private static long frame;
    private static int delay = 4;
    private static bool closing;

    public static void Add(Action destroy)
    {
        if (closing)
        {
            destroy();
            return;
        }
        pending.Add((frame, destroy));
    }

    // the game is closing and the GPU is idle (the engine waits for it before disposing the render features)
    public static void Close()
    {
        closing = true;
        foreach ((long _, Action destroy) in pending)
        {
            destroy();
        }
        pending.Clear();
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
