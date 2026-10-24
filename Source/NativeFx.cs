using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ReShadeForRimWorld;

// bindings for libreshadefx (ReShade's effect compiler plus our small C API, see Native/rfx_capi.cpp)
public static unsafe class NativeFx
{
    private const string Lib = "reshadefx";

    public static string NativeDir;

    public static void Init(string modRoot)
    {
        string rid = OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsMacOS() ? "osx" : "linux-x64";
        NativeDir = Path.Combine(modRoot, "1.7", "Native", rid);
        NativeLibrary.SetDllImportResolver(typeof(NativeFx).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib)
        {
            return IntPtr.Zero;
        }
        string file = OperatingSystem.IsWindows() ? "reshadefx.dll" : OperatingSystem.IsMacOS() ? "libreshadefx.dylib" : "libreshadefx.so";
        return NativeLibrary.Load(Path.Combine(NativeDir, file));
    }

    [DllImport(Lib)] private static extern IntPtr rfx_compile(byte* path, byte** includePaths, int includeCount, byte** defines, int defineCount);
    [DllImport(Lib)] private static extern int rfx_success(IntPtr r);
    [DllImport(Lib)] private static extern IntPtr rfx_errors(IntPtr r);
    [DllImport(Lib)] private static extern IntPtr rfx_module_json(IntPtr r);
    [DllImport(Lib)] private static extern int rfx_entry_spirv(IntPtr r, byte* entryPoint, out IntPtr data, out nuint size);
    [DllImport(Lib)] private static extern void rfx_free(IntPtr r);

    public sealed class Result
    {
        public bool Success;
        public string Errors;
        public string ModuleJson;
        public Dictionary<string, byte[]> Spirv = new();
    }

    public static Result Compile(string path, IList<string> includePaths, IList<string> defines, IEnumerable<string> entryPoints = null)
    {
        var handles = new List<GCHandle>();
        byte* Pin(string s)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s + "\0");
            GCHandle h = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            handles.Add(h);
            return (byte*)h.AddrOfPinnedObject();
        }
        try
        {
            byte** inc = stackalloc byte*[Math.Max(1, includePaths.Count)];
            for (int i = 0; i < includePaths.Count; i++)
            {
                inc[i] = Pin(includePaths[i]);
            }
            byte** defs = stackalloc byte*[Math.Max(1, defines.Count)];
            for (int i = 0; i < defines.Count; i++)
            {
                defs[i] = Pin(defines[i]);
            }
            IntPtr r = rfx_compile(Pin(path), inc, includePaths.Count, defs, defines.Count);
            try
            {
                var result = new Result
                {
                    Success = rfx_success(r) != 0,
                    Errors = Marshal.PtrToStringUTF8(rfx_errors(r)) ?? "",
                    ModuleJson = Marshal.PtrToStringUTF8(rfx_module_json(r)) ?? ""
                };
                if (result.Success && entryPoints != null)
                {
                    foreach (string entry in entryPoints)
                    {
                        if (rfx_entry_spirv(r, Pin(entry), out IntPtr data, out nuint size) != 0)
                        {
                            byte[] spirv = new byte[(int)size];
                            Marshal.Copy(data, spirv, 0, spirv.Length);
                            result.Spirv[entry] = spirv;
                        }
                    }
                }
                return result;
            }
            finally
            {
                rfx_free(r);
            }
        }
        finally
        {
            foreach (GCHandle h in handles)
            {
                h.Free();
            }
        }
    }
}
