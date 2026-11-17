using Dream.Client.Scenes;
using Xunit;

namespace Dream.Tests.Client.Scenes;

public class LoadingGateTests
{
    [Fact]
    public void Off_IsOpenFromTheStart()
    {
        LoadingGate gate = new(LoadingGateMode.Off);
        Assert.Equal(LoadingGatePhase.Open, gate.Phase);
        Assert.False(gate.BlocksInput);
        Assert.False(gate.OverlayVisible);
        gate.Update(true, true);
        Assert.Equal(LoadingGatePhase.Open, gate.Phase);
    }

    [Fact]
    public void WaitForKey_StartsLoadingAndBlocksInput()
    {
        LoadingGate gate = new(LoadingGateMode.WaitForKey);
        Assert.Equal(LoadingGatePhase.Loading, gate.Phase);
        Assert.True(gate.BlocksInput);
        Assert.True(gate.OverlayVisible);
        gate.Update(false, true);
        gate.Update(false, false);
        Assert.Equal(LoadingGatePhase.Loading, gate.Phase);
    }

    [Fact]
    public void InputHeldFromBeforeReady_DoesNotDismiss()
    {
        LoadingGate gate = new(LoadingGateMode.WaitForKey);
        gate.Update(false, true);
        gate.Update(true, true);
        Assert.Equal(LoadingGatePhase.AwaitingKey, gate.Phase);
        gate.Update(true, true);
        gate.Update(true, true);
        Assert.Equal(LoadingGatePhase.AwaitingKey, gate.Phase);
        gate.Update(true, false); // armed now
        Assert.Equal(LoadingGatePhase.AwaitingKey, gate.Phase);
        gate.Update(true, true);
        Assert.Equal(LoadingGatePhase.Releasing, gate.Phase);
    }

    [Fact]
    public void NeverOpensWhileInputIsDown()
    {
        LoadingGate gate = new(LoadingGateMode.WaitForKey);
        gate.Update(true, false);
        gate.Update(true, false);
        gate.Update(true, true);
        Assert.Equal(LoadingGatePhase.Releasing, gate.Phase);
        for (int i = 0; i < 10; i++)
        {
            gate.Update(true, true);
            Assert.True(gate.BlocksInput);
        }

        gate.Update(true, false);
        Assert.Equal(LoadingGatePhase.Open, gate.Phase);
        Assert.False(gate.BlocksInput);
    }

    [Fact]
    public void ReleasingIsNotAnOverlay()
    {
        LoadingGate gate = new(LoadingGateMode.WaitForKey);
        gate.Update(true, false);
        gate.Update(true, false);
        gate.Update(true, true);
        Assert.False(gate.OverlayVisible);
        Assert.True(gate.BlocksInput);
    }

    [Fact]
    public void PressContinue_OnlyActsInAwaitingKey()
    {
        LoadingGate gate = new(LoadingGateMode.WaitForKey);
        gate.PressContinue();
        Assert.Equal(LoadingGatePhase.Loading, gate.Phase);
        gate.Update(true, true);
        gate.PressContinue(); // unarmed is fine
        Assert.Equal(LoadingGatePhase.Releasing, gate.Phase);
        gate.PressContinue();
        Assert.Equal(LoadingGatePhase.Releasing, gate.Phase);
        gate.Update(true, false);
        Assert.Equal(LoadingGatePhase.Open, gate.Phase);
        gate.PressContinue();
        Assert.Equal(LoadingGatePhase.Open, gate.Phase);
    }

    [Fact]
    public void Skip_LeavesLoadingAndAwaitingKey()
    {
        LoadingGate loading = new(LoadingGateMode.WaitForKey);
        loading.Skip();
        Assert.Equal(LoadingGatePhase.Releasing, loading.Phase);

        LoadingGate awaiting = new(LoadingGateMode.WaitForKey);
        awaiting.Update(true, false);
        awaiting.Skip();
        Assert.Equal(LoadingGatePhase.Releasing, awaiting.Phase);
        awaiting.Update(true, false);
        Assert.Equal(LoadingGatePhase.Open, awaiting.Phase);

        LoadingGate off = new(LoadingGateMode.Off);
        off.Skip();
        Assert.Equal(LoadingGatePhase.Open, off.Phase);
    }
}
