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

    [TestMethod]
    public void RenderGraph_UsesLiveGroupsLinksAndTaskStatesInsteadOfIllustrativeNodes()
    {
        var graph = AgentPortalRenderGraphProjection.Create(
            new AgentGraphSnapshot
            {
                Agents =
                [
                    new AgentGraphAgent { Id = 7, Name = "Planner", ChattingCount = 1, Enable = true },
                    new AgentGraphAgent { Id = 8, Name = "Reviewer", ChattingCount = 0, Enable = true },
                    new AgentGraphAgent { Id = 9, Name = "Offline", ChattingCount = 0, Enable = false }
                ],
                Groups =
                [
                    new AgentGraphGroup { Id = 3, Name = "Planning Mesh", Enable = true, RunningTaskCount = 1 }
                ],
                Links =
                [
                    new AgentGraphLink { GroupId = 3, AgentId = 7 },
                    new AgentGraphLink { GroupId = 3, AgentId = 8 }
                ],
                Collaborations =
                [
                    new AgentGraphCollaboration
                    {
                        TaskId = 12,
                        GroupId = 3,
                        TaskName = "Research brief",
                        Status = 1,
                        AgentIds = [7, 8]
                    }
                ]
            },
            fallbackNodes: null);

        Assert.IsTrue(graph.UsesLiveSnapshot);
        Assert.AreEqual(3, graph.Agents.Count);
        Assert.AreEqual(AgentPortalNodeState.Working, graph.Agents.Single(item => item.Id == 7).State);
        Assert.AreEqual(AgentPortalNodeState.Working, graph.Agents.Single(item => item.Id == 8).State);
        Assert.AreEqual(AgentPortalNodeState.Cancelled, graph.Agents.Single(item => item.Id == 9).State);
        Assert.AreEqual(AgentPortalNodeState.Working, graph.Groups.Single().State);
        Assert.IsTrue(graph.Links.All(item => item.IsActive));
        Assert.AreEqual("Research brief", graph.Collaborations.Single().Label);
        Assert.AreEqual(2, graph.Collaborations.Single().AgentIds.Count);
    }

    [TestMethod]
    public void RenderGraph_SummarizesAllTaskStatesAndAgentLoadWithoutTruncatingGroups()
    {
        var graph = AgentPortalRenderGraphProjection.Create(
            new AgentGraphSnapshot
            {
                Agents =
                [
                    new AgentGraphAgent { Id = 1, Name = "Planner", ChattingCount = 1, Enable = true, Score = 84 },
                    new AgentGraphAgent { Id = 2, Name = "Builder", ChattingCount = 1, Enable = true, Score = 96 },
                    new AgentGraphAgent { Id = 3, Name = "Auditor", ChattingCount = 1, Enable = true, Score = -1 },
                    new AgentGraphAgent { Id = 4, Name = "Disabled", ChattingCount = 0, Enable = false, Score = 73 }
                ],
                Groups =
                [
                    new AgentGraphGroup
                    {
                        Id = 11,
                        Name = "Delivery",
                        Enable = true,
                        RunningTaskCount = 2,
                        TaskStatusCounts = new Dictionary<int, int> { [1] = 2, [3] = 4 }
                    },
                    new AgentGraphGroup
                    {
                        Id = 12,
                        Name = "Quality",
                        Enable = true,
                        RunningTaskCount = 2,
                        TaskStatusCounts = new Dictionary<int, int> { [0] = 1, [2] = 1, [4] = 1, [5] = 1 }
                    }
                ],
                Collaborations =
                [
                    new AgentGraphCollaboration { TaskId = 71, GroupId = 11, TaskName = "Build", Status = 1, AgentIds = [1, 2] }
                ]
            },
            fallbackNodes: null);

        Assert.AreEqual(2, graph.Groups.Count);
        Assert.AreEqual(2, graph.TaskSummary.WorkingCount);
        Assert.AreEqual(1, graph.TaskSummary.WaitingCount);
        Assert.AreEqual(1, graph.TaskSummary.PausedCount);
        Assert.AreEqual(4, graph.TaskSummary.FinishedCount);
        Assert.AreEqual(1, graph.TaskSummary.CancelledCount);
        Assert.AreEqual(1, graph.TaskSummary.FailedCount);
        Assert.AreEqual(3, graph.AgentSummary.WorkingCount);
        Assert.AreEqual(1, graph.AgentSummary.DisabledCount);
        Assert.AreEqual(3, graph.AgentSummary.ActiveAssignments);
        Assert.AreEqual(90d, graph.AgentSummary.AveragePromptScore, .01);
    }

    [TestMethod]
    public void RenderGraph_DeduplicatesRepeatedSnapshotEntitiesBeforeRendering()
    {
        var graph = AgentPortalRenderGraphProjection.Create(
            new AgentGraphSnapshot
            {
                Agents =
                [
                    new AgentGraphAgent { Id = 2, Name = "Planner", ChattingCount = 1, Enable = true },
                    new AgentGraphAgent { Id = 2, Name = "Planner duplicate", ChattingCount = 1, Enable = true }
                ],
                Groups =
                [
                    new AgentGraphGroup { Id = 5, Name = "Delivery", Enable = true, RunningTaskCount = 1 },
                    new AgentGraphGroup { Id = 5, Name = "Delivery duplicate", Enable = true, RunningTaskCount = 1 }
                ],
                Links =
                [
                    new AgentGraphLink { GroupId = 5, AgentId = 2 },
                    new AgentGraphLink { GroupId = 5, AgentId = 2 }
                ],
                Collaborations =
                [
                    new AgentGraphCollaboration { TaskId = 8, GroupId = 5, TaskName = "Build", Status = 1, AgentIds = [2, 2] },
                    new AgentGraphCollaboration { TaskId = 8, GroupId = 5, TaskName = "Build duplicate", Status = 1, AgentIds = [2] }
                ]
            },
            fallbackNodes: null);

        Assert.AreEqual(1, graph.Agents.Count);
        Assert.AreEqual(1, graph.Groups.Count);
        Assert.AreEqual(1, graph.Links.Count);
        Assert.AreEqual(1, graph.Collaborations.Count);
        CollectionAssert.AreEqual(new[] { 2 }, graph.Collaborations.Single().AgentIds.ToArray());
    }

    [TestMethod]
    public void RecentCompletionTracker_OnlyLabelsCompletionWhenFinishedCountConfirmsIt()
    {
        var tracker = new AgentPortalRecentCompletionTracker();
        var before = new AgentGraphSnapshot
        {
            Groups =
            [
                new AgentGraphGroup
                {
                    Id = 9,
                    Name = "Release",
                    TaskStatusCounts = new Dictionary<int, int> { [3] = 5, [1] = 1 }
                }
            ],
            Collaborations =
            [
                new AgentGraphCollaboration { TaskId = 42, GroupId = 9, TaskName = "Publish package", Status = 1 }
            ]
        };
        var after = new AgentGraphSnapshot
        {
            Groups =
            [
                new AgentGraphGroup
                {
                    Id = 9,
                    Name = "Release",
                    TaskStatusCounts = new Dictionary<int, int> { [3] = 6 }
                }
            ]
        };

        tracker.Update(before, DateTimeOffset.Parse("2026-08-10T00:00:00Z"));
        var recent = tracker.Update(after, DateTimeOffset.Parse("2026-08-10T00:00:02Z"));

        Assert.AreEqual(1, recent.Count);
        Assert.AreEqual("Release", recent[0].GroupLabel);
        Assert.AreEqual("Publish package", recent[0].TaskLabel);
        Assert.AreEqual(1, recent[0].CompletedTaskCount);
    }

    [TestMethod]
    public void UsageSummary_LabelsSamplingScopeAndAggregatesOnlyQueriedTasks()
    {
        var summary = AgentPortalUsageSummary.Create(
            runningTaskCount: 7,
            analytics:
            [
                new AgentTaskUsageAnalytics
                {
                    Overview = new AgentTaskUsageOverview
                    {
                        MessageCount = 2,
                        PromptTokens = 40,
                        CompletionTokens = 10,
                        TotalTokens = 50,
                        AverageResponseMilliseconds = 100,
                        P95ResponseMilliseconds = 180
                    }
                },
                new AgentTaskUsageAnalytics
                {
                    Overview = new AgentTaskUsageOverview
                    {
                        MessageCount = 1,
                        PromptTokens = 20,
                        CompletionTokens = 30,
                        TotalTokens = 50,
                        AverageResponseMilliseconds = 250,
                        P95ResponseMilliseconds = 420
                    }
                }
            ],
            observedAt: DateTimeOffset.Parse("2026-08-10T00:00:00Z"));

        Assert.IsTrue(summary.IsAvailable);
        Assert.AreEqual(7, summary.RunningTaskCount);
        Assert.AreEqual(2, summary.SampledTaskCount);
        Assert.AreEqual(100, summary.TotalTokens);
        Assert.AreEqual(150d, summary.AverageResponseMilliseconds, .01);
        Assert.AreEqual(420, summary.P95ResponseMilliseconds);
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
