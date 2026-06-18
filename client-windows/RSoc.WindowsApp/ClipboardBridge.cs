using System.Runtime.InteropServices;

namespace RSoc.WindowsApp;

/// <summary>
/// Puente de portapapeles de texto para una ventana. Escucha los cambios del portapapeles del
/// sistema (WM_CLIPBOARDUPDATE) y notifica el texto; permite aplicar texto remoto sin reenviarlo
/// de vuelta (evita el eco). Todo en el hilo de UI.
/// </summary>
public sealed class ClipboardBridge : NativeWindow, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly Control _owner;
    private bool _suppress;

    /// <summary>Texto copiado localmente (para enviar al otro extremo).</summary>
    public event Action<string>? LocalTextCopied;

    public ClipboardBridge(Control owner)
    {
        _owner = owner;
        if (owner.IsHandleCreated) Attach();
        else owner.HandleCreated += (_, _) => Attach();
        owner.HandleDestroyed += (_, _) => Detach();
    }

    private void Attach()
    {
        AssignHandle(_owner.Handle);
        AddClipboardFormatListener(_owner.Handle);
    }

    private void Detach()
    {
        try { RemoveClipboardFormatListener(_owner.Handle); } catch { }
        ReleaseHandle();
    }

    /// <summary>Aplica texto remoto al portapapeles local sin disparar el eco.</summary>
    public void ApplyRemoteText(string text)
    {
        if (!_owner.IsHandleCreated) return;
        _owner.BeginInvoke(() =>
        {
            try
            {
                _suppress = true;
                Clipboard.SetText(text);
            }
            catch { }
            finally { _suppress = false; }
        });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE && !_suppress)
        {
            try
            {
                if (Clipboard.ContainsText())
                    LocalTextCopied?.Invoke(Clipboard.GetText());
            }
            catch { }
        }
        base.WndProc(ref m);
    }

    public void Dispose() => Detach();
}
