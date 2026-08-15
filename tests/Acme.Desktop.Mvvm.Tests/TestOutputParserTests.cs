using System.Linq;
using Acme.Desktop.Mvvm.Build;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary><see cref="TestOutputParser"/> 纯函数解析测试。</summary>
public class TestOutputParserTests
{
    [Fact]
    public void ParsesAllPassedSummary()
    {
        var output = """
  Passed Acme.X.Tests.TestA [1ms]
  Passed Acme.X.Tests.TestB [2ms]

Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3ms
""";

        var s = TestOutputParser.Parse(output);

        Assert.True(s.Success);
        Assert.Equal(2, s.Total);
        Assert.Equal(2, s.Passed);
        Assert.Equal(0, s.Failed);
        Assert.Empty(s.Failures);
    }

    [Fact]
    public void ParsesFailuresWithErrorMessageAndStackTrace()
    {
        var output = """
  Passed Acme.X.Tests.TestA [1ms]
  Failed Acme.X.Tests.TestB [1s]
  Error Message:
     Assert.Equal() Failure: Expected: 1
     Actual:   2
  Stack Trace:
     at Acme.X.Tests.TestB() in /path/T.cs:line 10
     at System.RuntimeMethodHandle.InvokeMethod()

  Failed!  - Failed:     1, Passed:     1, Skipped:     0, Total:     2, Duration: 1s
""";

        var s = TestOutputParser.Parse(output);

        Assert.False(s.Success);
        Assert.Equal(2, s.Total);
        Assert.Equal(1, s.Passed);
        Assert.Equal(1, s.Failed);

        var f = Assert.Single(s.Failures);
        Assert.Equal("Acme.X.Tests.TestB", f.Name);
        Assert.Contains("Assert.Equal() Failure", f.ErrorMessage);
        Assert.Contains("Expected: 1", f.ErrorMessage);
        Assert.NotNull(f.StackTrace);
        Assert.Contains("at Acme.X.Tests.TestB()", f.StackTrace);
    }

    [Fact]
    public void ParsesMultipleFailures()
    {
        var output = """
  Failed Acme.X.Tests.A [1s]
  Error Message:
     msg a
  Failed Acme.X.Tests.B [1s]
  Error Message:
     msg b

  Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 2s
""";

        var s = TestOutputParser.Parse(output);

        Assert.Equal(2, s.Failures.Count);
        Assert.Equal("Acme.X.Tests.A", s.Failures[0].Name);
        Assert.Contains("msg a", s.Failures[0].ErrorMessage);
        Assert.Equal("Acme.X.Tests.B", s.Failures[1].Name);
        Assert.Contains("msg b", s.Failures[1].ErrorMessage);
    }

    [Fact]
    public void EmptyInputReturnsUnsuccessfulEmpty()
    {
        var s = TestOutputParser.Parse("");
        Assert.False(s.Success);
        Assert.Equal(0, s.Total);
        Assert.Empty(s.Failures);
    }

    [Fact]
    public void FailureWithoutStackTraceHasNullStackTrace()
    {
        var output = """
  Failed Acme.X.Tests.TestB [1s]
  Error Message:
     simple failure

  Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 1s
""";

        var s = TestOutputParser.Parse(output);

        var f = Assert.Single(s.Failures);
        Assert.Contains("simple failure", f.ErrorMessage);
        Assert.Null(f.StackTrace);
    }
}
