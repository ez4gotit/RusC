using System.Reflection.PortableExecutable;
using RusLang.Compiler;
using Xunit;

namespace RusLang.Compiler.Tests;

public sealed class CompilerTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "RusLangCompilerTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CyrillicProgramCompilesToManagedExecutable()
    {
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "Пример.rus");
        File.WriteAllText(
            source,
            "Князь\nрцы \"Привет\"\nаминь\n");

        var artifact = new RusCompiler().Compile(
            new CompilationRequest(source, "Пример", Debug: false, References: []),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            artifact.Diagnostics,
            static value => value.Severity == RusDiagnosticSeverity.Error);
        using var stream = new MemoryStream(artifact.MainAssemblyBytes);
        using var reader = new PEReader(stream);
        Assert.True(reader.HasMetadata);
        Assert.StartsWith("Пример, Version=", artifact.AssemblyIdentity, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownConstructReportsRusSourceLocation()
    {
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "bad.rus");
        File.WriteAllText(source, "Царь\nнеизвестно\nконец\n");

        var artifact = new RusCompiler().Compile(
            new CompilationRequest(source, "bad", Debug: false, References: []),
            TestContext.Current.CancellationToken);

        var diagnostic = Assert.Single(artifact.Diagnostics);
        Assert.Equal("RUS1001", diagnostic.Code);
        Assert.Equal(2, diagnostic.Line);
    }

    [Fact]
    public void SortingProgramCompiles()
    {
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "sort.rus");
        File.WriteAllText(
            source,
            """
            Царь
                пусть числа есть ряд 3 и 1 и 2
                для проход от 0 до длина числа минус 1
                    для индекс от 0 до длина числа минус проход минус 1
                        пусть следующий есть индекс плюс 1
                        если числа по индекс больше числа по следующий
                            пусть временное это числа по индекс
                            числа по индекс есть числа по следующий
                            числа по следующий это временное
                        конец
                    конец
                конец
                печать соединить " " и числа
            конец
            """);

        var artifact = new RusCompiler().Compile(
            new CompilationRequest(source, "sort", Debug: false, References: []),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            artifact.Diagnostics,
            static value => value.Severity == RusDiagnosticSeverity.Error);
        Assert.NotEmpty(artifact.MainAssemblyBytes);
    }

    [Fact]
    public void UnclosedEntryPointReportsOpeningLine()
    {
        var (source, diagnostics) = RusSourceEmitter.Emit(
            "Царь\n    печать \"да\"\n",
            "test.rus");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("RUS1006", diagnostic.Code);
        Assert.Equal(1, diagnostic.Line);
        Assert.Contains("Console.WriteLine(\"да\")", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("пусть числа есть [1 и 2]")]
    [InlineData("печать длина(числа)")]
    [InlineData("пусть числа есть ряд 1, 2")]
    [InlineData("пусть ответ = 42")]
    [InlineData("если a == b")]
    public void ClassicPunctuationIsRejected(string command)
    {
        var (_, diagnostics) = RusSourceEmitter.Emit(
            $"Царь\n{command}\nконец\n",
            "test.rus");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("RUS1010", diagnostic.Code);
    }

    [Fact]
    public void StandardConstructsDoNotRequireInvocations()
    {
        var (source, diagnostics) = RusSourceEmitter.Emit(
            "Царь\nпусть числа есть ряд 1 и 2\nпечать соединить \" \" и числа\nконец\n",
            "test.rus");

        Assert.Empty(diagnostics);
        Assert.Contains("Console.WriteLine", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OldRussianAliasesCompile()
    {
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "дружина.rus");
        File.WriteAllText(
            source,
            """
            Князь
                наречь числа суть строй 3 да 1 да 2
                наречь мера_строя бысть мера числа
                ступай место от 0 до мера_строя
                    аще числа на месте место не паче 3
                        рцы числа на месте место
                    инако
                        возопи "нестроение"
                    аминь
                аминь
            аминь
            """);

        var artifact = new RusCompiler().Compile(
            new CompilationRequest(source, "дружина", Debug: false, References: []),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            artifact.Diagnostics,
            static value => value.Severity == RusDiagnosticSeverity.Error);
    }

    [Fact]
    public void AdditionalAliasesCompile()
    {
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "государь.rus");
        File.WriteAllText(
            source,
            """
            Государь
                да будет место бысть 0
                да будет числа есть полк 4 и 5
                доколе место менее мера числа
                    вещай числа по место
                    место возрасти
                совершено
                аще правда али кривда
                    молви совокупить " " и числа
                совершено
            совершено
            """);

        var artifact = new RusCompiler().Compile(
            new CompilationRequest(source, "государь", Debug: false, References: []),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            artifact.Diagnostics,
            static value => value.Severity == RusDiagnosticSeverity.Error);
    }

    [Fact]
    public void ObjectOrientedProgramCompiles()
    {
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "род.rus");
        File.WriteAllText(
            source,
            """
            отвлечённый род Живое
                родовая черта строка имя суть ""
                всенародный зачин при строка новое_имя
                    сей у имя суть новое_имя
                аминь
                всенародное отвлечённое умение пусто представиться без деяния
            аминь

            род Воин наследует Живое
                сокровенная черта целое сила суть 100
                всенародный зачин при строка имя да целое мощь от предка имя
                    сей у сила суть мощь
                аминь
                всенародное переиначенное умение пусто представиться
                    молви сей у имя
                аминь
                всенародное умение целое узнать_силу
                    воздать сей у сила
                аминь
            аминь

            Князь
                наречь богатырь суть породить Воин "Илья" да 100
                богатырь воззови представиться
                наречь мощь суть воззвать богатырь узнать_силу
                молви мощь
            аминь
            """);

        var artifact = new RusCompiler().Compile(
            new CompilationRequest(source, "род", Debug: false, References: []),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            artifact.Diagnostics,
            static value => value.Severity == RusDiagnosticSeverity.Error);
        Assert.Contains("abstract class Живое", artifact.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("class Воин : Живое", artifact.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("private int сила", artifact.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("public override void представиться", artifact.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretMemberCannotBeReadOutsideItsClan()
    {
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "сокрытие.rus");
        File.WriteAllText(
            source,
            """
            род Сокровищница
                сокровенная черта целое злато суть 100
            аминь

            Князь
                наречь клад суть породить Сокровищница
                молви клад у злато
            аминь
            """);

        var artifact = new RusCompiler().Compile(
            new CompilationRequest(source, "сокрытие", Debug: false, References: []),
            TestContext.Current.CancellationToken);

        Assert.Contains(
            artifact.Diagnostics,
            static diagnostic => diagnostic.Code == "CS0122"
                && diagnostic.Severity == RusDiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("a точно b", "if (a == b)")]
    [InlineData("a бля буду b", "if (a == b)")]
    [InlineData("a не есть b", "if (a != b)")]
    [InlineData("a это не b", "if (a != b)")]
    [InlineData("a не меньше b", "if (a >= b)")]
    [InlineData("a не больше b", "if (a <= b)")]
    public void WordComparisonIsTranslated(string condition, string expected)
    {
        var (source, diagnostics) = RusSourceEmitter.Emit(
            $"Царь\nпусть a есть 2\nпусть b это 1\nесли {condition}\nконец\nконец\n",
            "test.rus");

        Assert.Empty(diagnostics);
        Assert.Contains(expected, source, StringComparison.Ordinal);
    }

    [Fact]
    public void WordMutationsCompile()
    {
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "mutations.rus");
        File.WriteAllText(
            source,
            """
            Царь
                пусть число есть 10
                число плюс есть 5
                число минус есть 2
                число умножить на 3
                число разделить есть 2
                число остаток есть 4
                число плюс плюс
                число уменьшить
            конец
            """);

        var artifact = new RusCompiler().Compile(
            new CompilationRequest(source, "mutations", Debug: false, References: []),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            artifact.Diagnostics,
            static value => value.Severity == RusDiagnosticSeverity.Error);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
