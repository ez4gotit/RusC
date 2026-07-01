using System.Text;
using System.Text.RegularExpressions;

namespace RusLang.Compiler;

internal sealed record RusObjectEmission(
    string RemainingSource,
    string Source,
    IReadOnlyList<RusDiagnostic> Diagnostics);

internal static partial class RusObjectEmitter
{
    public static RusObjectEmission Emit(string source, string sourcePath)
    {
        var lines = source.ReplaceLineEndings("\n").Split('\n');
        var remaining = lines.ToArray();
        var generated = new StringBuilder();
        var diagnostics = new List<RusDiagnostic>();

        for (var index = 0; index < lines.Length; index++)
        {
            var header = TypePattern().Match(lines[index].Trim());
            if (!header.Success)
            {
                continue;
            }

            var end = FindBlockEnd(lines, index);
            if (end < 0)
            {
                diagnostics.Add(Diagnostic(
                    sourcePath,
                    index + 1,
                    "RUS1101",
                    "Род не закрыт словом «аминь», «конец» или «совершено»"));
                break;
            }

            generated.AppendLine(ParseType(
                lines[index..(end + 1)],
                sourcePath,
                index + 1,
                header,
                diagnostics));
            for (var line = index; line <= end; line++)
            {
                remaining[line] = string.Empty;
            }

            index = end;
        }

        return new(
            string.Join('\n', remaining),
            generated.ToString(),
            diagnostics);
    }

    private static string ParseType(
        string[] lines,
        string sourcePath,
        int firstLine,
        Match header,
        ICollection<RusDiagnostic> diagnostics)
    {
        var name = header.Groups["name"].Value;
        var modifier = header.Groups["modifier"].Value.ToLowerInvariant() switch
        {
            "отвлечённый" => "abstract ",
            "последний" => "sealed ",
            _ => string.Empty,
        };
        var inheritance = header.Groups["base"].Success
            ? $" : {header.Groups["base"].Value}"
            : string.Empty;
        var result = new StringBuilder();
        result.Append("internal ").Append(modifier).Append("class ").Append(name)
            .Append(inheritance).AppendLine();
        result.AppendLine("{");

        for (var index = 1; index < lines.Length - 1; index++)
        {
            var raw = lines[index];
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var field = FieldPattern().Match(line);
            if (field.Success)
            {
                result.Append("    ").Append(Access(field.Groups["access"].Value));
                if (field.Groups["static"].Success)
                {
                    result.Append("static ");
                }

                result.Append(TypeName(field.Groups["type"].Value)).Append(' ')
                    .Append(field.Groups["name"].Value).Append(" = ")
                    .Append(RusSourceEmitter.TranslateExpression(field.Groups["expression"].Value))
                    .AppendLine(";");
                continue;
            }

            var constructor = ConstructorPattern().Match(line);
            if (constructor.Success)
            {
                var end = FindBlockEnd(lines, index);
                if (end < 0 || end >= lines.Length)
                {
                    diagnostics.Add(Diagnostic(
                        sourcePath,
                        firstLine + index,
                        "RUS1102",
                        "Зачин рода не закрыт"));
                    break;
                }

                result.Append("    ").Append(Access(constructor.Groups["access"].Value))
                    .Append(name).Append('(')
                    .Append(Parameters(constructor.Groups["parameters"].Value))
                    .Append(')');
                if (constructor.Groups["baseArguments"].Success)
                {
                    result.Append(" : base(")
                        .Append(RusSourceEmitter.TranslateArguments(
                            constructor.Groups["baseArguments"].Value))
                        .Append(')');
                }

                result.AppendLine();
                result.AppendLine("    {");
                result.Append(EmitBody(
                    lines[(index + 1)..end],
                    sourcePath,
                    firstLine + index + 1,
                    diagnostics));
                result.AppendLine("    }");
                index = end;
                continue;
            }

            var method = MethodPattern().Match(line);
            if (method.Success)
            {
                var methodModifiers = MethodModifiers(method.Groups["modifiers"].Value);
                var signature = new StringBuilder()
                    .Append("    ")
                    .Append(Access(method.Groups["access"].Value))
                    .Append(methodModifiers)
                    .Append(TypeName(method.Groups["type"].Value))
                    .Append(' ')
                    .Append(method.Groups["name"].Value)
                    .Append('(')
                    .Append(Parameters(method.Groups["parameters"].Value))
                    .Append(')');

                if (method.Groups["abstract"].Success)
                {
                    result.Append(signature).AppendLine(";");
                    continue;
                }

                var end = FindBlockEnd(lines, index);
                if (end < 0 || end >= lines.Length)
                {
                    diagnostics.Add(Diagnostic(
                        sourcePath,
                        firstLine + index,
                        "RUS1103",
                        "Умение рода не закрыто"));
                    break;
                }

                result.AppendLine(signature.ToString());
                result.AppendLine("    {");
                result.Append(EmitBody(
                    lines[(index + 1)..end],
                    sourcePath,
                    firstLine + index + 1,
                    diagnostics));
                result.AppendLine("    }");
                index = end;
                continue;
            }

            diagnostics.Add(Diagnostic(
                sourcePath,
                firstLine + index,
                "RUS1104",
                $"Неизвестная часть рода: {line}"));
        }

        result.AppendLine("}");
        return result.ToString();
    }

    private static string EmitBody(
        string[] lines,
        string sourcePath,
        int firstLine,
        ICollection<RusDiagnostic> diagnostics)
    {
        var linePadding = new string('\n', Math.Max(0, firstLine - 2));
        var synthetic = linePadding + "Царь\n" + string.Join('\n', lines) + "\nконец";
        var (source, bodyDiagnostics) = RusSourceEmitter.Emit(synthetic, sourcePath);
        foreach (var diagnostic in bodyDiagnostics)
        {
            diagnostics.Add(diagnostic);
        }

        var signature = source.IndexOf(
            "private static int Main(string[] args)",
            StringComparison.Ordinal);
        var openingBrace = source.IndexOf('{', signature);
        var contentStart = source.IndexOf('\n', openingBrace) + 1;
        var finalReturn = source.LastIndexOf("        return 0;", StringComparison.Ordinal);
        if (signature < 0 || openingBrace < 0 || contentStart <= 0 || finalReturn < contentStart)
        {
            return string.Empty;
        }

        return source[contentStart..finalReturn];
    }

    private static int FindBlockEnd(string[] lines, int start)
    {
        var depth = 1;
        for (var index = start + 1; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (IsEnd(line))
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }

                continue;
            }

            if (OpensBlock(line))
            {
                depth++;
            }
        }

        return -1;
    }

    private static bool OpensBlock(string line) =>
        StartsWithKeyword(line, "если", "аще", "пока", "доколе", "для", "ступай")
        || ConstructorPattern().IsMatch(line)
        || MethodPattern().IsMatch(line) && !line.EndsWith("без деяния", StringComparison.OrdinalIgnoreCase);

    private static bool IsEnd(string line) =>
        line.Equals("конец", StringComparison.OrdinalIgnoreCase)
        || line.Equals("аминь", StringComparison.OrdinalIgnoreCase)
        || line.Equals("совершено", StringComparison.OrdinalIgnoreCase);

    private static bool StartsWithKeyword(string line, params string[] keywords) =>
        keywords.Any(keyword =>
            line.StartsWith(keyword + " ", StringComparison.OrdinalIgnoreCase));

    private static string Access(string value) =>
        value.ToLowerInvariant() switch
        {
            "всенародное" or "всенародная" or "всенародный" => "public ",
            "родовое" or "родовая" or "родовой" => "protected ",
            "земское" or "земская" or "земской" => "internal ",
            _ => "private ",
        };

    private static string TypeName(string value) =>
        value.ToLowerInvariant() switch
        {
            "целое" => "int",
            "дробное" => "double",
            "строка" => "string",
            "правдиво" => "bool",
            "пусто" => "void",
            _ => value,
        };

    private static string Parameters(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(", ", ParameterSeparatorPattern()
            .Split(value.Trim())
            .Select(parameter =>
            {
                var parts = WhitespacePattern().Split(parameter.Trim(), 2);
                return parts.Length == 2
                    ? $"{TypeName(parts[0])} {parts[1]}"
                    : parameter;
            }));
    }

    private static string MethodModifiers(string value)
    {
        var words = WhitespacePattern().Split(value.Trim())
            .Where(word => word.Length > 0)
            .Select(word => word.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var result = new StringBuilder();
        if (words.Contains("общинное"))
        {
            result.Append("static ");
        }

        if (words.Contains("наследуемое"))
        {
            result.Append("virtual ");
        }

        if (words.Contains("переиначенное"))
        {
            result.Append("override ");
        }

        if (words.Contains("отвлечённое"))
        {
            result.Append("abstract ");
        }

        if (words.Contains("последнее"))
        {
            result.Append("sealed ");
        }

        return result.ToString();
    }

    private static RusDiagnostic Diagnostic(
        string path,
        int line,
        string code,
        string message) =>
        new(code, RusDiagnosticSeverity.Error, path, line, 1, message);

    [GeneratedRegex(
        @"^(?:(?<modifier>отвлечённый|последний)\s+)?род\s+" +
        @"(?<name>[\p{L}_][\p{L}\p{Nd}_]*)(?:\s+наследует\s+" +
        @"(?<base>[\p{L}_][\p{L}\p{Nd}_]*))?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TypePattern();

    [GeneratedRegex(
        @"^(?<access>всенародная|родовая|земская|сокровенная)\s+" +
        @"(?<static>общинная\s+)?черта\s+(?<type>[\p{L}_][\p{L}\p{Nd}_]*)\s+" +
        @"(?<name>[\p{L}_][\p{L}\p{Nd}_]*)\s+(?:суть|бысть|есть|это)\s+" +
        @"(?<expression>.+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex FieldPattern();

    [GeneratedRegex(
        @"^(?<access>всенародный|родовой|земской|сокровенный)\s+зачин" +
        @"(?:\s+при\s+(?<parameters>.+?))?(?:\s+от\s+предка\s+" +
        @"(?<baseArguments>.+))?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ConstructorPattern();

    [GeneratedRegex(
        @"^(?<access>всенародное|родовое|земское|сокровенное)\s+" +
        @"(?<modifiers>(?:(?:общинное|наследуемое|переиначенное|отвлечённое|" +
        @"последнее)\s+)*)умение\s+(?<type>[\p{L}_][\p{L}\p{Nd}_]*)\s+" +
        @"(?<name>[\p{L}_][\p{L}\p{Nd}_]*)(?:\s+при\s+(?<parameters>.+?))?" +
        @"(?<abstract>\s+без\s+деяния)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex MethodPattern();

    [GeneratedRegex(@"\s+(?:и|да)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex ParameterSeparatorPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
