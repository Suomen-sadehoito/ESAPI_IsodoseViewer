using EQD2Viewer.Core.Data;
using EQD2Viewer.Registration.Services;
using FluentAssertions;
using System;
using System.IO;
using System.Text;

namespace EQD2Viewer.Tests.Registration
{
    /// <summary>
    /// Unit tests for MhaReader.
    /// Generates minimal in-memory MHA files and verifies parsed output.
    /// </summary>
    public class MhaReaderTests
    {
        private readonly MhaReader _reader = new MhaReader();

        // ── Helpers ──────────────────────────────────────────────────────

        /// <summary>Creates a minimal MHA byte array with the given header map and binary payload.</summary>
        private static byte[] BuildMha(string header, byte[] payload)
        {
            var headerBytes = Encoding.ASCII.GetBytes(header);
            var result = new byte[headerBytes.Length + payload.Length];
            headerBytes.CopyTo(result, 0);
            payload.CopyTo(result, headerBytes.Length);
            return result;
        }

        private static string WriteTempMha(byte[] data)
        {
            string path = Path.GetTempFileName() + ".mha";
            File.WriteAllBytes(path, data);
            return path;
        }

        // ── DeformationField ─────────────────────────────────────────────

        [Fact]
        public void ReadDeformationField_MinimalDvf_ParsesDimensions()
        {
            // 2×2×1 DVF, each voxel = (1.0, 2.0, 3.0) mm
            const int xSize = 2, ySize = 2, zSize = 1, ch = 3;
            int voxelCount = xSize * ySize * zSize;
            var payload = new byte[voxelCount * ch * 4];
            for (int i = 0; i < voxelCount; i++)
            {
                int off = i * ch * 4;
                BitConverter.GetBytes(1.0f).CopyTo(payload, off);
                BitConverter.GetBytes(2.0f).CopyTo(payload, off + 4);
                BitConverter.GetBytes(3.0f).CopyTo(payload, off + 8);
            }

            string header =
                "ObjectType = Image\r\n" +
                "NDims = 3\r\n" +
                "DimSize = 2 2 1\r\n" +
                "ElementType = MET_FLOAT\r\n" +
                "ElementNumberOfChannels = 3\r\n" +
                "ElementSpacing = 1.5 1.5 3.0\r\n" +
                "Offset = 10 20 30\r\n" +
                "TransformMatrix = 1 0 0 0 1 0 0 0 1\r\n" +
                "ElementDataFile = LOCAL\r\n";

            string path = WriteTempMha(BuildMha(header, payload));
            try
            {
                var dvf = _reader.ReadDeformationField(path);

                dvf.Should().NotBeNull();
                dvf!.XSize.Should().Be(2);
                dvf.YSize.Should().Be(2);
                dvf.ZSize.Should().Be(1);
                dvf.XRes.Should().BeApproximately(1.5, 1e-6);
                dvf.YRes.Should().BeApproximately(1.5, 1e-6);
                dvf.ZRes.Should().BeApproximately(3.0, 1e-6);
                dvf.Origin.X.Should().BeApproximately(10, 1e-6);
                dvf.Origin.Y.Should().BeApproximately(20, 1e-6);
                dvf.Origin.Z.Should().BeApproximately(30, 1e-6);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void ReadDeformationField_VectorValues_ParsedCorrectly()
        {
            const int xSize = 1, ySize = 1, zSize = 1, ch = 3;
            var payload = new byte[xSize * ySize * zSize * ch * 4];
            BitConverter.GetBytes(5.5f).CopyTo(payload, 0);
            BitConverter.GetBytes(-3.0f).CopyTo(payload, 4);
            BitConverter.GetBytes(7.25f).CopyTo(payload, 8);

            string header =
                "ObjectType = Image\r\n" +
                "NDims = 3\r\n" +
                "DimSize = 1 1 1\r\n" +
                "ElementType = MET_FLOAT\r\n" +
                "ElementNumberOfChannels = 3\r\n" +
                "ElementSpacing = 1.0 1.0 1.0\r\n" +
                "Offset = 0 0 0\r\n" +
                "ElementDataFile = LOCAL\r\n";

            string path = WriteTempMha(BuildMha(header, payload));
            try
            {
                var dvf = _reader.ReadDeformationField(path);

                dvf.Should().NotBeNull();
                var v = dvf!.Vectors[0][0, 0];
                v.X.Should().BeApproximately(5.5, 1e-5);
                v.Y.Should().BeApproximately(-3.0, 1e-5);
                v.Z.Should().BeApproximately(7.25, 1e-5);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void ReadDeformationField_MissingFile_ReturnsNull()
        {
            var result = _reader.ReadDeformationField(@"C:\nonexistent\field.mha");
            result.Should().BeNull();
        }

        [Fact]
        public void ReadDeformationField_WrongChannelCount_ReturnsNull()
        {
            // 1-channel field (not a DVF)
            string header =
                "ObjectType = Image\r\n" +
                "NDims = 3\r\n" +
                "DimSize = 1 1 1\r\n" +
                "ElementType = MET_FLOAT\r\n" +
                "ElementNumberOfChannels = 1\r\n" +
                "ElementSpacing = 1.0 1.0 1.0\r\n" +
                "Offset = 0 0 0\r\n" +
                "ElementDataFile = LOCAL\r\n";
            var payload = new byte[4];
            string path = WriteTempMha(BuildMha(header, payload));
            try
            {
                var result = _reader.ReadDeformationField(path);
                result.Should().BeNull();
            }
            finally { File.Delete(path); }
        }

        // ── IDeformationFieldLoader ──────────────────────────────────────

        [Fact]
        public void Load_MissingFile_ReturnsNullWithoutThrowing()
        {
            Action act = () => _reader.Load(@"C:\does\not\exist.mha");
            act.Should().NotThrow();
            _reader.Load(@"C:\does\not\exist.mha").Should().BeNull();
        }
    }
}
