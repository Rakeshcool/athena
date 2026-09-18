using Jot.Core;
using Xunit;

namespace Jot.Core.Tests;

public class DictationStateMachineTests
{
    [Theory]
    [InlineData(DictationState.Idle, DictationEvent.HotkeyBegin, DictationState.Warming)]
    [InlineData(DictationState.Warming, DictationEvent.EngineStarted, DictationState.Recording)]
    [InlineData(DictationState.Warming, DictationEvent.Finalize, DictationState.Finalizing)]
    [InlineData(DictationState.Warming, DictationEvent.EngineFailed, DictationState.Failed)]
    [InlineData(DictationState.Warming, DictationEvent.Cancel, DictationState.Cancelled)]
    [InlineData(DictationState.Recording, DictationEvent.Finalize, DictationState.Finalizing)]
    [InlineData(DictationState.Recording, DictationEvent.LockIn, DictationState.Recording)]
    [InlineData(DictationState.Recording, DictationEvent.Cancel, DictationState.Cancelled)]
    [InlineData(DictationState.Recording, DictationEvent.EngineFailed, DictationState.Finalizing)]
    [InlineData(DictationState.Finalizing, DictationEvent.AudioFinalized, DictationState.Transcribing)]
    [InlineData(DictationState.Finalizing, DictationEvent.NoAudioCaptured, DictationState.Failed)]
    [InlineData(DictationState.Finalizing, DictationEvent.SilenceOnly, DictationState.Done)]
    [InlineData(DictationState.Transcribing, DictationEvent.TranscriptReady, DictationState.Inserting)]
    [InlineData(DictationState.Transcribing, DictationEvent.SilenceOnly, DictationState.Done)]
    [InlineData(DictationState.Transcribing, DictationEvent.TranscriptFailed, DictationState.Failed)]
    [InlineData(DictationState.Transcribing, DictationEvent.QueuedForRetry, DictationState.Done)]
    [InlineData(DictationState.Inserting, DictationEvent.Inserted, DictationState.Done)]
    [InlineData(DictationState.Inserting, DictationEvent.InsertionFellBackToClipboard, DictationState.Done)]
    [InlineData(DictationState.Inserting, DictationEvent.InsertionBlockedSecure, DictationState.Done)]
    public void Valid_transitions_apply(DictationState state, DictationEvent ev, DictationState expected)
    {
        Assert.Equal(expected, DictationStateMachine.Transition(state, ev));
    }

    [Theory]
    [InlineData(DictationState.Idle, DictationEvent.Finalize)]
    [InlineData(DictationState.Idle, DictationEvent.Inserted)]
    [InlineData(DictationState.Done, DictationEvent.TranscriptReady)]
    [InlineData(DictationState.Failed, DictationEvent.Inserted)]
    [InlineData(DictationState.Inserting, DictationEvent.Cancel)]
    [InlineData(DictationState.Transcribing, DictationEvent.LockIn)]
    public void Invalid_transitions_are_dropped(DictationState state, DictationEvent ev)
    {
        Assert.Null(DictationStateMachine.Transition(state, ev));
    }

    [Theory]
    [InlineData(DictationState.Done)]
    [InlineData(DictationState.Failed)]
    [InlineData(DictationState.Cancelled)]
    public void Terminal_states_are_terminal(DictationState state)
    {
        Assert.True(DictationStateMachine.IsTerminal(state));
    }
}
