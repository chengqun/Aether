using System.Linq;
using Acme.Desktop.Mvvm.Build;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary><see cref="MsBuildOutputParser"/> 纯函数解析测试。</summary>
public class MsBuildOutputParserTests
{
    [Fact]
    public void ParsesErrorWithCode()
    {
        var output = "/path/File.cs(10,5): error CS0246: The type or namespace name 'Foo' could not be found";

        var errors = MsBuildOutputParser.Parse(output);

        var e = Assert.Single(errors);
        Assert.Equal("/path/File.cs", e.File);
        Assert.Equal(10, e.Line);
        Assert.Equal(5, e.Column);
        Assert.Equal("error", e.Severity);
        Assert.Equal("CS0246", e.Code);
        Assert.Contains("'Foo'", e.Message);
    }

    [Fact]
    public void ParsesWarning()
    {
        var output = "/path/File.cs(20,3): warning CS0168: Variable 'x' is declared but never used";

        var errors = MsBuildOutputParser.Parse(output);

        var e = Assert.Single(errors);
        Assert.Equal("warning", e.Severity);
        Assert.Equal("CS0168", e.Code);
    }

    [Fact]
    public void ParsesErrorWithoutCode()
    {
        // 某些 target 错误无错误码：`error : message`
        var output = "/path/Other.cs(5,1): error : Object reference not set to an instance of an object.";

        var errors = MsBuildOutputParser.Parse(output);

        var e = Assert.Single(errors);
        Assert.Equal("error", e.Severity);
        Assert.Equal(string.Empty, e.Code);
        Assert.Contains("Object reference", e.Message);
    }

    [Fact]
    public void ParsesMultipleLinesAndSkipsNonDiagnostic()
    {
        var output = """
MSBuild version 17.14.0
  Determining projects to restore...
  Restored /path/Acme.X.csproj (in 1.2 sec).
/path/File.cs(10,5): error CS0246: The type or namespace name 'Foo' could not be found
/path/File.cs(20,3): warning CS0168: Variable 'x' is declared but never used
Build FAILED.
""";

        var errors = MsBuildOutputParser.Parse(output);

        Assert.Equal(2, errors.Count);
        Assert.Equal("error", errors[0].Severity);
        Assert.Equal("warning", errors[1].Severity);
    }

    [Fact]
    public void EmptyInputReturnsEmpty()
    {
        Assert.Empty(MsBuildOutputParser.Parse(""));
        Assert.Empty(MsBuildOutputParser.Parse(null!));
    }

    [Fact]
    public void WindowsPathParses()
    {
        var output = @"C:\repo\src\File.cs(42,7): error CS1002: ; expected";

        var errors = MsBuildOutputParser.Parse(output);

        var e = Assert.Single(errors);
        Assert.Equal(@"C:\repo\src\File.cs", e.File);
        Assert.Equal(42, e.Line);
        Assert.Equal("CS1002", e.Code);
    }
}
