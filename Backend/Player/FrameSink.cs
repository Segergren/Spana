using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Serilog;

namespace Spana.Backend.Player;

// Receives I420 frames from libvlc's vmem callbacks and ships the newest one to the webview.
// Frames that arrive while a send is still in flight replace the pending one, so the UI never lags behind.
public sealed unsafe class FrameSink : IDisposable
{
    public const int HeaderSize = 40;
    private const uint Magic = 0x56544553;
    private const int MaxWidth = 1920;
    private const int MaxHeight = 1080;

    private byte* _buffer;
    private nuint _bufferSize;
    private int _width, _height, _yPitch, _uvPitch, _yLines, _uvLines;
    private int _sarNum = 1, _sarDen = 1, _visW, _visH;
    private uint _frameNo;

    private readonly object _swap = new();
    private byte[] _pending = Array.Empty<byte>();
    private byte[] _sending = Array.Empty<byte>();
    private int _pendingLength;
    private bool _hasPending;
    private int _sendingLength;
    private volatile bool _resend;
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _sender;
    private volatile bool _disposed;

    private WebSocket? _socket;
    private readonly object _socketGate = new();

    public Func<(int sarNum, int sarDen, int width, int height)>? TrackInfoProvider { get; set; }

    public FrameSink()
    {
        _sender = new Thread(SendLoop) { IsBackground = true, Name = "FrameSender" };
        _sender.Start();
    }

    public void Attach(WebSocket socket)
    {
        lock (_socketGate)
        {
            _socket = socket;
        }
        _resend = true;
        _signal.Set();
    }

    public void Detach(WebSocket socket)
    {
        lock (_socketGate)
        {
            if (ReferenceEquals(_socket, socket)) _socket = null;
        }
    }

    public uint Format(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        var (sn, sd, vw, vh) = TrackInfoProvider?.Invoke() ?? (1, 1, 0, 0);
        if (sn <= 0 || sd <= 0) (sn, sd) = (1, 1);

        int w = (int)width, h = (int)height;
        if (vw <= 0 || vw > w) vw = w;
        if (vh <= 0 || vh > h) vh = h == 1088 ? 1080 : h;
        double scale = Math.Min(1.0, Math.Min((double)MaxWidth / vw, (double)MaxHeight / vh));
        w = Math.Max(2, (int)(w * scale) & ~1);
        h = Math.Max(2, (int)(h * scale) & ~1);
        _visW = Math.Min(w, (int)(vw * scale));
        _visH = Math.Min(h, (int)(vh * scale));

        byte* c = (byte*)chroma;
        c[0] = (byte)'I'; c[1] = (byte)'4'; c[2] = (byte)'2'; c[3] = (byte)'0';
        width = (uint)w;
        height = (uint)h;

        _width = w;
        _height = h;
        _sarNum = sn;
        _sarDen = sd;
        _yPitch = Align(w, 32);
        _uvPitch = Align(w / 2, 32);
        _yLines = Align(h, 16);
        _uvLines = Align(h / 2, 16);

        pitches = (uint)_yPitch;
        Unsafe.Add(ref pitches, 1) = (uint)_uvPitch;
        Unsafe.Add(ref pitches, 2) = (uint)_uvPitch;
        lines = (uint)_yLines;
        Unsafe.Add(ref lines, 1) = (uint)_uvLines;
        Unsafe.Add(ref lines, 2) = (uint)_uvLines;

        FreeBuffer();
        _bufferSize = (nuint)(_yPitch * _yLines + 2 * _uvPitch * _uvLines + 64);
        _buffer = (byte*)NativeMemory.AlignedAlloc(_bufferSize, 64);
        NativeMemory.Clear(_buffer, _bufferSize);
        Log.Information("Video format {W}x{H} visible {VW}x{VH} sar {SarNum}:{SarDen}", w, h, _visW, _visH, sn, sd);
        return 1;
    }

    public void Cleanup(ref IntPtr opaque)
    {
    }

    public IntPtr Lock(IntPtr opaque, IntPtr planes)
    {
        var p = (IntPtr*)planes;
        p[0] = (IntPtr)_buffer;
        p[1] = (IntPtr)(_buffer + _yPitch * _yLines);
        p[2] = (IntPtr)(_buffer + _yPitch * _yLines + _uvPitch * _uvLines);
        return IntPtr.Zero;
    }

    public void Unlock(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
    }

    public void Display(IntPtr opaque, IntPtr picture)
    {
        if (_buffer == null || _socket == null) return;
        int ySize = _yPitch * _height, uvSize = _uvPitch * (_height / 2);
        int total = HeaderSize + ySize + 2 * uvSize;

        lock (_swap)
        {
            if (_pending.Length < total) _pending = new byte[total];
            fixed (byte* dst = _pending)
            {
                var header = new Span<byte>(dst, HeaderSize);
                BinaryPrimitives.WriteUInt32LittleEndian(header[0..], Magic);
                BinaryPrimitives.WriteInt32LittleEndian(header[4..], _width);
                BinaryPrimitives.WriteInt32LittleEndian(header[8..], _height);
                BinaryPrimitives.WriteInt32LittleEndian(header[12..], _yPitch);
                BinaryPrimitives.WriteInt32LittleEndian(header[16..], _uvPitch);
                BinaryPrimitives.WriteInt32LittleEndian(header[20..], _sarNum);
                BinaryPrimitives.WriteInt32LittleEndian(header[24..], _sarDen);
                BinaryPrimitives.WriteUInt32LittleEndian(header[28..], ++_frameNo);
                BinaryPrimitives.WriteInt32LittleEndian(header[32..], _visW);
                BinaryPrimitives.WriteInt32LittleEndian(header[36..], _visH);
                Buffer.MemoryCopy(_buffer, dst + HeaderSize, ySize, ySize);
                Buffer.MemoryCopy(_buffer + _yPitch * _yLines, dst + HeaderSize + ySize, uvSize, uvSize);
                Buffer.MemoryCopy(_buffer + _yPitch * _yLines + _uvPitch * _uvLines, dst + HeaderSize + ySize + uvSize, uvSize, uvSize);
            }
            _pendingLength = total;
            _hasPending = true;
        }
        _signal.Set();
    }

    private void SendLoop()
    {
        while (!_disposed)
        {
            _signal.WaitOne(500);
            lock (_swap)
            {
                if (_hasPending)
                {
                    (_pending, _sending) = (_sending, _pending);
                    _sendingLength = _pendingLength;
                    _hasPending = false;
                }
                else if (!_resend || _sendingLength == 0)
                {
                    continue;
                }
                _resend = false;
            }
            int length = _sendingLength;

            WebSocket? socket;
            lock (_socketGate) socket = _socket;
            if (socket?.State != WebSocketState.Open) continue;
            try
            {
                socket.SendAsync(new ArraySegment<byte>(_sending, 0, length), WebSocketMessageType.Binary, true, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Debug("Frame send failed: {Message}", ex.Message);
            }
        }
    }

    public void Clear()
    {
        lock (_swap)
        {
            _hasPending = false;
            _pendingLength = 0;
            _sendingLength = 0;
        }
    }

    private static int Align(int v, int a) => (v + a - 1) / a * a;

    private void FreeBuffer()
    {
        if (_buffer != null)
        {
            NativeMemory.AlignedFree(_buffer);
            _buffer = null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _signal.Set();
        FreeBuffer();
    }
}
