using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ReShadeForRimWorld;

// mirrors reshadefx::effect_module as written by rfx_capi.cpp (field names are snake_case there)
public sealed class FxType
{
    public int base_ { get; set; }
    public int rows { get; set; }
    public int cols { get; set; }
    public long array_length { get; set; }

    public const int Bool = 1, Int = 3, Uint = 5, Float = 7, String = 8;
}

public sealed class FxConstant
{
    public float[] f { get; set; }
    public int[] i { get; set; }
    public string s { get; set; }
    public List<FxConstant> array { get; set; }
}

public sealed class FxAnnotation
{
    public string name { get; set; }
    public FxType type { get; set; }
    public FxConstant value { get; set; }
}

public sealed class FxTextureInfo
{
    public string name { get; set; }
    public string unique_name { get; set; }
    public string semantic { get; set; }
    public int width { get; set; }
    public int height { get; set; }
    public int depth { get; set; }
    public int levels { get; set; }
    public int type { get; set; }
    public int format { get; set; }
    public bool render_target { get; set; }
    public bool storage_access { get; set; }
    public List<FxAnnotation> annotations { get; set; }
}

public sealed class FxSampler
{
    public string name { get; set; }
    public string unique_name { get; set; }
    public string texture_name { get; set; }
    public int filter { get; set; }
    public int address_u { get; set; }
    public int address_v { get; set; }
    public int address_w { get; set; }
    public float min_lod { get; set; }
    public float max_lod { get; set; }
    public float lod_bias { get; set; }
    public bool srgb { get; set; }
}

public sealed class FxStorage
{
    public string name { get; set; }
    public string unique_name { get; set; }
    public string texture_name { get; set; }
    public int level { get; set; }
}

public sealed class FxUniform
{
    public string name { get; set; }
    public string unique_name { get; set; }
    public FxType type { get; set; }
    public int size { get; set; }
    public int offset { get; set; }
    public List<FxAnnotation> annotations { get; set; }
    public bool has_initializer { get; set; }
    public FxConstant initializer { get; set; }
}

public sealed class FxBlend
{
    public bool enable { get; set; }
    public int src_color { get; set; }
    public int dst_color { get; set; }
    public int color_op { get; set; }
    public int src_alpha { get; set; }
    public int dst_alpha { get; set; }
    public int alpha_op { get; set; }
    public int write_mask { get; set; }
}

public sealed class FxBinding
{
    public int index { get; set; }
    public int binding { get; set; }
    public bool srgb { get; set; }
}

public sealed class FxPass
{
    public string name { get; set; }
    public List<string> render_targets { get; set; }
    public string vs { get; set; }
    public string ps { get; set; }
    public string cs { get; set; }
    public bool generate_mipmaps { get; set; }
    public bool clear_render_targets { get; set; }
    public bool srgb_write_enable { get; set; }
    public List<FxBlend> blend { get; set; }
    public bool stencil_enable { get; set; }
    public int stencil_read_mask { get; set; }
    public int stencil_write_mask { get; set; }
    public int stencil_ref { get; set; }
    public int stencil_func { get; set; }
    public int stencil_pass_op { get; set; }
    public int stencil_fail_op { get; set; }
    public int stencil_depth_fail_op { get; set; }
    public int topology { get; set; }
    public int num_vertices { get; set; }
    public int viewport_width { get; set; }
    public int viewport_height { get; set; }
    public int viewport_dispatch_z { get; set; }
    public List<FxBinding> texture_bindings { get; set; }
    public List<FxBinding> sampler_bindings { get; set; }
    public List<FxBinding> storage_bindings { get; set; }
}

public sealed class FxTechnique
{
    public string name { get; set; }
    public List<FxAnnotation> annotations { get; set; }
    public List<FxPass> passes { get; set; }
}

public sealed class FxEntryPoint
{
    public string name { get; set; }
    public int type { get; set; }
}

public sealed class FxModule
{
    public List<FxTextureInfo> textures { get; set; }
    public List<FxSampler> samplers { get; set; }
    public List<FxStorage> storages { get; set; }
    public List<FxUniform> uniforms { get; set; }
    public List<FxUniform> spec_constants { get; set; }
    public int total_uniform_size { get; set; }
    public List<FxTechnique> techniques { get; set; }
    public List<FxEntryPoint> entry_points { get; set; }

    public static FxModule Parse(string json)
    {
        // "base" is a C# keyword-ish name we avoid, so map it by hand
        json = json.Replace("\"base\":", "\"base_\":");
        return JsonSerializer.Deserialize<FxModule>(json);
    }
}

public static class FxAnnotations
{
    public static FxAnnotation Find(this List<FxAnnotation> list, string name)
    {
        return list?.FirstOrDefault(a => a.name == name);
    }

    public static string GetString(this List<FxAnnotation> list, string name)
    {
        return list.Find(name)?.value.s;
    }
}
