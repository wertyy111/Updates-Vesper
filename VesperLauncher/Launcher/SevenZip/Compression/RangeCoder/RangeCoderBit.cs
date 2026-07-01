#nullable disable
using System;

namespace SevenZip.Compression.RangeCoder
{
    internal struct BitEncoder
    {
        public const int kNumBitModelTotalBits = 11;
        public const uint kBitModelTotal = (1 << kNumBitModelTotalBits);
        private const int kNumMoveBits = 5;
        private const int kNumMoveReducingBits = 2;
        public const int kNumBitPriceShiftBits = 6;

        private uint _prob;

        public void Init()
        {
            _prob = kBitModelTotal >> 1;
        }

        public void UpdateModel(uint symbol)
        {
            if (symbol == 0)
            {
                _prob += (kBitModelTotal - _prob) >> kNumMoveBits;
            }
            else
            {
                _prob -= _prob >> kNumMoveBits;
            }
        }

        public void Encode(Encoder encoder, uint symbol)
        {
            uint newBound = (encoder.Range >> kNumBitModelTotalBits) * _prob;
            if (symbol == 0)
            {
                encoder.Range = newBound;
                _prob += (kBitModelTotal - _prob) >> kNumMoveBits;
            }
            else
            {
                encoder.Low += newBound;
                encoder.Range -= newBound;
                _prob -= _prob >> kNumMoveBits;
            }

            if (encoder.Range < Encoder.kTopValue)
            {
                encoder.Range <<= 8;
                encoder.ShiftLow();
            }
        }

        private static readonly uint[] ProbPrices = new uint[kBitModelTotal >> kNumMoveReducingBits];

        static BitEncoder()
        {
            const int kNumBits = kNumBitModelTotalBits - kNumMoveReducingBits;
            for (var i = kNumBits - 1; i >= 0; i--)
            {
                uint start = (uint)1 << (kNumBits - i - 1);
                uint end = (uint)1 << (kNumBits - i);
                for (uint j = start; j < end; j++)
                {
                    ProbPrices[j] = ((uint)i << kNumBitPriceShiftBits) +
                                    (((end - j) << kNumBitPriceShiftBits) >> (kNumBits - i - 1));
                }
            }
        }

        public uint GetPrice(uint symbol)
        {
            return ProbPrices[((( _prob - symbol) ^ (-(int)symbol)) & (kBitModelTotal - 1)) >> kNumMoveReducingBits];
        }

        public uint GetPrice0()
        {
            return ProbPrices[_prob >> kNumMoveReducingBits];
        }

        public uint GetPrice1()
        {
            return ProbPrices[(kBitModelTotal - _prob) >> kNumMoveReducingBits];
        }
    }

    internal struct BitDecoder
    {
        public const int kNumBitModelTotalBits = 11;
        public const uint kBitModelTotal = (1 << kNumBitModelTotalBits);
        private const int kNumMoveBits = 5;

        private uint _prob;

        public void UpdateModel(int numMoveBits, uint symbol)
        {
            if (symbol == 0)
            {
                _prob += (kBitModelTotal - _prob) >> numMoveBits;
            }
            else
            {
                _prob -= _prob >> numMoveBits;
            }
        }

        public void Init()
        {
            _prob = kBitModelTotal >> 1;
        }

        public uint Decode(Decoder rangeDecoder)
        {
            uint newBound = (rangeDecoder.Range >> kNumBitModelTotalBits) * _prob;
            if (rangeDecoder.Code < newBound)
            {
                rangeDecoder.Range = newBound;
                _prob += (kBitModelTotal - _prob) >> kNumMoveBits;
                if (rangeDecoder.Range < Decoder.kTopValue)
                {
                    rangeDecoder.Code = (rangeDecoder.Code << 8) | (byte)rangeDecoder.Stream.ReadByte();
                    rangeDecoder.Range <<= 8;
                }

                return 0;
            }

            rangeDecoder.Range -= newBound;
            rangeDecoder.Code -= newBound;
            _prob -= _prob >> kNumMoveBits;
            if (rangeDecoder.Range < Decoder.kTopValue)
            {
                rangeDecoder.Code = (rangeDecoder.Code << 8) | (byte)rangeDecoder.Stream.ReadByte();
                rangeDecoder.Range <<= 8;
            }

            return 1;
        }
    }
}
#nullable restore
