﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection.Metadata;

namespace System.Reflection.PortableExecutable
{
    public sealed class DebugDirectoryBuilder
    {
        private struct Entry
        {
            public uint Stamp;
            public uint Version;
            public DebugDirectoryEntryType Type;
            public int DataSize;
        }

        private readonly List<Entry> _entries;
        private readonly BlobBuilder _dataBuilder;

        public DebugDirectoryBuilder()
        {
            _entries = new List<Entry>(2);
            _dataBuilder = new BlobBuilder();
        }

        internal void AddEntry(DebugDirectoryEntryType type, uint version, uint stamp, int dataSize = 0)
        {
            _entries.Add(new Entry()
            {
                Stamp = stamp,
                Version = version,
                Type = type,
                DataSize = dataSize,
            });
        }

        public void AddCodeViewEntry(
            string pdbPath,
            BlobContentId pdbContentId,
            ushort portablePdbVersion)
        {
            if (pdbPath == null)
            {
                Throw.ArgumentNull(nameof(pdbPath));
            }

            int dataSize = WriteCodeViewData(_dataBuilder, pdbPath, pdbContentId.Guid);
            
            AddEntry(
                type: DebugDirectoryEntryType.CodeView,
                version: (portablePdbVersion == 0) ? 0 : PortablePdbVersions.DebugDirectoryEntryVersion(portablePdbVersion),
                stamp: pdbContentId.Stamp,
                dataSize: dataSize);
        }

        public void AddReproducibleEntry()
        {
            AddEntry(type: DebugDirectoryEntryType.Reproducible, version: 0, stamp: 0);
        }

        public void AddEmbeddedPortablePdbEntry(BlobBuilder debugMetadata, ushort portablePdbVersion)
        {
            if (debugMetadata == null)
            {
                Throw.ArgumentNull(nameof(debugMetadata));
            }

            int dataSize = WriteEmbeddedPortablePdbData(_dataBuilder, debugMetadata);

            AddEntry(
                type: DebugDirectoryEntryType.EmbeddedPortablePdb, 
                version: PortablePdbVersions.DebugDirectoryEmbeddedVersion(portablePdbVersion),
                stamp: 0,
                dataSize: dataSize);
        }

        private static int WriteEmbeddedPortablePdbData(BlobBuilder builder, BlobBuilder debugMetadata)
        {
            int start = builder.Count;

            // header (signature, decompressed size):
            builder.WriteUInt32(PortablePdbVersions.DebugDirectoryEmbeddedSignature);
            builder.WriteInt32(debugMetadata.Count);

            // compressed data:
            var compressed = new MemoryStream();
            using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                foreach (var blob in debugMetadata.GetBlobs())
                {
                    var segment = blob.GetBytes();
                    deflate.Write(segment.Array, segment.Offset, segment.Count);
                }
            }

            // TODO: avoid multiple copies:
            builder.WriteBytes(compressed.ToArray());

            return builder.Count - start;
        }

        private static int WriteCodeViewData(BlobBuilder builder, string pdbPath, Guid pdbGuid)
        {
            int start = builder.Count;

            builder.WriteByte((byte)'R');
            builder.WriteByte((byte)'S');
            builder.WriteByte((byte)'D');
            builder.WriteByte((byte)'S');

            // PDB id:
            builder.WriteGuid(pdbGuid);

            // age
            builder.WriteUInt32(1);

            // UTF-8 encoded zero-terminated path to PDB
            int pathStart = builder.Count;
            builder.WriteUTF8(pdbPath, allowUnpairedSurrogates: true);
            builder.WriteByte(0);

            return builder.Count - start;
        }

        internal int TableSize => DebugDirectoryEntry.Size * _entries.Count;
        internal int Size => TableSize + _dataBuilder?.Count ?? 0; 

        /// <summary>
        /// Serialize the Debug Table and Data.
        /// </summary>
        /// <param name="builder">Builder.</param>
        /// <param name="sectionLocation">The containing PE section location.</param>
        /// <param name="sectionOffset">Offset of the table within the containing section.</param>
        internal void Serialize(BlobBuilder builder, SectionLocation sectionLocation, int sectionOffset)
        {
            int dataOffset = sectionOffset + TableSize;
            foreach (var entry in _entries)
            {
                int addressOfRawData;
                int pointerToRawData;
                if (entry.DataSize > 0)
                {
                    addressOfRawData = sectionLocation.RelativeVirtualAddress + dataOffset;
                    pointerToRawData = sectionLocation.PointerToRawData + dataOffset;
                }
                else
                {
                    addressOfRawData = 0;
                    pointerToRawData = 0;
                }

                builder.WriteUInt32(0); // characteristics, always 0
                builder.WriteUInt32(entry.Stamp);
                builder.WriteUInt32(entry.Version);
                builder.WriteInt32((int)entry.Type);
                builder.WriteInt32(entry.DataSize);
                builder.WriteInt32(addressOfRawData);
                builder.WriteInt32(pointerToRawData);

                dataOffset += entry.DataSize;
            }

            builder.LinkSuffix(_dataBuilder);
        }
    }
}
