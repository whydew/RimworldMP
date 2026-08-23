using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Multiplayer.Common
{
    public class ByteWriter
    {
        private MemoryStream stream;
        public object? context;

        // Reusable scratch buffer for little-endian scalar writes. Per-instance, so it is safe as long as a single
        // ByteWriter is not written from multiple threads at once (already the case: each packet/opinion is serialized
        // on one thread). Replaces the per-call BitConverter.GetBytes() heap allocation.
        private readonly byte[] numBuffer = new byte[8];

        public int Position => (int)stream.Position;

        public ByteWriter(int capacity = 0)
        {
            stream = new MemoryStream(capacity);
        }

        public virtual void WriteByte(byte val) => stream.WriteByte(val);

        public virtual void WriteSByte(sbyte val) => stream.WriteByte((byte)val);

        // All multi-byte scalars are written little-endian to stay byte-for-byte identical to the previous
        // BitConverter.GetBytes() output on the x64 (little-endian) platforms RimWorld runs on. This preserves wire
        // and replay compatibility.
        public virtual void WriteShort(short val) => WriteLE((ushort)val, 2);

        public virtual void WriteUShort(ushort val) => WriteLE(val, 2);

        public virtual void WriteInt32(int val) => WriteLE((uint)val, 4);

        public virtual void WriteUInt32(uint val) => WriteLE(val, 4);

        public virtual void WriteLong(long val) => WriteLE((ulong)val, 8);

        public virtual void WriteULong(ulong val) => WriteLE(val, 8);

        public virtual void WriteFloat(float val)
        {
            uint bits;
            unsafe { bits = *(uint*)&val; }
            WriteLE(bits, 4);
        }

        public virtual void WriteDouble(double val) => WriteLE((ulong)BitConverter.DoubleToInt64Bits(val), 8);

        // Writes the low `count` bytes of `value` in little-endian order via the shared scratch buffer.
        // `count` is only ever 2, 4 or 8, and the buffer is 8 bytes, so the indexing is always in range.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteLE(ulong value, int count)
        {
            var b = numBuffer;
            b[0] = (byte)value;
            b[1] = (byte)(value >> 8);
            if (count > 2)
            {
                b[2] = (byte)(value >> 16);
                b[3] = (byte)(value >> 24);
            }
            if (count > 4)
            {
                b[4] = (byte)(value >> 32);
                b[5] = (byte)(value >> 40);
                b[6] = (byte)(value >> 48);
                b[7] = (byte)(value >> 56);
            }
            stream.Write(b, 0, count);
        }

        public virtual void WriteBool(bool val) => stream.WriteByte(val ? (byte)1 : (byte)0);

        public virtual void WritePrefixedBytes(byte[]? bytes)
        {
            if (bytes == null)
            {
                WriteInt32(-1);
                return;
            }

            WriteInt32(bytes.Length);
            WriteRaw(bytes);
        }

        public virtual void WritePrefixedInts(IList<int> ints)
        {
            WriteInt32(ints.Count);
            foreach (var @int in ints)
                WriteInt32(@int);
        }

        public virtual void WritePrefixedUInts(IList<uint> ints)
        {
            WriteInt32(ints.Count);
            foreach (var @int in ints)
                WriteUInt32(@int);
        }

        public virtual void WriteRaw(byte[] bytes)
        {
            stream.Write(bytes, 0, bytes.Length);
        }

        public virtual void WriteFrom(byte[] buffer, int offset, int length)
        {
            stream.Write(buffer, offset, length);
        }

        public virtual ByteWriter WriteString(string? s)
        {
            WritePrefixedBytes(s == null ? null : Encoding.UTF8.GetBytes(s));
            return this;
        }

        public virtual void WriteEnum<T>(T value) where T : Enum
        {
            Type type = Enum.GetUnderlyingType(typeof(T) == typeof(Enum) ? value.GetType() : typeof(T));

            if (type == typeof(byte))
            {
                WriteByte(Convert.ToByte(value));
            }
            else if (type == typeof(sbyte))
            {
                WriteSByte(Convert.ToSByte(value));
            }
            else if (type == typeof(short))
            {
                WriteShort(Convert.ToInt16(value));
            }
            else if (type == typeof(ushort))
            {
                WriteUShort(Convert.ToUInt16(value));
            }
            else if (type == typeof(int))
            {
                WriteInt32(Convert.ToInt32(value));
            }
            else if (type == typeof(uint))
            {
                WriteUInt32(Convert.ToUInt32(value));
            }
            else if (type == typeof(long) || type == typeof(IntPtr))
            {
                WriteLong(Convert.ToInt64(value));
            }
            else if (type == typeof(ulong) || type == typeof(UIntPtr))
            {
                WriteULong(Convert.ToUInt64(value));
            }
            else
            {
                ServerLog.Error($"MP ByteWriter.WriteEnum: Unknown type {type}");
            }
        }
        private void Write(object obj)
        {
            if (obj is int @int)
            {
                WriteInt32(@int);
            }
            else if (obj is ushort @ushort)
            {
                WriteUShort(@ushort);
            }
            else if (obj is short @short)
            {
                WriteShort(@short);
            }
            else if (obj is bool @bool)
            {
                WriteBool(@bool);
            }
            else if (obj is long @long)
            {
                WriteLong(@long);
            }
            else if (obj is ulong @ulong)
            {
                WriteULong(@ulong);
            }
            else if (obj is byte @byte)
            {
                WriteByte(@byte);
            }
            else if (obj is float @float)
            {
                WriteFloat(@float);
            }
            else if (obj is double @double)
            {
                WriteDouble(@double);
            }
            else if (obj is byte[] bytes)
            {
                WritePrefixedBytes(bytes);
            }
            else if (obj is Enum enumObj)
            {
                WriteEnum(enumObj);
            }
            else if (obj is string @string)
            {
                WriteString(@string);
            }
            else if (obj is Array arr)
            {
                Write(arr.Length);
                foreach (object o in arr)
                    Write(o);
            }
            else if (obj is IList list)
            {
                Write(list.Count);
                foreach (object o in list)
                    Write(o);
            }
            else if (obj is ITuple tuple)
            {
                for (int i = 0; i < tuple.Length; i++)
                    Write(tuple[i]);
            }
            else
            {
                ServerLog.Error($"MP ByteWriter.Write: Unknown type {obj.GetType()}");
            }
        }

        public byte[] ToArray()
        {
            return stream.ToArray();
        }

        /// <summary>
        /// Writes all objects in the order given and returns the resulting bytes.
        /// </summary>
        public static byte[] GetBytes(params object[] data)
        {
            var writer = new ByteWriter();
            foreach (object o in data)
                writer.Write(o);
            return writer.ToArray();
        }

        public void SetLength(long value)
        {
            stream.SetLength(value);
        }
    }

    public class WriterException : Exception
    {
        public WriterException(string msg) : base(msg)
        {
        }
    }

}