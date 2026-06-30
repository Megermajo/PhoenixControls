using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Visualist.Core;

namespace Phoenix.Controls.Visualist.WinUI.Hosting;

/// <summary>
/// Builds the <c>window.PHX = {…}</c> catalogue that the bundled
/// <c>widget-node-reference.html</c> renders for the <b>Visualist</b> widget-graph
/// editor. The structure (categories, nodes, sockets, types, default chips) is
/// generated live from <see cref="WidgetNodeRegistry"/> so the in-app reference
/// can never drift from the editor; <see cref="WidgetNodeProse"/> supplies the
/// authored prose / category palette on top.
///
/// <para>The Architect mirror is <c>NodeReferenceData</c>; the two pillars keep
/// independent generators + content layers per the chrome-independence rule. The
/// shapes differ: Visualist sockets carry a <see cref="SocketDataType"/> directly
/// (no colour round-trip), there are no hidden nodes, and the type system spans
/// the visual sockets (Image / Color / Scalar / Vector).</para>
///
/// <para>Process-stable: the catalogue is built once and cached; only the per-open
/// <c>INITIAL_ANCHOR</c> (F1-from-node deep link) varies between calls.</para>
/// </summary>
internal static class WidgetNodeReferenceData
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string? s_cachedCoreJson;
    private static readonly object s_gate = new();

    /// <summary>
    /// Returns a script that defines <c>window.PHX</c> for the Widget Node
    /// Reference page, deep-linking to <paramref name="anchorNodeTitle"/> when supplied.
    /// </summary>
    public static string BuildInjectionScript(string? anchorNodeTitle)
    {
        string core = GetCoreJson();
        string? anchor = string.IsNullOrEmpty(anchorNodeTitle) ? null : "n-" + Slug(anchorNodeTitle!);
        string anchorJson = anchor is null ? "null" : JsonSerializer.Serialize(anchor, s_json);
        string spliced = core.Length >= 1 && core[^1] == '}'
            ? string.Concat(core.AsSpan(0, core.Length - 1), ",\"INITIAL_ANCHOR\":", anchorJson, "}")
            : core;
        return "window.PHX = " + spliced + ";";
    }

    private static string GetCoreJson()
    {
        lock (s_gate)
        {
            if (s_cachedCoreJson is not null) return s_cachedCoreJson;
            // The registry is populated by NodeTemplates.RegisterAll(); calling it
            // here makes the reference work even if the Visualist pillar hasn't been
            // opened yet (e.g. from Hub's Help menu at startup). Idempotent — guarded
            // by WidgetNodeRegistry.HasTemplates inside RegisterAll.
            try { NodeTemplates.RegisterAll(); } catch { /* best-effort — fall through to whatever is registered */ }

            var root = BuildRoot();
            s_cachedCoreJson = JsonSerializer.Serialize(root, s_json);
            return s_cachedCoreJson;
        }
    }

    private static object BuildRoot()
    {
        var templates = WidgetNodeRegistry.Templates.Values.ToList();
        var catalog = BuildCatalog(templates);

        return new
        {
            META = new
            {
                categories = catalog.Count,
                nodes = templates.Count,
                version = ResolveVersion(),
                pillar = "Visualist",
            },
            SOCKETS = BuildSockets(),
            USER_FIELDS = Array.Empty<object>(),
            NODE_CATALOG = catalog,
        };
    }

    // ── socket type system (the visual kinds) ─────────────────────────────
    private static object BuildSockets() => new Dictionary<string, object>
    {
        ["flow"]    = new { color = "#F5EFE3", shape = "chevron" },
        ["image"]   = new { color = "#5BA3D0", shape = "square" },
        ["color"]   = new { color = "#E27DA8", shape = "circle" },
        ["scalar"]  = new { color = "#9CC97A", shape = "circle" },
        ["float"]   = new { color = "#7FBED1", shape = "circle" },
        ["vector2"] = new { color = "#E5A24E", shape = "diamond" },
        ["vector3"] = new { color = "#E0913A", shape = "diamond" },
        ["vector4"] = new { color = "#D9812A", shape = "diamond" },
        ["string"]  = new { color = "#D8C28A", shape = "circle" },
        ["audio"]   = new { color = "#C893BC", shape = "triangle" },
        ["any"]     = new { color = "#A89683", shape = "square" },
    };

    // ── catalogue ─────────────────────────────────────────────────────────
    private static List<object> BuildCatalog(List<WidgetNodeRegistry.NodeTemplate> templates)
    {
        var byCat = templates
            .GroupBy(t => string.IsNullOrEmpty(t.Category) ? "Other" : t.Category)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var ordered = new List<WidgetNodeProse.CategoryMeta>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var meta in WidgetNodeProse.Categories)
        {
            if (byCat.ContainsKey(meta.Category) && seen.Add(meta.Category))
                ordered.Add(meta);
        }
        foreach (var cat in byCat.Keys.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Add(cat))
                ordered.Add(new WidgetNodeProse.CategoryMeta(cat, "#3A4257", string.Empty));
        }

        var result = new List<object>(ordered.Count);
        foreach (var meta in ordered)
        {
            var members = byCat[meta.Category];
            var nodeObjs = members.Select(BuildNode).ToList();
            result.Add(new
            {
                cat = meta.Category,
                color = meta.Color,
                tint = HexToRgba(meta.Color, 0.10),
                blurb = string.IsNullOrEmpty(meta.Blurb) ? null : meta.Blurb,
                badge = meta.Badge,
                nodes = nodeObjs,
            });
        }
        return result;
    }

    private static object BuildNode(WidgetNodeRegistry.NodeTemplate t)
    {
        WidgetNodeProse.Nodes.TryGetValue(t.Title, out var info);

        string summary = info.Summary
            ?? WidgetNodeRegistry.GetTooltip(t.Title)
            ?? t.Title;

        return new
        {
            name = t.Title,
            summary = Escape(summary),
            ins = MapSockets(t.Inputs, t, isInput: true),
            outs = MapSockets(t.Outputs, t, isInput: false),
            description = info.Description,
            example = info.Example,
            since = info.Since,
            badge = info.Badge,
        };
    }

    private static List<object> MapSockets(
        IReadOnlyList<WidgetNodeRegistry.SocketSpec> sockets, WidgetNodeRegistry.NodeTemplate t, bool isInput)
    {
        var list = new List<object>(sockets.Count);
        foreach (var s in sockets)
        {
            string kind = KindFor(s.DataType);
            string display = kind == "flow" && IsBareExecName(s.Name) ? "" : s.Name;

            string? def = null;
            if (isInput && TryGetDefault(t.DefaultAttributes, s.Name, out var d) && !string.IsNullOrEmpty(d))
                def = d;

            list.Add(new { k = kind, n = display, v = def });
        }
        return list;
    }

    private static string KindFor(SocketDataType dt) => dt switch
    {
        SocketDataType.Flow    => "flow",
        SocketDataType.Image   => "image",
        SocketDataType.Color   => "color",
        SocketDataType.Scalar  => "scalar",
        SocketDataType.Int     => "scalar",
        SocketDataType.Float   => "float",
        SocketDataType.Vector2 => "vector2",
        SocketDataType.Vector3 => "vector3",
        SocketDataType.Vector4 => "vector4",
        SocketDataType.String  => "string",
        SocketDataType.Audio   => "audio",
        _                      => "any",
    };

    private static bool IsBareExecName(string name) =>
        string.IsNullOrEmpty(name)
        || name.Equals("Flow", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Exec", StringComparison.OrdinalIgnoreCase);

    // Match an input socket to a default attribute by name (case-insensitive),
    // skipping the editor-metadata companions (Foo__Range / Foo__KnownValues).
    private static bool TryGetDefault(IReadOnlyDictionary<string, string> attrs, string socketName, out string value)
    {
        foreach (var kv in attrs)
        {
            if (kv.Key.IndexOf("__", StringComparison.Ordinal) >= 0) continue;
            if (kv.Key.Equals(socketName, StringComparison.OrdinalIgnoreCase)) { value = kv.Value; return true; }
        }
        value = string.Empty;
        return false;
    }

    // ── helpers ───────────────────────────────────────────────────────────
    private static string HexToRgba(string hex, double alpha)
    {
        try
        {
            string h = hex.TrimStart('#');
            if (h.Length != 6) return $"rgba(58,66,87,{alpha.ToString(CultureInfo.InvariantCulture)})";
            int r = int.Parse(h.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int g = int.Parse(h.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int b = int.Parse(h.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return $"rgba({r},{g},{b},{alpha.ToString(CultureInfo.InvariantCulture)})";
        }
        catch { return $"rgba(58,66,87,{alpha.ToString(CultureInfo.InvariantCulture)})"; }
    }

    private static string Escape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s!.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Anchor slug — MUST match the JS <c>slug()</c> in widget-node-reference.js.</summary>
    internal static string Slug(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool prevDash = false;
        foreach (char c in s.ToLowerInvariant())
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                sb.Append(c);
                prevDash = false;
            }
            else if (!prevDash)
            {
                sb.Append('-');
                prevDash = true;
            }
        }
        return sb.ToString().Trim('-');
    }

    private static string ResolveVersion()
    {
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(WidgetNodeReferenceData).Assembly;
            string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(info))
            {
                int plus = info.IndexOf('+');
                return plus >= 0 ? info[..plus] : info;
            }
            var v = asm.GetName().Version;
            return v?.ToString(3) ?? "current";
        }
        catch { return "current"; }
    }
}
