using Xunit;

namespace McpSense.Server.Tests;

/// <summary>
/// M0 のスキャフォールドが restore / build / test まで通ることを確認するだけのテスト。
/// M2 でツール一覧生成 / dispatch の統合テストを追加したら削除してよい。
/// </summary>
public class ScaffoldSmokeTests
{
    [Fact]
    public void SolutionBuildsAndTestsRun()
    {
        Assert.True(true);
    }
}
