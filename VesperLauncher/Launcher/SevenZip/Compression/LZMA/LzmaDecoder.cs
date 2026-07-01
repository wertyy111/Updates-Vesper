#nullable disable
using System;
using SevenZip.Compression.LZ;
using SevenZip.Compression.RangeCoder;

namespace SevenZip.Compression.LZMA
{
    public class Decoder : SevenZip.ICoder, SevenZip.ISetDecoderProperties
    {
        class LenDecoder
        {
            private BitDecoder _choice = new BitDecoder();
            private BitDecoder _choice2 = new BitDecoder();
            private BitTreeDecoder[] _lowCoder = new BitTreeDecoder[Base.kNumPosStatesMax];
            private BitTreeDecoder[] _midCoder = new BitTreeDecoder[Base.kNumPosStatesMax];
            private BitTreeDecoder _highCoder = new BitTreeDecoder(Base.kNumHighLenBits);
            private uint _numPosStates;

            public void Create(uint numPosStates)
            {
                for (uint posState = _numPosStates; posState < numPosStates; posState++)
                {
                    _lowCoder[posState] = new BitTreeDecoder(Base.kNumLowLenBits);
                    _midCoder[posState] = new BitTreeDecoder(Base.kNumMidLenBits);
                }

                _numPosStates = numPosStates;
            }

            public void Init()
            {
                _choice.Init();
                for (uint posState = 0; posState < _numPosStates; posState++)
                {
                    _lowCoder[posState].Init();
                    _midCoder[posState].Init();
                }

                _choice2.Init();
                _highCoder.Init();
            }

            public uint Decode(RangeCoder.Decoder rangeDecoder, uint posState)
            {
                if (_choice.Decode(rangeDecoder) == 0)
                {
                    return _lowCoder[posState].Decode(rangeDecoder);
                }

                uint symbol = Base.kNumLowLenSymbols;
                if (_choice2.Decode(rangeDecoder) == 0)
                {
                    symbol += _midCoder[posState].Decode(rangeDecoder);
                }
                else
                {
                    symbol += Base.kNumMidLenSymbols;
                    symbol += _highCoder.Decode(rangeDecoder);
                }

                return symbol;
            }
        }

        class LiteralDecoder
        {
            struct Decoder2
            {
                private BitDecoder[] _decoders;

                public void Create()
                {
                    _decoders = new BitDecoder[0x300];
                }

                public void Init()
                {
                    for (var i = 0; i < 0x300; i++)
                    {
                        _decoders[i].Init();
                    }
                }

                public byte DecodeNormal(RangeCoder.Decoder rangeDecoder)
                {
                    uint symbol = 1;
                    do
                    {
                        symbol = (symbol << 1) | _decoders[symbol].Decode(rangeDecoder);
                    }
                    while (symbol < 0x100);

                    return (byte)symbol;
                }

                public byte DecodeWithMatchByte(RangeCoder.Decoder rangeDecoder, byte matchByte)
                {
                    uint symbol = 1;
                    do
                    {
                        uint matchBit = (uint)(matchByte >> 7) & 1;
                        matchByte <<= 1;
                        uint bit = _decoders[((1 + matchBit) << 8) + symbol].Decode(rangeDecoder);
                        symbol = (symbol << 1) | bit;
                        if (matchBit != bit)
                        {
                            while (symbol < 0x100)
                            {
                                symbol = (symbol << 1) | _decoders[symbol].Decode(rangeDecoder);
                            }

                            break;
                        }
                    }
                    while (symbol < 0x100);

                    return (byte)symbol;
                }
            }

            private Decoder2[] _coders;
            private int _numPrevBits;
            private int _numPosBits;
            private uint _posMask;

            public void Create(int numPosBits, int numPrevBits)
            {
                if (_coders != null && _numPrevBits == numPrevBits && _numPosBits == numPosBits)
                {
                    return;
                }

                _numPosBits = numPosBits;
                _posMask = ((uint)1 << numPosBits) - 1;
                _numPrevBits = numPrevBits;

                uint numStates = (uint)1 << (_numPrevBits + _numPosBits);
                _coders = new Decoder2[numStates];
                for (uint i = 0; i < numStates; i++)
                {
                    _coders[i].Create();
                }
            }

            public void Init()
            {
                uint numStates = (uint)1 << (_numPrevBits + _numPosBits);
                for (uint i = 0; i < numStates; i++)
                {
                    _coders[i].Init();
                }
            }

            private uint GetState(uint pos, byte prevByte)
            {
                return ((pos & _posMask) << _numPrevBits) + (uint)(prevByte >> (8 - _numPrevBits));
            }

            public byte DecodeNormal(RangeCoder.Decoder rangeDecoder, uint pos, byte prevByte)
            {
                return _coders[GetState(pos, prevByte)].DecodeNormal(rangeDecoder);
            }

            public byte DecodeWithMatchByte(RangeCoder.Decoder rangeDecoder, uint pos, byte prevByte, byte matchByte)
            {
                return _coders[GetState(pos, prevByte)].DecodeWithMatchByte(rangeDecoder, matchByte);
            }
        }

        private OutWindow _outWindow = new OutWindow();
        private RangeCoder.Decoder _rangeDecoder = new RangeCoder.Decoder();

        private BitDecoder[] _isMatchDecoders = new BitDecoder[Base.kNumStates << Base.kNumPosStatesBitsMax];
        private BitDecoder[] _isRepDecoders = new BitDecoder[Base.kNumStates];
        private BitDecoder[] _isRepG0Decoders = new BitDecoder[Base.kNumStates];
        private BitDecoder[] _isRepG1Decoders = new BitDecoder[Base.kNumStates];
        private BitDecoder[] _isRepG2Decoders = new BitDecoder[Base.kNumStates];
        private BitDecoder[] _isRep0LongDecoders = new BitDecoder[Base.kNumStates << Base.kNumPosStatesBitsMax];

        private BitTreeDecoder[] _posSlotDecoder = new BitTreeDecoder[Base.kNumLenToPosStates];
        private BitDecoder[] _posDecoders = new BitDecoder[Base.kNumFullDistances - Base.kEndPosModelIndex];
        private BitTreeDecoder _posAlignDecoder = new BitTreeDecoder(Base.kNumAlignBits);

        private LenDecoder _lenDecoder = new LenDecoder();
        private LenDecoder _repLenDecoder = new LenDecoder();
        private LiteralDecoder _literalDecoder = new LiteralDecoder();

        private uint _dictionarySize;
        private uint _dictionarySizeCheck;
        private uint _posStateMask;

        public Decoder()
        {
            _dictionarySize = 0xFFFFFFFF;
            for (var i = 0; i < Base.kNumLenToPosStates; i++)
            {
                _posSlotDecoder[i] = new BitTreeDecoder(Base.kNumPosSlotBits);
            }
        }

        private void SetDictionarySize(uint dictionarySize)
        {
            if (_dictionarySize != dictionarySize)
            {
                _dictionarySize = dictionarySize;
                _dictionarySizeCheck = Math.Max(_dictionarySize, 1);
                uint blockSize = Math.Max(_dictionarySizeCheck, (uint)(1 << 12));
                _outWindow.Create(blockSize);
            }
        }

        private void SetLiteralProperties(int lp, int lc)
        {
            if (lp > 8 || lc > 8)
            {
                throw new SevenZip.InvalidParamException();
            }

            _literalDecoder.Create(lp, lc);
        }

        private void SetPosBitsProperties(int pb)
        {
            if (pb > Base.kNumPosStatesBitsMax)
            {
                throw new SevenZip.InvalidParamException();
            }

            uint numPosStates = (uint)1 << pb;
            _lenDecoder.Create(numPosStates);
            _repLenDecoder.Create(numPosStates);
            _posStateMask = numPosStates - 1;
        }

        private void Init(System.IO.Stream inStream, System.IO.Stream outStream)
        {
            _rangeDecoder.Init(inStream);
            _outWindow.Init(outStream, solid: false);

            for (uint i = 0; i < Base.kNumStates; i++)
            {
                for (uint j = 0; j <= _posStateMask; j++)
                {
                    uint index = (i << Base.kNumPosStatesBitsMax) + j;
                    _isMatchDecoders[index].Init();
                    _isRep0LongDecoders[index].Init();
                }

                _isRepDecoders[i].Init();
                _isRepG0Decoders[i].Init();
                _isRepG1Decoders[i].Init();
                _isRepG2Decoders[i].Init();
            }

            _literalDecoder.Init();
            for (uint i = 0; i < Base.kNumLenToPosStates; i++)
            {
                _posSlotDecoder[i].Init();
            }

            for (uint i = 0; i < Base.kNumFullDistances - Base.kEndPosModelIndex; i++)
            {
                _posDecoders[i].Init();
            }

            _lenDecoder.Init();
            _repLenDecoder.Init();
            _posAlignDecoder.Init();
        }

        public void Code(System.IO.Stream inStream, System.IO.Stream outStream, long inSize, long outSize, SevenZip.ICodeProgress progress)
        {
            Init(inStream, outStream);

            Base.State state = new Base.State();
            state.Init();

            uint rep0 = 0;
            uint rep1 = 0;
            uint rep2 = 0;
            uint rep3 = 0;
            ulong nowPos64 = 0;
            ulong outSize64 = (ulong)outSize;

            if (nowPos64 < outSize64)
            {
                if (_isMatchDecoders[state.Index << Base.kNumPosStatesBitsMax].Decode(_rangeDecoder) != 0)
                {
                    throw new SevenZip.DataErrorException();
                }

                state.UpdateChar();
                byte b = _literalDecoder.DecodeNormal(_rangeDecoder, 0, 0);
                _outWindow.PutByte(b);
                nowPos64++;
            }

            while (nowPos64 < outSize64)
            {
                uint posState = (uint)nowPos64 & _posStateMask;
                if (_isMatchDecoders[(state.Index << Base.kNumPosStatesBitsMax) + posState].Decode(_rangeDecoder) == 0)
                {
                    byte b;
                    byte prevByte = _outWindow.GetByte(0);
                    if (!state.IsCharState())
                    {
                        b = _literalDecoder.DecodeWithMatchByte(_rangeDecoder, (uint)nowPos64, prevByte, _outWindow.GetByte(rep0));
                    }
                    else
                    {
                        b = _literalDecoder.DecodeNormal(_rangeDecoder, (uint)nowPos64, prevByte);
                    }

                    _outWindow.PutByte(b);
                    state.UpdateChar();
                    nowPos64++;
                }
                else
                {
                    uint len;
                    if (_isRepDecoders[state.Index].Decode(_rangeDecoder) == 1)
                    {
                        if (_isRepG0Decoders[state.Index].Decode(_rangeDecoder) == 0)
                        {
                            if (_isRep0LongDecoders[(state.Index << Base.kNumPosStatesBitsMax) + posState].Decode(_rangeDecoder) == 0)
                            {
                                state.UpdateShortRep();
                                _outWindow.PutByte(_outWindow.GetByte(rep0));
                                nowPos64++;
                                continue;
                            }
                        }
                        else
                        {
                            uint distance;
                            if (_isRepG1Decoders[state.Index].Decode(_rangeDecoder) == 0)
                            {
                                distance = rep1;
                            }
                            else
                            {
                                if (_isRepG2Decoders[state.Index].Decode(_rangeDecoder) == 0)
                                {
                                    distance = rep2;
                                }
                                else
                                {
                                    distance = rep3;
                                    rep3 = rep2;
                                }

                                rep2 = rep1;
                            }

                            rep1 = rep0;
                            rep0 = distance;
                        }

                        len = _repLenDecoder.Decode(_rangeDecoder, posState) + Base.kMatchMinLen;
                        state.UpdateRep();
                    }
                    else
                    {
                        rep3 = rep2;
                        rep2 = rep1;
                        rep1 = rep0;
                        len = Base.kMatchMinLen + _lenDecoder.Decode(_rangeDecoder, posState);
                        state.UpdateMatch();
                        uint posSlot = _posSlotDecoder[Base.GetLenToPosState(len)].Decode(_rangeDecoder);
                        if (posSlot >= Base.kStartPosModelIndex)
                        {
                            int numDirectBits = (int)((posSlot >> 1) - 1);
                            rep0 = (2 | (posSlot & 1)) << numDirectBits;
                            if (posSlot < Base.kEndPosModelIndex)
                            {
                                rep0 += BitTreeDecoder.ReverseDecode(_posDecoders, rep0 - posSlot - 1, _rangeDecoder, numDirectBits);
                            }
                            else
                            {
                                rep0 += _rangeDecoder.DecodeDirectBits(numDirectBits - Base.kNumAlignBits) << Base.kNumAlignBits;
                                rep0 += _posAlignDecoder.ReverseDecode(_rangeDecoder);
                            }
                        }
                        else
                        {
                            rep0 = posSlot;
                        }
                    }

                    if (rep0 >= _outWindow.TrainSize + nowPos64 || rep0 >= _dictionarySizeCheck)
                    {
                        if (rep0 == 0xFFFFFFFF)
                        {
                            break;
                        }

                        throw new SevenZip.DataErrorException();
                    }

                    _outWindow.CopyBlock(rep0, len);
                    nowPos64 += len;
                }

                progress?.SetProgress(inSize < 0 ? -1 : inSize, (long)nowPos64);
            }

            _outWindow.Flush();
            _outWindow.ReleaseStream();
            _rangeDecoder.ReleaseStream();
        }

        public void SetDecoderProperties(byte[] properties)
        {
            if (properties.Length < 5)
            {
                throw new SevenZip.InvalidParamException();
            }

            int lc = properties[0] % 9;
            int remainder = properties[0] / 9;
            int lp = remainder % 5;
            int pb = remainder / 5;
            if (pb > Base.kNumPosStatesBitsMax)
            {
                throw new SevenZip.InvalidParamException();
            }

            uint dictionarySize = 0;
            for (var i = 0; i < 4; i++)
            {
                dictionarySize += (uint)(properties[1 + i]) << (i * 8);
            }

            SetDictionarySize(dictionarySize);
            SetLiteralProperties(lp, lc);
            SetPosBitsProperties(pb);
        }
    }
}
#nullable restore
