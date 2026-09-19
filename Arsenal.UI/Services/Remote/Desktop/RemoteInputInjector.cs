using Arsenal.Helpers;
using System.Buffers.Binary;

namespace Arsenal.UI.Services.Remote.Desktop;

/// <summary>
/// Turns the phone's pointer and keyboard messages into real Windows input.
/// </summary>
/// <remarks>
/// Everything arrives normalised: a pointer position is 0..65535 across the captured
/// surface, never a pixel. The phone does not know the desktop's geometry, its own view
/// is scaled and letterboxed to whatever aspect the screen it is held at happens to be,
/// and the monitor can change resolution mid session. Converting here, against the
/// monitor this session is actually capturing, is the only place all three are known.
///
/// <para>SendInput rather than SetCursorPos and mouse_event: it is the only path that
/// delivers a coherent press and release pair to the same window, and the only one that
/// games and remote desktop clients both agree on.</para>
/// </remarks>
internal sealed class RemoteInputInjector
{
    private readonly RemoteMonitor _monitor;
    private readonly bool _allowed;
    private readonly RemoteNative.INPUT[] _one = new RemoteNative.INPUT[1];
    private readonly HashSet<ushort> _keysDown = new();
    private readonly object _gate = new();

    internal RemoteInputInjector(RemoteMonitor monitor, bool allowed)
    {
        _monitor = monitor;
        _allowed = allowed;
    }

    /// <summary>Whether anything from the phone reaches the desktop at all.</summary>
    internal bool Allowed => _allowed;

    internal void Handle(ReadOnlySpan<byte> payload)
    {
        if (!_allowed || payload.Length < 1) return;
        try
        {
            switch ((RemoteDesktopProtocol.InputKind)payload[0])
            {
                case RemoteDesktopProtocol.InputKind.PointerMove when payload.Length >= 5:
                    Move(BinaryPrimitives.ReadUInt16BigEndian(payload[1..]), BinaryPrimitives.ReadUInt16BigEndian(payload[3..]));
                    break;
                case RemoteDesktopProtocol.InputKind.PointerButton when payload.Length >= 3:
                    Button((RemoteDesktopProtocol.PointerButton)payload[1], payload[2] != 0);
                    break;
                case RemoteDesktopProtocol.InputKind.Wheel when payload.Length >= 3:
                    Wheel(BinaryPrimitives.ReadInt16BigEndian(payload[1..]), horizontal: false);
                    break;
                case RemoteDesktopProtocol.InputKind.HorizontalWheel when payload.Length >= 3:
                    Wheel(BinaryPrimitives.ReadInt16BigEndian(payload[1..]), horizontal: true);
                    break;
                case RemoteDesktopProtocol.InputKind.Key when payload.Length >= 5:
                    Key(BinaryPrimitives.ReadUInt16BigEndian(payload[1..]), payload[3] != 0, payload[4] != 0);
                    break;
                case RemoteDesktopProtocol.InputKind.Text when payload.Length >= 3:
                    Text(payload[1..]);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Remote input: " + ex.Message);
        }
    }

    /// <summary>
    /// Moves the pointer to a normalised position on the captured monitor.
    /// </summary>
    /// <remarks>
    /// MOUSEEVENTF_ABSOLUTE is relative to the whole virtual desktop only when
    /// VIRTUALDESK is also set; without it the coordinate space is the primary monitor
    /// and every click on a second screen lands on the first. Both flags are always set
    /// here, and the monitor's own offset is applied before normalising, so a session on
    /// a monitor sitting at a negative x still points where it was told.
    /// </remarks>
    private void Move(ushort normalisedX, ushort normalisedY)
    {
        int screenX = _monitor.Left + (int)Math.Round(normalisedX / 65535d * (_monitor.Width - 1));
        int screenY = _monitor.Top + (int)Math.Round(normalisedY / 65535d * (_monitor.Height - 1));

        int virtualLeft = RemoteNative.GetSystemMetrics(RemoteNative.SM_XVIRTUALSCREEN);
        int virtualTop = RemoteNative.GetSystemMetrics(RemoteNative.SM_YVIRTUALSCREEN);
        int virtualWidth = Math.Max(1, RemoteNative.GetSystemMetrics(RemoteNative.SM_CXVIRTUALSCREEN));
        int virtualHeight = Math.Max(1, RemoteNative.GetSystemMetrics(RemoteNative.SM_CYVIRTUALSCREEN));

        int absoluteX = (int)Math.Round((screenX - virtualLeft) * 65535d / (virtualWidth - 1 <= 0 ? 1 : virtualWidth - 1));
        int absoluteY = (int)Math.Round((screenY - virtualTop) * 65535d / (virtualHeight - 1 <= 0 ? 1 : virtualHeight - 1));

        Send(new RemoteNative.INPUT
        {
            type = RemoteNative.INPUT_MOUSE,
            u = new RemoteNative.INPUTUNION
            {
                mi = new RemoteNative.MOUSEINPUT
                {
                    dx = Math.Clamp(absoluteX, 0, 65535),
                    dy = Math.Clamp(absoluteY, 0, 65535),
                    dwFlags = RemoteNative.MOUSEEVENTF_MOVE | RemoteNative.MOUSEEVENTF_ABSOLUTE | RemoteNative.MOUSEEVENTF_VIRTUALDESK,
                },
            },
        });
    }

    private void Button(RemoteDesktopProtocol.PointerButton button, bool down)
    {
        (uint flags, uint data) = button switch
        {
            RemoteDesktopProtocol.PointerButton.Left => (down ? RemoteNative.MOUSEEVENTF_LEFTDOWN : RemoteNative.MOUSEEVENTF_LEFTUP, 0u),
            RemoteDesktopProtocol.PointerButton.Right => (down ? RemoteNative.MOUSEEVENTF_RIGHTDOWN : RemoteNative.MOUSEEVENTF_RIGHTUP, 0u),
            RemoteDesktopProtocol.PointerButton.Middle => (down ? RemoteNative.MOUSEEVENTF_MIDDLEDOWN : RemoteNative.MOUSEEVENTF_MIDDLEUP, 0u),
            RemoteDesktopProtocol.PointerButton.XButton1 => (down ? RemoteNative.MOUSEEVENTF_XDOWN : RemoteNative.MOUSEEVENTF_XUP, RemoteNative.XBUTTON1),
            RemoteDesktopProtocol.PointerButton.XButton2 => (down ? RemoteNative.MOUSEEVENTF_XDOWN : RemoteNative.MOUSEEVENTF_XUP, RemoteNative.XBUTTON2),
            _ => (0u, 0u),
        };
        if (flags == 0) return;

        Send(new RemoteNative.INPUT
        {
            type = RemoteNative.INPUT_MOUSE,
            u = new RemoteNative.INPUTUNION { mi = new RemoteNative.MOUSEINPUT { dwFlags = flags, mouseData = data } },
        });
    }

    /// <summary>
    /// A wheel movement, in notches scaled by 120 the way Windows counts them.
    /// </summary>
    /// <remarks>
    /// The phone sends the fractional notches its own fling produced rather than whole
    /// clicks, because a touch scroll is continuous and rounding each gesture to a notch
    /// makes a slow drag do nothing at all.
    /// </remarks>
    private void Wheel(short notchesTimes120, bool horizontal) => Send(new RemoteNative.INPUT
    {
        type = RemoteNative.INPUT_MOUSE,
        u = new RemoteNative.INPUTUNION
        {
            mi = new RemoteNative.MOUSEINPUT
            {
                dwFlags = horizontal ? RemoteNative.MOUSEEVENTF_HWHEEL : RemoteNative.MOUSEEVENTF_WHEEL,
                mouseData = unchecked((uint)notchesTimes120),
            },
        },
    });

    private void Key(ushort virtualKey, bool down, bool extended)
    {
        if (virtualKey == 0) return;
        lock (_gate)
        {
            if (down) _keysDown.Add(virtualKey);
            else _keysDown.Remove(virtualKey);
        }

        Send(new RemoteNative.INPUT
        {
            type = RemoteNative.INPUT_KEYBOARD,
            u = new RemoteNative.INPUTUNION
            {
                ki = new RemoteNative.KEYBDINPUT
                {
                    wVk = virtualKey,
                    dwFlags = (down ? 0 : RemoteNative.KEYEVENTF_KEYUP) | (extended ? RemoteNative.KEYEVENTF_EXTENDEDKEY : 0),
                },
            },
        });
    }

    /// <summary>
    /// Types text the phone's keyboard produced but no virtual key describes.
    /// </summary>
    /// <remarks>
    /// KEYEVENTF_UNICODE carries the character itself rather than a key on a layout, so
    /// an emoji, an accented letter or anything from a language the desktop's layout does
    /// not have still arrives. Surrogate pairs are sent as their two units in order,
    /// which is what the flag expects.
    /// </remarks>
    private void Text(ReadOnlySpan<byte> payload)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(payload);
        if (count <= 0 || payload.Length < 2 + count * 2) return;

        var inputs = new RemoteNative.INPUT[count * 2];
        for (int i = 0; i < count; i++)
        {
            ushort unit = BinaryPrimitives.ReadUInt16BigEndian(payload[(2 + i * 2)..]);
            inputs[i * 2] = UnicodeInput(unit, up: false);
            inputs[i * 2 + 1] = UnicodeInput(unit, up: true);
        }
        RemoteNative.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<RemoteNative.INPUT>());
    }

    private static RemoteNative.INPUT UnicodeInput(ushort unit, bool up) => new()
    {
        type = RemoteNative.INPUT_KEYBOARD,
        u = new RemoteNative.INPUTUNION
        {
            ki = new RemoteNative.KEYBDINPUT
            {
                wScan = unit,
                dwFlags = RemoteNative.KEYEVENTF_UNICODE | (up ? RemoteNative.KEYEVENTF_KEYUP : 0),
            },
        },
    };

    /// <summary>
    /// Releases everything the phone was holding.
    /// </summary>
    /// <remarks>
    /// A session that drops while Alt is down leaves Alt down, and the desktop is then
    /// stuck in a menu with no way out short of pressing the physical key. Every session
    /// ends through here whether it was closed or lost.
    /// </remarks>
    internal void ReleaseHeldKeys()
    {
        ushort[] held;
        lock (_gate)
        {
            if (_keysDown.Count == 0) return;
            held = _keysDown.ToArray();
            _keysDown.Clear();
        }

        foreach (ushort key in held)
        {
            Send(new RemoteNative.INPUT
            {
                type = RemoteNative.INPUT_KEYBOARD,
                u = new RemoteNative.INPUTUNION { ki = new RemoteNative.KEYBDINPUT { wVk = key, dwFlags = RemoteNative.KEYEVENTF_KEYUP } },
            });
        }

        // Mouse buttons too, for the same reason: a lost session mid drag otherwise
        // leaves the desktop dragging whatever was under the pointer.
        Button(RemoteDesktopProtocol.PointerButton.Left, down: false);
        Button(RemoteDesktopProtocol.PointerButton.Right, down: false);
        Button(RemoteDesktopProtocol.PointerButton.Middle, down: false);
    }

    private void Send(RemoteNative.INPUT input)
    {
        lock (_one)
        {
            _one[0] = input;
            RemoteNative.SendInput(1, _one, System.Runtime.InteropServices.Marshal.SizeOf<RemoteNative.INPUT>());
        }
    }
}
