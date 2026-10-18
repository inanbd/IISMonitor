using System.Buffers.Binary;

namespace IISMonitor.Website.Site;

/// <summary>Reads the pixel size of a PNG, JPEG or WebP file from its header.</summary>
public static class ImageSize
{
    public static (int Width, int Height)? Read(Stream stream)
    {
        Span<byte> head = stackalloc byte[30];
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        head = head[..read];

        // PNG: signature, then the IHDR chunk with width and height.
        if (read >= 24 && head[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return (BinaryPrimitives.ReadInt32BigEndian(head[16..]), BinaryPrimitives.ReadInt32BigEndian(head[20..]));

        // WebP: RIFF container with a VP8, VP8L or VP8X chunk.
        if (read >= 30 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
        {
            var chunk = head[12..16];
            if (chunk.SequenceEqual("VP8 "u8))
                return (BinaryPrimitives.ReadUInt16LittleEndian(head[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(head[28..]) & 0x3FFF);
            if (chunk.SequenceEqual("VP8L"u8))
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(head[21..]);
                return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
            }

            if (chunk.SequenceEqual("VP8X"u8))
                return (1 + (head[24] | head[25] << 8 | head[26] << 16), 1 + (head[27] | head[28] << 8 | head[29] << 16));
            return null;
        }

        // JPEG: walk the segments to the first start-of-frame marker.
        if (read >= 2 && head[0] == 0xFF && head[1] == 0xD8)
        {
            stream.Position = 2;
            Span<byte> segment = stackalloc byte[9];
            while (stream.ReadAtLeast(segment[..4], 4, throwOnEndOfStream: false) == 4)
            {
                if (segment[0] != 0xFF)
                    return null;
                var marker = segment[1];
                var length = BinaryPrimitives.ReadUInt16BigEndian(segment[2..]);
                var isFrame = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
                if (isFrame)
                {
                    if (stream.ReadAtLeast(segment[..5], 5, throwOnEndOfStream: false) != 5)
                        return null;
                    return (BinaryPrimitives.ReadUInt16BigEndian(segment[3..]), BinaryPrimitives.ReadUInt16BigEndian(segment[1..]));
                }

                stream.Seek(length - 2, SeekOrigin.Current);
            }
        }

        return null;
    }
}
