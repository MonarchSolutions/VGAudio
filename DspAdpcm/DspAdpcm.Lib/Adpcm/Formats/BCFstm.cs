﻿using System;
using System.IO;
using System.Linq;
using System.Text;
using static DspAdpcm.Lib.Helpers;

namespace DspAdpcm.Lib.Adpcm.Formats
{
    /// <summary>
    /// Represents a BCSTM or BFSTM file.
    /// </summary> 
    internal class BCFstm
    {
        public AdpcmStream AudioStream { get; set; }

        public BCFstmConfiguration Configuration { get; internal set; } = new BCFstmConfiguration();

        private int NumSamples => AudioStream.Looping ? LoopEnd : AudioStream.NumSamples;
        private int NumChannels => AudioStream.Channels.Count;
        private int NumTracks => AudioStream.Tracks.Count;

        private int AlignmentSamples => GetNextMultiple(AudioStream.LoopStart, Configuration.LoopPointAlignment) - AudioStream.LoopStart;
        private int LoopStart => AudioStream.LoopStart + AlignmentSamples;
        private int LoopEnd => AudioStream.LoopEnd + AlignmentSamples;

        private BcstmCodec Codec { get; } = BcstmCodec.Adpcm;
        private byte Looping => (byte)(AudioStream.Looping ? 1 : 0);
        private int AudioDataOffset => DataChunkOffset + 0x20;

        private int SamplesPerInterleave => Configuration.SamplesPerInterleave;
        private int InterleaveSize => GetBytesForAdpcmSamples(SamplesPerInterleave);
        private int InterleaveCount => NumSamples.DivideByRoundUp(SamplesPerInterleave);

        private int LastBlockSamples => NumSamples - ((InterleaveCount - 1) * SamplesPerInterleave);
        private int LastBlockSizeWithoutPadding => GetBytesForAdpcmSamples(LastBlockSamples);
        private int LastBlockSize => GetNextMultiple(LastBlockSizeWithoutPadding, 0x20);

        private int SamplesPerSeekTableEntry => Configuration.SamplesPerSeekTableEntry;
        private int BytesPerSeekTableEntry => 4;
        private int NumSeekTableEntries => NumSamples.DivideByRoundUp(SamplesPerSeekTableEntry);

        private int HeaderLength => 0x40;

        private int HeadChunkOffset => HeaderLength;

        private int InfoChunkOffset => HeaderLength;
        private int InfoChunkLength => GetNextMultiple(InfoChunkHeaderLength + InfoChunkTableLength +
            InfoChunk1Length + InfoChunk2Length + InfoChunk3Length, 0x20);
        private int InfoChunkHeaderLength => 8;
        private int InfoChunkTableLength => 8 * 3;
        private int InfoChunk1Length => 0x38 + (!Configuration.InfoPart1Extra ? 0 : 0xc) + (!Configuration.IncludeUnalignedLoopPoints ? 0 : 8);
        private int InfoChunk2Length => Configuration.IncludeTrackInformation ? 4 + 8 * NumTracks : 0;
        private int InfoChunk3Length => (4 + 8 * NumChannels) +
            (Configuration.IncludeTrackInformation ? 0x14 * NumTracks : 0) +
            8 * NumChannels +
            ChannelInfoLength * NumChannels;

        private int ChannelInfoLength => 0x2e;

        private int SeekChunkOffset => HeaderLength + InfoChunkLength;
        private int SeekChunkLength => GetNextMultiple(8 + NumSeekTableEntries * NumChannels * BytesPerSeekTableEntry, 0x20);

        private int DataChunkOffset => HeaderLength + InfoChunkLength + SeekChunkLength;
        private int DataChunkLength => 0x20 + GetNextMultiple(GetBytesForAdpcmSamples(NumSamples), 0x20) * NumChannels;

        private int GetVersion(BCFstmType type)
        {
            if (type == BCFstmType.Bfstm)
            {
                return Configuration.IncludeUnalignedLoopPoints ? 4 : 3;
            }

            //All BCSTM files I've seen follow this pattern except for Kingdom Hearts 3D
            if (Configuration.IncludeTrackInformation && Configuration.InfoPart1Extra)
                return 0x201;

            if (!Configuration.IncludeTrackInformation && Configuration.InfoPart1Extra)
                return 0x202;

            return 0x200;
        }

        /// <summary>
        /// The size in bytes of the file.
        /// </summary>
        public int FileLength => HeaderLength + InfoChunkLength + SeekChunkLength + DataChunkLength;

        private void RecalculateData()
        {
            var seekTableToCalculate = Configuration.RecalculateSeekTable
                ? AudioStream.Channels.Where(
                    x => !x.SelfCalculatedSeekTable || x.SamplesPerSeekTableEntry != SamplesPerSeekTableEntry)
                : AudioStream.Channels.Where(
                    x => x.SeekTable == null || x.SamplesPerSeekTableEntry != SamplesPerSeekTableEntry);

            var loopContextToCalculate = Configuration.RecalculateLoopContext
                ? AudioStream.Channels.Where(x => !x.SelfCalculatedLoopContext)
                : AudioStream.Channels.Where(x => !x.LoopContextCalculated);

            if (AudioStream.Looping)
            {
                Decode.CalculateLoopAlignment(AudioStream.Channels, Configuration.LoopPointAlignment,
                    AudioStream.LoopStart, AudioStream.LoopEnd);
            }
            Decode.CalculateSeekTable(seekTableToCalculate, SamplesPerSeekTableEntry);
            Decode.CalculateLoopContext(loopContextToCalculate, AudioStream.Looping ? LoopStart : 0);
        }

        internal void WriteBCFstmFile(Stream stream, BCFstmType type)
        {
            if (stream.Length != FileLength)
            {
                try
                {
                    stream.SetLength(FileLength);
                }
                catch (NotSupportedException ex)
                {
                    throw new ArgumentException("Stream is too small.", nameof(stream), ex);
                }
            }

            Endianness endianness = type == BCFstmType.Bcstm ? Endianness.LittleEndian : Endianness.BigEndian;

            RecalculateData();

            using (BinaryWriter writer = endianness == Endianness.LittleEndian ?
                new BinaryWriter(stream, Encoding.UTF8, true) :
                new BinaryWriterBE(stream, Encoding.UTF8, true))
            {
                stream.Position = 0;
                GetHeader(writer, type);
                stream.Position = HeadChunkOffset;
                GetInfoChunk(writer, endianness);
                stream.Position = SeekChunkOffset;
                GetSeekChunk(writer);
                stream.Position = DataChunkOffset;
                GetDataChunk(writer);
            }
        }

        private void GetHeader(BinaryWriter writer, BCFstmType type)
        {
            writer.WriteUTF8(type == BCFstmType.Bcstm ? "CSTM" : "FSTM");
            writer.Write((ushort)0xfeff); //Endianness
            writer.Write((short)HeaderLength);
            writer.Write(GetVersion(type) << 16);
            writer.Write(FileLength);

            writer.Write((short)3); // NumEntries
            writer.Write((short)0);
            writer.Write((short)0x4000);
            writer.Write((short)0);
            writer.Write(InfoChunkOffset);
            writer.Write(InfoChunkLength);
            writer.Write((short)0x4001);
            writer.Write((short)0);
            writer.Write(SeekChunkOffset);
            writer.Write(SeekChunkLength);
            writer.Write((short)0x4002);
            writer.Write((short)0);
            writer.Write(DataChunkOffset);
            writer.Write(DataChunkLength);
        }

        private void GetInfoChunk(BinaryWriter writer, Endianness endianness)
        {
            writer.WriteUTF8("INFO");
            writer.Write(InfoChunkLength);

            int headerTableLength = 8 * 3;

            writer.Write((short)0x4100);
            writer.Write((short)0);
            writer.Write(headerTableLength);
            if (Configuration.IncludeTrackInformation)
            {
                writer.Write((short)0x0101);
                writer.Write((short)0);
                writer.Write(headerTableLength + InfoChunk1Length);
            }
            else
            {
                writer.Write(0);
                writer.Write(-1);
            }
            writer.Write((short)0x0101);
            writer.Write((short)0);
            writer.Write(headerTableLength + InfoChunk1Length + InfoChunk2Length);

            GetInfoChunk1(writer);
            GetInfoChunk2(writer);
            GetInfoChunk3(writer, endianness);
        }

        private void GetInfoChunk1(BinaryWriter writer)
        {
            writer.Write((byte)Codec);
            writer.Write(Looping);
            writer.Write((byte)NumChannels);
            writer.Write((byte)0);
            writer.Write(AudioStream.SampleRate);
            writer.Write(LoopStart);
            writer.Write(NumSamples);
            writer.Write(InterleaveCount);
            writer.Write(InterleaveSize);
            writer.Write(SamplesPerInterleave);
            writer.Write(LastBlockSizeWithoutPadding);
            writer.Write(LastBlockSamples);
            writer.Write(LastBlockSize);
            writer.Write(BytesPerSeekTableEntry);
            writer.Write(SamplesPerSeekTableEntry);
            writer.Write((short)0x1f00);
            writer.Write((short)0);
            writer.Write(0x18);

            if (Configuration.InfoPart1Extra)
            {
                writer.Write((short)0x0100);
                writer.Write((short)0);
                writer.Write(0);
                writer.Write(-1);
            }

            if (Configuration.IncludeUnalignedLoopPoints)
            {
                writer.Write(AudioStream.LoopStart);
                writer.Write(AudioStream.LoopEnd);
            }
        }

        private void GetInfoChunk2(BinaryWriter writer)
        {
            if (!Configuration.IncludeTrackInformation) return;

            int trackTableLength = 4 + 8 * NumTracks;
            int channelTableLength = 4 + 8 * NumChannels;
            int trackLength = 0x14;

            writer.Write(NumTracks);

            for (int i = 0; i < NumTracks; i++)
            {
                writer.Write((short)0x4101);
                writer.Write((short)0);
                writer.Write(trackTableLength + channelTableLength + trackLength * i);
            }
        }

        private void GetInfoChunk3(BinaryWriter writer, Endianness endianness)
        {
            int channelTableLength = 4 + 8 * NumChannels;
            int trackTableLength = Configuration.IncludeTrackInformation ? 0x14 * NumTracks : 0;

            writer.Write(NumChannels);
            for (int i = 0; i < NumChannels; i++)
            {
                writer.Write((short)0x4102);
                writer.Write((short)0);
                writer.Write(channelTableLength + trackTableLength + 8 * i);
            }

            if (Configuration.IncludeTrackInformation)
            {
                foreach (var track in AudioStream.Tracks)
                {
                    writer.Write((byte)track.Volume);
                    writer.Write((byte)track.Panning);
                    writer.Write((short)0);
                    writer.Write(0x0100);
                    writer.Write(0xc);
                    writer.Write(track.NumChannels);
                    writer.Write((byte)track.ChannelLeft);
                    writer.Write((byte)track.ChannelRight);
                    writer.Write((short)0);
                }
            }

            int channelTable2Length = 8 * NumChannels;
            for (int i = 0; i < NumChannels; i++)
            {
                writer.Write((short)0x0300);
                writer.Write((short)0);
                writer.Write(channelTable2Length - 8 * i + ChannelInfoLength * i);
            }

            foreach (var channel in AudioStream.Channels)
            {
                writer.Write(channel.Coefs.ToByteArray(endianness));
                writer.Write((short)channel.GetAudioData[0]);
                writer.Write(channel.Hist1);
                writer.Write(channel.Hist2);
                writer.Write(channel.LoopPredScale);
                writer.Write(channel.LoopHist1);
                writer.Write(channel.LoopHist2);
                writer.Write(channel.Gain);
            }
        }

        private void GetSeekChunk(BinaryWriter writer)
        {
            writer.WriteUTF8("SEEK");
            writer.Write(SeekChunkLength);

            var table = Decode.BuildSeekTable(AudioStream.Channels, SamplesPerSeekTableEntry, NumSeekTableEntries, Endianness.LittleEndian);

            writer.Write(table);
        }

        private void GetDataChunk(BinaryWriter writer)
        {
            writer.WriteUTF8("DATA");
            writer.Write(DataChunkLength);

            writer.BaseStream.Position = AudioDataOffset;

            byte[][] channels = AudioStream.Channels.Select(x => x.GetAudioData).ToArray();

            channels.Interleave(writer.BaseStream, GetBytesForAdpcmSamples(NumSamples), InterleaveSize, 0x20);
        }

        internal BcstmStructure ReadBcstmFile(Stream stream, bool readAudioData = true)
        {
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (Encoding.UTF8.GetString(reader.ReadBytes(4), 0, 4) != "CSTM")
                {
                    throw new InvalidDataException("File has no CSTM header");
                }

                BcstmStructure structure = new BcstmStructure();

                ParseHeader(reader, structure);
                ParseInfoChunk(reader, structure);
                ParseSeekChunk(reader, structure);
                ParseDataChunk(reader, structure, readAudioData);

                if (readAudioData)
                    SetProperties(structure);

                return structure;
            }
        }

        internal BfstmStructure ReadBfstmFile(Stream stream, bool readAudioData = true)
        {
            using (BinaryReader reader = new BinaryReaderBE(stream, Encoding.UTF8, true))
            {
                if (Encoding.UTF8.GetString(reader.ReadBytes(4), 0, 4) != "FSTM")
                {
                    throw new InvalidDataException("File has no FSTM header");
                }

                BfstmStructure structure = new BfstmStructure();

                ParseHeader(reader, structure);
                ParseInfoChunk(reader, structure);
                ParseSeekChunk(reader, structure);
                ParseDataChunk(reader, structure, readAudioData);

                if (readAudioData)
                    SetProperties(structure);

                return structure;
            }
        }

        private void SetProperties(BCFstmStructure structure)
        {
            Configuration.SamplesPerInterleave = structure.SamplesPerInterleave;
            Configuration.SamplesPerSeekTableEntry = structure.SamplesPerSeekTableEntry;
            Configuration.IncludeTrackInformation = structure.IncludeTracks;
            Configuration.InfoPart1Extra = structure.InfoPart1Extra;
            if (structure.Version == 4)
            {
                Configuration.IncludeUnalignedLoopPoints = true;
            }

            AudioStream = new AdpcmStream(structure.NumSamples, structure.SampleRate);
            if (structure.Looping)
            {
                AudioStream.SetLoop(structure.LoopStart, structure.NumSamples);
            }
            AudioStream.Tracks = structure.Tracks;

            for (int c = 0; c < structure.NumChannels; c++)
            {
                var channel = new AdpcmChannel(structure.NumSamples, structure.AudioData[c])
                {
                    Coefs = structure.Channels[c].Coefs,
                    Gain = structure.Channels[c].Gain,
                    Hist1 = structure.Channels[c].Hist1,
                    Hist2 = structure.Channels[c].Hist2,
                    SeekTable = structure.SeekTable?[c],
                    SamplesPerSeekTableEntry = structure.SamplesPerSeekTableEntry
                };
                channel.SetLoopContext(structure.Channels[c].LoopPredScale, structure.Channels[c].LoopHist1,
                    structure.Channels[c].LoopHist2);
                AudioStream.Channels.Add(channel);
            }
        }

        private static void ParseHeader(BinaryReader reader, BCFstmStructure structure)
        {
            reader.Expect((ushort)0xfeff);
            structure.HeaderLength = reader.ReadInt16();
            structure.Version = reader.ReadInt32() >> 16;
            structure.FileLength = reader.ReadInt32();

            if (reader.BaseStream.Length < structure.FileLength)
            {
                throw new InvalidDataException("Actual file length is less than stated length");
            }

            structure.CstmHeaderSections = reader.ReadInt16();
            reader.BaseStream.Position += 2;

            for (int i = 0; i < structure.CstmHeaderSections; i++)
            {
                int type = reader.ReadInt16();
                reader.BaseStream.Position += 2;
                switch (type)
                {
                    case 0x4000:
                        structure.InfoChunkOffset = reader.ReadInt32();
                        structure.InfoChunkLengthHeader = reader.ReadInt32();
                        break;
                    case 0x4001:
                        structure.SeekChunkOffset = reader.ReadInt32();
                        structure.SeekChunkLengthHeader = reader.ReadInt32();
                        break;
                    case 0x4002:
                        structure.DataChunkOffset = reader.ReadInt32();
                        structure.DataChunkLengthHeader = reader.ReadInt32();
                        break;
                    default:
                        throw new InvalidDataException($"Unknown section type {type}");
                }
            }
        }

        private static void ParseInfoChunk(BinaryReader reader, BCFstmStructure structure)
        {
            reader.BaseStream.Position = structure.InfoChunkOffset;
            if (Encoding.UTF8.GetString(reader.ReadBytes(4), 0, 4) != "INFO")
            {
                throw new InvalidDataException("Unknown or invalid INFO chunk");
            }

            structure.InfoChunkLength = reader.ReadInt32();
            if (structure.InfoChunkLength != structure.InfoChunkLengthHeader)
            {
                throw new InvalidDataException("INFO chunk length in CSTM header doesn't match length in INFO header");
            }

            reader.Expect((short)0x4100);
            reader.BaseStream.Position += 2;
            structure.InfoChunk1Offset = reader.ReadInt32();
            reader.Expect((short)0x0101, (short)0);
            reader.BaseStream.Position += 2;
            structure.InfoChunk2Offset = reader.ReadInt32();
            reader.Expect((short)0x0101);
            reader.BaseStream.Position += 2;
            structure.InfoChunk3Offset = reader.ReadInt32();

            ParseInfoChunk1(reader, structure);
            ParseInfoChunk2(reader, structure);
            ParseInfoChunk3(reader, structure);
        }

        private static void ParseInfoChunk1(BinaryReader reader, BCFstmStructure structure)
        {
            reader.BaseStream.Position = structure.InfoChunkOffset + 8 + structure.InfoChunk1Offset;
            structure.Codec = (BcstmCodec)reader.ReadByte();
            if (structure.Codec != BcstmCodec.Adpcm)
            {
                throw new InvalidDataException("File must contain 4-bit ADPCM encoded audio");
            }

            structure.Looping = reader.ReadByte() == 1;
            structure.NumChannels = reader.ReadByte();
            reader.BaseStream.Position += 1;

            structure.SampleRate = reader.ReadInt32();

            structure.LoopStart = reader.ReadInt32();
            structure.NumSamples = reader.ReadInt32();

            structure.InterleaveCount = reader.ReadInt32();
            structure.InterleaveSize = reader.ReadInt32();
            structure.SamplesPerInterleave = reader.ReadInt32();
            structure.LastBlockSizeWithoutPadding = reader.ReadInt32();
            structure.LastBlockSamples = reader.ReadInt32();
            structure.LastBlockSize = reader.ReadInt32();
            structure.BytesPerSeekTableEntry = reader.ReadInt32();
            structure.SamplesPerSeekTableEntry = reader.ReadInt32();

            reader.Expect((short)0x1f00);
            reader.BaseStream.Position += 2;
            structure.AudioDataOffset = reader.ReadInt32() + structure.DataChunkOffset + 8;
            structure.InfoPart1Extra = reader.ReadInt16() == 0x100;
            if (structure.InfoPart1Extra)
            {
                reader.BaseStream.Position += 10;
            }
            if (structure.Version == 4)
            {
                structure.LoopStartUnaligned = reader.ReadInt32();
                structure.LoopEndUnaligned = reader.ReadInt32();
            }
        }

        private static void ParseInfoChunk2(BinaryReader reader, BCFstmStructure structure)
        {
            if (structure.InfoChunk2Offset == -1)
            {
                structure.IncludeTracks = false;
                return;
            }

            structure.IncludeTracks = true;
            int part2Offset = structure.InfoChunkOffset + 8 + structure.InfoChunk2Offset;
            reader.BaseStream.Position = part2Offset;

            int numTracks = reader.ReadInt32();

            int[] trackOffsets = new int[numTracks];
            for (int i = 0; i < numTracks; i++)
            {
                reader.Expect((short)0x4101);
                reader.BaseStream.Position += 2;
                trackOffsets[i] = reader.ReadInt32();
            }

            foreach (int offset in trackOffsets)
            {
                reader.BaseStream.Position = part2Offset + offset;

                var track = new AdpcmTrack();
                track.Volume = reader.ReadByte();
                track.Panning = reader.ReadByte();
                reader.BaseStream.Position += 2;

                reader.BaseStream.Position += 8;
                track.NumChannels = reader.ReadInt32();
                track.ChannelLeft = reader.ReadByte();
                track.ChannelRight = reader.ReadByte();
                structure.Tracks.Add(track);
            }
        }

        private static void ParseInfoChunk3(BinaryReader reader, BCFstmStructure structure)
        {
            int part3Offset = structure.InfoChunkOffset + 8 + structure.InfoChunk3Offset;
            reader.BaseStream.Position = part3Offset;

            reader.Expect(structure.NumChannels);

            for (int i = 0; i < structure.NumChannels; i++)
            {
                var channel = new B_stmChannelInfo();
                reader.Expect((short)0x4102);
                reader.BaseStream.Position += 2;
                channel.Offset = reader.ReadInt32();
                structure.Channels.Add(channel);
            }

            foreach (B_stmChannelInfo channel in structure.Channels)
            {
                int channelInfoOffset = part3Offset + channel.Offset;
                reader.BaseStream.Position = channelInfoOffset;
                reader.Expect((short)0x0300);
                reader.BaseStream.Position += 2;
                int coefsOffset = reader.ReadInt32() + channelInfoOffset;
                reader.BaseStream.Position = coefsOffset;

                channel.Coefs = Enumerable.Range(0, 16).Select(x => reader.ReadInt16()).ToArray();
                channel.PredScale = reader.ReadInt16();
                channel.Hist1 = reader.ReadInt16();
                channel.Hist2 = reader.ReadInt16();
                channel.LoopPredScale = reader.ReadInt16();
                channel.LoopHist1 = reader.ReadInt16();
                channel.LoopHist2 = reader.ReadInt16();
                channel.Gain = reader.ReadInt16();
            }
        }

        private static void ParseSeekChunk(BinaryReader reader, BCFstmStructure structure)
        {
            reader.BaseStream.Position = structure.SeekChunkOffset;

            if (Encoding.UTF8.GetString(reader.ReadBytes(4), 0, 4) != "SEEK")
            {
                throw new InvalidDataException("Unknown or invalid SEEK chunk");
            }
            structure.SeekChunkLength = reader.ReadInt32();

            if (structure.SeekChunkLengthHeader != structure.SeekChunkLength)
            {
                throw new InvalidDataException("SEEK chunk length in header doesn't match length in SEEK header");
            }

            int bytesPerEntry = 4 * structure.NumChannels;
            int numSeekTableEntries = structure.NumSamples.DivideByRoundUp(structure.SamplesPerSeekTableEntry);

            structure.SeekTableLength = bytesPerEntry * numSeekTableEntries;

            byte[] tableBytes = reader.ReadBytes(structure.SeekTableLength);

            structure.SeekTable = tableBytes.ToShortArray()
                .DeInterleave(2, structure.NumChannels);
        }

        private static void ParseDataChunk(BinaryReader reader, BCFstmStructure structure, bool readAudioData)
        {
            reader.BaseStream.Position = structure.DataChunkOffset;

            if (Encoding.UTF8.GetString(reader.ReadBytes(4), 0, 4) != "DATA")
            {
                throw new InvalidDataException("Unknown or invalid DATA chunk");
            }
            structure.DataChunkLength = reader.ReadInt32();

            if (structure.DataChunkLengthHeader != structure.DataChunkLength)
            {
                throw new InvalidDataException("DATA chunk length in header doesn't match length in DATA header");
            }

            if (!readAudioData) return;

            reader.BaseStream.Position = structure.AudioDataOffset;
            int audioDataLength = structure.DataChunkLength - (structure.AudioDataOffset - structure.DataChunkOffset);

            structure.AudioData = reader.BaseStream.DeInterleave(audioDataLength, structure.InterleaveSize,
                structure.NumChannels);
        }

        internal enum BCFstmType
        {
            Bcstm,
            Bfstm
        }
    }

    internal class BCFstmConfiguration : B_stmConfiguration
    {
        /// <summary>
        /// If <c>true</c>, include track information in the BCSTM
        /// header. Default is <c>true</c>.
        /// </summary>
        public bool IncludeTrackInformation { get; set; } = true;
        /// <summary>
        /// If <c>true</c>, include an extra chunk in the header
        /// after the stream info and before the track offset table.
        /// The purpose of this chunk is unknown.
        /// Default is <c>false</c>.
        /// </summary>
        public bool InfoPart1Extra { get; set; }
        public bool IncludeUnalignedLoopPoints { get; set; }
    }
}
