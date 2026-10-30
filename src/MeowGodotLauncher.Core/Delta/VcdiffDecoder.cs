using Microsoft.Win32.SafeHandles;
using ZstdSharp;

namespace MeowGodotLauncher.Core.Delta;

/// <summary>
/// Applies the `xpatch` data written by `generate_delta_patches.py`: a zstd stream of a VCDIFF patch (RFC 3284), as
/// made by `xdelta3 -S none -A -n`. Only what xdelta3 writes with those options is supported: the default code
/// table, windows copying from the old file, and no secondary compression. xdelta3's window checksums and
/// application header are skipped, since the applier checks the SHA-256 of the result anyway.
/// </summary>
public static class VcdiffDecoder
{
    private static ReadOnlySpan<byte> Magic => [0xD6, 0xC3, 0xC4, 0x00];

    // Header and window indicator bits. VCD_APPHEADER and VCD_ADLER32 are xdelta3 extensions.
    private const int VcdDecompress = 0x01, VcdCodeTable = 0x02, VcdAppHeader = 0x04;
    private const int VcdSource = 0x01, VcdTarget = 0x02, VcdAdler32 = 0x04;

    private const byte Noop = 0, Add = 1, Run = 2, Copy = 3;
    private const int NearSize = 4, SameSize = 3;

    // xdelta3 windows are at most 16 MiB, this only guards against allocating huge buffers for a corrupt patch.
    private const int MaxWindowSize = 64 << 20;

    private readonly record struct Instruction(byte Type, byte Size, byte Mode);

    /// <summary>The default code table of RFC 3284 section 5.6: two instructions per opcode.</summary>
    private static readonly (Instruction First, Instruction Second)[] CodeTable = BuildCodeTable();

    private static (Instruction, Instruction)[] BuildCodeTable()
    {
        var table = new List<(Instruction, Instruction)>(256);
        var none = new Instruction(Noop, 0, 0);
        table.Add((new Instruction(Run, 0, 0), none));
        for (int size = 0; size <= 17; size++)
            table.Add((new Instruction(Add, (byte)size, 0), none));
        for (byte mode = 0; mode < 9; mode++)
        {
            table.Add((new Instruction(Copy, 0, mode), none));
            for (int size = 4; size <= 18; size++)
                table.Add((new Instruction(Copy, (byte)size, mode), none));
        }
        for (byte mode = 0; mode < 6; mode++)
            for (int add = 1; add <= 4; add++)
                for (int copy = 4; copy <= 6; copy++)
                    table.Add((new Instruction(Add, (byte)add, 0), new Instruction(Copy, (byte)copy, mode)));
        for (byte mode = 6; mode < 9; mode++)
            for (int add = 1; add <= 4; add++)
                table.Add((new Instruction(Add, (byte)add, 0), new Instruction(Copy, 4, mode)));
        for (byte mode = 0; mode < 9; mode++)
            table.Add((new Instruction(Copy, 4, mode), new Instruction(Add, 1, 0)));
        return [.. table];
    }

    /// <summary>Writes the patched file to <paramref name="output"/>. Stops with an exception on malformed patches.</summary>
    public static void Apply(Stream patch, string oldPath, Stream output, long newSize, CancellationToken cancellationToken = default)
    {
        using var old = File.OpenHandle(oldPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long oldSize = RandomAccess.GetLength(old);
        using var data = new DecompressionStream(patch, leaveOpen: true);

        Span<byte> magic = stackalloc byte[4];
        data.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic))
            throw new InvalidDataException("Not a VCDIFF patch.");
        int header = ReadByte(data);
        if ((header & (VcdDecompress | VcdCodeTable)) != 0)
            throw new InvalidDataException("VCDIFF secondary compression and custom code tables aren't supported.");
        if ((header & VcdAppHeader) != 0)
            data.ReadExactly(new byte[CheckedLength(ReadVarint(data), MaxWindowSize)]);

        Span<byte> checksum = stackalloc byte[4];
        long written = 0;
        int indicator;
        while ((indicator = data.ReadByte()) >= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((indicator & VcdTarget) != 0)
                throw new InvalidDataException("VCDIFF windows copying from the target aren't supported.");
            long sourceLength = 0, sourcePosition = 0;
            if ((indicator & VcdSource) != 0)
            {
                sourceLength = ReadVarint(data);
                sourcePosition = ReadVarint(data);
                if (sourcePosition < 0 || sourceLength < 0 || sourcePosition > oldSize || sourceLength > oldSize - sourcePosition)
                    throw new InvalidDataException("Corrupt patch: source segment exceeds the old file.");
            }

            ReadVarint(data); // Length of the delta encoding.
            int windowLength = CheckedLength(ReadVarint(data), (int)Math.Min(MaxWindowSize, newSize - written));
            if (ReadByte(data) != 0)
                throw new InvalidDataException("VCDIFF compressed sections aren't supported.");
            int dataLength = CheckedLength(ReadVarint(data), MaxWindowSize);
            int instLength = CheckedLength(ReadVarint(data), MaxWindowSize);
            int addrLength = CheckedLength(ReadVarint(data), MaxWindowSize);
            if ((indicator & VcdAdler32) != 0)
                data.ReadExactly(checksum);

            var section = new byte[dataLength + instLength + addrLength];
            data.ReadExactly(section);
            var window = new Window(section, dataLength, instLength, old, sourcePosition, sourceLength, new byte[windowLength]);
            window.Decode();
            output.Write(window.Target);
            written += windowLength;
        }
        if (written != newSize)
            throw new InvalidDataException("Corrupt patch: it doesn't produce a file of the expected size.");
    }

    /// <summary>Decodes one window: the instructions read their data and addresses from their own sections.</summary>
    private sealed class Window
    {
        private readonly byte[] _section;
        private readonly int _dataLength;
        private readonly int _instLength;
        private readonly SafeFileHandle _old;
        private readonly long _sourcePosition;
        private readonly long _sourceLength;
        private readonly byte[] _target;

        public byte[] Target => _target;

        private int _data;
        private int _inst;
        private int _addr;
        private int _position;
        private readonly long[] _near = new long[NearSize];
        private int _nextNear;
        private readonly long[] _same = new long[SameSize * 256];

        public Window(byte[] section, int dataLength, int instLength, SafeFileHandle old, long sourcePosition,
            long sourceLength, byte[] target)
        {
            _section = section;
            _dataLength = dataLength;
            _instLength = instLength;
            _old = old;
            _sourcePosition = sourcePosition;
            _sourceLength = sourceLength;
            _target = target;
            _inst = dataLength;
            _addr = dataLength + instLength;
        }

        public void Decode()
        {
            int instEnd = _dataLength + _instLength;
            while (_inst < instEnd)
            {
                var (first, second) = CodeTable[_section[_inst++]];
                Execute(first, instEnd);
                Execute(second, instEnd);
            }
            if (_position != _target.Length || _data != _dataLength || _addr != _section.Length)
                throw new InvalidDataException("Corrupt patch: window sections don't match its length.");
        }

        private void Execute(Instruction instruction, int instEnd)
        {
            if (instruction.Type == Noop)
                return;
            int size = instruction.Size != 0
                ? instruction.Size
                : CheckedLength(ReadSectionVarint(ref _inst, instEnd), _target.Length);
            if (size > _target.Length - _position)
                throw new InvalidDataException("Corrupt patch: instruction exceeds the target window.");

            switch (instruction.Type)
            {
                case Add:
                    TakeData(size).CopyTo(_target.AsSpan(_position));
                    break;
                case Run:
                    _target.AsSpan(_position, size).Fill(TakeData(1)[0]);
                    break;
                case Copy:
                    long address = DecodeAddress(_sourceLength + _position, instruction.Mode);
                    if (address < _sourceLength)
                    {
                        // Copies can't span the source segment and the target window.
                        if (size > _sourceLength - address)
                            throw new InvalidDataException("Corrupt patch: copy exceeds the source segment.");
                        ReadOld(_sourcePosition + address, _target.AsSpan(_position, size));
                    }
                    else
                    {
                        // Copies from earlier in the target window can overlap their output, repeating it.
                        int from = (int)(address - _sourceLength);
                        for (int i = 0; i < size; i++)
                            _target[_position + i] = _target[from + i];
                    }
                    break;
            }
            _position += size;
        }

        private ReadOnlySpan<byte> TakeData(int size)
        {
            if (size > _dataLength - _data)
                throw new InvalidDataException("Corrupt patch: data section is too short.");
            var span = _section.AsSpan(_data, size);
            _data += size;
            return span;
        }

        private long DecodeAddress(long here, byte mode)
        {
            long address;
            if (mode == 0)
                address = ReadSectionVarint(ref _addr, _section.Length);
            else if (mode == 1)
                address = here - ReadSectionVarint(ref _addr, _section.Length);
            else if (mode < 2 + NearSize)
                address = _near[mode - 2] + ReadSectionVarint(ref _addr, _section.Length);
            else
            {
                if (_addr >= _section.Length)
                    throw new InvalidDataException("Corrupt patch: address section is too short.");
                address = _same[(mode - 2 - NearSize) * 256 + _section[_addr++]];
            }
            if (address < 0 || address >= here)
                throw new InvalidDataException("Corrupt patch: copy address out of range.");

            _near[_nextNear] = address;
            _nextNear = (_nextNear + 1) % NearSize;
            _same[address % (SameSize * 256)] = address;
            return address;
        }

        private long ReadSectionVarint(ref int position, int end)
        {
            long value = 0;
            for (int i = 0; i < 10; i++)
            {
                if (position >= end)
                    throw new InvalidDataException("Corrupt patch: truncated integer.");
                byte b = _section[position++];
                value = (value << 7) | (long)(b & 0x7F);
                if ((b & 0x80) == 0)
                    return value;
            }
            throw new InvalidDataException("Corrupt patch: integer too large.");
        }

        private void ReadOld(long offset, Span<byte> destination)
        {
            while (!destination.IsEmpty)
            {
                int read = RandomAccess.Read(_old, destination, offset);
                if (read <= 0)
                    throw new EndOfStreamException("Old file is shorter than expected.");
                destination = destination[read..];
                offset += read;
            }
        }
    }

    private static int ReadByte(Stream stream)
    {
        int b = stream.ReadByte();
        return b >= 0 ? b : throw new EndOfStreamException("Truncated VCDIFF patch.");
    }

    private static long ReadVarint(Stream stream)
    {
        long value = 0;
        for (int i = 0; i < 10; i++)
        {
            int b = ReadByte(stream);
            value = (value << 7) | (long)(b & 0x7F);
            if ((b & 0x80) == 0)
                return value;
        }
        throw new InvalidDataException("Corrupt patch: integer too large.");
    }

    private static int CheckedLength(long value, long max) =>
        value >= 0 && value <= max ? (int)value : throw new InvalidDataException("Corrupt patch: length out of range.");
}
