using System.Text.Json;
using LanBridge.Core;
using Xunit;

namespace LanBridge.Core.Tests;

public sealed class FirewallPreferenceTests
{
    [Fact]
    public void Firewall_management_is_opt_in_and_persisted()
    {
        var property = typeof(ForwardRule).GetProperty("AutoFirewall");
        Assert.NotNull(property);
        Assert.False((bool)property.GetValue(new ForwardRule())!);
        var rule = new ForwardRule { Name = "firewall preference" };
        property.SetValue(rule, true);
        var restored = JsonSerializer.Deserialize<ForwardRule>(JsonSerializer.Serialize(rule));
        Assert.True((bool)property.GetValue(restored)!);
    }
}
