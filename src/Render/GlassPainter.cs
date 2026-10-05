using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using RefractionLab.Logic;
using System.Numerics;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace RefractionLab.Render;

/// <summary>
/// Draws the refraction (blur, then bezel displacement, then a dark tint) from a panel-sized crop.
/// Everything is in device pixels on a 96-DPI drawing session, so the same code serves the UI-thread
/// canvas and the render thread. One instance must only be used from one thread at a time.
/// </summary>
internal sealed class GlassPainter : IDisposable
{
    private CanvasBitmap? _bezelMap;
    private (int Width, int Height, float Radius, float Bezel) _bezelKey;
    private GaussianBlurEffect? _blurFx;
    private Transform2DEffect? _mapFx;
    private DisplacementMapEffect? _displaceFx;
    private CanvasGeometry? _clipGeometry;
    private (CanvasBitmap?, CanvasBitmap?, float, float, float, float, int) _effectKey;

    public void Draw(
        ICanvasResourceCreator creator, CanvasDrawingSession ds, CanvasBitmap crop, int padPx,
        float w, float h, float radiusPx, float bezelPx, float blurPx, float shiftPx)
    {
        EnsureBezelMap(creator, (int)Math.Round(w), (int)Math.Round(h), radiusPx, bezelPx);
        EnsureEffects(creator, crop, padPx, w, h, radiusPx, blurPx);

        _displaceFx!.Amount = 2 * shiftPx; // map spans [-0.5, +0.5] of Amount, see BezelMap
        using CanvasActiveLayer layer = ds.CreateLayer(1f, _clipGeometry!);
        ds.DrawImage(_displaceFx, 0, 0, new Rect(padPx, padPx, w, h));
        ds.FillRectangle(0, 0, w, h, Color.FromArgb(56, 12, 14, 20));
    }

    // Built once per size or source change, so the per-frame cost is the draw itself.
    private void EnsureEffects(ICanvasResourceCreator creator, CanvasBitmap crop, int padPx, float w, float h, float radius, float blurPx)
    {
        (CanvasBitmap?, CanvasBitmap?, float, float, float, float, int) key = (crop, _bezelMap, w, h, radius, blurPx, padPx);
        if (_displaceFx is not null && _effectKey == key)
            return;

        DisposeEffects();
        _clipGeometry = CanvasGeometry.CreateRoundedRectangle(creator, 0, 0, w, h, radius, radius);
        _blurFx = new GaussianBlurEffect { Source = crop, BlurAmount = blurPx, BorderMode = EffectBorderMode.Hard };
        _mapFx = new Transform2DEffect { Source = _bezelMap, TransformMatrix = Matrix3x2.CreateTranslation(padPx, padPx) };
        _displaceFx = new DisplacementMapEffect
        {
            Source = _blurFx,
            Displacement = _mapFx,
            XChannelSelect = EffectChannelSelect.Red,
            YChannelSelect = EffectChannelSelect.Green,
        };
        _effectKey = key;
    }

    private void EnsureBezelMap(ICanvasResourceCreator creator, int width, int height, float radius, float bezel)
    {
        var key = (width, height, radius, bezel);
        if (_bezelMap is not null && _bezelKey == key)
            return;
        _bezelMap?.Dispose();
        _bezelMap = CanvasBitmap.CreateFromBytes(
            creator, BezelMap.Generate(width, height, radius, bezel), width, height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96f);
        _bezelKey = key;
    }

    private void DisposeEffects()
    {
        _displaceFx?.Dispose();
        _mapFx?.Dispose();
        _blurFx?.Dispose();
        _clipGeometry?.Dispose();
        _displaceFx = null;
        _mapFx = null;
        _blurFx = null;
        _clipGeometry = null;
    }

    public void Dispose()
    {
        DisposeEffects();
        _bezelMap?.Dispose();
        _bezelMap = null;
    }
}
