using Microsoft.VisualStudio.TestTools.UnitTesting;
using NcfDesktopApp.GUI.Models;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.Tests;

[TestClass]
public sealed class NeuBellProjectionTests
{
    [TestMethod]
    public void Create_SumsAllActiveItems_ButOpensOnlySameOriginDetailPage()
    {
        var state = new NeuBellState
        {
            Providers =
            [
                new NeuBellProviderSnapshot
                {
                    ProviderId = "provider-a",
                    Items =
                    [
                        new NeuBellItemSnapshot
                        {
                            Id = "external-error",
                            Title = "不可信提醒",
                            Count = 5,
                            Severity = "error",
                            DetailUrl = "https://evil.example/steal",
                            UpdatedAt = DateTimeOffset.Parse("2026-08-07T10:05:00+08:00")
                        },
                        new NeuBellItemSnapshot
                        {
                            Id = "pending-pairings",
                            Title = "远程连接审核",
                            Summary = "有 3 个设备等待处理。",
                            Count = 3,
                            Severity = "warning",
                            DetailUrl = "/Admin/DesktopBridge/Index?uid=bridge-uid",
                            UpdatedAt = DateTimeOffset.Parse("2026-08-07T10:00:00+08:00")
                        }
                    ]
                }
            ]
        };

        var result = NeuBellProjection.Create(state, "https://ncf.example.com/base");

        Assert.AreEqual(8, result.Count);
        Assert.AreEqual("8", result.BadgeText);
        Assert.AreEqual("https://ncf.example.com/Admin/DesktopBridge/Index?uid=bridge-uid", result.TargetUri?.AbsoluteUri);
        StringAssert.Contains(result.Summary, "远程连接审核");
        Assert.IsFalse(result.Summary.Contains("不可信提醒", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Create_WhenCountExceedsBadgeCapacity_Uses99PlusAndAdminFallback()
    {
        var state = new NeuBellState
        {
            Providers =
            [
                new NeuBellProviderSnapshot
                {
                    Items =
                    [
                        new NeuBellItemSnapshot
                        {
                            Title = "批量提醒",
                            Count = 120,
                            Severity = "info",
                            DetailUrl = "javascript:alert(1)"
                        }
                    ]
                }
            ]
        };

        var result = NeuBellProjection.Create(state, "http://localhost:5123");

        Assert.AreEqual(120, result.Count);
        Assert.AreEqual("99+", result.BadgeText);
        Assert.AreEqual("http://localhost:5123/Admin/Index", result.TargetUri?.AbsoluteUri);
    }

    [TestMethod]
    public void Create_WhenNothingIsPending_HidesBadgeAndTarget()
    {
        var result = NeuBellProjection.Create(new NeuBellState(), "http://localhost:5123");

        Assert.AreEqual(0, result.Count);
        Assert.AreEqual(string.Empty, result.BadgeText);
        Assert.IsNull(result.TargetUri);
    }
}
