using Android.Content;
using Android.Graphics;
using Android.Views;

namespace RSoc.Android;

/// <summary>
/// Vista de control remoto 100% por gestos (estilo touchpad), pensada para móvil:
///   · 1 dedo arrastra → mueve el cursor (auto-paneo al ampliar para mantenerlo visible).
///   · Tocar → clic izquierdo. Doble toque → doble clic.
///   · Mantener pulsado y arrastrar → arrastrar con el botón izquierdo pulsado.
///   · 2 dedos toque → clic derecho. 2 dedos arrastrar → rueda/scroll.
///   · Pellizco → zoom.
/// Expone eventos de input que la actividad reenvía a la sesión.
/// </summary>
public sealed class RemoteView : View
{
    private Bitmap? _frame;
    private int _rw, _rh;
    private float _zoom = 1f, _panX, _panY;
    private PointF _cursor = new(0, 0);
    private bool _cursorInit;

    private readonly ScaleGestureDetector _scaleDet;
    private readonly GestureDetector _gestureDet;
    private readonly int _slop;

    // estado de gestos
    private bool _dragging;        // botón izq mantenido (long-press + arrastre)
    private bool _moved;
    private int _maxPointers;
    private long _downTime;
    private float _startX, _startY;
    private float _scrollAccum;

    public event Action<int, int>? Move;       // cursor normalizado 0..65535
    public event Action<int, bool>? ButtonEvent; // code (0=izq,1=der,2=medio), down
    public event Action<int>? Wheel;            // delta (±120)

    public RemoteView(Context c) : base(c)
    {
        _scaleDet = new ScaleGestureDetector(c, new ScaleListener(this));
        _gestureDet = new GestureDetector(c, new GestureListener(this));
        _slop = ViewConfiguration.Get(c)!.ScaledTouchSlop;
        SetBackgroundColor(Color.Argb(255, 24, 26, 32));
    }

    public void SetFrame(Bitmap bmp, int w, int h)
    {
        var old = _frame;
        _frame = bmp; _rw = w; _rh = h;
        if (!_cursorInit) { _cursor = new PointF(w / 2f, h / 2f); _cursorInit = true; }
        old?.Recycle();
        Invalidate();
    }

    private float Density => Resources!.DisplayMetrics!.Density;
    private float BaseScale => (_rw <= 0 || _rh <= 0) ? 1f : Math.Min((float)Width / _rw, (float)Height / _rh);
    private float ContentScale => BaseScale * _zoom;
    private float OriginX => (Width - _rw * ContentScale) / 2f + _panX;
    private float OriginY => (Height - _rh * ContentScale) / 2f + _panY;

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (_frame is null) return;
        float cs = ContentScale;
        var dst = new RectF(OriginX, OriginY, OriginX + _rw * cs, OriginY + _rh * cs);
        canvas.DrawBitmap(_frame, null, dst, null);
        DrawCursor(canvas, OriginX + _cursor.X * cs, OriginY + _cursor.Y * cs);
    }

    private void DrawCursor(Canvas canvas, float x, float y)
    {
        float s = 22 * Density;
        float[] pts = { 0f, 0f, 0f, 0.85f, 0.22f, 0.62f, 0.38f, 1.0f, 0.52f, 0.93f, 0.36f, 0.56f, 0.62f, 0.56f };
        var path = new global::Android.Graphics.Path();
        path.MoveTo(x + pts[0] * s, y + pts[1] * s);
        for (int i = 2; i < pts.Length; i += 2) path.LineTo(x + pts[i] * s, y + pts[i + 1] * s);
        path.Close();
        using var outline = new Paint { AntiAlias = true, Color = Color.Black };
        outline.SetStyle(Paint.Style.Stroke);
        outline.StrokeWidth = 3 * Density;
        using var fill = new Paint { AntiAlias = true, Color = _dragging ? Color.Argb(255, 120, 200, 255) : Color.White };
        fill.SetStyle(Paint.Style.Fill);
        canvas.DrawPath(path, outline);
        canvas.DrawPath(path, fill);
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;
        _scaleDet.OnTouchEvent(e);
        _gestureDet.OnTouchEvent(e);

        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _downTime = e.EventTime; _maxPointers = 1; _moved = false;
                _startX = e.GetX(); _startY = e.GetY(); _scrollAccum = 0;
                break;
            case MotionEventActions.PointerDown:
                _maxPointers = Math.Max(_maxPointers, e.PointerCount);
                break;
            case MotionEventActions.Move:
                if (!_moved)
                {
                    float dx = e.GetX() - _startX, dy = e.GetY() - _startY;
                    if (dx * dx + dy * dy > _slop * _slop) _moved = true;
                }
                break;
            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                if (_dragging) { ButtonEvent?.Invoke(0, false); _dragging = false; Invalidate(); }
                else if (_maxPointers >= 2 && !_moved && !_scaleDet.IsInProgress && (e.EventTime - _downTime) < 300)
                {
                    ButtonEvent?.Invoke(1, true); ButtonEvent?.Invoke(1, false); // clic derecho
                }
                break;
        }
        return true;
    }

    private void MoveCursorBy(float dxView, float dyView)
    {
        float cs = ContentScale;
        _cursor.X = Math.Clamp(_cursor.X + dxView / cs, 0, _rw);
        _cursor.Y = Math.Clamp(_cursor.Y + dyView / cs, 0, _rh);
        EnsureCursorVisible();
        if (_rw > 0 && _rh > 0)
            Move?.Invoke(
                Math.Clamp((int)(_cursor.X / _rw * 65535), 0, 65535),
                Math.Clamp((int)(_cursor.Y / _rh * 65535), 0, 65535));
        Invalidate();
    }

    private void EnsureCursorVisible()
    {
        if (_zoom <= 1.001f) return;
        float cs = ContentScale;
        float sx = OriginX + _cursor.X * cs, sy = OriginY + _cursor.Y * cs;
        float mx = Width * 0.15f, my = Height * 0.15f;
        if (sx < mx) _panX += mx - sx; else if (sx > Width - mx) _panX -= sx - (Width - mx);
        if (sy < my) _panY += my - sy; else if (sy > Height - my) _panY -= sy - (Height - my);
    }

    private void Scroll(float dy)
    {
        _scrollAccum += dy;
        float step = 14 * Density;
        while (Math.Abs(_scrollAccum) >= step)
        {
            int sign = _scrollAccum > 0 ? 1 : -1;
            Wheel?.Invoke(sign * 120);
            _scrollAccum -= sign * step;
        }
    }

    private void ZoomAround(float factor, float focusX, float focusY)
    {
        float oldCs = ContentScale;
        float imgX = (focusX - OriginX) / oldCs;
        float imgY = (focusY - OriginY) / oldCs;
        _zoom = Math.Clamp(_zoom * factor, 1f, 6f);
        float newCs = ContentScale;
        _panX = focusX - imgX * newCs - (Width - _rw * newCs) / 2f;
        _panY = focusY - imgY * newCs - (Height - _rh * newCs) / 2f;
        Invalidate();
    }

    private sealed class ScaleListener(RemoteView v) : ScaleGestureDetector.SimpleOnScaleGestureListener
    {
        public override bool OnScale(ScaleGestureDetector d) { v.ZoomAround(d.ScaleFactor, d.FocusX, d.FocusY); return true; }
    }

    private sealed class GestureListener(RemoteView v) : GestureDetector.SimpleOnGestureListener
    {
        public override bool OnDown(MotionEvent e) => true;

        public override bool OnScroll(MotionEvent? e1, MotionEvent e2, float distanceX, float distanceY)
        {
            if (e2.PointerCount >= 2)
            {
                if (!v._scaleDet.IsInProgress) v.Scroll(distanceY);
            }
            else
            {
                v.MoveCursorBy(-distanceX, -distanceY);
            }
            return true;
        }

        public override bool OnSingleTapUp(MotionEvent e)
        {
            if (!v._dragging) { v.ButtonEvent?.Invoke(0, true); v.ButtonEvent?.Invoke(0, false); }
            return true;
        }

        public override bool OnDoubleTap(MotionEvent e)
        {
            v.ButtonEvent?.Invoke(0, true); v.ButtonEvent?.Invoke(0, false);
            v.ButtonEvent?.Invoke(0, true); v.ButtonEvent?.Invoke(0, false);
            return true;
        }

        public override void OnLongPress(MotionEvent e)
        {
            v._dragging = true;
            v.ButtonEvent?.Invoke(0, true); // mantener botón izq para arrastrar
            v.Invalidate();
        }
    }
}
