#nullable disable
using System;
using System.IO;

namespace SevenZip
{
    internal class DataErrorException : ApplicationException
    {
        public DataErrorException() : base("Data Error") { }
    }

    internal class InvalidParamException : ApplicationException
    {
        public InvalidParamException() : base("Invalid Parameter") { }
    }

    public interface ICodeProgress
    {
        void SetProgress(long inSize, long outSize);
    }

    public interface ICoder
    {
        void Code(Stream inStream, Stream outStream, long inSize, long outSize, ICodeProgress progress);
    }

    public interface ISetDecoderProperties
    {
        void SetDecoderProperties(byte[] properties);
    }
}
#nullable restore
