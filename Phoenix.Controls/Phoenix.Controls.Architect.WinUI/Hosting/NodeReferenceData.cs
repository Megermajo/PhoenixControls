using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Phoenix.Controls.Architect.Core;
using Phoenix.Controls.Architect.WinUI.Canvas;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.WinUI.Hosting;

/// <summary>
/// Builds the <c>window.PHX = {…}</c> catalogue that the bundled
/// <c>node-reference.html</c> renders. The structure (categories, nodes,
/// sockets, types, default chips, socket counts) is generated live from
/// <see cref="NodeRegistry"/> so the in-app reference can never drift from the
/// editor; <see cref="NodeProse"/> supplies the authored prose / category
/// palette layer on top, and <see cref="ArchitectHotkeyCatalog"/> supplies the
/// keyboard reference.
///
/// <para>The catalogue is process-stable (templates never change at runtime), so
/// it is built once and cached; only the per-open <c>INITIAL_ANCHOR</c> (the
/// F1-from-node deep link) varies between calls.</para>
/// </summary>
internal static class NodeReferenceData
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Default (HTML-safe) encoder: escapes < > & and non-ASCII to \uXXXX,
        // which both keeps the injected script safe and round-trips correctly
        // to the original characters at runtime (innerHTML sees real <code> etc).
    };

    private static string? s_cachedCoreJson; // everything except INITIAL_ANCHOR
    private static readonly object s_gate = new();

    /// <summary>
    /// Returns a script that defines <c>window.PHX</c> for the Node Reference
    /// page, deep-linking to <paramref name="anchorNodeTitle"/> when supplied.
    /// </summary>
    public static string BuildInjectionScript(string? anchorNodeTitle)
    {
        string core = GetCoreJson();
        string? anchor = string.IsNullOrEmpty(anchorNodeTitle) ? null : "n-" + Slug(anchorNodeTitle!);
        string anchorJson = anchor is null ? "null" : JsonSerializer.Serialize(anchor, s_json);
        // Splice the anchor into the cached core object (which ends with `}`).
        // core = "{ …catalogue… }" — insert the anchor key before the close.
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
            var root = BuildRoot();
            s_cachedCoreJson = JsonSerializer.Serialize(root, s_json);
            return s_cachedCoreJson;
        }
    }

    private static object BuildRoot()
    {
        // Hidden (no live path) nodes are omitted from the in-app Node Reference
        // too — see NodeRegistry.HiddenFromPalette.
        var templates = NodeRegistry.GetPaletteTemplates().ToList();
        var catalog = BuildCatalog(templates);

        return new
        {
            META = new
            {
                categories = catalog.Count,
                nodes = templates.Count,
                version = ResolveVersion(),
            },
            SOCKETS = BuildSockets(),
            USER_FIELDS = UserFields,
            NODE_CATALOG = catalog,
            PRIMER = BuildPrimer(),
        };
    }

    // ── sockets (the 6 design kinds) ──────────────────────────────────────
    private static object BuildSockets() => new Dictionary<string, object>
    {
        ["flow"]   = new { color = "#F5EFE3", shape = "chevron" },
        ["string"] = new { color = "#E5A24E", shape = "circle" },
        ["number"] = new { color = "#7FBED1", shape = "circle" },
        ["bool"]   = new { color = "#9CC97A", shape = "triangle" },
        ["user"]   = new { color = "#C893BC", shape = "diamond" },
        ["object"] = new { color = "#7BCFB0", shape = "square" },
    };

    private static readonly object[] UserFields =
    {
        new { n = "name",        k = "string", d = "Login name — lowercased, no @ prefix." },
        new { n = "displayName", k = "string", d = "Capitalised display name as shown in chat." },
        new { n = "id",          k = "string", d = "Stable Twitch user ID. Survives renames." },
        new { n = "isBot",       k = "bool",   d = "True for known bot accounts." },
        new { n = "isMod",       k = "bool",   d = "True for channel moderators (and the broadcaster)." },
        new { n = "isSub",       k = "bool",   d = "True if the user is currently subscribed." },
        new { n = "isVip",       k = "bool",   d = "True if the user has VIP." },
        new { n = "color",       k = "string", d = "Their chat name colour as #RRGGBB, or empty if Twitch default." },
    };

    // ── catalogue ─────────────────────────────────────────────────────────
    private static List<object> BuildCatalog(List<NodeTemplate> templates)
    {
        var byCat = templates
            .GroupBy(t => string.IsNullOrEmpty(t.Category) ? "Other" : t.Category)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        // Authored order first, then any registry category not covered, alpha.
        var ordered = new List<NodeProse.CategoryMeta>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var meta in NodeProse.Categories)
        {
            if (byCat.ContainsKey(meta.Category) && seen.Add(meta.Category))
                ordered.Add(meta);
        }
        foreach (var cat in byCat.Keys.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Add(cat))
                ordered.Add(new NodeProse.CategoryMeta(cat, "#3A4257", string.Empty));
        }

        var result = new List<object>(ordered.Count);
        foreach (var meta in ordered)
        {
            var members = byCat[meta.Category];
            bool subGrouped = string.Equals(meta.Category, "Platforms", StringComparison.OrdinalIgnoreCase);
            var nodeObjs = members.Select(t => BuildNode(t, subGrouped)).ToList();
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

    private static object BuildNode(NodeTemplate t, bool subGroupByNamespace)
    {
        NodeProse.Nodes.TryGetValue(t.Title, out var info);

        string summary = info.Summary
            ?? FirstSentence(t.Description)
            ?? (string.IsNullOrEmpty(t.DisplayName) ? t.Title : t.DisplayName);

        // When there's no authored body, the registry description IS the summary
        // — don't repeat it as a second paragraph.
        string? description = info.Description;
        if (description is null && !string.IsNullOrEmpty(t.Description)
            && !string.Equals(t.Description, summary, StringComparison.Ordinal))
        {
            description = "<p>" + Escape(t.Description) + "</p>";
        }

        return new
        {
            name = t.Title,
            summary = Escape(summary),
            ins = MapSockets(t.Inputs, t, isInput: true),
            outs = MapSockets(t.Outputs, t, isInput: false),
            description,
            example = info.Example,
            since = info.Since,
            badge = info.Badge,
            sub = subGroupByNamespace ? NamespaceLabel(t.Title) : null,
        };
    }

    private static List<object> MapSockets(
        IReadOnlyList<(string Name, Color Color)> sockets, NodeTemplate t, bool isInput)
    {
        var list = new List<object>(sockets.Count);
        foreach (var (name, color) in sockets)
        {
            string kind = KindFor(color, name);
            string display = kind == "flow" && IsBareExecName(name) ? "" : name;

            string? def = null;
            if (isInput && TryGetDefault(t.DefaultProperties, name, out var d) && !string.IsNullOrEmpty(d))
                def = d;

            bool? opt = null;
            if (isInput && t.RequiredInputs.Count > 0 && !t.RequiredInputs.Contains(name))
                opt = true;

            list.Add(new { k = kind, n = display, v = def, opt });
        }
        return list;
    }

    private static string KindFor(Color c, string socketName)
    {
        SocketDataType dt = NodeRegistry.DataTypeFromColorPublic(c);
        string kind = dt switch
        {
            SocketDataType.Flow => "flow",
            SocketDataType.String => "string",
            SocketDataType.Int => "number",
            SocketDataType.Float => "number",
            SocketDataType.Bool => "bool",
            SocketDataType.Collection => "object",
            _ => "object",
        };
        if ((kind is "object" or "string") && NodeProse.UserSocketNames.Contains(socketName))
            kind = "user";
        return kind;
    }

    private static bool IsBareExecName(string name) =>
        string.IsNullOrEmpty(name)
        || name.Equals("Flow", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Exec", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetDefault(IReadOnlyDictionary<string, string> props, string key, out string value)
    {
        foreach (var kv in props)
        {
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) { value = kv.Value; return true; }
        }
        value = string.Empty;
        return false;
    }

    private static string? NamespaceLabel(string title)
    {
        int dot = title.IndexOf('.');
        string ns = dot > 0 ? title[..dot] : title;
        return ns switch
        {
            "Twitch" => "Twitch actions",
            "OBS" => "OBS",
            "Discord" => "Discord",
            "HTTP" or "API" => "HTTP & Web",
            "File" => "File",
            "Audio" => "Audio",
            "StreamerBot" => "Streamer.bot",
            _ => ns,
        };
    }

    // ── primer ────────────────────────────────────────────────────────────
    private static object BuildPrimer() => new
    {
        tokenFamilies = NodeProse.TokenFamilies.Select(f => new
        {
            root = f.Root,
            dot = f.Dot,
            label = f.Label,
            note = f.Note,
            toks = f.Tokens,
        }).ToList(),
        storageRoots = NodeProse.StorageRoots.Select(s => new { root = s.Root, d = s.Desc, ex = s.Example }).ToList(),
        systemToks = NodeProse.SystemTokens,
        streamToks = NodeProse.StreamTokens,
        hotkeyGroups = BuildHotkeyGroups(),
    };

    private static List<object> BuildHotkeyGroups()
    {
        var groups = new List<object>();
        foreach (var (section, entries) in ArchitectHotkeyCatalog.GroupedBySection())
        {
            if (section.StartsWith("Retired", StringComparison.OrdinalIgnoreCase)) continue;

            // Split a "(hint)" parenthetical out of the section name into a note,
            // matching the design's compact section title + dim hint layout.
            string sec = section;
            string? note = null;
            int paren = section.IndexOf('(');
            if (paren > 0)
            {
                sec = section[..paren].Trim();
                note = section[paren..].Trim('(', ')', ' ');
            }

            var rows = entries.Select(e => new[] { e.Combo, e.Description }).ToList();
            groups.Add(new { sec, note, rows });
        }
        return groups;
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

    private static string? FirstSentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        int dot = text.IndexOfAny(new[] { '.', '!', '?' });
        if (dot > 0 && dot < text.Length - 1) return text[..(dot + 1)];
        return text;
    }

    /// <summary>HTML-escapes a plain-text fragment for safe innerHTML use.</summary>
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

    /// <summary>
    /// Anchor slug — MUST match the JS <c>slug()</c> in node-reference.js
    /// (<c>[^a-z0-9]+ → '-'</c>, lowercased, trimmed) so F1 deep-links resolve.
    /// </summary>
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
            var asm = Assembly.GetEntryAssembly() ?? typeof(NodeReferenceData).Assembly;
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
