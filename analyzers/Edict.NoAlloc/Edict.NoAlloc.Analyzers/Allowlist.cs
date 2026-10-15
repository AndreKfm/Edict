// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Edict.NoAlloc.Analyzers;

/// <summary>
/// One <c>[[member]]</c> table of an allowlist: a canonical symbol (<c>cs:&lt;assembly or *&gt;/&lt;documentation comment id&gt;</c>),
/// the constraints it is assumed to satisfy, the runtime version it applies to and a reason. <see cref="Error"/> is
/// set for a malformed entry.
/// </summary>
internal sealed class AllowlistEntry(string symbol, Constraint constraints, string runtime, string? reason, TextSpan span, string? error)
{
    public string Symbol { get; } = symbol;
    public Constraint Constraints { get; } = constraints;
    public string Runtime { get; } = runtime;
    public string? Reason { get; } = reason;
    public TextSpan Span { get; } = span;
    public string? Error { get; } = error;

    /// <summary>The assembly part of the symbol; <c>*</c> matches any assembly.</summary>
    public string Assembly => Symbol.Substring(3, Symbol.IndexOf('/') - 3);

    /// <summary>The documentation comment id part of the symbol.</summary>
    public string DocumentationId => Symbol.Substring(Symbol.IndexOf('/') + 1);
}

/// <summary>
/// Reads <c>noalloc-allowlist.toml</c> files: the TOML subset the schema uses, one <c>[[member]]</c> table per entry
/// with string and string-array values and <c>#</c> comments.
/// </summary>
internal static class Allowlist
{
    public const string FileName = "noalloc-allowlist.toml";

    public static bool IsAllowlistPath(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        return name.Equals(FileName, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("." + FileName, StringComparison.OrdinalIgnoreCase);
    }

    public static ImmutableArray<AllowlistEntry> Parse(SourceText text)
    {
        var entries = ImmutableArray.CreateBuilder<AllowlistEntry>();
        Table? table = null;
        foreach (var line in text.Lines)
        {
            var content = StripComment(text.ToString(line.Span)).Trim();
            if (content.Length == 0)
                continue;
            if (content == "[[member]]")
            {
                if (table is not null)
                    entries.Add(table.ToEntry());
                table = new Table(line.Span);
                continue;
            }
            if (table is null)
            {
                entries.Add(Malformed(line.Span, $"'{content}' is outside a [[member]] table"));
                continue;
            }
            table.Read(content);
        }
        if (table is not null)
            entries.Add(table.ToEntry());
        return entries.ToImmutable();
    }

    /// <summary>The symbols an entry names in this compilation; empty when its runtime does not apply.</summary>
    public static ImmutableArray<ISymbol> Resolve(AllowlistEntry entry, Compilation compilation)
    {
        if (entry.Error is not null || !AppliesTo(entry, compilation))
            return [];
        var symbols = DocumentationCommentId.GetSymbolsForDeclarationId(entry.DocumentationId, compilation);
        return entry.Assembly == "*"
            ? symbols
            : symbols.Where(symbol => symbol.ContainingAssembly?.Name == entry.Assembly).ToImmutableArray();
    }

    /// <summary>Whether the entry's runtime version is <c>*</c> or the version of the compilation's runtime library.</summary>
    public static bool AppliesTo(AllowlistEntry entry, Compilation compilation) =>
        entry.Runtime == "*" || entry.Runtime == RuntimeVersion(compilation);

    /// <summary>Major and minor version of the assembly that defines <see cref="object"/>: <c>10.0</c>, <c>2.0</c>, ….</summary>
    public static string RuntimeVersion(Compilation compilation) =>
        compilation.GetSpecialType(SpecialType.System_Object).ContainingAssembly?.Identity.Version is { } version
            ? $"{version.Major}.{version.Minor}"
            : "";

    private static AllowlistEntry Malformed(TextSpan span, string error) =>
        new("", Constraint.None, "*", null, span, error);

    /// <summary>A <c>#</c> outside a string starts a comment.</summary>
    private static string StripComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\'))
                inString = !inString;
            else if (line[i] == '#' && !inString)
                return line.Substring(0, i);
        }
        return line;
    }

    private sealed class Table(TextSpan span)
    {
        private string? _symbol;
        private Constraint _constraints;
        private bool _hasConstraints;
        private string _runtime = "*";
        private string? _reason;
        private string? _error;

        public void Read(string content)
        {
            var equals = content.IndexOf('=');
            if (equals <= 0)
            {
                Fail($"'{content}' is not a key = value pair");
                return;
            }
            var key = content.Substring(0, equals).Trim();
            var value = content.Substring(equals + 1).Trim();
            switch (key)
            {
                case "symbol" when TryString(value, out var symbol):
                    _symbol = symbol;
                    break;
                case "runtime" when TryString(value, out var runtime):
                    _runtime = runtime;
                    break;
                case "reason" when TryString(value, out var reason):
                    _reason = reason;
                    break;
                case "constraints" when TryStrings(value, out var names):
                    _hasConstraints = true;
                    foreach (var name in names)
                    {
                        if (Constraints.TryParse(name, out var constraint))
                            _constraints |= constraint;
                        else
                            Fail($"unknown constraint '{name}'");
                    }
                    break;
                case "symbol" or "runtime" or "reason" or "constraints":
                    Fail($"'{key}' has an invalid value: {value}");
                    break;
                default:
                    Fail($"unknown key '{key}'");
                    break;
            }
        }

        public AllowlistEntry ToEntry()
        {
            if (_error is null && _symbol is null)
                Fail("'symbol' is missing");
            else if (_error is null && !IsCanonical(_symbol!))
                Fail($"'{_symbol}' is not a canonical symbol cs:<assembly or *>/<M:, P:, T:, F: or E: id>");
            if (_error is null && !_hasConstraints)
                Fail("'constraints' is missing");
            return new AllowlistEntry(_symbol ?? "", _constraints, _runtime, _reason, span, _error);
        }

        private void Fail(string error) => _error ??= error;

        private static bool IsCanonical(string symbol)
        {
            var slash = symbol.IndexOf('/');
            return symbol.StartsWith("cs:", StringComparison.Ordinal) && slash > 3 && symbol.Length > slash + 3
                && symbol[slash + 2] == ':' && symbol[slash + 1] is 'M' or 'P' or 'T' or 'F' or 'E';
        }

        private static bool TryString(string value, out string result)
        {
            result = "";
            if (value.Length < 2 || value[0] != '"' || value[value.Length - 1] != '"')
                return false;
            var builder = new StringBuilder();
            for (var i = 1; i < value.Length - 1; i++)
            {
                if (value[i] == '\\' && i + 1 < value.Length - 1)
                    i++;
                else if (value[i] == '"')
                    return false;
                builder.Append(value[i]);
            }
            result = builder.ToString();
            return true;
        }

        private static bool TryStrings(string value, out List<string> result)
        {
            result = [];
            if (TryString(value, out var single))
            {
                result.Add(single);
                return true;
            }
            if (value.Length < 2 || value[0] != '[' || value[value.Length - 1] != ']')
                return false;
            foreach (var item in value.Substring(1, value.Length - 2).Split(','))
            {
                var trimmed = item.Trim();
                if (trimmed.Length == 0)
                    continue;
                if (!TryString(trimmed, out var name))
                    return false;
                result.Add(name);
            }
            return true;
        }
    }
}
