using Xunit;

namespace McpSense.Core.Tests;

/// <summary>
/// M0 のスキャフォールドが restore / build / test まで通ることを確認するだけのテスト。
/// M1 で OperationModelBuilder のテストを追加したら削除してよい。
/// </summary>
public class ScaffoldSmokeTests
{
    [Fact]
    public void SolutionBuildsAndTestsRun()
    {
        Assert.True(true);
    }
}
