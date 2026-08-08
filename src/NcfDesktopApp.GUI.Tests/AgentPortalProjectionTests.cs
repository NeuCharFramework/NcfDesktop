using Microsoft.VisualStudio.TestTools.UnitTesting;
using NcfDesktopApp.GUI.Models;

namespace NcfDesktopApp.GUI.Tests;

[TestClass]
public sealed class AgentPortalProjectionTests
{
    [TestMethod]
    public void IsAgentsManagerActivity_RequiresExactModuleSource()
    {
        Assert.IsTrue(AgentPortalProjection.IsAgentsManagerActivity(CreateActivity("AgentsManager")));
        Assert.IsTrue(AgentPortalProjection.IsAgentsManagerActivity(CreateActivity("agentsmanager")));
        Assert.IsFalse(AgentPortalProjection.IsAgentsManagerActivity(CreateActivity("PromptRange")));
        Assert.IsFalse(AgentPortalProjection.IsAgentsManagerActivity(CreateActivity("FakeAgentsManagerProxy")));
    }

    [TestMethod]
    public void Project_MapsWorkingStateAndBoundsDisplayData()
    {
        var activity = CreateActivity(
            "AgentsManager",
            state: "Working",
            title: new string('A', 40),
            progress: 140);

        var node = AgentPortalProjection.Project(activity);

        Assert.AreEqual(AgentPortalNodeState.Working, node.State);
        Assert.AreEqual(100, node.Progress);
        Assert.AreEqual(24, node.Label.Length);
        Assert.IsTrue(node.Label.EndsWith('…'));
    }

    [TestMethod]
    public void Project_UsesSequenceWhenActivityIdIsMissing()
    {
        var node = AgentPortalProjection.Project(CreateActivity("AgentsManager", activityId: string.Empty));

        Assert.AreEqual("7", node.Id);
        Assert.AreEqual(0, node.Progress);
    }

    private static DesktopActivityMessage CreateActivity(
        string source,
        string activityId = "agent-1",
        string state = "Working",
        string title = "Agent 工作",
        double? progress = null) =>
        new(
            Sequence: 7,
            ActivityId: activityId,
            Source: source,
            State: state,
            Title: title,
            Detail: null,
            Progress: progress,
            Time: DateTimeOffset.Parse("2026-08-07T00:00:00Z"),
            IsTerminal: false,
            ActionUrl: null);
}
