using Dream.Rendering.Core;
using Dream.Rendering.Ui;
using Xunit;

namespace Dream.Tests.Rendering.Ui;

public class UiAtlasStoreTests
{
    private const int PageW = 8;
    private const int PageH = 8;

    private sealed class FakeLoader : IUiAtlasLoader
    {
        public Func<UiSpriteTable>? Table { get; set; }

        public Func<UiPage, DecodedTexture>? Page { get; set; }

        public UiSpriteTable LoadTable() => (Table ?? DefaultTable)();

        public DecodedTexture DecodePage(UiPage page) => (Page ?? DefaultPage)(page);

        public static UiSpriteTable DefaultTable()
        {
            UiSpriteTable t = new();
            foreach (UiPage page in UiAtlasStore.FilePages)
            {
                t.AddPage(page, "{\"size\":[8,8],\"sprites\":{\"" + page + ".s\":{\"x\":0,\"y\":0,\"w\":4,\"h\":4}}}", PageW, PageH);
            }

            return t;
        }

        public static DecodedTexture DefaultPage(UiPage page) => new(PageW, PageH, new byte[PageW * PageH * 4]);
    }

    private sealed class FakeUploader : IUiTextureUploader
    {
        private int _next = 100;

        public List<UiPage> Uploaded { get; } = [];

        public List<int> Deleted { get; } = [];

        public Func<UiPage, int>? Override { get; set; }

        public int Upload(UiPage page, DecodedTexture texture)
        {
            int handle = Override?.Invoke(page) ?? _next++;
            if (handle != 0)
            {
                Uploaded.Add(page);
            }

            return handle;
        }

        public void Delete(int handle) => Deleted.Add(handle);
    }

    private static void PumpUntilSettled(UiAtlasStore store, FakeUploader up, List<int>? perFrameUploads = null)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (store.State == UiAtlasState.Loading)
        {
            int before = up.Uploaded.Count;
            store.Update();
            perFrameUploads?.Add(up.Uploaded.Count - before);
            Assert.True(DateTime.UtcNow < deadline, "atlas store never settled");
            Thread.Sleep(1);
        }
    }

    [Fact]
    public void Success_UploadsOnePagePerUpdateThenIsReady()
    {
        FakeUploader up = new();
        UiAtlasStore store = new(new FakeLoader(), up);
        Assert.Equal(UiAtlasState.Idle, store.State);
        store.Start();
        Assert.Equal(UiAtlasState.Loading, store.State);

        List<int> perFrame = [];
        PumpUntilSettled(store, up, perFrame);

        Assert.Equal(UiAtlasState.Ready, store.State);
        Assert.All(perFrame, n => Assert.InRange(n, 0, 1));
        Assert.Equal(UiAtlasStore.FilePages.Length, up.Uploaded.Count);
        Assert.NotNull(store.Table);
        foreach (UiPage page in UiAtlasStore.FilePages)
        {
            Assert.NotEqual(0, store.GetTexture(page));
        }

        Assert.Equal(0, store.GetTexture(UiPage.Font));
        Assert.Equal(string.Empty, store.FailureReason);
    }

    [Fact]
    public void TableIsNotExposedWhileLoading()
    {
        UiAtlasStore store = new(new FakeLoader(), new FakeUploader());
        store.Start();
        Assert.Null(store.Table);
        Assert.True(store.LoadingElapsed >= TimeSpan.Zero);
    }

    [Fact]
    public void LoaderTableThrows_Fails()
    {
        FakeUploader up = new();
        UiAtlasStore store = new(new FakeLoader { Table = () => throw new UiSpriteTableException("bad json") }, up);
        store.Start();
        PumpUntilSettled(store, up);

        Assert.Equal(UiAtlasState.Failed, store.State);
        Assert.Contains("bad json", store.FailureReason);
        Assert.Empty(up.Uploaded);
        Assert.Null(store.Table);
    }

    [Fact]
    public void PageDecodeThrows_FailsAndDeletesWhatWasUploaded()
    {
        FakeUploader up = new();
        FakeLoader loader = new()
        {
            Page = page => page == UiPage.Digits
                ? throw new InvalidDataException("corrupt png")
                : FakeLoader.DefaultPage(page)
        };
        UiAtlasStore store = new(loader, up);
        store.Start();
        PumpUntilSettled(store, up);

        Assert.Equal(UiAtlasState.Failed, store.State);
        Assert.Contains("Digits", store.FailureReason);
        Assert.Contains("corrupt png", store.FailureReason);
        Assert.Equal(up.Uploaded.Count, up.Deleted.Count);
        foreach (UiPage page in UiAtlasStore.FilePages)
        {
            Assert.Equal(0, store.GetTexture(page));
        }
    }

    [Fact]
    public void ImageSizeNotMatchingTheTable_Fails()
    {
        FakeUploader up = new();
        FakeLoader loader = new()
        {
            Page = page => page == UiPage.Hud ? new DecodedTexture(16, 16, new byte[16 * 16 * 4]) : FakeLoader.DefaultPage(page)
        };
        UiAtlasStore store = new(loader, up);
        store.Start();
        PumpUntilSettled(store, up);

        Assert.Equal(UiAtlasState.Failed, store.State);
        Assert.Contains("size", store.FailureReason);
    }

    [Fact]
    public void UploadThrows_FailsWithoutThrowing()
    {
        FakeUploader up = new() { Override = page => page == UiPage.Cursors ? throw new InvalidOperationException("gl out of memory") : 7 };
        UiAtlasStore store = new(new FakeLoader(), up);
        store.Start();

        Exception? thrown = Record.Exception(() => PumpUntilSettled(store, up));

        Assert.Null(thrown);
        Assert.Equal(UiAtlasState.Failed, store.State);
        Assert.Contains("gl out of memory", store.FailureReason);
    }

    [Fact]
    public void UploadReturnsZero_Fails()
    {
        FakeUploader up = new() { Override = _ => 0 };
        UiAtlasStore store = new(new FakeLoader(), up);
        store.Start();
        PumpUntilSettled(store, up);

        Assert.Equal(UiAtlasState.Failed, store.State);
    }

    [Fact]
    public void FailedStoreIgnoresFurtherUpdatesAndStart()
    {
        FakeUploader up = new();
        UiAtlasStore store = new(new FakeLoader { Table = () => throw new UiSpriteTableException("x") }, up);
        store.Start();
        PumpUntilSettled(store, up);

        store.Update();
        store.Start();

        Assert.Equal(UiAtlasState.Failed, store.State);
    }

    [Fact]
    public void Dispose_DeletesTexturesAndIsIdempotent()
    {
        FakeUploader up = new();
        UiAtlasStore store = new(new FakeLoader(), up);
        store.Start();
        PumpUntilSettled(store, up);

        store.Dispose();
        store.Dispose();

        Assert.Equal(UiAtlasStore.FilePages.Length, up.Deleted.Count);
        Assert.Equal(0, store.GetTexture(UiPage.Window));
    }

    private sealed class ManualTime : TimeProvider
    {
        public long Ticks { get; set; }

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Ticks;
    }

    [Fact]
    public void LoadingElapsed_FollowsTheClockAndStopsWhenSettled()
    {
        ManualTime time = new();
        FakeUploader up = new();
        UiAtlasStore store = new(new FakeLoader(), up, time);
        Assert.Equal(TimeSpan.Zero, store.LoadingElapsed);
        store.Start();
        time.Ticks = 6000;
        Assert.Equal(TimeSpan.FromSeconds(6), store.LoadingElapsed);

        PumpUntilSettled(store, up);

        Assert.Equal(TimeSpan.Zero, store.LoadingElapsed);
    }
}
