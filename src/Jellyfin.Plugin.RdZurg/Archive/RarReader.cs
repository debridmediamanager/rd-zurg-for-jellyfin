using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Jellyfin.Plugin.RdZurg.Archive;

/// <summary>
/// Locates stored members inside a RAR archive by reading only its headers.
/// </summary>
/// <remarks>
/// <para>
/// Real-Debrid regularly serves a release as a single-file RAR whose existence its own
/// <c>/torrents/info</c> file list does not mention: the list says <c>X.mkv</c> and the link serves
/// <c>X.mkv.rar</c>, larger by the 131 to 157 bytes of a header. Handing that to a player produces
/// an item nothing can read, so the archive has to be seen through.
/// </para>
/// <para>
/// Scope is deliberate. A <em>stored</em> member is a contiguous byte range of the archive, so
/// serving it is offset arithmetic and every seek stays a range request against the CDN. A
/// compressed member is not playable without decompressing the whole stream, and is reported as
/// present but not stored so the caller can leave it out of the library rather than publish
/// something broken.
/// </para>
/// </remarks>
public static class RarReader
{
    private static readonly byte[] _rar5Signature = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 };
    private static readonly byte[] _rar4Signature = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 };

    /// <summary>The number of leading bytes of an archive that are enough to list its members.</summary>
    /// <remarks>
    /// Headers sit at the front, and each one is small. A single-file archive needs a few hundred
    /// bytes; the allowance is generous so a long name or an extra area cannot truncate the scan.
    /// </remarks>
    public const int HeaderProbeLength = 64 * 1024;

    /// <summary>Reports whether the buffer starts with a RAR signature of either generation.</summary>
    /// <param name="head">The leading bytes of the candidate archive.</param>
    /// <returns><c>true</c> when the bytes are a RAR archive.</returns>
    public static bool LooksLikeRar(ReadOnlySpan<byte> head)
        => head.StartsWith(_rar5Signature) || head.StartsWith(_rar4Signature);

    /// <summary>
    /// Lists the members described by the headers at the start of an archive.
    /// </summary>
    /// <param name="head">The leading bytes of the archive, at least <see cref="HeaderProbeLength"/> where available.</param>
    /// <returns>The members found, in archive order. Empty when the bytes are not a readable RAR.</returns>
    public static IReadOnlyList<RarEntry> ListEntries(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(_rar5Signature))
        {
            return ListRar5(head);
        }

        if (head.StartsWith(_rar4Signature))
        {
            return ListRar4(head);
        }

        return Array.Empty<RarEntry>();
    }

    /// <summary>
    /// Picks the member a player should be pointed at: the largest stored one.
    /// </summary>
    /// <param name="head">The leading bytes of the archive.</param>
    /// <param name="entry">The chosen member, when there is one.</param>
    /// <returns><c>true</c> when a stored member was found.</returns>
    public static bool TryGetPrimaryEntry(ReadOnlySpan<byte> head, [NotNullWhen(true)] out RarEntry? entry)
    {
        entry = null;
        long best = -1;

        foreach (var candidate in ListEntries(head))
        {
            if (candidate.IsStored && candidate.Length > best)
            {
                best = candidate.Length;
                entry = candidate;
            }
        }

        return entry is not null;
    }

    private static List<RarEntry> ListRar5(ReadOnlySpan<byte> head)
    {
        var entries = new List<RarEntry>();
        var pos = _rar5Signature.Length;

        while (pos < head.Length)
        {
            // CRC32 of the header, then its size. The size covers everything after itself, so the
            // next header begins at (start of size field + size value) plus any data area.
            if (pos + 4 > head.Length)
            {
                break;
            }

            pos += 4;

            if (!TryReadVInt(head, ref pos, out var headerSize) || headerSize == 0)
            {
                break;
            }

            var headerStart = pos;
            var headerEnd = headerStart + (int)headerSize;
            if (headerEnd > head.Length || headerEnd < headerStart)
            {
                break;
            }

            if (!TryReadVInt(head, ref pos, out var headerType)
                || !TryReadVInt(head, ref pos, out var headerFlags))
            {
                break;
            }

            long extraSize = 0;
            if ((headerFlags & 0x0001) != 0 && !TryReadVInt(head, ref pos, out extraSize))
            {
                break;
            }

            long dataSize = 0;
            if ((headerFlags & 0x0002) != 0 && !TryReadVInt(head, ref pos, out dataSize))
            {
                break;
            }

            // 2 = file, 3 = service (recovery records and the like, which are not content).
            if (headerType == 2)
            {
                var entry = ReadRar5FileHeader(head, pos, headerEnd, dataSize, headerEnd);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }

            var next = headerEnd + (int)dataSize;
            if (next <= headerStart)
            {
                break;
            }

            pos = next;
            _ = extraSize;
        }

        return entries;
    }

    private static RarEntry? ReadRar5FileHeader(ReadOnlySpan<byte> head, int pos, int headerEnd, long dataSize, int dataOffset)
    {
        if (!TryReadVInt(head, ref pos, out var fileFlags)
            || !TryReadVInt(head, ref pos, out var unpackedSize)
            || !TryReadVInt(head, ref pos, out _))
        {
            return null;
        }

        // Directory entries carry no data worth pointing at.
        if ((fileFlags & 0x0001) != 0)
        {
            return null;
        }

        if ((fileFlags & 0x0002) != 0)
        {
            pos += 4;
        }

        if ((fileFlags & 0x0004) != 0)
        {
            pos += 4;
        }

        if (!TryReadVInt(head, ref pos, out var compressionInfo)
            || !TryReadVInt(head, ref pos, out _)
            || !TryReadVInt(head, ref pos, out var nameLength))
        {
            return null;
        }

        if (pos + nameLength > headerEnd || nameLength < 0)
        {
            return null;
        }

        var name = Encoding.UTF8.GetString(head.Slice(pos, (int)nameLength));

        // Bits 7 to 9 hold the method; 0 is store. An unknown unpacked size (flag 0x0008) means the
        // length cannot be trusted, so fall back to the data size the header already gave us.
        var method = (compressionInfo >> 7) & 0x07;
        var isStored = method == 0;
        var length = (fileFlags & 0x0008) != 0 ? dataSize : unpackedSize;

        if (length <= 0)
        {
            return null;
        }

        return new RarEntry
        {
            Name = name,
            DataOffset = dataOffset,
            Length = length,
            IsStored = isStored
        };
    }

    private static List<RarEntry> ListRar4(ReadOnlySpan<byte> head)
    {
        var entries = new List<RarEntry>();
        var pos = _rar4Signature.Length;

        while (pos + 7 <= head.Length)
        {
            var headerStart = pos;
            var headerType = head[pos + 2];
            var headerFlags = BitConverter.ToUInt16(head.Slice(pos + 3, 2));
            var headerSize = BitConverter.ToUInt16(head.Slice(pos + 5, 2));

            if (headerSize < 7)
            {
                break;
            }

            long addedSize = 0;

            // 0x74 is a file header; 0x8000 marks any header that is followed by a data area.
            if (headerType == 0x74)
            {
                if (headerStart + 32 > head.Length)
                {
                    break;
                }

                long packedSize = BitConverter.ToUInt32(head.Slice(headerStart + 7, 4));
                long unpackedSize = BitConverter.ToUInt32(head.Slice(headerStart + 11, 4));
                var method = head[headerStart + 25];
                var nameSize = BitConverter.ToUInt16(head.Slice(headerStart + 26, 2));
                var namePos = headerStart + 32;

                // 0x100 adds the high 32 bits of both sizes, ahead of the name.
                if ((headerFlags & 0x0100) != 0)
                {
                    if (headerStart + 40 > head.Length)
                    {
                        break;
                    }

                    packedSize |= (long)BitConverter.ToUInt32(head.Slice(headerStart + 32, 4)) << 32;
                    unpackedSize |= (long)BitConverter.ToUInt32(head.Slice(headerStart + 36, 4)) << 32;
                    namePos += 8;
                }

                addedSize = packedSize;

                if (namePos + nameSize <= head.Length && nameSize > 0)
                {
                    var name = Encoding.UTF8.GetString(head.Slice(namePos, nameSize));
                    var dataOffset = headerStart + headerSize;

                    if (unpackedSize > 0)
                    {
                        entries.Add(new RarEntry
                        {
                            Name = name,
                            DataOffset = dataOffset,
                            Length = unpackedSize,
                            IsStored = method == 0x30
                        });
                    }
                }
            }
            else if ((headerFlags & 0x8000) != 0)
            {
                if (headerStart + 11 > head.Length)
                {
                    break;
                }

                addedSize = BitConverter.ToUInt32(head.Slice(headerStart + 7, 4));
            }

            var next = headerStart + headerSize + (int)addedSize;
            if (next <= headerStart)
            {
                break;
            }

            pos = next;
        }

        return entries;
    }

    /// <summary>Reads RAR5's variable-length integer: seven bits per byte, high bit continues.</summary>
    private static bool TryReadVInt(ReadOnlySpan<byte> buffer, ref int pos, out long value)
    {
        value = 0;
        var shift = 0;

        while (pos < buffer.Length && shift < 64)
        {
            var b = buffer[pos++];
            value |= (long)(b & 0x7F) << shift;

            if ((b & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        return false;
    }
}
