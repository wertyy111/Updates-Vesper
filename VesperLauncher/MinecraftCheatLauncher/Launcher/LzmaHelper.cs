using System;
using System.IO;

namespace VesperLauncher.Launcher
{
    internal static class LzmaHelper
    {
        public static void DecodeLzmaStream(Stream input, Stream output, long? expectedSizeOverride = null)
        {
            if (input is null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            if (output is null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            var properties = new byte[5];
            if (input.Read(properties, 0, properties.Length) != properties.Length)
            {
                throw new InvalidDataException("LZMA stream is too short.");
            }

            long outSize = 0;
            for (var i = 0; i < 8; i++)
            {
                var value = input.ReadByte();
                if (value < 0)
                {
                    throw new InvalidDataException("LZMA stream is too short.");
                }

                outSize |= ((long)(byte)value) << (8 * i);
            }

            if (outSize < 0 && expectedSizeOverride.HasValue && expectedSizeOverride.Value >= 0)
            {
                outSize = expectedSizeOverride.Value;
            }

            var decoder = new SevenZip.Compression.LZMA.Decoder();
            decoder.SetDecoderProperties(properties);
            var inSize = input.CanSeek ? input.Length - input.Position : -1;
            decoder.Code(input, output, inSize, outSize, null);
        }
    }
}

