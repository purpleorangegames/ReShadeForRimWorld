// Small C API over ReShade's effect compiler (reshadefx), so the C# side can load .fx files.
// Compiles one effect to SPIR-V and describes its module as JSON.

#include "effect_parser.hpp"
#include "effect_codegen.hpp"
#include "effect_preprocessor.hpp"
#include <cstdio>
#include <cstring>
#include <memory>
#include <string>
#include <unordered_map>

#if defined(_WIN32)
#define RFX_API extern "C" __declspec(dllexport)
#else
#define RFX_API extern "C" __attribute__((visibility("default")))
#endif

struct rfx_result
{
	bool success = false;
	std::string errors;
	std::string json;
	std::string preprocessed;
	std::unordered_map<std::string, std::string> spirv;
};

namespace
{
	struct json_writer
	{
		std::string s;
		bool first = true;

		void sep() { if (!first) s += ','; first = false; }
		void key(const char *k) { sep(); str(k); s += ':'; first = true; }
		void str(const std::string &v)
		{
			s += '"';
			for (unsigned char c : v)
			{
				switch (c)
				{
				case '"': s += "\\\""; break;
				case '\\': s += "\\\\"; break;
				case '\n': s += "\\n"; break;
				case '\r': s += "\\r"; break;
				case '\t': s += "\\t"; break;
				default:
					if (c < 0x20) { char buf[8]; snprintf(buf, sizeof(buf), "\\u%04x", c); s += buf; }
					else s += static_cast<char>(c);
				}
			}
			s += '"';
		}
		void begin_obj() { sep(); s += '{'; first = true; }
		void end_obj() { s += '}'; first = false; }
		void begin_arr() { sep(); s += '['; first = true; }
		void end_arr() { s += ']'; first = false; }
		void kv(const char *k, const std::string &v) { key(k); first = false; str(v); }
		void kv(const char *k, long long v) { key(k); first = false; s += std::to_string(v); }
		void kv(const char *k, bool v) { key(k); first = false; s += v ? "true" : "false"; }
		void kv(const char *k, float v) { key(k); first = false; num(v); }
		void num(float v)
		{
			char buf[32];
			if (v != v) { s += "0"; return; }
			if (v > 3.4e38f) { s += "3.4e38"; return; }
			if (v < -3.4e38f) { s += "-3.4e38"; return; }
			snprintf(buf, sizeof(buf), "%.9g", v);
			s += buf;
		}
		void val_num(float v) { sep(); num(v); }
		void val_int(long long v) { sep(); s += std::to_string(v); }
	};

	void write_type(json_writer &w, const char *k, const reshadefx::type &t)
	{
		w.key(k);
		w.begin_obj();
		w.kv("base", (long long)t.base);
		w.kv("rows", (long long)t.rows);
		w.kv("cols", (long long)t.cols);
		w.kv("array_length", (long long)t.array_length);
		w.end_obj();
	}

	// Constants are written as {f:[..16], i:[..16], s:"", array:[...]} so the C# side can read whichever view it needs.
	void write_constant(json_writer &w, const char *k, const reshadefx::constant &c)
	{
		if (k) w.key(k);
		w.begin_obj();
		w.key("f"); w.first = false; w.s += '['; w.first = true;
		for (float f : c.as_float) w.val_num(f);
		w.end_arr();
		w.key("i"); w.first = false; w.s += '['; w.first = true;
		for (int32_t i : c.as_int) w.val_int(i);
		w.end_arr();
		w.kv("s", c.string_data);
		w.key("array"); w.first = false; w.s += '['; w.first = true;
		for (const reshadefx::constant &e : c.array_data) write_constant(w, nullptr, e);
		w.end_arr();
		w.end_obj();
	}

	void write_annotations(json_writer &w, const std::vector<reshadefx::annotation> &list)
	{
		w.key("annotations");
		w.first = false; w.s += '['; w.first = true;
		for (const reshadefx::annotation &a : list)
		{
			w.begin_obj();
			w.kv("name", a.name);
			write_type(w, "type", a.type);
			write_constant(w, "value", a.value);
			w.end_obj();
		}
		w.end_arr();
	}

	void write_uniform(json_writer &w, const reshadefx::uniform &u)
	{
		w.begin_obj();
		w.kv("name", u.name);
		w.kv("unique_name", u.unique_name);
		write_type(w, "type", u.type);
		w.kv("size", (long long)u.size);
		w.kv("offset", (long long)u.offset);
		write_annotations(w, u.annotations);
		w.kv("has_initializer", u.has_initializer_value);
		write_constant(w, "initializer", u.initializer_value);
		w.end_obj();
	}

	std::string module_to_json(const reshadefx::effect_module &m)
	{
		json_writer w;
		w.begin_obj();

		w.key("textures"); w.first = false; w.s += '['; w.first = true;
		for (const reshadefx::texture &t : m.textures)
		{
			w.begin_obj();
			w.kv("name", t.name);
			w.kv("unique_name", t.unique_name);
			w.kv("semantic", t.semantic);
			w.kv("width", (long long)t.width);
			w.kv("height", (long long)t.height);
			w.kv("depth", (long long)t.depth);
			w.kv("levels", (long long)t.levels);
			w.kv("type", (long long)t.type);
			w.kv("format", (long long)t.format);
			w.kv("render_target", t.render_target);
			w.kv("storage_access", t.storage_access);
			write_annotations(w, t.annotations);
			w.end_obj();
		}
		w.end_arr();

		w.key("samplers"); w.first = false; w.s += '['; w.first = true;
		for (const reshadefx::sampler &s : m.samplers)
		{
			w.begin_obj();
			w.kv("name", s.name);
			w.kv("unique_name", s.unique_name);
			w.kv("texture_name", s.texture_name);
			w.kv("filter", (long long)s.filter);
			w.kv("address_u", (long long)s.address_u);
			w.kv("address_v", (long long)s.address_v);
			w.kv("address_w", (long long)s.address_w);
			w.kv("min_lod", s.min_lod);
			w.kv("max_lod", s.max_lod);
			w.kv("lod_bias", s.lod_bias);
			w.kv("srgb", s.srgb);
			w.end_obj();
		}
		w.end_arr();

		w.key("storages"); w.first = false; w.s += '['; w.first = true;
		for (const reshadefx::storage &s : m.storages)
		{
			w.begin_obj();
			w.kv("name", s.name);
			w.kv("unique_name", s.unique_name);
			w.kv("texture_name", s.texture_name);
			w.kv("level", (long long)s.level);
			w.end_obj();
		}
		w.end_arr();

		w.key("uniforms"); w.first = false; w.s += '['; w.first = true;
		for (const reshadefx::uniform &u : m.uniforms) write_uniform(w, u);
		w.end_arr();

		w.key("spec_constants"); w.first = false; w.s += '['; w.first = true;
		for (const reshadefx::uniform &u : m.spec_constants) write_uniform(w, u);
		w.end_arr();

		w.kv("total_uniform_size", (long long)m.total_uniform_size);

		w.key("techniques"); w.first = false; w.s += '['; w.first = true;
		for (const reshadefx::technique &t : m.techniques)
		{
			w.begin_obj();
			w.kv("name", t.name);
			write_annotations(w, t.annotations);
			w.key("passes"); w.first = false; w.s += '['; w.first = true;
			for (const reshadefx::pass &p : t.passes)
			{
				w.begin_obj();
				w.kv("name", p.name);
				w.key("render_targets"); w.first = false; w.s += '['; w.first = true;
				for (const std::string &rt : p.render_target_names) { w.sep(); w.str(rt); }
				w.end_arr();
				w.kv("vs", p.vs_entry_point);
				w.kv("ps", p.ps_entry_point);
				w.kv("cs", p.cs_entry_point);
				w.kv("generate_mipmaps", p.generate_mipmaps);
				w.kv("clear_render_targets", p.clear_render_targets);
				w.kv("srgb_write_enable", p.srgb_write_enable);
				w.key("blend"); w.first = false; w.s += '['; w.first = true;
				for (int i = 0; i < 8; ++i)
				{
					w.begin_obj();
					w.kv("enable", p.blend_enable[i]);
					w.kv("src_color", (long long)p.source_color_blend_factor[i]);
					w.kv("dst_color", (long long)p.dest_color_blend_factor[i]);
					w.kv("color_op", (long long)p.color_blend_op[i]);
					w.kv("src_alpha", (long long)p.source_alpha_blend_factor[i]);
					w.kv("dst_alpha", (long long)p.dest_alpha_blend_factor[i]);
					w.kv("alpha_op", (long long)p.alpha_blend_op[i]);
					w.kv("write_mask", (long long)p.render_target_write_mask[i]);
					w.end_obj();
				}
				w.end_arr();
				w.kv("stencil_enable", p.stencil_enable);
				w.kv("stencil_read_mask", (long long)p.stencil_read_mask);
				w.kv("stencil_write_mask", (long long)p.stencil_write_mask);
				w.kv("stencil_ref", (long long)p.stencil_reference_value);
				w.kv("stencil_func", (long long)p.stencil_comparison_func);
				w.kv("stencil_pass_op", (long long)p.stencil_pass_op);
				w.kv("stencil_fail_op", (long long)p.stencil_fail_op);
				w.kv("stencil_depth_fail_op", (long long)p.stencil_depth_fail_op);
				w.kv("topology", (long long)p.topology);
				w.kv("num_vertices", (long long)p.num_vertices);
				w.kv("viewport_width", (long long)p.viewport_width);
				w.kv("viewport_height", (long long)p.viewport_height);
				w.kv("viewport_dispatch_z", (long long)p.viewport_dispatch_z);
				w.key("texture_bindings"); w.first = false; w.s += '['; w.first = true;
				for (const reshadefx::texture_binding &b : p.texture_bindings)
				{
					w.begin_obj();
					w.kv("index", (long long)b.index);
					w.kv("binding", (long long)b.entry_point_binding);
					w.kv("srgb", b.srgb);
					w.end_obj();
				}
				w.end_arr();
				w.key("sampler_bindings"); w.first = false; w.s += '['; w.first = true;
				for (const reshadefx::sampler_binding &b : p.sampler_bindings)
				{
					w.begin_obj();
					w.kv("index", (long long)b.index);
					w.kv("binding", (long long)b.entry_point_binding);
					w.end_obj();
				}
				w.end_arr();
				w.key("storage_bindings"); w.first = false; w.s += '['; w.first = true;
				for (const reshadefx::storage_binding &b : p.storage_bindings)
				{
					w.begin_obj();
					w.kv("index", (long long)b.index);
					w.kv("binding", (long long)b.entry_point_binding);
					w.end_obj();
				}
				w.end_arr();
				w.end_obj();
			}
			w.end_arr();
			w.end_obj();
		}
		w.end_arr();

		w.key("entry_points"); w.first = false; w.s += '['; w.first = true;
		for (const auto &e : m.entry_points)
		{
			w.begin_obj();
			w.kv("name", e.first);
			w.kv("type", (long long)e.second);
			w.end_obj();
		}
		w.end_arr();

		w.end_obj();
		return w.s;
	}
}

// defines: "NAME=VALUE" strings. include_paths: directories searched for #include.
RFX_API rfx_result *rfx_compile(const char *path, const char *const *include_paths, int include_count, const char *const *defines, int define_count)
{
	auto *r = new rfx_result();

	reshadefx::preprocessor pp;
	for (int i = 0; i < define_count; ++i)
	{
		std::string d = defines[i];
		const size_t eq = d.find('=');
		if (eq == std::string::npos)
			pp.add_macro_definition(d, "1");
		else
			pp.add_macro_definition(d.substr(0, eq), d.substr(eq + 1));
	}
	for (int i = 0; i < include_count; ++i)
		pp.add_include_path(std::filesystem::u8path(include_paths[i]));

	// Same compatibility macros ReShade itself adds
	pp.append_string(
		"#define tex2Doffset(s, coords, offset) tex2D(s, coords, offset)\n"
		"#define tex2Dlodoffset(s, coords, offset) tex2Dlod(s, coords, offset)\n"
		"#define tex2Dgather(s, t, c) tex2Dgather##c(s, t)\n"
		"#define tex2Dgatheroffset(s, t, o, c) tex2Dgather##c(s, t, o)\n"
		"#define tex2Dgather0 tex2DgatherR\n"
		"#define tex2Dgather1 tex2DgatherG\n"
		"#define tex2Dgather2 tex2DgatherB\n"
		"#define tex2Dgather3 tex2DgatherA\n");

	const bool preprocessed = pp.append_file(std::filesystem::u8path(path));
	r->errors += pp.errors();
	if (!preprocessed)
		return r;
	r->preprocessed = pp.output();

	std::unique_ptr<reshadefx::codegen> codegen(reshadefx::create_codegen_spirv(true, false, false, false, false));
	reshadefx::parser parser;
	const bool compiled = parser.parse(r->preprocessed, codegen.get());
	r->errors += parser.errors();
	if (!compiled)
		return r;

	const reshadefx::effect_module &m = codegen->module();
	r->json = module_to_json(m);

	for (const auto &e : m.entry_points)
	{
		std::string binary, assembly, errors;
		if (!codegen->assemble_code_for_entry_point(e.first, binary, assembly, errors))
		{
			r->errors += errors;
			return r;
		}
		r->spirv.emplace(e.first, std::move(binary));
	}

	r->success = true;
	return r;
}

RFX_API int rfx_success(const rfx_result *r) { return r && r->success ? 1 : 0; }
RFX_API const char *rfx_errors(const rfx_result *r) { return r ? r->errors.c_str() : ""; }
RFX_API const char *rfx_module_json(const rfx_result *r) { return r ? r->json.c_str() : ""; }

RFX_API int rfx_entry_spirv(const rfx_result *r, const char *entry_point, const void **data, size_t *size)
{
	if (!r) return 0;
	const auto it = r->spirv.find(entry_point);
	if (it == r->spirv.end()) return 0;
	*data = it->second.data();
	*size = it->second.size();
	return 1;
}

RFX_API void rfx_free(rfx_result *r) { delete r; }
