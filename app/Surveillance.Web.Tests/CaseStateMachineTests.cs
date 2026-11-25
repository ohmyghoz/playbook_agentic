using Surveillance.Agents.Abstractions;
using Surveillance.Web.Data;
using Surveillance.Web.Investigations;

namespace Surveillance.Web.Tests;

public class CaseStateMachineTests
{
    private static readonly (CaseStatus From, CaseStatus To)[] Allowed =
    [
        (CaseStatus.Starting, CaseStatus.Running),
        (CaseStatus.Starting, CaseStatus.Failed),
        (CaseStatus.Running, CaseStatus.Running),
        (CaseStatus.Running, CaseStatus.AwaitingApproval),
        (CaseStatus.Running, CaseStatus.Approved),
        (CaseStatus.Running, CaseStatus.Rejected),
        (CaseStatus.Running, CaseStatus.Cancelled),
        (CaseStatus.Running, CaseStatus.Failed),
        (CaseStatus.AwaitingApproval, CaseStatus.AwaitingApproval),
        (CaseStatus.AwaitingApproval, CaseStatus.Running),
        (CaseStatus.AwaitingApproval, CaseStatus.Cancelled),
        (CaseStatus.AwaitingApproval, CaseStatus.Failed),
        (CaseStatus.AwaitingApproval, CaseStatus.Expired),
        (CaseStatus.Starting, CaseStatus.Starting),
    ];

    /// <summary>Every pair of statuses, so a status added later is checked too.</summary>
    public static TheoryData<CaseStatus, CaseStatus, bool> AllPairs()
    {
        var data = new TheoryData<CaseStatus, CaseStatus, bool>();
        foreach (var from in Enum.GetValues<CaseStatus>())
        {
            foreach (var to in Enum.GetValues<CaseStatus>())
            {
                data.Add(from, to, Allowed.Contains((from, to)));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Only_listed_transitions_are_allowed(CaseStatus from, CaseStatus to, bool allowed)
    {
        Assert.Equal(allowed, CaseStateMachine.CanMove(from, to));
        if (allowed)
        {
            Assert.Equal(to, CaseStateMachine.Move(from, to));
        }
        else
        {
            var ex = Assert.Throws<InvalidCaseTransitionException>(() => CaseStateMachine.Move(from, to));
            Assert.Equal((from, to), (ex.From, ex.To));
        }
    }

    [Theory]
    [InlineData(CaseStatus.Approved)]
    [InlineData(CaseStatus.Rejected)]
    [InlineData(CaseStatus.Cancelled)]
    [InlineData(CaseStatus.Failed)]
    [InlineData(CaseStatus.Expired)]
    public void Final_statuses_never_change(CaseStatus final)
    {
        Assert.All(Enum.GetValues<CaseStatus>(), to => Assert.False(CaseStateMachine.CanMove(final, to)));
    }

    public static TheoryData<AgentEvent, CaseStatus> Events() => new()
    {
        { AgentEvent.Create("run_1", 1, AgentEventType.RunStarted, new RunStartedPayload("case-1", "BBCA", 15)), CaseStatus.Running },
        { AgentEvent.Create("run_1", 2, AgentEventType.Token, new TokenPayload("teks")), CaseStatus.Running },
        { AgentEvent.Create("run_1", 3, AgentEventType.Error,
            new ErrorPayload(AgentErrorCode.ModelError, "gagal", Retryable: true)), CaseStatus.Failed },
        { AgentEvent.Create("run_1", 4, AgentEventType.Done, new DonePayload(RunOutcome.Approved, 5)), CaseStatus.Approved },
        { AgentEvent.Create("run_1", 4, AgentEventType.Done, new DonePayload(RunOutcome.Rejected, 5)), CaseStatus.Rejected },
        { AgentEvent.Create("run_1", 4, AgentEventType.Done, new DonePayload(RunOutcome.Cancelled, 5)), CaseStatus.Cancelled },
    };

    [Theory]
    [MemberData(nameof(Events))]
    public void Events_ask_for_the_matching_status(AgentEvent e, CaseStatus expected)
    {
        Assert.Equal(expected, CaseStateMachine.StatusAfter(e));
    }
}
