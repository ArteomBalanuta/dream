using Dream.Assets;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Gl;
using ImGuiNET;

namespace Dream.Rendering.Core;

// The classic HUD's GL surface: atlas load, CPU build into a UiBatch, one submit. Render thread
// only. Frame flow (see the plan, section 3): BeginHudFrame, then BuildClassicHud (CPU, no GL), then
// SubmitClassicHud (GL). A failure here never throws out of BeginHudFrame; it shows up as
// ClassicHudAtlasState == Failed and the HUD surface selector falls back to ImGui.
public partial class Render
{
    private UiGlyphTable? _uiGlyphs;
    private UiAtlasStore? _uiAtlas;
    private UiBatch? _uiBatch;
    private UiGlRenderer? _uiGl;
    private string? _classicHudInitError;
    private bool _classicHudStarted;

    // Idle until the first BeginHudFrame, Loading while the PNGs decode, Ready once every page is
    // on the GPU, Failed otherwise (including a font or shader setup failure).
    public UiAtlasState ClassicHudAtlasState =>
        _classicHudInitError != null ? UiAtlasState.Failed : _uiAtlas?.State ?? UiAtlasState.Idle;

    // One-line reason when ClassicHudAtlasState is Failed; empty otherwise.
    public string ClassicHudFailureReason =>
        _classicHudInitError ?? _uiAtlas?.FailureReason ?? string.Empty;

    // Time spent Loading (zero when not Loading). The selector latches the fallback after 5 s.
    public TimeSpan ClassicHudLoadingElapsed => _uiAtlas?.LoadingElapsed ?? TimeSpan.Zero;

    // Diagnostics of the last built frame.
    public int ClassicHudQuadCount => _uiBatch?.QuadCount ?? 0;

    public int ClassicHudCommandCount => _uiBatch?.CommandCount ?? 0;

    // Called once per frame before building. First call starts the background atlas decode and
    // creates the GL objects; later calls upload at most one atlas page. Returns the state.
    public UiAtlasState BeginHudFrame()
    {
        GLUtils.AssertRenderThread("Render.BeginHudFrame");
        if (_classicHudInitError != null)
        {
            return UiAtlasState.Failed;
        }

        try
        {
            if (!_classicHudStarted)
            {
                _classicHudStarted = true;
                if (_uiGlyphs == null)
                {
                    _classicHudInitError = "UI font glyph table is unavailable";
                    return UiAtlasState.Failed;
                }

                _uiGl = new UiGlRenderer();
                _uiAtlas = new UiAtlasStore(new UiAtlasFileLoader(AssetPaths.UiDirectory), new UiGlTextureUploader());
                _uiAtlas.Start();
            }

            _uiAtlas!.Update();
            if (_uiAtlas.State == UiAtlasState.Ready && _uiBatch == null)
            {
                UiBatch batch = new(_uiGlyphs!);
                batch.SetSpriteTable(_uiAtlas.Table!);
                _uiBatch = batch;
            }

            return _uiAtlas.State;
        }
        catch (Exception ex)
        {
            _classicHudInitError = $"{ex.GetType().Name}: {ex.Message}";
            return UiAtlasState.Failed;
        }
    }

    // CPU only, no GL calls: resets the batch for the current viewport, resolves the layout
    // (configuredScale 0 is auto) and runs ClassicHud.Build into it. Returns false, building
    // nothing, unless the atlas is Ready. Exceptions from panels propagate to the caller, which
    // owns the single try/catch at the surface boundary.
    public bool BuildClassicHud(HudView view, IUiInput input, IHudActions actions, float configuredScale, out UiLayout layout)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(actions);
        layout = default;
        if (_uiBatch == null || _uiAtlas == null || _uiAtlas.State != UiAtlasState.Ready || _uiAtlas.Table == null)
        {
            return false;
        }

        OpenTK.Mathematics.Vector2i size = ViewportSize;
        layout = UiLayout.Create(size.X, size.Y, configuredScale);
        _uiBatch.BeginFrame(size.X, size.Y, layout.Scale);
        ClassicHud.Build(_uiBatch, input, view, in layout, _uiAtlas.Table, actions);
        return true;
    }

    // GL: uploads the built vertices and draws them, then restores the GL state. Call after a true
    // BuildClassicHud and before the ImGui windows. Does nothing when there is nothing built.
    public void SubmitClassicHud()
    {
        if (_uiBatch == null || _uiGl == null || _uiAtlas == null || _uiAtlas.State != UiAtlasState.Ready)
        {
            return;
        }

        _uiGl.Submit(_uiBatch, _uiAtlas, _fontTexture);
    }

    // Reads the back buffer and writes a PNG (alpha forced opaque). Call after all drawing and
    // before the buffer swap, on the render thread.
    public void CaptureBackBufferPng(string path)
    {
        GLUtils.AssertRenderThread("Render.CaptureBackBufferPng");
        OpenTK.Mathematics.Vector2i size = ViewportSize;
        UiFramebufferCapture.CaptureBackBufferToPng(path, size.X, size.Y);
    }

    // Builds the glyph table after the font atlas texture exists. A failure is remembered and
    // reported through ClassicHudAtlasState instead of breaking ImGui start-up.
    private void InitializeUiGlyphs()
    {
        try
        {
            _uiGlyphs = UiFontSetup.BuildGlyphTable(ImGui.GetIO().Fonts);
        }
        catch (Exception ex)
        {
            _uiGlyphs = null;
            _classicHudInitError = $"UI font: {ex.Message}";
        }
    }

    private void DisposeClassicHud()
    {
        UiAtlasStore? atlas = _uiAtlas;
        UiGlRenderer? gl = _uiGl;
        _uiAtlas = null;
        _uiGl = null;
        _uiBatch = null;
        try
        {
            atlas?.Dispose();
        }
        finally
        {
            gl?.Dispose();
        }
    }
}
