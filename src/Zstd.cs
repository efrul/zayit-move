// Reading the library bundle (seforim_bundle.tar.zst: one Zstandard frame of a tar) as a stream: a Zstandard
// decoder (RFC 8878) and a tar reader. C# 5 and the .NET Framework's own library only, so Windows's csc.exe builds it.
// The bit reader (BackBits) and the FSE table builder follow Zstandard's reference decoder (lib/common/bitstream.h,
// lib/common/fse_decompress.c), Copyright (c) Meta Platforms, Inc. and affiliates, under its BSD license: see
// assets/zstd-LICENSE.txt.
//
// ZstdDecodeStream: frames one after another, skippable frames, raw / RLE / compressed blocks, Huffman literals
// (1 or 4 streams, treeless reuse), FSE sequences (predefined, RLE, compressed and repeat modes), repeat offsets,
// windows up to 1 GiB, the XXH64 content checksum. No dictionaries. Memory: 1.25 x the frame's window (160 MiB for
// the bundle's 128 MiB) plus about 1.5 MiB. After an exception the stream is unusable.
//
// TarReader: ustar, pax and GNU headers, front to back.
using System;
using System.IO;
using System.Text;

sealed class ZstdDecodeStream : Stream
{
    const uint Magic = 0xFD2FB528;
    const int MaxBlock = 1 << 17;
    // A byte[] holds at most 0x7FFFFFC7 bytes on .NET Framework; the window plus its slack must fit.
    const int MaxWindow = 1 << 30;

    readonly Stream src;
    readonly bool verifyChecksum;

    readonly byte[] ib = new byte[1 << 20];
    int ibPos, ibLen;

    // The window buffer: history of up to one window, then new output. When there is no room for another block,
    // the last window of history is moved back to the start (every windowSize/4 bytes of output).
    byte[] win = new byte[0];
    int winPos, readPos, windowSize, blockMax;
    long frameOut, contentSize, totalOut, totalIn;
    bool inFrame, hasChecksum, broken;
    readonly XXH64 hash = new XXH64();

    readonly byte[] blk = new byte[MaxBlock + 8];
    readonly byte[] lit = new byte[MaxBlock + 16];

    // Huffman decoding table: entry = symbol | bits << 8, indexed by the next hufLog bits.
    readonly ushort[] huf = new ushort[1 << 11];
    int hufLog;

    readonly Fse llOwn = new Fse(9), ofOwn = new Fse(8), mlOwn = new Fse(9);
    Fse llT, ofT, mlT;
    int rep1, rep2, rep3;
    readonly short[] norm = new short[64];


    public ZstdDecodeStream(Stream source) : this(source, true) { }
    public ZstdDecodeStream(Stream source, bool verifyChecksum) { src = source; this.verifyChecksum = verifyChecksum; }

    // Bytes produced so far.
    public long TotalOut { get { return totalOut; } }
    // Compressed bytes taken from the source so far (including what sits in the input buffer).
    public long TotalIn { get { return totalIn; } }
    // Window of the current (or last) frame.
    public int WindowSize { get { return windowSize; } }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (broken) throw new InvalidOperationException("zstd: the stream failed earlier");
        if (count <= 0) return 0;
        try
        {
            while (readPos == winPos)
            {
                if (!inFrame) { if (!StartFrame()) return 0; }
                else DecodeBlock();
            }
        }
        catch { broken = true; throw; }
        int n = Math.Min(count, winPos - readPos);
        Buffer.BlockCopy(win, readPos, buffer, offset, n);
        readPos += n;
        return n;
    }

    // ---- input ----

    int Fill(byte[] dst, int off, int n)
    {
        int got = 0;
        while (got < n)
        {
            if (ibPos == ibLen)
            {
                ibPos = 0;
                ibLen = src.Read(ib, 0, ib.Length);
                if (ibLen <= 0) { ibLen = 0; return got; }
                totalIn += ibLen;
            }
            int k = Math.Min(n - got, ibLen - ibPos);
            Buffer.BlockCopy(ib, ibPos, dst, off + got, k);
            ibPos += k; got += k;
        }
        return got;
    }

    void ReadExact(byte[] dst, int off, int n)
    {
        if (Fill(dst, off, n) != n) throw new EndOfStreamException("zstd: the compressed data ends too soon");
    }

    readonly byte[] tmp = new byte[16];

    uint ReadLE(int n)
    {
        ReadExact(tmp, 0, n);
        uint v = 0;
        for (int i = n - 1; i >= 0; i--) v = (v << 8) | tmp[i];
        return v;
    }

    static InvalidDataException Bad(string what) { return new InvalidDataException("zstd: " + what); }

    // ---- frames ----

    bool StartFrame()
    {
        for (;;)
        {
            int got = Fill(tmp, 0, 4);
            if (got == 0) return false;
            if (got != 4) throw Bad("truncated frame header");
            uint magic = (uint)(tmp[0] | tmp[1] << 8 | tmp[2] << 16 | tmp[3] << 24);
            if ((magic & 0xFFFFFFF0) == 0x184D2A50)
            {
                long skip = ReadLE(4);
                byte[] junk = new byte[4096];
                while (skip > 0) { int k = (int)Math.Min(skip, junk.Length); ReadExact(junk, 0, k); skip -= k; }
                continue;
            }
            if (magic != Magic) throw Bad("not a zstd frame (magic " + magic.ToString("X8") + ")");
            break;
        }
        int fhd = (int)ReadLE(1);
        int fcsFlag = fhd >> 6;
        bool single = (fhd & 0x20) != 0;
        if ((fhd & 0x08) != 0) throw Bad("reserved bit set in the frame header");
        hasChecksum = (fhd & 0x04) != 0;
        int didFlag = fhd & 3;
        long ws = 0;
        if (!single)
        {
            int wd = (int)ReadLE(1);
            int wlog = 10 + (wd >> 3);
            long b = 1L << wlog;
            ws = b + (b >> 3) * (wd & 7);
        }
        if (didFlag != 0)
        {
            uint did = ReadLE(didFlag == 3 ? 4 : didFlag);
            if (did != 0) throw new NotSupportedException("zstd: frames that need a dictionary are not supported");
        }
        int fcsSize = fcsFlag == 0 ? (single ? 1 : 0) : 1 << fcsFlag;
        contentSize = -1;
        if (fcsSize > 0)
        {
            ReadExact(tmp, 0, fcsSize);
            long v = 0;
            for (int i = fcsSize - 1; i >= 0; i--) v = (v << 8) | tmp[i];
            if (fcsSize == 2) v += 256;
            if (v < 0) throw Bad("frame content size out of range");
            contentSize = v;
        }
        if (single) ws = contentSize;
        if (ws > MaxWindow) throw new NotSupportedException("zstd: window of " + ws + " bytes is larger than this decoder takes (" + MaxWindow + ")");
        windowSize = (int)ws;
        blockMax = Math.Min(windowSize, MaxBlock);
        long slack = Math.Max((long)windowSize / 4, 4L * MaxBlock);
        int cap = (int)Math.Min((long)windowSize + slack, 0x7FFFFFC7L);
        if (win.Length < cap) { win = null; win = new byte[cap]; }
        winPos = readPos = 0;
        frameOut = 0;
        rep1 = 1; rep2 = 4; rep3 = 8;
        hufLog = 0;
        llT = ofT = mlT = null;
        hash.Reset();
        inFrame = true;
        return true;
    }

    void DecodeBlock()
    {
        uint h = ReadLE(3);
        bool last = (h & 1) != 0;
        int type = (int)(h >> 1) & 3;
        int size = (int)(h >> 3);
        if (winPos + MaxBlock > win.Length)
        {
            int keep = Math.Min(winPos, windowSize);
            Buffer.BlockCopy(win, winPos - keep, win, 0, keep);
            winPos = readPos = keep;
        }
        int start = winPos;
        switch (type)
        {
            case 0:
                if (size > blockMax) throw Bad("raw block too large");
                ReadExact(win, winPos, size);
                winPos += size;
                break;
            case 1:
                {
                    if (size > blockMax) throw Bad("RLE block too large");
                    byte b = (byte)ReadLE(1);
                    for (int i = 0; i < size; i++) win[winPos + i] = b;
                    winPos += size;
                    break;
                }
            case 2:
                if (size > blockMax) throw Bad("compressed block too large");
                ReadExact(blk, 0, size);
                DecodeCompressed(size);
                if (winPos - start > blockMax) throw Bad("block decodes to more than the block maximum");
                break;
            default:
                throw Bad("reserved block type");
        }
        int produced = winPos - start;
        if (hasChecksum && verifyChecksum) hash.Update(win, start, produced);
        frameOut += produced;
        totalOut += produced;
        if (contentSize >= 0 && frameOut > contentSize) throw Bad("frame larger than its declared content size");
        if (last)
        {
            if (contentSize >= 0 && frameOut != contentSize) throw Bad("frame content size mismatch");
            if (hasChecksum)
            {
                uint want = ReadLE(4);
                if (verifyChecksum && (uint)hash.Digest() != want) throw Bad("content checksum mismatch (corrupted or misordered data)");
            }
            inFrame = false;
        }
    }

    // ---- compressed blocks ----

    void DecodeCompressed(int size)
    {
        int litCount;
        int p = DecodeLiterals(size, out litCount);
        if (p >= size) throw Bad("missing sequences section");
        int nbSeq = blk[p++];
        if (nbSeq >= 128)
        {
            if (nbSeq == 255) { if (p + 2 > size) throw Bad("bad sequence count"); nbSeq = blk[p] + (blk[p + 1] << 8) + 0x7F00; p += 2; }
            else { if (p + 1 > size) throw Bad("bad sequence count"); nbSeq = ((nbSeq - 128) << 8) + blk[p]; p += 1; }
        }
        if (nbSeq == 0)
        {
            if (p != size) throw Bad("data after an empty sequences section");
            CopyLiterals(0, litCount);
            return;
        }
        if (p >= size) throw Bad("missing symbol compression modes");
        int modes = blk[p++];
        if ((modes & 3) != 0) throw Bad("reserved bits in symbol compression modes");
        p = ReadTable(p, size, (modes >> 6) & 3, llOwn, Fse.LLDefault, 35, 9, ref llT);
        p = ReadTable(p, size, (modes >> 4) & 3, ofOwn, Fse.OFDefault, 31, 8, ref ofT);
        p = ReadTable(p, size, (modes >> 2) & 3, mlOwn, Fse.MLDefault, 52, 9, ref mlT);
        ExecuteSequences(p, size, nbSeq, litCount);
    }

    int ReadTable(int p, int end, int mode, Fse own, Fse def, int maxSym, int maxLog, ref Fse cur)
    {
        switch (mode)
        {
            case 0: cur = def; return p;
            case 1:
                if (p >= end) throw Bad("missing RLE symbol");
                if (blk[p] > maxSym) throw Bad("RLE symbol out of range");
                own.Rle(blk[p]); cur = own; return p + 1;
            case 2:
                {
                    int log, maxS;
                    int used = Fse.ReadNCount(blk, p, end, norm, maxSym, out maxS, out log);
                    if (log > maxLog) throw Bad("FSE table log too large");
                    own.Build(norm, maxS, log);
                    cur = own;
                    return p + used;
                }
            default:
                if (cur == null) throw Bad("repeat mode without a previous table");
                return p;
        }
    }

    int DecodeLiterals(int size, out int litCount)
    {
        if (size < 1) throw Bad("empty compressed block");
        int b0 = blk[0];
        int type = b0 & 3, sf = (b0 >> 2) & 3;
        if (type < 2)
        {
            int n, hs;
            if ((sf & 1) == 0) { n = b0 >> 3; hs = 1; }
            else if (sf == 1) { if (size < 2) throw Bad("short literals header"); n = (b0 >> 4) + (blk[1] << 4); hs = 2; }
            else { if (size < 3) throw Bad("short literals header"); n = (b0 >> 4) + (blk[1] << 4) + (blk[2] << 12); hs = 3; }
            if (n > blockMax) throw Bad("too many literals");
            litCount = n;
            if (type == 0)
            {
                if (hs + n > size) throw Bad("raw literals past the block");
                Buffer.BlockCopy(blk, hs, lit, 0, n);
                return hs + n;
            }
            if (hs + 1 > size) throw Bad("RLE literals past the block");
            byte v = blk[hs];
            for (int i = 0; i < n; i++) lit[i] = v;
            return hs + 1;
        }
        int hsz, regen, comp;
        bool four = sf != 0;
        if (sf < 2)
        {
            if (size < 3) throw Bad("short literals header");
            int hv = b0 | blk[1] << 8 | blk[2] << 16;
            regen = (hv >> 4) & 0x3FF; comp = (hv >> 14) & 0x3FF; hsz = 3;
        }
        else if (sf == 2)
        {
            if (size < 4) throw Bad("short literals header");
            long hv = (uint)(b0 | blk[1] << 8 | blk[2] << 16 | blk[3] << 24);
            regen = (int)(hv >> 4) & 0x3FFF; comp = (int)(hv >> 18) & 0x3FFF; hsz = 4;
        }
        else
        {
            if (size < 5) throw Bad("short literals header");
            long hv = (uint)(b0 | blk[1] << 8 | blk[2] << 16 | blk[3] << 24) | ((long)blk[4] << 32);
            regen = (int)(hv >> 4) & 0x3FFFF; comp = (int)(hv >> 22) & 0x3FFFF; hsz = 5;
        }
        if (regen > blockMax) throw Bad("too many literals");
        if (hsz + comp > size) throw Bad("compressed literals past the block");
        int p = hsz, end = hsz + comp;
        if (type == 2) p = ReadHuffmanTable(p, end);
        else if (hufLog == 0) throw Bad("treeless literals without a previous Huffman table");
        if (!four) HufStream(p, end, 0, regen);
        else
        {
            if (p + 6 > end) throw Bad("short jump table");
            int s1 = blk[p] | blk[p + 1] << 8, s2 = blk[p + 2] | blk[p + 3] << 8, s3 = blk[p + 4] | blk[p + 5] << 8;
            int q = p + 6;
            int s4 = end - q - s1 - s2 - s3;
            if (s4 < 1 || s1 < 1 || s2 < 1 || s3 < 1) throw Bad("bad jump table");
            int seg = (regen + 3) / 4;
            if (3 * seg > regen) throw Bad("too few literals for 4 streams");
            HufStream(q, q + s1, 0, seg); q += s1;
            HufStream(q, q + s2, seg, seg); q += s2;
            HufStream(q, q + s3, 2 * seg, seg); q += s3;
            HufStream(q, end, 3 * seg, regen - 3 * seg);
        }
        litCount = regen;
        return end;
    }

    readonly byte[] weights = new byte[260];
    readonly int[] rankStart = new int[13];
    readonly Fse wFse = new Fse(6);

    int ReadHuffmanTable(int p, int end)
    {
        if (p >= end) throw Bad("missing Huffman tree");
        int hb = blk[p++];
        int n;
        if (hb >= 128)
        {
            n = hb - 127;
            int bytes = (n + 1) / 2;
            if (p + bytes > end) throw Bad("Huffman weights past the block");
            for (int i = 0; i < n; i++)
            {
                int b = blk[p + i / 2];
                weights[i] = (byte)((i & 1) == 0 ? b >> 4 : b & 15);
            }
            p += bytes;
        }
        else
        {
            if (p + hb > end) throw Bad("Huffman weights past the block");
            int log, maxS;
            int used = Fse.ReadNCount(blk, p, p + hb, norm, 12, out maxS, out log);
            if (log > 6) throw Bad("Huffman weights table log too large");
            wFse.Build(norm, maxS, log);
            n = DecodeWeights(p + used, p + hb);
            p += hb;
        }
        // The last weight is implied: the total must reach the next power of two.
        int sum = 0;
        for (int i = 0; i < n; i++)
        {
            if (weights[i] > 11) throw Bad("Huffman weight too large");
            if (weights[i] > 0) sum += 1 << (weights[i] - 1);
        }
        if (sum == 0) throw Bad("empty Huffman tree");
        int maxBits = HighBit(sum) + 1;
        if (maxBits > 11) throw Bad("Huffman code too long");
        int rest = (1 << maxBits) - sum;
        if ((rest & (rest - 1)) != 0) throw Bad("Huffman weights do not complete the tree");
        if (n > 255) throw Bad("too many Huffman symbols");
        weights[n] = (byte)(HighBit(rest) + 1);
        n++;
        Array.Clear(rankStart, 0, rankStart.Length);
        for (int i = 0; i < n; i++) rankStart[weights[i]]++;
        int next = 0;
        for (int w = 1; w <= maxBits; w++) { int c = rankStart[w]; rankStart[w] = next; next += c << (w - 1); }
        // Codes are given by increasing weight, then by symbol value.
        for (int s = 0; s < n; s++)
        {
            int w = weights[s];
            if (w == 0) continue;
            int len = 1 << (w - 1);
            int at = rankStart[w];
            ushort e = (ushort)(s | (maxBits + 1 - w) << 8);
            for (int u = at; u < at + len; u++) huf[u] = e;
            rankStart[w] = at + len;
        }
        hufLog = maxBits;
        return p;
    }

    int DecodeWeights(int start, int end)
    {
        var br = new BackBits(blk, start, end);
        int s1 = (int)br.Read(wFse.Log), s2 = (int)br.Read(wFse.Log);
        uint[] t = wFse.T;
        int n = 0;
        // Two interleaved states; when an update reads past the start, the other state's symbol is the last one.
        for (;;)
        {
            if (n >= 254) throw Bad("too many Huffman weights");
            uint e = t[s1];
            weights[n++] = (byte)e;
            s1 = (int)(e >> 16) + (int)br.Read((int)(e >> 8) & 0xFF);
            if (br.Reload() == 3) { weights[n++] = (byte)t[s2]; break; }
            e = t[s2];
            weights[n++] = (byte)e;
            s2 = (int)(e >> 16) + (int)br.Read((int)(e >> 8) & 0xFF);
            if (br.Reload() == 3) { weights[n++] = (byte)t[s1]; break; }
        }
        return n;
    }

    void HufStream(int start, int end, int outAt, int count)
    {
        if (end <= start) throw Bad("empty Huffman stream");
        var br = new BackBits(blk, start, end);
        int shift = 64 - hufLog;
        ushort[] t = huf;
        byte[] o = lit;
        int i = outAt, stop = outAt + count;
        // Four symbols per refill: 4 x 11 bits fit in the 57 bits a refill guarantees.
        while (stop - i >= 4)
        {
            br.Reload();
            ulong c = br.C; int used = br.Used;
            if (used > 64) throw Bad("Huffman stream overrun");
            int e = t[(int)((c << used) >> shift)]; o[i] = (byte)e; used += e >> 8;
            e = t[(int)((c << used) >> shift)]; o[i + 1] = (byte)e; used += e >> 8;
            e = t[(int)((c << used) >> shift)]; o[i + 2] = (byte)e; used += e >> 8;
            e = t[(int)((c << used) >> shift)]; o[i + 3] = (byte)e; used += e >> 8;
            br.Used = used;
            i += 4;
        }
        while (i < stop)
        {
            br.Reload();
            if (br.Used >= 64) throw Bad("Huffman stream overrun");
            int e = t[(int)((br.C << br.Used) >> shift)]; o[i++] = (byte)e; br.Used += e >> 8;
        }
        if (!br.Finished) throw Bad("Huffman stream not fully consumed");
    }

    void CopyLiterals(int from, int n)
    {
        if (n <= 0) return;
        if (winPos + n > win.Length) throw Bad("output past the window buffer");
        Buffer.BlockCopy(lit, from, win, winPos, n);
        winPos += n;
    }

    static readonly int[] LLBase = {
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32, 40, 48, 64, 128, 256, 512,
        1024, 2048, 4096, 8192, 16384, 32768, 65536 };
    static readonly byte[] LLBits = {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
    static readonly int[] MLBase = {
        3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
        33, 34, 35, 37, 39, 41, 43, 47, 51, 59, 67, 83, 99, 131, 259, 515, 1027, 2051, 4099, 8195, 16387, 32771, 65539 };
    static readonly byte[] MLBits = {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2,
        3, 3, 4, 4, 5, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };

    void ExecuteSequences(int start, int end, int nbSeq, int litCount)
    {
        if (end <= start) throw Bad("empty sequences bitstream");
        // The backward bitstream, kept in locals (see BackBits for the same logic as methods).
        var br = new BackBits(blk, start, end);
        ulong c = br.C; int used = br.Used, ptr = br.Ptr, lim = start + 8;
        byte[] bb = blk;
        uint[] llTab = llT.T, ofTab = ofT.T, mlTab = mlT.T;
        int llS = (int)br.Read(llT.Log), ofS, mlS;
        used = br.Used;
        ofS = (int)(((c << used) >> 1) >> ((63 - ofT.Log) & 63)); used += ofT.Log;
        mlS = (int)(((c << used) >> 1) >> ((63 - mlT.Log) & 63)); used += mlT.Log;
        byte[] w = win, l = lit;
        int pos = winPos, litPos = 0, limit = Math.Min(w.Length, winPos + blockMax);
        int r1 = rep1, r2 = rep2, r3 = rep3;
        for (int s = 0; s < nbSeq; s++)
        {
            // reload: at least 57 bits available afterwards, unless the stream start is reached
            if (ptr >= lim) { ptr -= used >> 3; used &= 7; c = BitConverter.ToUInt64(bb, ptr); }
            else if (ptr > start && used < 65)
            {
                int nb = used >> 3; if (ptr - nb < start) nb = ptr - start;
                ptr -= nb; used -= nb * 8; c = BitConverter.ToUInt64(bb, ptr);
            }
            uint llE = llTab[llS], ofE = ofTab[ofS], mlE = mlTab[mlS];
            int ofCode = (int)(ofE & 0xFF), mlCode = (int)(mlE & 0xFF), llCode = (int)(llE & 0xFF);
            if (ofCode > 30) throw Bad("offset code out of range");
            int ofVal = (1 << ofCode) + (int)(((c << used) >> 1) >> ((63 - ofCode) & 63)); used += ofCode;
            if (ofCode > 16)
            {
                if (ptr >= lim) { ptr -= used >> 3; used &= 7; c = BitConverter.ToUInt64(bb, ptr); }
                else if (ptr > start && used < 65)
                {
                    int nb = used >> 3; if (ptr - nb < start) nb = ptr - start;
                    ptr -= nb; used -= nb * 8; c = BitConverter.ToUInt64(bb, ptr);
                }
            }
            int nbits = MLBits[mlCode];
            int ml = MLBase[mlCode] + (int)(((c << used) >> 1) >> ((63 - nbits) & 63)); used += nbits;
            nbits = LLBits[llCode];
            int ll = LLBase[llCode] + (int)(((c << used) >> 1) >> ((63 - nbits) & 63)); used += nbits;
            int offset;
            if (ofVal > 3) { offset = ofVal - 3; r3 = r2; r2 = r1; r1 = offset; }
            else
            {
                int idx = ofVal - 1 + (ll == 0 ? 1 : 0);
                if (idx == 0) offset = r1;
                else if (idx == 1) { offset = r2; r2 = r1; r1 = offset; }
                else if (idx == 2) { offset = r3; r3 = r2; r2 = r1; r1 = offset; }
                else { offset = r1 - 1; if (offset == 0) throw Bad("zero offset"); r3 = r2; r2 = r1; r1 = offset; }
            }
            if (s + 1 < nbSeq)
            {
                if (ptr >= lim) { ptr -= used >> 3; used &= 7; c = BitConverter.ToUInt64(bb, ptr); }
                else if (ptr > start && used < 65)
                {
                    int nb = used >> 3; if (ptr - nb < start) nb = ptr - start;
                    ptr -= nb; used -= nb * 8; c = BitConverter.ToUInt64(bb, ptr);
                }
                nbits = (int)(llE >> 8) & 0xFF;
                llS = (int)(llE >> 16) + (int)(((c << used) >> 1) >> ((63 - nbits) & 63)); used += nbits;
                nbits = (int)(mlE >> 8) & 0xFF;
                mlS = (int)(mlE >> 16) + (int)(((c << used) >> 1) >> ((63 - nbits) & 63)); used += nbits;
                nbits = (int)(ofE >> 8) & 0xFF;
                ofS = (int)(ofE >> 16) + (int)(((c << used) >> 1) >> ((63 - nbits) & 63)); used += nbits;
            }
            // literals
            if (ll > 0)
            {
                if (litPos + ll > litCount || pos + ll > limit) throw Bad("literal length past the literals");
                if (ll <= 16) { for (int k = 0; k < ll; k++) w[pos + k] = l[litPos + k]; }
                else Buffer.BlockCopy(l, litPos, w, pos, ll);
                pos += ll; litPos += ll;
            }
            // match; the buffer always holds at least one window of history before the block, or the whole frame
            if (offset > pos) throw Bad("offset reaches before the start of the window");
            if (pos + ml > limit) throw Bad("match past the block");
            int from = pos - offset;
            if (offset >= ml)
            {
                if (ml <= 32) { for (int k = 0; k < ml; k++) w[pos + k] = w[from + k]; }
                else Buffer.BlockCopy(w, from, w, pos, ml);
            }
            else if (offset == 1)
            {
                byte b = w[from];
                for (int k = 0; k < ml; k++) w[pos + k] = b;
            }
            else
            {
                // Overlapping copy: the last 'offset' bytes repeat; copy them in doubling pieces.
                int left = ml, d = pos;
                while (left > 0)
                {
                    int k = Math.Min(left, d - from);
                    if (k < 32) { for (int j = 0; j < k; j++) w[d + j] = w[from + j]; }
                    else Buffer.BlockCopy(w, from, w, d, k);
                    d += k; left -= k;
                }
            }
            pos += ml;
        }
        if (!(ptr == start && used == 64)) throw Bad("sequences bitstream not fully consumed");
        rep1 = r1; rep2 = r2; rep3 = r3;
        winPos = pos;
        if (litPos < litCount) CopyLiterals(litPos, litCount - litPos);
        if (winPos > limit) throw Bad("block too large");
    }

    static int HighBit(int v) { int r = 0; while ((v >>= 1) != 0) r++; return r; }

    // ---- Stream plumbing ----
    public override bool CanRead { get { return true; } }
    public override bool CanSeek { get { return false; } }
    public override bool CanWrite { get { return false; } }
    public override long Length { get { throw new NotSupportedException(); } }
    public override long Position { get { return totalOut - (winPos - readPos); } set { throw new NotSupportedException(); } }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
    public override void SetLength(long value) { throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    protected override void Dispose(bool disposing) { if (disposing) src.Dispose(); base.Dispose(disposing); }
}

// A bitstream read backwards (from its last byte), as zstd writes Huffman, FSE and sequence streams.
struct BackBits
{
    readonly byte[] b;
    readonly int start;
    public int Ptr;
    public ulong C;
    public int Used;

    public BackBits(byte[] buf, int start, int end)
    {
        b = buf; this.start = start;
        int size = end - start;
        if (size < 1) throw new InvalidDataException("zstd: empty bitstream");
        int last = buf[end - 1];
        if (last == 0) throw new InvalidDataException("zstd: bitstream without its end mark");
        int hb = 0; while ((last >> (hb + 1)) != 0) hb++;
        if (size >= 8) { Ptr = end - 8; C = BitConverter.ToUInt64(buf, Ptr); Used = 8 - hb; }
        else
        {
            Ptr = start; C = 0;
            for (int i = size - 1; i >= 0; i--) C = (C << 8) | buf[start + i];
            Used = 8 - hb + (8 - size) * 8;
        }
    }

    // n bits, 0..31 (the bits past the start of the stream read as zeros while Used <= 64)
    public uint Read(int n)
    {
        ulong v = ((C << (Used & 63)) >> 1) >> ((63 - n) & 63);
        Used += n;
        return (uint)v;
    }

    // 0: more to read, 1: at the start, 2: exactly finished, 3: read past the start (overflow)
    public int Reload()
    {
        if (Used > 64) return 3;
        if (Ptr >= start + 8)
        {
            Ptr -= Used >> 3; Used &= 7; C = BitConverter.ToUInt64(b, Ptr);
            return 0;
        }
        if (Ptr == start) return Used < 64 ? 1 : 2;
        int nb = Used >> 3, r = 0;
        if (Ptr - nb < start) { nb = Ptr - start; r = 1; }
        Ptr -= nb; Used -= nb * 8; C = BitConverter.ToUInt64(b, Ptr);
        return r;
    }

    public bool Finished { get { return Ptr == start && Used == 64; } }
}

// An FSE decoding table: per state, symbol | bits to read << 8 | base of the next state << 16.
sealed class Fse
{
    public int Log;
    public readonly uint[] T;
    readonly byte[] sym;

    public Fse(int maxLog) { int n = 1 << maxLog; T = new uint[n]; sym = new byte[n]; }

    public void Rle(byte s) { Log = 0; T[0] = s; }

    readonly ushort[] next = new ushort[64];

    public void Build(short[] norm, int maxSym, int log)
    {
        int size = 1 << log, high = size - 1;
        for (int s = 0; s <= maxSym; s++)
        {
            if (norm[s] == -1) { sym[high--] = (byte)s; next[s] = 1; }
            else next[s] = (ushort)norm[s];
        }
        int step = (size >> 1) + (size >> 3) + 3, mask = size - 1, pos = 0;
        for (int s = 0; s <= maxSym; s++)
            for (int i = 0; i < norm[s]; i++)
            {
                sym[pos] = (byte)s;
                do pos = (pos + step) & mask; while (pos > high);
            }
        if (pos != 0) throw new InvalidDataException("zstd: bad FSE distribution");
        for (int u = 0; u < size; u++)
        {
            int s = sym[u];
            int ns = next[s]++;
            int hb = 0; while ((ns >> (hb + 1)) != 0) hb++;
            int nb = log - hb;
            T[u] = (uint)(s | nb << 8 | ((ns << nb) - size) << 16);
        }
        Log = log;
    }

    static uint Peek32(byte[] b, int bitPos, int start, int end)
    {
        int i = start + (bitPos >> 3);
        ulong v = 0;
        for (int k = 4; k >= 0; k--) { int j = i + k; v = (v << 8) | (j < end ? b[j] : (byte)0); }
        return (uint)(v >> (bitPos & 7));
    }

    // Reads a normalized distribution (RFC 8878 4.1.1); returns the bytes it takes.
    public static int ReadNCount(byte[] b, int start, int end, short[] norm, int maxSym, out int maxSymUsed, out int log)
    {
        if (end <= start) throw new InvalidDataException("zstd: missing FSE table");
        int bit = 0;
        uint v = Peek32(b, bit, start, end);
        log = (int)(v & 15) + 5;
        bit += 4;
        int remaining = (1 << log) + 1, threshold = 1 << log, nbBits = log + 1;
        int s = 0;
        bool prev0 = false;
        for (int i = 0; i <= maxSym; i++) norm[i] = 0;
        while (remaining > 1)
        {
            if (prev0)
            {
                for (;;)
                {
                    int r = (int)(Peek32(b, bit, start, end) & 3);
                    bit += 2;
                    s += r;
                    if (r != 3) break;
                    if (s > maxSym) break;
                }
            }
            if (s > maxSym) throw new InvalidDataException("zstd: FSE symbol out of range");
            v = Peek32(b, bit, start, end);
            int max = (2 * threshold - 1) - remaining;
            int count;
            if ((v & (uint)(threshold - 1)) < (uint)max) { count = (int)(v & (uint)(threshold - 1)); bit += nbBits - 1; }
            else
            {
                count = (int)(v & (uint)(2 * threshold - 1));
                if (count >= threshold) count -= max;
                bit += nbBits;
            }
            count--;
            remaining -= count < 0 ? -count : count;
            norm[s++] = (short)count;
            prev0 = count == 0;
            if (remaining <= 1) break;
            while (remaining < threshold) { nbBits--; threshold >>= 1; }
        }
        if (remaining != 1) throw new InvalidDataException("zstd: bad FSE distribution");
        int used = (bit + 7) >> 3;
        if (start + used > end) throw new InvalidDataException("zstd: FSE table past its end");
        maxSymUsed = s - 1;
        return used;
    }

    static Fse Predefined(short[] d, int log)
    {
        var f = new Fse(log);
        f.Build(d, d.Length - 1, log);
        return f;
    }

    public static readonly Fse LLDefault = Predefined(new short[] {
        4, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 2, 1, 1, 1, 1, 1, -1, -1, -1, -1 }, 6);
    public static readonly Fse MLDefault = Predefined(new short[] {
        1, 4, 3, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1, -1, -1 }, 6);
    public static readonly Fse OFDefault = Predefined(new short[] {
        1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1 }, 5);
}

// XXH64, streaming; zstd keeps the low 32 bits of the digest (seed 0) as the content checksum.
sealed class XXH64
{
    const ulong P1 = 11400714785074694791UL, P2 = 14029467366897019727UL, P3 = 1609587929392839161UL,
                P4 = 9650029242287828579UL, P5 = 2870177450012600261UL;
    ulong v1, v2, v3, v4, total;
    readonly byte[] mem = new byte[32];
    int memLen;

    public XXH64() { Reset(); }

    public void Reset()
    {
        unchecked { v1 = P1 + P2; v2 = P2; v3 = 0; v4 = 0UL - P1; }
        total = 0; memLen = 0;
    }

    static ulong Rotl(ulong x, int r) { return (x << r) | (x >> (64 - r)); }
    static ulong Round(ulong acc, ulong input) { unchecked { acc += input * P2; acc = Rotl(acc, 31); return acc * P1; } }
    static ulong Merge(ulong acc, ulong v) { unchecked { v = Round(0, v); acc ^= v; return acc * P1 + P4; } }

    public void Update(byte[] b, int off, int len)
    {
        total += (ulong)len;
        if (memLen + len < 32) { Buffer.BlockCopy(b, off, mem, memLen, len); memLen += len; return; }
        int end = off + len;
        if (memLen > 0)
        {
            int k = 32 - memLen;
            Buffer.BlockCopy(b, off, mem, memLen, k);
            v1 = Round(v1, BitConverter.ToUInt64(mem, 0)); v2 = Round(v2, BitConverter.ToUInt64(mem, 8));
            v3 = Round(v3, BitConverter.ToUInt64(mem, 16)); v4 = Round(v4, BitConverter.ToUInt64(mem, 24));
            off += k; memLen = 0;
        }
        ulong a = v1, c = v2, d = v3, e = v4;
        for (; off + 32 <= end; off += 32)
        {
            a = Round(a, BitConverter.ToUInt64(b, off)); c = Round(c, BitConverter.ToUInt64(b, off + 8));
            d = Round(d, BitConverter.ToUInt64(b, off + 16)); e = Round(e, BitConverter.ToUInt64(b, off + 24));
        }
        v1 = a; v2 = c; v3 = d; v4 = e;
        if (off < end) { Buffer.BlockCopy(b, off, mem, 0, end - off); memLen = end - off; }
    }

    public ulong Digest()
    {
        unchecked
        {
            ulong h;
            if (total >= 32)
            {
                h = Rotl(v1, 1) + Rotl(v2, 7) + Rotl(v3, 12) + Rotl(v4, 18);
                h = Merge(h, v1); h = Merge(h, v2); h = Merge(h, v3); h = Merge(h, v4);
            }
            else h = v3 + P5;
            h += total;
            int i = 0;
            for (; i + 8 <= memLen; i += 8) { h ^= Round(0, BitConverter.ToUInt64(mem, i)); h = Rotl(h, 27) * P1 + P4; }
            if (i + 4 <= memLen) { h ^= (ulong)BitConverter.ToUInt32(mem, i) * P1; h = Rotl(h, 23) * P2 + P3; i += 4; }
            for (; i < memLen; i++) { h ^= mem[i] * P5; h = Rotl(h, 11) * P1; }
            h ^= h >> 33; h *= P2; h ^= h >> 29; h *= P3; h ^= h >> 32;
            return h;
        }
    }
}

// One tar entry. Type: '0' file, '5' directory, or the raw type flag of anything else.
sealed class TarEntry
{
    public string Name;
    public char Type;
    public long Size;
    public long ModifiedUnix;
}

// Reads a tar stream front to back: ustar headers, pax extended headers ('x' path/size/mtime, 'g' ignored),
// GNU long names ('L'), base-256 sizes. Next() skips whatever of the previous entry was not read.
sealed class TarReader
{
    readonly Stream s;
    readonly byte[] h = new byte[512];
    readonly byte[] skipBuf = new byte[1 << 16];
    long left, pad;
    bool done;

    public TarReader(Stream s) { this.s = s; }

    public TarEntry Next()
    {
        if (done) return null;
        Skip(left + pad); left = pad = 0;
        string longName = null, paxPath = null;
        long paxSize = -1, paxMtime = long.MinValue;
        for (;;)
        {
            if (!ReadFull(h, 512)) throw new EndOfStreamException("tar: ends inside a header");
            bool allZero = true;
            for (int i = 0; i < 512; i++) if (h[i] != 0) { allZero = false; break; }
            if (allZero)
            {
                // the end: two zero blocks (one is accepted too, as some writers stop there)
                if (!ReadFull(h, 512)) { done = true; return null; }
                for (int i = 0; i < 512; i++) if (h[i] != 0) throw new InvalidDataException("tar: data after an end block");
                done = true;
                return null;
            }
            long sum = 0, ssum = 0;
            for (int i = 0; i < 512; i++) { int c = (i >= 148 && i < 156) ? 32 : h[i]; sum += c; ssum += (sbyte)(byte)c; }
            long want = Octal(148, 8);
            if (want != sum && want != ssum) throw new InvalidDataException("tar: bad header checksum");
            char type = (char)h[156];
            long size = Size();
            if (type == 'x' || type == 'g' || type == 'L' || type == 'K')
            {
                if (size > 1 << 20) throw new InvalidDataException("tar: extended header too large");
                var data = new byte[size];
                if (!ReadFull(data, (int)size)) throw new EndOfStreamException("tar: ends inside an extended header");
                Skip(Pad(size));
                if (type == 'L') longName = Encoding.UTF8.GetString(data).TrimEnd('\0');
                else if (type == 'x') ParsePax(data, ref paxPath, ref paxSize, ref paxMtime);
                continue;
            }
            var e = new TarEntry();
            string name = Str(0, 100);
            // the prefix field exists in POSIX ustar only (magic "ustar\0"); GNU tar keeps other data there
            if (h[257] == 'u' && h[258] == 's' && h[259] == 't' && h[260] == 'a' && h[261] == 'r' && h[262] == 0)
            {
                string prefix = Str(345, 155);
                if (prefix.Length > 0) name = prefix + "/" + name;
            }
            if (longName != null) name = longName;
            if (paxPath != null) name = paxPath;
            if (paxSize >= 0) size = paxSize;
            e.Name = name;
            e.Type = type == '\0' || type == '7' ? '0' : type;
            if (e.Type == '0' && name.EndsWith("/")) e.Type = '5';
            e.ModifiedUnix = paxMtime != long.MinValue ? paxMtime : Octal(136, 12);
            // directories, links and devices carry no data even when a size is given (except old-style hard links)
            e.Size = (e.Type == '0' || e.Type == 'S') ? size : (e.Type == '5' || e.Type == '1' || e.Type == '2' ? 0 : size);
            left = e.Size; pad = Pad(e.Size);
            return e;
        }
    }

    // Reads the current entry's data; 0 at its end.
    public int Read(byte[] buffer, int offset, int count)
    {
        if (left == 0) return 0;
        int n = s.Read(buffer, offset, (int)Math.Min(count, left));
        if (n <= 0) throw new EndOfStreamException("tar: ends inside an entry");
        left -= n;
        return n;
    }

    // The entry name as a relative Windows path, or null when it is not safe to create: absolute, with a drive,
    // "..", ':' (alternate data streams), a backslash, characters Windows does not take, trailing dots or spaces,
    // or a device name.
    public static string SafeRelativePath(string name)
    {
        if (string.IsNullOrEmpty(name) || name[0] == '/' || name.IndexOf('\\') >= 0 || name.IndexOf(':') >= 0) return null;
        var parts = name.Split('/');
        var sb = new StringBuilder();
        char[] bad = Path.GetInvalidFileNameChars();
        foreach (string p in parts)
        {
            if (p.Length == 0 || p == ".") continue;
            if (p == ".." || p.IndexOfAny(bad) >= 0 || p.EndsWith(".") || p.EndsWith(" ")) return null;
            string stem = p.Split('.')[0].ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] >= '0' && stem[3] <= '9')) return null;
            if (sb.Length > 0) sb.Append(Path.DirectorySeparatorChar);
            sb.Append(p);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    static long Pad(long size) { return (512 - size % 512) % 512; }

    static void ParsePax(byte[] d, ref string path, ref long size, ref long mtime)
    {
        int i = 0;
        while (i < d.Length)
        {
            int sp = Array.IndexOf(d, (byte)' ', i);
            if (sp < 0) break;
            int len;
            if (!int.TryParse(Encoding.ASCII.GetString(d, i, sp - i), out len) || len <= sp - i + 1 || i + len > d.Length)
                throw new InvalidDataException("tar: bad pax record");
            string kv = Encoding.UTF8.GetString(d, sp + 1, len - (sp - i) - 2);
            int eq = kv.IndexOf('=');
            if (eq > 0)
            {
                string k = kv.Substring(0, eq), v = kv.Substring(eq + 1);
                if (k == "path") path = v;
                else if (k == "size") size = long.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                else if (k == "mtime")
                {
                    double t;
                    if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out t)) mtime = (long)t;
                }
            }
            i += len;
        }
    }

    long Size()
    {
        if ((h[124] & 0x80) != 0)
        {
            long v = h[124] & 0x7F;
            for (int i = 125; i < 136; i++) v = (v << 8) | h[i];
            return v;
        }
        return Octal(124, 12);
    }

    long Octal(int off, int len)
    {
        long v = 0;
        int i = off, e = off + len;
        while (i < e && h[i] == ' ') i++;
        for (; i < e; i++)
        {
            byte c = h[i];
            if (c == 0 || c == ' ') break;
            if (c < '0' || c > '7') throw new InvalidDataException("tar: bad number in a header");
            v = v * 8 + (c - '0');
        }
        return v;
    }

    string Str(int off, int len)
    {
        int n = 0;
        while (n < len && h[off + n] != 0) n++;
        return Encoding.UTF8.GetString(h, off, n);
    }

    bool ReadFull(byte[] b, int n)
    {
        int got = 0;
        while (got < n) { int r = s.Read(b, got, n - got); if (r <= 0) return false; got += r; }
        return true;
    }

    void Skip(long n)
    {
        while (n > 0)
        {
            int r = s.Read(skipBuf, 0, (int)Math.Min(n, skipBuf.Length));
            if (r <= 0) throw new EndOfStreamException("tar: ends inside an entry");
            n -= r;
        }
    }
}
