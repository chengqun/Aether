using System.Linq;
using Acme.Desktop.Mvvm.Build;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary><see cref="TrxParser"/> 纯函数解析测试。</summary>
public class TrxParserTests
{
    // 最小 TRX：1 passed + 1 failed（带 ErrorInfo）+ 1 skipped(NotExecuted)
    private const string SampleTrx = """
<?xml version="1.0" encoding="UTF-8"?>
<TestRun>
  <Results>
    <UnitTestResult testName="Acme.X.Tests.TestA" outcome="Passed" duration="00:00:00.001" />
    <UnitTestResult testName="Acme.X.Tests.TestB" outcome="Failed" duration="00:00:01.000">
      <Output>
        <ErrorInfo>
          <Message>Assert.Equal() Failure: Expected: 1
Actual:   2</Message>
          <StackTrace>   at Acme.X.Tests.TestB() in /path/T.cs:line 10
   at System.RuntimeMethodHandle.InvokeMethod()</StackTrace>
        </ErrorInfo>
      </Output>
    </UnitTestResult>
    <UnitTestResult testName="Acme.X.Tests.TestC" outcome="NotExecuted" duration="00:00:00" />
  </Results>
</TestRun>
""";

    [Fact]
    public void ParsesMixedResultsFromTrx()
    {
        var s = TrxParser.Parse(SampleTrx);

        Assert.NotNull(s);
        Assert.False(s!.Success); // 有失败
        Assert.Equal(3, s.Total);
        Assert.Equal(1, s.Passed);
        Assert.Equal(1, s.Failed);
        Assert.Equal(1, s.Skipped);
    }

    [Fact]
    public void ExtractsFailureMessageAndStackTrace()
    {
        var s = TrxParser.Parse(SampleTrx);

        Assert.NotNull(s);
        var failure = Assert.Single(s!.Failures);
        Assert.Equal("Acme.X.Tests.TestB", failure.Name);
        Assert.Contains("Assert.Equal() Failure", failure.ErrorMessage);
        Assert.Contains("at Acme.X.Tests.TestB()", failure.StackTrace);
    }

    [Fact]
    public void AllPassedYieldsSuccess()
    {
        var trx = """
<?xml version="1.0" encoding="UTF-8"?>
<TestRun>
  <Results>
    <UnitTestResult testName="A" outcome="Passed" />
    <UnitTestResult testName="B" outcome="Passed" />
  </Results>
</TestRun>
""";
        var s = TrxParser.Parse(trx);

        Assert.NotNull(s);
        Assert.True(s!.Success);
        Assert.Equal(2, s.Total);
        Assert.Equal(2, s.Passed);
        Assert.Equal(0, s.Failed);
        Assert.Empty(s.Failures);
    }

    [Fact]
    public void ReturnsNullForNonTrxInput()
    {
        Assert.Null(TrxParser.Parse(""));
        Assert.Null(TrxParser.Parse("not xml at all"));
        // XML 但无 UnitTestResult 元素 → 不算 TRX
        Assert.Null(TrxParser.Parse("<Foo><Bar/></Foo>"));
    }

    [Fact]
    public void ReturnsNullForMalformedXml()
    {
        Assert.Null(TrxParser.Parse("<TestRun><Results>")); // 未闭合
    }

    [Fact]
    public void FailedWithoutErrorInfoGetsPlaceholderMessage()
    {
        var trx = """
<?xml version="1.0" encoding="UTF-8"?>
<TestRun>
  <Results>
    <UnitTestResult testName="Weird" outcome="Failed" />
  </Results>
</TestRun>
""";
        var s = TrxParser.Parse(trx);

        Assert.NotNull(s);
        var failure = Assert.Single(s!.Failures);
        Assert.Contains("未提供错误消息", failure.ErrorMessage);
    }
}
