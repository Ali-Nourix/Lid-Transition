using LidFlow.Core.StateMachine;
using Xunit;

namespace LidFlow.Core.Tests;

public sealed class TransitionStateMachineTests
{
    private static TransitionStateMachine Machine() => new();

    private static TransitionStateMachine ClosedMachine()
    {
        TransitionStateMachine machine = Machine();
        machine.Fire(TransitionTrigger.LidClosed);
        machine.Fire(TransitionTrigger.CaptureSucceeded);
        machine.Fire(TransitionTrigger.AnimationCompleted);
        Assert.Equal(TransitionState.Closed, machine.Current);
        return machine;
    }

    [Fact]
    public void HappyPathClose()
    {
        TransitionStateMachine machine = Machine();
        Assert.Equal(TransitionState.IdleOpen, machine.Current);
        Assert.False(machine.OverlayShouldBeVisible);

        TransitionOutcome capture = machine.Fire(TransitionTrigger.LidClosed);
        Assert.True(capture.Accepted);
        Assert.Equal(TransitionState.CapturingForClose, capture.To);
        Assert.Equal(TransitionAction.CaptureForClose, capture.Action);

        // The overlay must still be down while capturing, or it would capture itself.
        Assert.False(machine.OverlayShouldBeVisible);

        TransitionOutcome animate = machine.Fire(TransitionTrigger.CaptureSucceeded);
        Assert.Equal(TransitionAction.StartCloseAnimation, animate.Action);
        Assert.Equal(0f, animate.StartPanelProgress);
        Assert.True(machine.OverlayShouldBeVisible);
        Assert.True(machine.IsAnimating);

        TransitionOutcome done = machine.Fire(TransitionTrigger.AnimationCompleted);
        Assert.Equal(TransitionState.Closed, done.To);
        Assert.Equal(TransitionAction.HoldBlack, done.Action);
        Assert.True(machine.OverlayShouldBeVisible);
        Assert.False(machine.IsAnimating);
    }

    [Fact]
    public void HappyPathOpen()
    {
        TransitionStateMachine machine = ClosedMachine();

        TransitionOutcome capture = machine.Fire(TransitionTrigger.LidOpened);
        Assert.Equal(TransitionState.CapturingForOpen, capture.To);
        Assert.Equal(TransitionAction.CaptureForOpen, capture.Action);

        // Crucially the overlay stays up and black while we capture behind it, so the
        // panel lighting up never reveals the desktop before the reveal animation.
        Assert.True(machine.OverlayShouldBeVisible);

        TransitionOutcome animate = machine.Fire(TransitionTrigger.CaptureSucceeded);
        Assert.Equal(TransitionAction.StartOpenAnimation, animate.Action);
        Assert.Equal(1f, animate.StartPanelProgress);

        TransitionOutcome done = machine.Fire(TransitionTrigger.AnimationCompleted);
        Assert.Equal(TransitionState.IdleOpen, done.To);
        Assert.Equal(TransitionAction.HideOverlay, done.Action);
        Assert.False(machine.OverlayShouldBeVisible);
    }

    [Fact]
    public void CaptureFailureOnCloseSkipsTheAnimationEntirely()
    {
        TransitionStateMachine machine = Machine();
        machine.Fire(TransitionTrigger.LidClosed);

        TransitionOutcome outcome = machine.Fire(TransitionTrigger.CaptureFailed);

        Assert.Equal(TransitionState.IdleOpen, outcome.To);
        Assert.Equal(TransitionAction.AbortToIdle, outcome.Action);
        Assert.False(machine.OverlayShouldBeVisible);
    }

    [Fact]
    public void CaptureFailureOnOpenDropsTheOverlaySoTheUserIsNotLeftLookingAtBlack()
    {
        TransitionStateMachine machine = ClosedMachine();
        machine.Fire(TransitionTrigger.LidOpened);

        TransitionOutcome outcome = machine.Fire(TransitionTrigger.CaptureFailed);

        Assert.Equal(TransitionState.IdleOpen, outcome.To);
        Assert.Equal(TransitionAction.HideOverlay, outcome.Action);
        Assert.False(machine.OverlayShouldBeVisible);
    }

    [Fact]
    public void OpeningDuringCloseAnimationReversesFromTheCurrentPanelPosition()
    {
        TransitionStateMachine machine = Machine();
        machine.Fire(TransitionTrigger.LidClosed);
        machine.Fire(TransitionTrigger.CaptureSucceeded);

        TransitionOutcome outcome = machine.Fire(TransitionTrigger.LidOpened, currentPanelProgress: 0.42f);

        Assert.Equal(TransitionState.OpenAnimation, outcome.To);
        Assert.Equal(TransitionAction.StartOpenAnimation, outcome.Action);
        Assert.True(outcome.IsReversal);
        Assert.Equal(0.42f, outcome.StartPanelProgress, 5);
        Assert.True(machine.OverlayShouldBeVisible);
    }

    [Fact]
    public void ClosingDuringOpenAnimationReversesFromTheCurrentPanelPosition()
    {
        TransitionStateMachine machine = ClosedMachine();
        machine.Fire(TransitionTrigger.LidOpened);
        machine.Fire(TransitionTrigger.CaptureSucceeded);

        TransitionOutcome outcome = machine.Fire(TransitionTrigger.LidClosed, currentPanelProgress: 0.73f);

        Assert.Equal(TransitionState.CloseAnimation, outcome.To);
        Assert.True(outcome.IsReversal);
        Assert.Equal(0.73f, outcome.StartPanelProgress, 5);
    }

    [Fact]
    public void RapidTogglingNeverStacksAnimationsOrStrandsTheOverlay()
    {
        TransitionStateMachine machine = Machine();

        for (int i = 0; i < 50; i++)
        {
            machine.Fire(TransitionTrigger.LidClosed, 0.5f);
            machine.Fire(TransitionTrigger.CaptureSucceeded);
            machine.Fire(TransitionTrigger.LidOpened, 0.5f);
            machine.Fire(TransitionTrigger.LidClosed, 0.5f);
            machine.Fire(TransitionTrigger.AnimationCompleted);
            machine.Fire(TransitionTrigger.LidOpened, 1f);
            machine.Fire(TransitionTrigger.CaptureSucceeded);
            machine.Fire(TransitionTrigger.AnimationCompleted);

            Assert.Equal(TransitionState.IdleOpen, machine.Current);
            Assert.False(machine.OverlayShouldBeVisible);
        }
    }

    [Fact]
    public void LidReopeningDuringCaptureAbortsWithoutShowingAnything()
    {
        TransitionStateMachine machine = Machine();
        machine.Fire(TransitionTrigger.LidClosed);

        TransitionOutcome outcome = machine.Fire(TransitionTrigger.LidOpened);

        Assert.Equal(TransitionState.IdleOpen, outcome.To);
        Assert.Equal(TransitionAction.AbortToIdle, outcome.Action);
        Assert.False(machine.OverlayShouldBeVisible);
    }

    [Fact]
    public void LidClosingAgainDuringOpenCaptureGoesStraightBackToBlack()
    {
        TransitionStateMachine machine = ClosedMachine();
        machine.Fire(TransitionTrigger.LidOpened);

        TransitionOutcome outcome = machine.Fire(TransitionTrigger.LidClosed);

        Assert.Equal(TransitionState.Closed, outcome.To);
        Assert.Equal(TransitionAction.HoldBlack, outcome.Action);
        Assert.True(machine.OverlayShouldBeVisible);
    }

    [Fact]
    public void SuspendingMidCloseKeepsTheOverlayBlack()
    {
        TransitionStateMachine machine = Machine();
        machine.Fire(TransitionTrigger.LidClosed);
        machine.Fire(TransitionTrigger.CaptureSucceeded);

        TransitionOutcome outcome = machine.Fire(TransitionTrigger.Suspending);

        Assert.Equal(TransitionState.Suspended, outcome.To);
        Assert.Equal(TransitionAction.HoldBlack, outcome.Action);
        Assert.True(machine.OverlayShouldBeVisible);
        Assert.False(machine.IsAnimating);
    }

    [Fact]
    public void SuspendingWhileIdleLeavesTheOverlayDown()
    {
        TransitionStateMachine machine = Machine();

        TransitionOutcome outcome = machine.Fire(TransitionTrigger.Suspending);

        Assert.Equal(TransitionState.Suspended, outcome.To);
        Assert.Equal(TransitionAction.HideOverlay, outcome.Action);
        Assert.False(machine.OverlayShouldBeVisible);
    }

    [Fact]
    public void ResumeAloneDoesNotAssumeTheLidWasOpened()
    {
        // Modern Standby wakes the machine for maintenance with the lid still shut, so a
        // resume must not trigger the reveal on its own.
        TransitionStateMachine machine = Machine();
        machine.Fire(TransitionTrigger.LidClosed);
        machine.Fire(TransitionTrigger.CaptureSucceeded);
        machine.Fire(TransitionTrigger.Suspending);

        TransitionOutcome outcome = machine.Fire(TransitionTrigger.Resumed);

        Assert.Equal(TransitionState.Closed, outcome.To);
        Assert.Equal(TransitionAction.HoldBlack, outcome.Action);
        Assert.True(machine.OverlayShouldBeVisible);
    }

    [Fact]
    public void ResumeWithNothingOnScreenReturnsToIdle()
    {
        TransitionStateMachine machine = Machine();
        machine.Fire(TransitionTrigger.Suspending);

        TransitionOutcome outcome = machine.Fire(TransitionTrigger.Resumed);

        Assert.Equal(TransitionState.IdleOpen, outcome.To);
        Assert.False(machine.OverlayShouldBeVisible);
    }

    [Fact]
    public void LidOpenedWhileSuspendedAndBlackStartsTheReveal()
    {
        TransitionStateMachine machine = Machine();
        machine.Fire(TransitionTrigger.LidClosed);
        machine.Fire(TransitionTrigger.CaptureSucceeded);
        machine.Fire(TransitionTrigger.Suspending);

        TransitionOutcome outcome = machine.Fire(TransitionTrigger.LidOpened);

        Assert.Equal(TransitionState.CapturingForOpen, outcome.To);
        Assert.Equal(TransitionAction.CaptureForOpen, outcome.Action);
    }

    [Theory]
    [InlineData(TransitionState.CapturingForClose)]
    [InlineData(TransitionState.CloseAnimation)]
    [InlineData(TransitionState.Closed)]
    [InlineData(TransitionState.CapturingForOpen)]
    [InlineData(TransitionState.OpenAnimation)]
    [InlineData(TransitionState.Suspended)]
    public void AbortAlwaysReturnsToIdleWithTheOverlayDown(TransitionState from)
    {
        TransitionStateMachine machine = DriveTo(from);

        TransitionOutcome outcome = machine.Fire(TransitionTrigger.Abort);

        Assert.Equal(TransitionState.IdleOpen, outcome.To);
        Assert.False(machine.OverlayShouldBeVisible);
    }

    [Fact]
    public void DisableFromAnyStateTakesTheOverlayDownAndStopsResponding()
    {
        TransitionStateMachine machine = Machine();
        machine.Fire(TransitionTrigger.LidClosed);
        machine.Fire(TransitionTrigger.CaptureSucceeded);

        TransitionOutcome disabled = machine.Fire(TransitionTrigger.Disable);
        Assert.Equal(TransitionState.Disabled, disabled.To);
        Assert.Equal(TransitionAction.HideOverlay, disabled.Action);
        Assert.False(machine.OverlayShouldBeVisible);

        Assert.False(machine.Fire(TransitionTrigger.LidClosed).Accepted);
        Assert.False(machine.Fire(TransitionTrigger.LidOpened).Accepted);

        TransitionOutcome enabled = machine.Fire(TransitionTrigger.Enable);
        Assert.Equal(TransitionState.IdleOpen, enabled.To);
        Assert.True(machine.Fire(TransitionTrigger.LidClosed).Accepted);
    }

    [Fact]
    public void MachineConstructedDisabledIgnoresLidEvents()
    {
        TransitionStateMachine machine = new(enabled: false);

        Assert.Equal(TransitionState.Disabled, machine.Current);
        Assert.False(machine.Fire(TransitionTrigger.LidClosed).Accepted);
    }

    [Fact]
    public void UnexpectedTriggersAreIgnoredRatherThanMisinterpreted()
    {
        TransitionStateMachine machine = Machine();

        // Nothing is capturing or animating, so these are meaningless here.
        Assert.False(machine.Fire(TransitionTrigger.CaptureSucceeded).Accepted);
        Assert.False(machine.Fire(TransitionTrigger.CaptureFailed).Accepted);
        Assert.False(machine.Fire(TransitionTrigger.AnimationCompleted).Accepted);
        Assert.False(machine.Fire(TransitionTrigger.LidOpened).Accepted);
        Assert.Equal(TransitionState.IdleOpen, machine.Current);
    }

    [Fact]
    public void StartProgressIsAlwaysClampedToTheUnitRange()
    {
        TransitionStateMachine machine = Machine();
        machine.Fire(TransitionTrigger.LidClosed);
        machine.Fire(TransitionTrigger.CaptureSucceeded);

        TransitionOutcome outcome = machine.Fire(TransitionTrigger.LidOpened, currentPanelProgress: 17.5f);

        Assert.Equal(1f, outcome.StartPanelProgress);
    }

    [Fact]
    public void NotANumberPanelProgressIsTreatedAsZero()
    {
        TransitionStateMachine machine = Machine();
        machine.Fire(TransitionTrigger.LidClosed);
        machine.Fire(TransitionTrigger.CaptureSucceeded);

        TransitionOutcome outcome = machine.Fire(TransitionTrigger.LidOpened, currentPanelProgress: float.NaN);

        Assert.Equal(0f, outcome.StartPanelProgress);
    }

    private static TransitionStateMachine DriveTo(TransitionState state)
    {
        TransitionStateMachine machine = Machine();

        switch (state)
        {
            case TransitionState.IdleOpen:
                break;

            case TransitionState.CapturingForClose:
                machine.Fire(TransitionTrigger.LidClosed);
                break;

            case TransitionState.CloseAnimation:
                machine.Fire(TransitionTrigger.LidClosed);
                machine.Fire(TransitionTrigger.CaptureSucceeded);
                break;

            case TransitionState.Closed:
                machine.Fire(TransitionTrigger.LidClosed);
                machine.Fire(TransitionTrigger.CaptureSucceeded);
                machine.Fire(TransitionTrigger.AnimationCompleted);
                break;

            case TransitionState.CapturingForOpen:
                machine.Fire(TransitionTrigger.LidClosed);
                machine.Fire(TransitionTrigger.CaptureSucceeded);
                machine.Fire(TransitionTrigger.AnimationCompleted);
                machine.Fire(TransitionTrigger.LidOpened);
                break;

            case TransitionState.OpenAnimation:
                machine.Fire(TransitionTrigger.LidClosed);
                machine.Fire(TransitionTrigger.CaptureSucceeded);
                machine.Fire(TransitionTrigger.AnimationCompleted);
                machine.Fire(TransitionTrigger.LidOpened);
                machine.Fire(TransitionTrigger.CaptureSucceeded);
                break;

            case TransitionState.Suspended:
                machine.Fire(TransitionTrigger.LidClosed);
                machine.Fire(TransitionTrigger.CaptureSucceeded);
                machine.Fire(TransitionTrigger.Suspending);
                break;
        }

        Assert.Equal(state, machine.Current);
        return machine;
    }
}
