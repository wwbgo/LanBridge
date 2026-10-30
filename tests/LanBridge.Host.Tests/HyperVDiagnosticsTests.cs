using LanBridge.Control;
using Xunit;

namespace LanBridge.Host.Tests;

public class HyperVDiagnosticsTests
{
    [Theory]
    [InlineData("LB_HYPERV_MODULE_MISSING", "Hyper-V PowerShell 模块")]
    [InlineData("LB_HYPERV_PERMISSION", "管理员")]
    [InlineData("unknown error", "查询失败")]
    public void Distinguishes_module_permission_and_other_failures(string diagnostic, string expected)
    {
        var type = typeof(BridgeClient).Assembly.GetType("LanBridge.Control.HyperVQuery");
        Assert.NotNull(type);
        var method = type.GetMethod("ExplainFailure");
        Assert.NotNull(method);
        var text = Assert.IsType<string>(method.Invoke(null, new object[] { diagnostic }));
        Assert.Contains(expected, text);
        Assert.DoesNotContain("unknown error", text);
    }
}
