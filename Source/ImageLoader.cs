using System;
using System.Collections.Generic;
using System.IO;
using Brutal.StbApi;
using Brutal.VulkanApi;

namespace ReShadeForRimWorld;

// loads the image a texture names in its "source" annotation and converts it to the texture's format and size
public static class ImageLoader
{
    // ReShade searches its texture paths recursively, the first match wins.
    // Names come from Windows, so backslashes and a different case still find the file.
    public static string Find(IEnumerable<string> searchPaths, string source)
    {
        string wanted = source.Replace('\\', '/').TrimStart('.', '/');
        foreach (string dir in searchPaths)
        {
            string direct = Path.Combine(dir, wanted);
            if (File.Exists(direct))
            {
                return direct;
            }
        }
        foreach (string dir in searchPaths)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(dir, file).Replace('\\', '/');
                if (relative.Equals(wanted, StringComparison.OrdinalIgnoreCase) || relative.EndsWith("/" + wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
        }
        return null;
    }

    public static unsafe byte[] Load(string path, int width, int height, VkFormat format, out string error)
    {
        error = null;
        int channels = format switch
        {
            VkFormat.R8UNorm => 1,
            VkFormat.R8G8UNorm => 2,
            VkFormat.R8G8B8A8UNorm => 4,
            _ => 0
        };
        if (channels == 0)
        {
            error = $"image textures in {format} are not supported yet";
            return null;
        }
        StbImage8 image = Stb.LoadFromMemory(File.ReadAllBytes(path), 4);
        try
        {
            if (image.IsNull())
            {
                error = "could not decode image (dds is not supported yet)";
                return null;
            }
            // nearest resize when the file doesn't match the declared size (ReShade resizes too)
            var pixels = new byte[width * height * channels];
            for (int y = 0; y < height; y++)
            {
                int sy = y * image.Height / height;
                for (int x = 0; x < width; x++)
                {
                    int sx = x * image.Width / width;
                    byte* src = image.Data + (sy * image.Width + sx) * 4;
                    int dst = (y * width + x) * channels;
                    for (int c = 0; c < channels; c++)
                    {
                        pixels[dst + c] = src[c];
                    }
                }
            }
            return pixels;
        }
        finally
        {
            image.Dispose();
        }
    }
}
