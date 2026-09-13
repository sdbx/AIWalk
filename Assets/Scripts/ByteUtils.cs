using System;
using System.Buffers.Binary;

public static class ByteUtils
{
    public static byte[] FloatsToBytes(params float[] values)
    {
        byte[] bytes = new byte[values.Length * sizeof(float)];

        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(i * sizeof(float), sizeof(float)),
                BitConverter.SingleToInt32Bits(values[i]));
        }

        return bytes;
    }

    public static float[] BytesToFloats(byte[] source)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));

        if (source.Length % sizeof(float) != 0)
            throw new ArgumentException(
                "Source length must be a multiple of 4.",
                nameof(source));

        int count = source.Length / sizeof(float);
        float[] values = new float[count];

        for (int i = 0; i < count; i++)
        {
            int bits = BinaryPrimitives.ReadInt32LittleEndian(
                source.AsSpan(i * sizeof(float), sizeof(float)));

            values[i] = BitConverter.Int32BitsToSingle(bits);
        }

        return values;
    }
}