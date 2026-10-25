using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ReShadeForRimWorld;

// compiled effects on disk, so the next start doesn't compile everything again.
// One file per effect, screen size, definitions and mode. It keeps a hash of the effect and of every
// file it included, and is only used while all of those still match.
public static class CompileCache
{
    private const int Version = 1;
    private static string nativeStamp;

    public static string Dir => Path.Combine(ReShadeMod.BaseDir, "Cache");

    // a new build of the compiler makes every old entry useless
    private static string NativeStamp()
    {
        if (nativeStamp == null)
        {
            try
            {
                var file = new FileInfo(Directory.GetFiles(NativeFx.NativeDir)[0]);
                nativeStamp = file.Length + "-" + file.LastWriteTimeUtc.Ticks;
            }
            catch
            {
                nativeStamp = "";
            }
        }
        return nativeStamp;
    }

    private static string FileFor(string effectPath, int width, int height, string defineKey, bool performanceMode)
    {
        string key = $"{Version}\n{NativeStamp()}\n{Path.GetFullPath(effectPath)}\n{width}x{height}\n{performanceMode}\n{defineKey}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 8);
        return Path.Combine(Dir, Path.GetFileNameWithoutExtension(effectPath) + "-" + hash + ".bin");
    }

    private static string Hash(string path)
    {
        return File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "";
    }

    public static bool TryLoad(string effectPath, int width, int height, string defineKey, bool performanceMode,
        out string moduleJson, out Dictionary<string, byte[]> spirv, out List<string> included, out string warnings)
    {
        moduleJson = null;
        spirv = null;
        included = null;
        warnings = null;
        try
        {
            string file = FileFor(effectPath, width, height, defineKey, performanceMode);
            if (!File.Exists(file))
            {
                return false;
            }
            using var reader = new BinaryReader(File.OpenRead(file));
            if (reader.ReadInt32() != Version || reader.ReadString() != Hash(effectPath))
            {
                return false;
            }
            int count = reader.ReadInt32();
            included = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                string path = reader.ReadString();
                if (reader.ReadString() != Hash(path))
                {
                    return false;
                }
                included.Add(path);
            }
            moduleJson = reader.ReadString();
            warnings = reader.ReadString();
            count = reader.ReadInt32();
            spirv = new Dictionary<string, byte[]>(count);
            for (int i = 0; i < count; i++)
            {
                string name = reader.ReadString();
                spirv[name] = reader.ReadBytes(reader.ReadInt32());
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Save(string effectPath, int width, int height, string defineKey, bool performanceMode,
        string moduleJson, Dictionary<string, byte[]> spirv, List<string> included, string warnings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string file = FileFor(effectPath, width, height, defineKey, performanceMode);
            string temp = file + "." + Environment.CurrentManagedThreadId + ".tmp";
            using (var writer = new BinaryWriter(File.Create(temp)))
            {
                writer.Write(Version);
                writer.Write(Hash(effectPath));
                writer.Write(included.Count);
                foreach (string path in included)
                {
                    writer.Write(path);
                    writer.Write(Hash(path));
                }
                writer.Write(moduleJson);
                writer.Write(warnings ?? "");
                writer.Write(spirv.Count);
                foreach (var kv in spirv)
                {
                    writer.Write(kv.Key);
                    writer.Write(kv.Value.Length);
                    writer.Write(kv.Value);
                }
            }
            File.Move(temp, file, true);
        }
        catch
        {
            // no cache this time, it just compiles again next start
        }
    }

    // the Settings tab's button
    public static void Clear()
    {
        try
        {
            if (Directory.Exists(Dir))
            {
                Directory.Delete(Dir, true);
            }
        }
        catch
        {
        }
    }
}
