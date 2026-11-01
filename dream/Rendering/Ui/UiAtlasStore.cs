using Dream.Rendering.Core;

namespace Dream.Rendering.Ui;

public enum UiAtlasState : byte
{
    // Start has not been called.
    Idle,
    Loading,
    Ready,
    Failed
}

// Where the atlas comes from. LoadTable and DecodePage run on a thread-pool thread and may throw;
// the store turns any exception into the Failed state.
public interface IUiAtlasLoader
{
    UiSpriteTable LoadTable();

    DecodedTexture DecodePage(UiPage page);
}

// Creates and deletes the GPU textures. Called on the render thread only.
public interface IUiTextureUploader
{
    int Upload(UiPage page, DecodedTexture texture);

    void Delete(int handle);
}

// Loads the sprite table and the page images off-thread, then uploads one page per Update call on the
// render thread. Never throws: every failure (loader, decode, size mismatch, upload) ends in Failed
// with a one-line reason, which the HUD surface selector turns into the ImGui fallback.
public sealed class UiAtlasStore : IDisposable
{
    // The pages that come from PNG files. UiPage.Font is the ImGui font texture and is not here.
    public static readonly UiPage[] FilePages =
    [
        UiPage.Window, UiPage.Hud, UiPage.Cursors, UiPage.ItemsCommon, UiPage.ItemsUncommon, UiPage.ItemsRare, UiPage.Digits
    ];

    private readonly IUiAtlasLoader _loader;
    private readonly IUiTextureUploader _uploader;
    private readonly TimeProvider _time;
    private readonly int[] _textures = new int[Enum.GetValues<UiPage>().Length];
    private readonly Task<DecodedTexture>?[] _pageTasks = new Task<DecodedTexture>?[FilePages.Length];
    private readonly bool[] _uploaded = new bool[FilePages.Length];
    private Task<UiSpriteTable>? _tableTask;
    private UiSpriteTable? _table;
    private long _startTimestamp;
    private bool _disposed;

    public UiAtlasStore(IUiAtlasLoader loader, IUiTextureUploader uploader, TimeProvider? time = null)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));
        _time = time ?? TimeProvider.System;
    }

    public UiAtlasState State { get; private set; }

    // Why the store failed; empty otherwise.
    public string FailureReason { get; private set; } = string.Empty;

    // Set once Ready.
    public UiSpriteTable? Table => State == UiAtlasState.Ready ? _table : null;

    // Time since Start while Loading, else zero. The surface selector compares it with its 5 s limit.
    public TimeSpan LoadingElapsed =>
        State == UiAtlasState.Loading ? _time.GetElapsedTime(_startTimestamp) : TimeSpan.Zero;

    // GL texture of a page, or 0 when it is not uploaded.
    public int GetTexture(UiPage page) => _textures[(int)page];

    // Starts the background decode. Safe to call twice (the second call does nothing).
    public void Start()
    {
        if (State != UiAtlasState.Idle || _disposed)
        {
            return;
        }

        State = UiAtlasState.Loading;
        _startTimestamp = _time.GetTimestamp();
        try
        {
            IUiAtlasLoader loader = _loader;
            _tableTask = Task.Run(loader.LoadTable);
            for (int i = 0; i < FilePages.Length; i++)
            {
                UiPage page = FilePages[i];
                _pageTasks[i] = Task.Run(() => loader.DecodePage(page));
            }
        }
        catch (Exception ex)
        {
            Fail($"could not start loading: {ex.Message}");
        }
    }

    // Render thread, once per frame. Uploads at most one page.
    public void Update()
    {
        if (State != UiAtlasState.Loading)
        {
            return;
        }

        try
        {
            if (_table == null)
            {
                if (_tableTask == null)
                {
                    Fail("sprite table task missing");
                    return;
                }

                if (BackgroundWork.TryTakeCompleted(ref _tableTask, out UiSpriteTable? table, out Exception? tableFault))
                {
                    _table = table ?? throw new InvalidOperationException("The loader returned no sprite table.");
                }
                else if (tableFault != null)
                {
                    Fail($"sprite table: {tableFault.Message}");
                    return;
                }
            }

            // Pages upload only after the table is known so sizes can be checked against it.
            if (_table != null)
            {
                UploadOnePage(_table);
            }
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private void UploadOnePage(UiSpriteTable table)
    {
        for (int i = 0; i < FilePages.Length; i++)
        {
            if (_uploaded[i])
            {
                continue;
            }

            if (BackgroundWork.TryTakeCompleted(ref _pageTasks[i], out DecodedTexture? decoded, out Exception? fault))
            {
                UiPage page = FilePages[i];
                if (decoded == null || !table.TryGetPageSize(page, out int w, out int h) || decoded.Width != w || decoded.Height != h)
                {
                    Fail($"{page} image size does not match the sprite table.");
                    return;
                }

                int handle = _uploader.Upload(page, decoded);
                if (handle == 0)
                {
                    Fail($"{page} upload returned no texture.");
                    return;
                }

                _textures[(int)page] = handle;
                _uploaded[i] = true;
                // One page per frame.
                break;
            }

            if (fault != null)
            {
                Fail($"{FilePages[i]} decode: {fault.Message}");
                return;
            }
        }

        if (State == UiAtlasState.Loading && Array.TrueForAll(_uploaded, static u => u))
        {
            State = UiAtlasState.Ready;
        }
    }

    private void Fail(string reason)
    {
        State = UiAtlasState.Failed;
        FailureReason = reason;
        DeleteTextures();
    }

    private void DeleteTextures()
    {
        for (int i = 0; i < _textures.Length; i++)
        {
            int handle = _textures[i];
            if (handle == 0)
            {
                continue;
            }

            _textures[i] = 0;
            try
            {
                _uploader.Delete(handle);
            }
            catch (Exception)
            {
                // Deleting a texture must not turn a clean fallback into a crash.
            }
        }
    }

    // Render thread. Deletes the textures; the store is Idle-like afterwards and cannot restart.
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DeleteTextures();
        if (State == UiAtlasState.Loading)
        {
            State = UiAtlasState.Failed;
            FailureReason = "disposed";
        }
    }
}
