using System.Text.RegularExpressions;

namespace PoeMtxChanger.Core.Formats.Shaders;

public enum FfxPortKind { In, Out, InOut, Uniform }

/// <summary>
/// One port of a fragment or extension point, e.g. <c>uniform float2 constant2 "x y" "-1 -1" "1 1" "0 0" : custom_range</c>.
/// </summary>
/// <param name="Scope">pass / object / pipeline for engine-provided uniforms, null for per-node parameters.</param>
/// <param name="Meta">Quoted UI metadata: label, min, max, default.</param>
/// <param name="Semantic">Binding to engine state (surface_data, TEXCOORD0 …) instead of a graph link.</param>
/// <param name="Condition">Preprocessor symbol the port is declared under (#ifdef X), if any.</param>
public sealed record FfxPort(FfxPortKind Kind, string Type, string Name, string? Scope, IReadOnlyList<string> Meta, string? Semantic, string? Condition)
{
    public string? DefaultValue => Meta.Count >= 4 ? Meta[3] : null;
    public bool IsEngineUniform => Kind == FfxPortKind.Uniform && Scope != null;
}

public enum FfxBlockKind { Fragment, ExtensionPoint, Declarations, Group }

/// <summary>A FRAGMENT / EXTENSIONPOINT / DECLARATIONS / GROUP block from the game's Shaders/Renderer/*.ffx files.</summary>
public sealed class FfxBlock
{
    public required FfxBlockKind Kind { get; init; }
    public required string Name { get; init; }
    public required string File { get; init; }
    public List<FfxPort> Ports { get; } = [];
    public List<string> Includes { get; } = [];
    public HashSet<string> Flags { get; } = new(StringComparer.Ordinal);
    /// <summary>Key/value attributes: cost, group, defaultstage, type, lightingmodel, unroll …</summary>
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);
    /// <summary>Labelled code of extension points: write, read, generate.</summary>
    public Dictionary<string, string> Code { get; } = new(StringComparer.Ordinal);
    /// <summary>HLSL body of fragments, groups and declarations.</summary>
    public string Body { get; set; } = "";

    public string? Attr(string key) => Attributes.TryGetValue(key, out var v) ? v : null;
    public IEnumerable<FfxPort> Inputs => Ports.Where(p => p.Kind is FfxPortKind.In or FfxPortKind.InOut);
    public IEnumerable<FfxPort> Outputs => Ports.Where(p => p.Kind is FfxPortKind.Out or FfxPortKind.InOut);
    public IEnumerable<FfxPort> Uniforms => Ports.Where(p => p.Kind == FfxPortKind.Uniform);
}

/// <summary>
/// Parser for the game's shader fragment library (Shaders/Renderer/**/*.ffx). The files are read from the
/// user's installation at run time; nothing from them is shipped with the app.
/// </summary>
public sealed partial class FfxLibrary
{
    public Dictionary<string, FfxBlock> Fragments { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, FfxBlock> ExtensionPoints { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, FfxBlock> Declarations { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, FfxBlock> Groups { get; } = new(StringComparer.Ordinal);
    public List<string> Warnings { get; } = [];

    // Names may be namespaced: "CpuTrails::EmitColor", "ParticlesUpdate::Velocity".
    [GeneratedRegex(@"^(FRAGMENT|EXTENSIONPOINT|DECLARATIONS|GROUP)\s+(\w+(?:::\w+)*)\s*$")]
    private static partial Regex BlockStart();

    [GeneratedRegex("\"([^\"]*)\"")]
    private static partial Regex Quoted();

    private static readonly HashSet<string> Scopes = ["pass", "object", "pipeline"];
    private static readonly HashSet<string> ShaderStages = ["vs", "ps", "cs", "gs", "hs", "ds"];

    public static FfxLibrary Parse(IEnumerable<(string File, string Text)> files)
    {
        var lib = new FfxLibrary();
        foreach (var (file, text) in files) lib.ParseFile(file, text);
        return lib;
    }

    private void ParseFile(string file, string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var m = BlockStart().Match(lines[i]);
            if (!m.Success) { i++; continue; }
            var block = new FfxBlock
            {
                Kind = m.Groups[1].Value switch
                {
                    "FRAGMENT" => FfxBlockKind.Fragment,
                    "EXTENSIONPOINT" => FfxBlockKind.ExtensionPoint,
                    "DECLARATIONS" => FfxBlockKind.Declarations,
                    _ => FfxBlockKind.Group,
                },
                Name = m.Groups[2].Value,
                File = file,
            };
            i = ParseBlock(block, lines, i + 1);
            var target = block.Kind switch
            {
                FfxBlockKind.Fragment => Fragments,
                FfxBlockKind.ExtensionPoint => ExtensionPoints,
                FfxBlockKind.Declarations => Declarations,
                _ => Groups,
            };
            if (!target.TryAdd(block.Name, block)) Warnings.Add($"{file}: duplicate {block.Kind} {block.Name} (first definition kept)");
        }
    }

    /// <summary>Reads header lines and the body/code blocks; returns the index of the first line after the block.</summary>
    private int ParseBlock(FfxBlock block, string[] lines, int i)
    {
        var conditions = new Stack<string>();
        while (i < lines.Length)
        {
            var raw = lines[i];
            if (BlockStart().IsMatch(raw)) return i;
            var line = StripComment(raw).Trim();
            if (line.Length == 0) { i++; continue; }

            // Body of a fragment / group / declarations block
            if (line.StartsWith("{{", StringComparison.Ordinal) && block.Kind != FfxBlockKind.ExtensionPoint)
            {
                (block.Body, i) = ReadCode(lines, i);
                // Fragments end at their body; a declarations block may be followed directly by the next block.
                return i;
            }

            // Labelled code block: "write: {{ … }}", "generate: {{}}" (possibly multi-line)
            int colon = line.IndexOf(':');
            if (colon > 0 && line.IndexOf("{{", StringComparison.Ordinal) > colon)
            {
                var label = line[..colon].Trim();
                (var code, i) = ReadCode(lines, i);
                block.Code[label] = code;
                continue;
            }

            if (line.StartsWith('#'))
            {
                var parts = line.Split((char[])[' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
                switch (parts[0])
                {
                    case "#ifdef" or "#if" or "#ifndef": conditions.Push(parts.Length > 1 ? (parts[0] == "#ifndef" ? "!" : "") + parts[1].Trim() : "?"); break;
                    case "#else" when conditions.Count > 0: var c = conditions.Pop(); conditions.Push(c.StartsWith('!') ? c[1..] : "!" + c); break;
                    case "#endif" when conditions.Count > 0: conditions.Pop(); break;
                }
                i++;
                continue;
            }

            var first = line.Split((char[])[' ', '\t'], 2)[0];
            // "pixel in float x": a port pinned to one stage; the stage is implied by where the node runs.
            if (first is "pixel" or "vertex" && line.Split((char[])[' ', '\t'], 3, StringSplitOptions.RemoveEmptyEntries) is [_, "in" or "out" or "inout", _])
            {
                line = line[first.Length..].Trim();
                first = line.Split((char[])[' ', '\t'], 2)[0];
            }
            if (first is "in" or "out" or "inout" or "uniform" or "dynamic" or "custom")
            {
                var port = ParsePort(line, conditions.Count > 0 ? string.Join("&&", conditions.Reverse()) : null);
                if (port != null) block.Ports.Add(port);
                else Warnings.Add($"{block.File}: {block.Name}: cannot parse port '{line}'");
            }
            else if (first == "include")
                block.Includes.AddRange(line[7..].Split((char[])[' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries));
            else if (first.EndsWith(':'))
                block.Attributes[first.TrimEnd(':')] = line[first.Length..].Trim();
            else if (line.Contains(' ') || line.Contains('\t'))
            {
                var kv = line.Split((char[])[' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
                block.Attributes[kv[0]] = kv.Length > 1 ? kv[1].Trim() : "";
            }
            else block.Flags.Add(line);
            i++;
        }
        return i;
    }

    /// <summary>Reads "{{ … }}" starting on line i (same-line or multi-line). Returns the code and the next line index.</summary>
    private static (string Code, int Next) ReadCode(string[] lines, int i)
    {
        var start = lines[i];
        int open = start.IndexOf("{{", StringComparison.Ordinal);
        var rest = start[(open + 2)..];
        int close = rest.IndexOf("}}", StringComparison.Ordinal);
        if (close >= 0) return (rest[..close].Trim(), i + 1);
        var sb = new System.Text.StringBuilder();
        if (rest.Trim().Length > 0) sb.AppendLine(rest);
        for (int j = i + 1; j < lines.Length; j++)
        {
            int end = lines[j].IndexOf("}}", StringComparison.Ordinal);
            if (end >= 0)
            {
                if (end > 0 && lines[j][..end].Trim().Length > 0) sb.AppendLine(lines[j][..end]);
                return (sb.ToString(), j + 1);
            }
            sb.AppendLine(lines[j]);
        }
        return (sb.ToString(), lines.Length);
    }

    private static string StripComment(string line)
    {
        int c = line.IndexOf("//", StringComparison.Ordinal);
        return c >= 0 ? line[..c] : line;
    }

    internal static FfxPort? ParsePort(string line, string? condition)
    {
        string? semantic = null;
        // Semantic follows the last ':' outside quotes.
        var noQuotes = Quoted().Replace(line, m => new string('_', m.Length));
        int colon = noQuotes.LastIndexOf(':');
        var head = line;
        if (colon > 0)
        {
            semantic = line[(colon + 1)..].Trim();
            head = line[..colon];
        }
        var meta = Quoted().Matches(head).Select(m => m.Groups[1].Value).ToList();
        head = Quoted().Replace(head, " ");
        var tokens = head.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tokens.Count < 3) return null;
        var kind = tokens[0] switch { "in" => FfxPortKind.In, "out" => FfxPortKind.Out, "inout" => FfxPortKind.InOut, _ => FfxPortKind.Uniform };
        int k = 1;
        // "dynamic" ports are per-object values the engine feeds at draw time (e.g. character FX stat values).
        string? scope = tokens[0] == "dynamic" ? "dynamic" : null;
        // "custom dynamic|epk|timeline T name": a value set on the node in the editor (animated in game); read like
        // a per-node parameter.
        if (tokens[0] == "custom" && tokens.Count >= 4) k++;
        if (kind == FfxPortKind.Uniform && scope is null && Scopes.Contains(tokens[k])) scope = tokens[k++];
        if (k < tokens.Count && (tokens[k].Contains('|') || ShaderStages.Contains(tokens[k]))) k++; // shader stage(s): "vs|ps", "cs"
        if (tokens.Count - k < 2) return null;
        var type = tokens[k];
        var name = tokens[k + 1].TrimEnd(';'); // a few real declarations end with ';'
        return new FfxPort(kind, type, name, scope, meta, string.IsNullOrEmpty(semantic) ? null : semantic, condition);
    }

    /// <summary>All DECLARATIONS reachable from the given include names, dependencies first.</summary>
    public IReadOnlyList<FfxBlock> ResolveDeclarations(IEnumerable<string> includes)
    {
        var ordered = new List<FfxBlock>();
        var state = new Dictionary<string, bool>(StringComparer.Ordinal); // false = visiting, true = done
        void Visit(string name)
        {
            if (state.TryGetValue(name, out var done)) { return; }
            if (!Declarations.TryGetValue(name, out var block)) { Warnings.Add($"missing DECLARATIONS {name}"); state[name] = true; return; }
            state[name] = false;
            foreach (var inc in block.Includes) Visit(inc);
            state[name] = true;
            ordered.Add(block);
        }
        foreach (var i in includes) Visit(i);
        return ordered;
    }
}
