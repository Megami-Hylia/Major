using System.Runtime.InteropServices;

namespace Major.Streaming;

public sealed record VideoFrame(int Width, int Height, byte[] Bgra, long PresentationTimeUs = 0);

/// <summary>FFmpeg 9 software H.264 decoder using the DLLs already shipped with scrcpy.</summary>
public sealed class H264Decoder : IDisposable
{
    private IntPtr context, packet, frame;
    private byte[] configuration = [];
    private int width, height;
    private bool disposed;
    private static readonly object LibraryLock = new();
    private static readonly Dictionary<string, IntPtr> Libraries = new(StringComparer.OrdinalIgnoreCase);

    public H264Decoder(string toolsDirectory)
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("Il ricevitore richiede Windows x64.");
        lock (LibraryLock)
        {
            if (Libraries.Count == 0)
            {
                var utility = NativeLibrary.Load(Path.Combine(Path.GetFullPath(toolsDirectory), "avutil-61.dll"));
                IntPtr codec;
                try { codec = NativeLibrary.Load(Path.Combine(Path.GetFullPath(toolsDirectory), "avcodec-63.dll")); }
                catch { NativeLibrary.Free(utility); throw; }
                Libraries["avutil-61.dll"] = utility;
                Libraries["avcodec-63.dll"] = codec;
                NativeLibrary.SetDllImportResolver(typeof(H264Decoder).Assembly, (name, _, _) => Libraries.GetValueOrDefault(name));
            }
        }
        if (Native.avcodec_version() >> 16 != 63 || Native.avutil_version() >> 16 != 61)
            throw new NotSupportedException("Versione FFmpeg incompatibile: servono avcodec 63 e avutil 61 del bundle.");
    }

    public void Reset(int videoWidth, int videoHeight)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        StreamProtocol.ValidateConfiguration(videoWidth, videoHeight, 30);
        ReleaseCodec();
        width = videoWidth; height = videoHeight; configuration = [];
        var codec = Native.avcodec_find_decoder(27); // AV_CODEC_ID_H264, audited libavcodec/codec_id.h n9.0.2.
        if (codec == IntPtr.Zero) throw new NotSupportedException("Decoder H.264 non disponibile.");
        context = Native.avcodec_alloc_context3(codec);
        packet = Native.av_packet_alloc(); frame = Native.av_frame_alloc();
        if (context == IntPtr.Zero || packet == IntPtr.Zero || frame == IntPtr.Zero) { ReleaseCodec(); throw new OutOfMemoryException(); }
        Native.av_opt_set_int(context, "threads", 1, 0);
        Native.av_opt_set_int(context, "flags", 1 << 19, 0); // AV_CODEC_FLAG_LOW_DELAY.
        Native.av_opt_set_int(context, "max_pixels", StreamProtocol.MaxPixels, 0);
        try { Check(Native.avcodec_open2(context, codec, IntPtr.Zero), "Apertura decoder"); }
        catch { ReleaseCodec(); throw; }
    }

    public IReadOnlyList<VideoFrame> Decode(ReadOnlySpan<byte> data, long ptsUs)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (context == IntPtr.Zero) throw new InvalidOperationException("Configurare il decoder prima dei frame.");
        if (data.Length is < 4 or > StreamProtocol.MaxFrameBytes) throw new InvalidDataException("Pacchetto H.264 fuori limite.");
        if (!StreamProtocol.HasAnnexBStartCode(data)) throw new InvalidDataException("Il video deve essere H.264 Annex B.");
        if (!ContainsVcl(data))
        {
            if (configuration.Length + data.Length > 256 * 1024) throw new InvalidDataException("Configurazione H.264 troppo grande.");
            configuration = [.. configuration, .. data];
            return [];
        }
        var bytes = configuration.Length == 0 ? data.ToArray() : [.. configuration, .. data];
        configuration = [];
        var output = new List<VideoFrame>();
        Native.av_packet_unref(packet);
        Check(Native.av_new_packet(packet, bytes.Length), "Allocazione pacchetto");
        // AVPacket x64 leading fields audited in the bundled FFmpeg 9.0.2 packet.h.
        Marshal.Copy(bytes, 0, Marshal.ReadIntPtr(packet, 24), bytes.Length);
        Marshal.WriteInt64(packet, 8, ptsUs); Marshal.WriteInt64(packet, 16, ptsUs);
        var result = Native.avcodec_send_packet(context, packet);
        if (result == -11) { Drain(output, ptsUs); result = Native.avcodec_send_packet(context, packet); }
        Check(result, "Decodifica H.264");
        Drain(output, ptsUs);
        return output;
    }

    private void Drain(List<VideoFrame> output, long ptsUs)
    {
        while (true)
        {
            var result = Native.avcodec_receive_frame(context, frame);
            if (result is -11 or -541478725) return;
            Check(result, "Lettura frame H.264");
            try
            {
                if (output.Count >= 2) throw new InvalidDataException("Troppi frame in un pacchetto.");
                var header = Marshal.PtrToStructure<FrameHeader>(frame);
                StreamProtocol.ValidateConfiguration(header.Width, header.Height, 30);
                if (header.Width != width || header.Height != height) throw new InvalidDataException("Le dimensioni H.264 non corrispondono alla configurazione.");
                output.Add(ConvertFrame(header, ptsUs));
            }
            finally { Native.av_frame_unref(frame); }
        }
    }

    private static unsafe VideoFrame ConvertFrame(FrameHeader source, long ptsUs)
    {
        if (source.Format is not (0 or 12 or 23 or 24)) throw new NotSupportedException($"Formato video {source.Format} non supportato. Usa H.264 SDR 8 bit.");
        var chromaWidth = (source.Width + 1) / 2;
        var interleaved = source.Format is 23 or 24;
        if (source.Y == IntPtr.Zero || source.U == IntPtr.Zero || (!interleaved && source.V == IntPtr.Zero)
            || Math.Abs((long)source.YStride) < source.Width || Math.Abs((long)source.UStride) < (interleaved ? chromaWidth * 2 : chromaWidth)
            || (!interleaved && Math.Abs((long)source.VStride) < chromaWidth)
            || Math.Abs((long)source.YStride) > 65536 || Math.Abs((long)source.UStride) > 65536 || Math.Abs((long)source.VStride) > 65536)
            throw new InvalidDataException("Piani o stride video non validi.");
        var bytes = new byte[checked(source.Width * source.Height * 4)];
        fixed (byte* destination = bytes)
        {
            for (var y = 0; y < source.Height; y++)
            {
                var luma = (byte*)source.Y + (long)y * source.YStride;
                var uRow = (byte*)source.U + (long)(y / 2) * source.UStride;
                var vRow = (byte*)source.V + (long)(y / 2) * source.VStride;
                var row = destination + y * source.Width * 4;
                for (var x = 0; x < source.Width; x++)
                {
                    var u = interleaved ? uRow[(x / 2) * 2 + (source.Format == 24 ? 1 : 0)] : uRow[x / 2];
                    var v = interleaved ? uRow[(x / 2) * 2 + (source.Format == 24 ? 0 : 1)] : vRow[x / 2];
                    var d = u - 128; var e = v - 128;
                    var c = source.Format == 12 ? luma[x] : Math.Max(0, luma[x] - 16);
                    var gain = source.Format == 12 ? 256 : 298;
                    row[x * 4] = Clamp((gain * c + (source.Format == 12 ? 454 : 516) * d + 128) >> 8);
                    row[x * 4 + 1] = Clamp((gain * c - (source.Format == 12 ? 88 : 100) * d - (source.Format == 12 ? 183 : 208) * e + 128) >> 8);
                    row[x * 4 + 2] = Clamp((gain * c + (source.Format == 12 ? 359 : 409) * e + 128) >> 8);
                    row[x * 4 + 3] = 255;
                }
            }
        }
        return new(source.Width, source.Height, bytes, ptsUs);
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
    private static bool ContainsVcl(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i + 3 < data.Length; i++)
            if (data[i] == 0 && data[i + 1] == 0 && (data[i + 2] == 1 || (data[i + 2] == 0 && data[i + 3] == 1)))
            {
                var index = i + (data[i + 2] == 1 ? 3 : 4);
                if (index < data.Length && (data[index] & 31) is >= 1 and <= 5) return true;
            }
        return false;
    }
    private static void Check(int result, string operation)
    {
        if (result < 0) throw new InvalidDataException($"{operation} non riuscita (FFmpeg {result}).");
    }
    private void ReleaseCodec()
    {
        if (packet != IntPtr.Zero) Native.av_packet_free(ref packet);
        if (frame != IntPtr.Zero) Native.av_frame_free(ref frame);
        if (context != IntPtr.Zero) Native.avcodec_free_context(ref context);
    }
    public void Dispose() { if (disposed) return; disposed = true; ReleaseCodec(); }

    [StructLayout(LayoutKind.Explicit)]
    private struct FrameHeader
    {
        [FieldOffset(0)] public IntPtr Y; [FieldOffset(8)] public IntPtr U; [FieldOffset(16)] public IntPtr V;
        [FieldOffset(64)] public int YStride; [FieldOffset(68)] public int UStride; [FieldOffset(72)] public int VStride;
        [FieldOffset(104)] public int Width; [FieldOffset(108)] public int Height; [FieldOffset(116)] public int Format;
    }

    private static class Native
    {
        [DllImport("avcodec-63.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern uint avcodec_version();
        [DllImport("avutil-61.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern uint avutil_version();
        [DllImport("avcodec-63.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr avcodec_find_decoder(int id);
        [DllImport("avcodec-63.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr avcodec_alloc_context3(IntPtr codec);
        [DllImport("avcodec-63.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int avcodec_open2(IntPtr context, IntPtr codec, IntPtr options);
        [DllImport("avcodec-63.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int avcodec_send_packet(IntPtr context, IntPtr packet);
        [DllImport("avcodec-63.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int avcodec_receive_frame(IntPtr context, IntPtr frame);
        [DllImport("avcodec-63.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern void avcodec_free_context(ref IntPtr context);
        [DllImport("avcodec-63.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr av_packet_alloc();
        [DllImport("avcodec-63.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int av_new_packet(IntPtr packet, int size);
        [DllImport("avcodec-63.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern void av_packet_unref(IntPtr packet);
        [DllImport("avcodec-63.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern void av_packet_free(ref IntPtr packet);
        [DllImport("avutil-61.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr av_frame_alloc();
        [DllImport("avutil-61.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern void av_frame_unref(IntPtr frame);
        [DllImport("avutil-61.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern void av_frame_free(ref IntPtr frame);
        [DllImport("avutil-61.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] internal static extern int av_opt_set_int(IntPtr obj, string name, long value, int searchFlags);
    }
}
