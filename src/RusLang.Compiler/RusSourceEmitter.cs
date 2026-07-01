using System.Text;
using System.Text.RegularExpressions;

namespace RusLang.Compiler;

public static partial class RusSourceEmitter
{
    private enum BlockKind
    {
        EntryPoint,
        If,
        While,
        For,
    }

    private sealed record OpenBlock(BlockKind Kind, int Line, bool HasElse = false);

    public static (string Source, IReadOnlyList<RusDiagnostic> Diagnostics) Emit(
        string source,
        string sourcePath)
    {
        if (source.StartsWith("#csharp", StringComparison.Ordinal))
        {
            var newline = source.IndexOf('\n');
            return (newline < 0 ? string.Empty : source[(newline + 1)..], []);
        }

        var diagnostics = new List<RusDiagnostic>();
        var body = new StringBuilder();
        var blocks = new Stack<OpenBlock>();
        var variables = new HashSet<string>(StringComparer.Ordinal);
        var hasEntryPoint = false;
        var insideEntryPoint = false;
        var lines = source.ReplaceLineEndings("\n").Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index];
            var line = raw.Trim();
            var lineNumber = index + 1;
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (TryReadArgument(line, out var module, "призвать"))
            {
                if (hasEntryPoint || !IdentifierPattern().IsMatch(module))
                {
                    AddDiagnostic(diagnostics, sourcePath, lineNumber, raw, "RUS1007",
                        "Внешняя дружина призывается до точки входа: призвать Имя");
                    continue;
                }

                continue;
            }

            if (IsKeyword(line, "Царь", "Князь", "Государь"))
            {
                if (hasEntryPoint)
                {
                    AddDiagnostic(diagnostics, sourcePath, lineNumber, raw, "RUS1008",
                        "В программе может быть только одна точка входа");
                    continue;
                }

                hasEntryPoint = true;
                insideEntryPoint = true;
                blocks.Push(new OpenBlock(BlockKind.EntryPoint, lineNumber));
                continue;
            }

            if (!insideEntryPoint)
            {
                AddDiagnostic(diagnostics, sourcePath, lineNumber, raw, "RUS1009",
                    "Исполняемая команда должна находиться внутри точки входа");
                continue;
            }

            if (ContainsForbiddenSyntax(line, out var forbidden))
            {
                AddDiagnostic(diagnostics, sourcePath, lineNumber, raw, "RUS1010",
                    $"Символ «{forbidden}» запрещён: используйте словесные операторы RusLang");
                continue;
            }

            AppendLineDirective(body, sourcePath, lineNumber);

            if (TryReadArgument(
                    line, out var printValue, "печать", "молви", "рцы", "вещай"))
            {
                body.Append("Console.WriteLine(")
                    .Append(ToOutputExpression(printValue, variables))
                    .AppendLine(");");
            }
            else if (TryReadArgument(line, out var errorValue, "ошибка", "возопи"))
            {
                body.Append("Console.Error.WriteLine(")
                    .Append(ToOutputExpression(errorValue, variables))
                    .AppendLine(");");
            }
            else if (TryReadArgument(line, out var exitCode, "выход", "изыди"))
            {
                body.Append("return ").Append(TranslateExpression(exitCode)).AppendLine(";");
            }
            else if (TryReadArgument(line, out var declaration, "пусть", "наречь", "да будет"))
            {
                var match = DeclarationPattern().Match(declaration);
                if (!match.Success)
                {
                    AddDiagnostic(diagnostics, sourcePath, lineNumber, raw, "RUS1002",
                        "Ожидалось объявление вида: наречь имя суть выражение");
                    continue;
                }

                var name = match.Groups["name"].Value;
                variables.Add(name);
                body.Append("var ").Append(name).Append(" = ")
                    .Append(TranslateExpression(match.Groups["expression"].Value))
                    .AppendLine(";");
            }
            else if (TryReadArgument(line, out var condition, "если", "аще"))
            {
                blocks.Push(new OpenBlock(BlockKind.If, lineNumber));
                body.Append("if (").Append(TranslateExpression(condition)).AppendLine(")");
                body.AppendLine("{");
            }
            else if (IsKeyword(line, "иначе", "инако"))
            {
                if (blocks.Count == 0 || blocks.Peek().Kind != BlockKind.If || blocks.Peek().HasElse)
                {
                    AddDiagnostic(diagnostics, sourcePath, lineNumber, raw, "RUS1003",
                        "«иначе» или «инако» должно находиться внутри условия");
                    continue;
                }

                var openIf = blocks.Pop();
                blocks.Push(openIf with { HasElse = true });
                body.AppendLine("}");
                body.AppendLine("else");
                body.AppendLine("{");
            }
            else if (TryReadArgument(line, out condition, "пока", "доколе"))
            {
                blocks.Push(new OpenBlock(BlockKind.While, lineNumber));
                body.Append("while (").Append(TranslateExpression(condition)).AppendLine(")");
                body.AppendLine("{");
            }
            else if (TryReadArgument(line, out var forClause, "для", "ступай"))
            {
                var match = ForPattern().Match(forClause);
                if (!match.Success)
                {
                    AddDiagnostic(diagnostics, sourcePath, lineNumber, raw, "RUS1004",
                        "Ожидался цикл вида: ступай имя от начало до граница");
                    continue;
                }

                var name = match.Groups["name"].Value;
                var start = TranslateExpression(match.Groups["start"].Value);
                var end = TranslateExpression(match.Groups["end"].Value);
                blocks.Push(new OpenBlock(BlockKind.For, lineNumber));
                variables.Add(name);
                body.Append("for (var ").Append(name).Append(" = ").Append(start)
                    .Append("; ").Append(name).Append(" < ").Append(end)
                    .Append("; ").Append(name).AppendLine("++)");
                body.AppendLine("{");
            }
            else if (IsKeyword(line, "конец", "аминь", "совершено"))
            {
                if (blocks.Count == 0)
                {
                    AddDiagnostic(diagnostics, sourcePath, lineNumber, raw, "RUS1005",
                        "Лишний завершитель: открытого блока нет");
                    continue;
                }

                var closedBlock = blocks.Pop();
                if (closedBlock.Kind == BlockKind.EntryPoint)
                {
                    insideEntryPoint = false;
                }
                else
                {
                    body.AppendLine("}");
                }
            }
            else if (IsKeyword(line, "прервать", "пресеки"))
            {
                body.AppendLine("break;");
            }
            else if (IsKeyword(line, "продолжить", "далее"))
            {
                body.AppendLine("continue;");
            }
            else
            {
                if (TryTranslateMutation(line, out var mutation))
                {
                    body.AppendLine(mutation);
                    continue;
                }

                var assignment = AssignmentPattern().Match(line);
                if (assignment.Success)
                {
                    body.Append(TranslateTarget(assignment.Groups["target"].Value)).Append(" = ")
                        .Append(TranslateExpression(assignment.Groups["expression"].Value))
                        .AppendLine(";");
                    continue;
                }

                AddDiagnostic(diagnostics, sourcePath, lineNumber, raw, "RUS1001",
                    $"Неизвестная конструкция: {line}");
            }
        }

        if (!hasEntryPoint)
        {
            diagnostics.Add(new RusDiagnostic(
                "RUS1011",
                RusDiagnosticSeverity.Error,
                sourcePath,
                1,
                1,
                "Точка входа «Князь», «Царь» или «Государь» не найдена"));
        }

        foreach (var block in blocks)
        {
            diagnostics.Add(new RusDiagnostic(
                "RUS1006",
                RusDiagnosticSeverity.Error,
                sourcePath,
                block.Line,
                1,
                "Блок не закрыт словом «аминь», «конец» или «совершено»"));
        }

        var generated = $$"""
            using System;
            using System.Collections.Generic;

            internal static class RusLangProgram
            {
                private static int длина<T>(T[] значения) => значения.Length;

                private static string соединить<T>(string разделитель, IEnumerable<T> значения) =>
                    string.Join(разделитель, значения);

                private static int Main(string[] args)
                {
            {{Indent(body.ToString(), 8)}}
                    return 0;
                }
            }
            """;
        return (generated, diagnostics);
    }

    private static bool TryReadArgument(
        string line,
        out string argument,
        params string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (line.StartsWith(keyword + " ", StringComparison.OrdinalIgnoreCase))
            {
                argument = line[(keyword.Length + 1)..].Trim();
                return true;
            }
        }

        argument = string.Empty;
        return false;
    }

    private static bool IsKeyword(string line, params string[] keywords) =>
        keywords.Any(keyword => line.Equals(keyword, StringComparison.OrdinalIgnoreCase));

    private static string ToOutputExpression(string text, IReadOnlySet<string> variables)
    {
        var value = text.Trim();
        if (LooksLikeExpression(value, variables))
        {
            return TranslateExpression(value);
        }

        return $"\"{EscapeText(value)}\"";
    }

    private static bool LooksLikeExpression(string value, IReadOnlySet<string> variables)
    {
        if (value.Length == 0)
        {
            return false;
        }

        if (value[0] == '"' || char.IsDigit(value[0]) || value[0] == '-')
        {
            return true;
        }

        var firstWord = IdentifierAtStartPattern().Match(value).Value;
        return variables.Contains(firstWord)
            || IsKeyword(
                firstWord,
                "ряд",
                "строй",
                "полк",
                "длина",
                "мера",
                "соединить",
                "сочетать",
                "совокупить",
                "истина",
                "правда",
                "ложь",
                "кривда")
            || value.Contains(" по ", StringComparison.OrdinalIgnoreCase)
            || value.Contains(" на месте ", StringComparison.OrdinalIgnoreCase)
            || value.Contains('+')
            || value.Contains('*')
            || value.Contains('/');
    }

    private static string TranslateExpression(string expression)
    {
        var value = expression.Trim();
        var rowKeyword = new[] { "ряд", "строй", "полк" }
            .FirstOrDefault(keyword =>
                value.StartsWith(keyword + " ", StringComparison.OrdinalIgnoreCase));
        if (rowKeyword is not null)
        {
            var elements = SplitByAnd(value[(rowKeyword.Length + 1)..]);
            return $"new[] {{ {string.Join(", ", elements.Select(TranslateExpression))} }}";
        }

        var join = JoinPattern().Match(value);
        if (join.Success)
        {
            return $"соединить({TranslateExpression(join.Groups["separator"].Value)}, " +
                $"{join.Groups["values"].Value})";
        }

        value = IndexAccessPattern().Replace(
            value,
            static match => $"{match.Groups["array"].Value}[{match.Groups["index"].Value}]");
        value = LengthPattern().Replace(
            value,
            static match => $"длина({match.Groups["values"].Value})");
        value = ReplaceExpressionPhrases(value);

        var result = new StringBuilder(value.Length);
        var inString = false;
        var escaped = false;
        for (var index = 0; index < value.Length;)
        {
            var character = value[index];
            if (inString)
            {
                result.Append(character);
                index++;
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
                result.Append(character);
                index++;
                continue;
            }

            if (char.IsLetter(character) || character == '_')
            {
                var start = index++;
                while (index < value.Length
                       && (char.IsLetterOrDigit(value[index]) || value[index] == '_'))
                {
                    index++;
                }

                var word = value[start..index];
                result.Append(word.ToLowerInvariant() switch
                {
                    "истина" or "правда" or "истинно" => "true",
                    "ложь" or "кривда" or "ложно" => "false",
                    "и" or "да" => "&&",
                    "или" or "али" => "||",
                    "не" => "!",
                    "плюс" or "сложить" => "+",
                    "минус" or "отнять" or "отъять" => "-",
                    "умножить" or "множить" => "*",
                    "разделить" or "делить" => "/",
                    "остаток" => "%",
                    "больше" or "паче" => ">",
                    "меньше" or "менее" => "<",
                    "точно" or "воистину" or "суть" => "==",
                    _ => word,
                });
                continue;
            }

            result.Append(character);
            index++;
        }

        return result.ToString();
    }

    private static string TranslateTarget(string target)
    {
        var index = IndexTargetPattern().Match(target);
        return index.Success
            ? $"{index.Groups["array"].Value}[{index.Groups["index"].Value}]"
            : target;
    }

    private static bool TryTranslateMutation(string line, out string source)
    {
        var unary = UnaryMutationPattern().Match(line);
        if (unary.Success)
        {
            var target = TranslateTarget(unary.Groups["target"].Value);
            var operation = unary.Groups["operation"].Value.ToLowerInvariant() switch
            {
                "плюс плюс" or "увеличить" or "возрасти" => "++",
                _ => "--",
            };
            source = $"{target}{operation};";
            return true;
        }

        var compound = CompoundAssignmentPattern().Match(line);
        if (compound.Success)
        {
            var target = TranslateTarget(compound.Groups["target"].Value);
            var operation = Regex.Replace(
                compound.Groups["operation"].Value.ToLowerInvariant(),
                @"\s+",
                " ") switch
            {
                "плюс есть" or "прибавить" or "приобщить" => "+=",
                "минус есть" or "убавить" or "отъять" => "-=",
                "умножить есть" or "умножить на" => "*=",
                "разделить есть" or "разделить на" => "/=",
                _ => "%=",
            };
            source = $"{target} {operation} {TranslateExpression(compound.Groups["expression"].Value)};";
            return true;
        }

        source = string.Empty;
        return false;
    }

    private static string ReplaceExpressionPhrases(string value)
    {
        var result = new StringBuilder(value.Length);
        var segmentStart = 0;
        for (var index = 0; index < value.Length;)
        {
            if (value[index] != '"')
            {
                index++;
                continue;
            }

            result.Append(ReplaceExpressionPhrasesInSegment(value[segmentStart..index]));
            var stringStart = index++;
            var escaped = false;
            while (index < value.Length)
            {
                var character = value[index++];
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    break;
                }
            }

            result.Append(value[stringStart..index]);
            segmentStart = index;
        }

        result.Append(ReplaceExpressionPhrasesInSegment(value[segmentStart..]));
        return result.ToString();
    }

    private static string ReplaceExpressionPhrasesInSegment(string value) =>
        ExpressionPhrasePattern().Replace(
            value,
            static match => Regex.Replace(match.Value.ToLowerInvariant(), @"\s+", " ") switch
            {
                "не есть" or "это не" or "не суть" or "суть не" => "!=",
                "бля буду" => "==",
                "не меньше" or "не менее" => ">=",
                "не больше" or "не паче" => "<=",
                _ => match.Value,
            });

    private static IReadOnlyList<string> SplitByAnd(string value)
    {
        var parts = new List<string>();
        var start = 0;
        var inString = false;
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
                continue;
            }

            var separatorLength = character is 'и' or 'И'
                ? 1
                : index + 1 < value.Length
                  && value[index..(index + 2)].Equals("да", StringComparison.OrdinalIgnoreCase)
                    ? 2
                    : 0;
            if (separatorLength > 0
                && index > 0
                && index + separatorLength < value.Length
                && char.IsWhiteSpace(value[index - 1])
                && char.IsWhiteSpace(value[index + separatorLength]))
            {
                parts.Add(value[start..index].Trim());
                start = index + separatorLength;
                index += separatorLength - 1;
            }
        }

        parts.Add(value[start..].Trim());
        return parts;
    }

    private static bool ContainsForbiddenSyntax(string value, out char forbidden)
    {
        var inString = false;
        var escaped = false;
        foreach (var character in value)
        {
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character is
                     '(' or ')' or '[' or ']' or '{' or '}' or ',' or
                     '=' or '!' or '<' or '>' or '+' or '-' or '*' or '/' or '%')
            {
                forbidden = character;
                return true;
            }
        }

        forbidden = default;
        return false;
    }

    private static void AppendLineDirective(StringBuilder body, string sourcePath, int lineNumber) =>
        body.Append("#line ").Append(lineNumber).Append(" \"")
            .Append(EscapePath(sourcePath)).AppendLine("\"");

    private static void AddDiagnostic(
        ICollection<RusDiagnostic> diagnostics,
        string sourcePath,
        int lineNumber,
        string raw,
        string code,
        string message) =>
        diagnostics.Add(new RusDiagnostic(
            code,
            RusDiagnosticSeverity.Error,
            sourcePath,
            lineNumber,
            Math.Max(1, raw.Length - raw.TrimStart().Length + 1),
            message));

    private static string EscapePath(string path) =>
        path.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string EscapeText(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string Indent(string value, int spaces)
    {
        var prefix = new string(' ', spaces);
        return string.Join(Environment.NewLine, value.Split('\n').Select(line => prefix + line));
    }

    [GeneratedRegex(
        @"^(?<name>[\p{L}_][\p{L}\p{Nd}_]*)\s+(?:есть|это|суть|бысть)\s+" +
        @"(?<expression>.+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DeclarationPattern();

    [GeneratedRegex(
        @"^(?<target>[\p{L}_][\p{L}\p{Nd}_]*(?:(?:\s+по|\s+на\s+месте)\s+" +
        @"[\p{L}\p{Nd}_]+)?)\s+(?:есть|это|суть|бысть)\s+(?<expression>.+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AssignmentPattern();

    [GeneratedRegex(
        @"^(?<name>[\p{L}_][\p{L}\p{Nd}_]*)\s+от\s+(?<start>.+?)\s+до\s+(?<end>.+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ForPattern();

    [GeneratedRegex(@"^[\p{L}_][\p{L}\p{Nd}_]*")]
    private static partial Regex IdentifierAtStartPattern();

    [GeneratedRegex(@"^[\p{L}_][\p{L}\p{Nd}_]*$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(
        "^(?:соединить|сочетать|совокупить)\\s+" +
        "(?<separator>\"(?:\\\\.|[^\"])*\"|[\\p{L}\\p{Nd}_]+)\\s+(?:и|да)\\s+" +
        "(?<values>[\\p{L}_][\\p{L}\\p{Nd}_]*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex JoinPattern();

    [GeneratedRegex(
        @"\b(?:длина|мера)\s+(?<values>[\p{L}_][\p{L}\p{Nd}_]*)",
        RegexOptions.IgnoreCase)]
    private static partial Regex LengthPattern();

    [GeneratedRegex(
        @"(?<array>[\p{L}_][\p{L}\p{Nd}_]*)\s+(?:по|на\s+месте)\s+" +
        @"(?<index>[\p{L}\p{Nd}_]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex IndexAccessPattern();

    [GeneratedRegex(
        @"^(?<array>[\p{L}_][\p{L}\p{Nd}_]*)\s+(?:по|на\s+месте)\s+" +
        @"(?<index>[\p{L}\p{Nd}_]+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex IndexTargetPattern();

    [GeneratedRegex(
        @"^(?<target>[\p{L}_][\p{L}\p{Nd}_]*(?:(?:\s+по|\s+на\s+месте)\s+" +
        @"[\p{L}\p{Nd}_]+)?)\s+(?<operation>плюс\s+плюс|минус\s+минус|" +
        @"увеличить|уменьшить|возрасти|умалить)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex UnaryMutationPattern();

    [GeneratedRegex(
        @"^(?<target>[\p{L}_][\p{L}\p{Nd}_]*(?:(?:\s+по|\s+на\s+месте)\s+" +
        @"[\p{L}\p{Nd}_]+)?)\s+" +
        @"(?<operation>плюс\s+есть|минус\s+есть|умножить\s+есть|разделить\s+есть|" +
        @"остаток\s+есть|прибавить|убавить|умножить\s+на|разделить\s+на|" +
        @"остаток\s+от|приобщить|отъять)\s+(?<expression>.+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CompoundAssignmentPattern();

    [GeneratedRegex(
        @"\b(?:не\s+есть|это\s+не|не\s+суть|суть\s+не|бля\s+буду|" +
        @"не\s+меньше|не\s+больше|не\s+менее|не\s+паче)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExpressionPhrasePattern();
}
