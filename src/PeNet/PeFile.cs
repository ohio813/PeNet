﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PeNet.FileParser;
using PeNet.Header.Authenticode;
using PeNet.Header.ImpHash;
using PeNet.Header.Net;
using PeNet.Header.Pe;
using PeNet.Header.Resource;
using PeNet.HeaderParser.Authenticode;
using PeNet.HeaderParser.Net;
using PeNet.HeaderParser.Pe;

namespace PeNet
{
    /// <summary>
    ///     This class represents a Portable Executable (PE) file and makes the different
    ///     header and properties accessible.
    /// </summary>
    public partial class PeFile
    {
        private readonly DataDirectoryParsers _dataDirectoryParsers;
        private readonly NativeStructureParsers _nativeStructureParsers;
        private readonly DotNetStructureParsers _dotNetStructureParsers;
        private readonly AuthenticodeParser _authenticodeParser;


        /// <summary>
        ///     The PE binary .
        /// </summary>
        public IRawFile RawFile { get; }

        private string? _impHash;
        private string? _md5;
        private string? _sha1;
        private string? _sha256;
        private NetGuids? _netGuids;


        public PeFile(IRawFile peFile)
        {
            RawFile = peFile;

            _nativeStructureParsers = new NativeStructureParsers(RawFile);

            _dataDirectoryParsers = new DataDirectoryParsers(
                RawFile,
                ImageNtHeaders?.OptionalHeader?.DataDirectory,
                ImageSectionHeaders,
                Is32Bit
                );

            _dotNetStructureParsers = new DotNetStructureParsers(
                RawFile,
                ImageComDescriptor,
                ImageSectionHeaders
                );

            _authenticodeParser = new AuthenticodeParser(this);
        }

        /// <summary>
        ///     Create a new PeFile object.
        /// </summary>
        /// <param name="buff">A PE file a byte array.</param>
        public PeFile(byte[] buff)
            : this(new BufferFile(buff))
        { }

        /// <summary>
        ///     Create a new PeFile object.
        /// </summary>
        /// <param name="peFile">Path to a PE file.</param>
        public PeFile(string peFile)
            : this(File.ReadAllBytes(peFile))
        { }

        /// <summary>
        ///     Create a new PeFile object.
        /// </summary>
        /// <param name="peFile">Stream containing a PE file.</param>
        public PeFile(Stream peFile)
            : this(new StreamFile(peFile))
        { }

        /// <summary>
        /// Try to parse the PE file. Reads the whole file content into memory.
        /// </summary>
        /// <param name="file">Path to a possible PE file.</param>
        /// <param name="peFile">Parsed PE file or Null.</param>
        /// <returns>True if parable PE file and false if not.</returns>
        public static bool TryParse(string file, out PeFile? peFile)
        {
            return TryParse(File.ReadAllBytes(file), out peFile);
        }

        /// <summary>
        /// Try to parse the PE file.
        /// </summary>
        /// <param name="buff">Buffer containing a possible PE file.</param>
        /// <param name="peFile">Parsed PE file or Null.</param>
        /// <returns>True if parable PE file and false if not.</returns>
        public static bool TryParse(byte[] buff, out PeFile? peFile)
        {
            peFile = null;

            if (!IsPeFile(buff))
                return false;

            try { peFile = new PeFile(buff); }
            catch { return false; }

            return true;
        }


        /// <summary>
        /// Try to parse the PE file.
        /// </summary>
        /// <param name="buff">Stream containing a possible PE file.</param>
        /// <param name="peFile">Parsed PE file or Null.</param>
        /// <returns>True if parable PE file and false if not.</returns>
        public static bool TryParse(Stream file, out PeFile? peFile)
        {
            peFile = null;

            if (!IsPeFile(file))
                return false;

            try { peFile = new PeFile(file); }
            catch { return false; }

            return true;
        }

        /// <summary>
        /// Try to parse the PE file. Best option for large files,
        /// as a memory mapped file is used.
        /// </summary>
        /// <param name="buff">Memory mapped file containing a possible PE file.</param>
        /// <param name="peFile">Parsed PE file or Null.</param>
        /// <returns>True if parable PE file and false if not.</returns>
        public static bool TryParse(MMFile file, out PeFile? peFile)
        {
            peFile = null;

            if (!IsPeFile(file))
                return false;

            try { peFile = new PeFile(file); }
            catch { return false; }

            return true;
        }

        /// <summary>
        ///     Returns true if the DLL flag in the
        ///     File Header is set.
        /// </summary>
        public bool IsDll
            => ImageNtHeaders?.FileHeader?.Characteristics.HasFlag(FileCharacteristicsType.Dll) ?? false;


        /// <summary>
        ///     Returns true if the Executable flag in the
        ///     File Header is set.
        /// </summary>
        public bool IsExe
            => ImageNtHeaders?.FileHeader.Characteristics.HasFlag(FileCharacteristicsType.ExecutableImage) ?? false;

        /// <summary>
        /// Returns true if the file is a
        /// .NET executable.
        /// </summary>
        public bool IsDotNet
            => ImageComDescriptor != null;

        /// <summary>
        ///     Returns true if the PE file is a system driver
        ///     based on the Subsytem = 0x1 value in the Optional Header.
        /// </summary>
        public bool IsDriver => ImageNtHeaders?.OptionalHeader.Subsystem == SubsystemType.Native
                                && ImportedFunctions.FirstOrDefault(i => i.DLL == "ntoskrnl.exe") != null;

        /// <summary>
        ///     Returns true if the PE file is signed. It
        ///     does not check if the signature is valid!
        /// </summary>
        public bool IsSigned => Pkcs7 != null;

        /// <summary>
        ///     Returns true if the PE file signature is valid signed.
        /// </summary>
        public bool HasValidSignature => Authenticode?.IsAuthenticodeValid ?? false;

        /// <summary>
        ///     Checks if cert is from a trusted CA with a valid certificate chain.
        /// </summary>
        /// <param name="useOnlineCrl">Check certificate chain online or offline.</param>
        /// <returns>True if cert chain is valid and from a trusted CA.</returns>
        public bool HasValidCertChain(bool useOnlineCrl)
            => Authenticode?.SigningCertificate != null
                   && HasValidCertChain(Authenticode.SigningCertificate, new TimeSpan(0, 0, 0, 10), useOnlineCrl);

        /// <summary>
        ///     Checks if cert is from a trusted CA with a valid certificate chain.
        /// </summary>
        /// <param name="cert">X509 Certificate</param>
        /// <param name="urlRetrievalTimeout">Timeout to validate the certificate online.</param>
        /// <param name="useOnlineCRL">If true, uses online certificate revocation lists, else on the local CRL.</param>
        /// <param name="excludeRoot">True if the root certificate should not be validated. False if the whole chain should be validated.</param>
        /// <returns>True if cert chain is valid and from a trusted CA.</returns>
        public bool HasValidCertChain(X509Certificate2? cert, TimeSpan urlRetrievalTimeout, bool useOnlineCRL = true, bool excludeRoot = true)
        {
            using var chain = new X509Chain
            {
                ChainPolicy =
                {
                    RevocationFlag      = excludeRoot ? X509RevocationFlag.ExcludeRoot : X509RevocationFlag.EntireChain,
                    RevocationMode      = useOnlineCRL ? X509RevocationMode.Online : X509RevocationMode.Offline,
                    UrlRetrievalTimeout = urlRetrievalTimeout,
                    VerificationFlags   = X509VerificationFlags.NoFlag
                }
            };
            return chain.Build(cert);
        }

        /// <summary>
        /// Information about a possible Authenticode binary signature.
        /// </summary>
        public AuthenticodeInfo? Authenticode => _authenticodeParser.ParseTarget();

        /// <summary>
        ///     Returns true if the PE file is x64.
        /// </summary>
        public bool Is64Bit => RawFile.Is64Bit();

        /// <summary>
        ///     Returns true if the PE file is x32.
        /// </summary>
        public bool Is32Bit => RawFile.Is32Bit();
                               
        /// <summary>
        ///     Access the ImageDosHeader of the PE file.
        /// </summary>
        public ImageDosHeader? ImageDosHeader => _nativeStructureParsers.ImageDosHeader;

        /// <summary>
        ///     Access the ImageNtHeaders of the PE file.
        /// </summary>
        public ImageNtHeaders? ImageNtHeaders => _nativeStructureParsers.ImageNtHeaders;

        /// <summary>
        ///     Access the ImageSectionHeader of the PE file.
        /// </summary>
        public ImageSectionHeader[]? ImageSectionHeaders => _nativeStructureParsers.ImageSectionHeaders;

        /// <summary>
        /// Remove a section from the PE file.
        /// </summary>
        /// <param name="name">Name of the section to remove.</param>
        /// <param name="removeContent">Flag if the content should be removed or only the section header entry.</param>
        public void RemoveSection(string name, bool removeContent = true)
        {
            var sectionToRemove = ImageSectionHeaders.First(s => s.Name == name);

            // Remove section from list of sections
            var newSections = ImageSectionHeaders.Where(s => s.Name != name).ToArray();

            // Change number of sections in the file header
            ImageNtHeaders!.FileHeader.NumberOfSections--;

            if (removeContent)
            {
                // Reloc the physical address of all sections
                foreach (var s in newSections)
                {
                    if (s.PointerToRawData > sectionToRemove.PointerToRawData)
                    {
                        s.PointerToRawData -= sectionToRemove.SizeOfRawData;
                    }
                }

                // Remove section content
                RawFile.RemoveRange(sectionToRemove.PointerToRawData, sectionToRemove.SizeOfRawData);
            }

            // Fix virtual size
            for (var i = 1; i < newSections.Count(); i++)
            {
                if(newSections[i - 1].VirtualAddress < sectionToRemove.VirtualAddress)
                {
                    newSections[i - 1].VirtualSize = newSections[i].VirtualAddress - newSections[i - 1].VirtualAddress;
                }
            }

            // Replace old section headers with new section headers
            var sectionHeaderOffset = ImageDosHeader!.E_lfanew + ImageNtHeaders!.FileHeader.SizeOfOptionalHeader + 0x18;
            var sizeOfSection = 0x28;
            var newRawSections = new byte[newSections.Count() * sizeOfSection];
            for (var i = 0; i < newSections.Count(); i++)
            {
                Array.Copy(newSections[i].ToArray(), 0, newRawSections, i * sizeOfSection, sizeOfSection);
            }

            // Null the data directory entry if any available
            var de = ImageNtHeaders
                .OptionalHeader
                .DataDirectory
                .FirstOrDefault(d => d.VirtualAddress == sectionToRemove.VirtualAddress
                    && d.Size == sectionToRemove.VirtualSize);

            if (de != null)
            {
                de.Size = 0;
                de.VirtualAddress = 0;
            }

            // Null the old section headers
            RawFile.WriteBytes(sectionHeaderOffset, new byte[ImageSectionHeaders.Count() * sizeOfSection]);

            // Write the new sections headers
            RawFile.WriteBytes(sectionHeaderOffset, newRawSections);

            // Reparse section header
            _nativeStructureParsers.ReparseSectionHeaders();
        }

        /// <summary>
        ///     Access the ImageExportDirectory of the PE file.
        /// </summary>
        public ImageExportDirectory? ImageExportDirectory => _dataDirectoryParsers.ImageExportDirectories;

        /// <summary>
        ///     Access the ImageImportDescriptor array of the PE file.
        /// </summary>
        public ImageImportDescriptor[]? ImageImportDescriptors => _dataDirectoryParsers.ImageImportDescriptors;

        /// <summary>
        ///     Access the ImageBaseRelocation array of the PE file.
        /// </summary>
        public ImageBaseRelocation[]? ImageRelocationDirectory => _dataDirectoryParsers.ImageBaseRelocations;

        /// <summary>
        ///     Access the ImageDebugDirectory of the PE file.
        /// </summary>
        public ImageDebugDirectory[]? ImageDebugDirectory => _dataDirectoryParsers.ImageDebugDirectory;

        /// <summary>
        ///     Access the exported functions as an array of parsed objects.
        /// </summary>
        public ExportFunction[]? ExportedFunctions => _dataDirectoryParsers.ExportFunctions;

        /// <summary>
        ///     Access the imported functions as an array of parsed objects.
        /// </summary>
        public ImportFunction[]? ImportedFunctions => _dataDirectoryParsers.ImportFunctions;

        /// <summary>
        ///     Access the ImageResourceDirectory of the PE file.
        /// </summary>
        public ImageResourceDirectory? ImageResourceDirectory => _dataDirectoryParsers.ImageResourceDirectory;

        /// <summary>
        ///     Access resources of the PE file.
        /// </summary>
        public Resources? Resources => _dataDirectoryParsers.Resources;

        /// <summary>
        ///     Access the array of RuntimeFunction from the Exception header.
        /// </summary>
        public RuntimeFunction[]? ExceptionDirectory => _dataDirectoryParsers.RuntimeFunctions;

        /// <summary>
        ///     Access the WinCertificate from the Security header.
        /// </summary>
        public WinCertificate? WinCertificate => _dataDirectoryParsers.WinCertificate;

        /// <summary>
        /// Access the IMAGE_BOUND_IMPORT_DESCRIPTOR form the data directory.
        /// </summary>
        public ImageBoundImportDescriptor? ImageBoundImportDescriptor => _dataDirectoryParsers.ImageBoundImportDescriptor;

        /// <summary>
        /// Access the IMAGE_TLS_DIRECTORY from the data directory.
        /// </summary>
        public ImageTlsDirectory? ImageTlsDirectory => _dataDirectoryParsers.ImageTlsDirectory;

        /// <summary>
        /// Access the ImageDelayImportDirectory from the data directory.
        /// </summary>
        public ImageDelayImportDescriptor? ImageDelayImportDescriptor => _dataDirectoryParsers.ImageDelayImportDescriptor;

        /// <summary>
        /// Access the ImageLoadConfigDirectory from the data directory.
        /// </summary>
        public ImageLoadConfigDirectory? ImageLoadConfigDirectory => _dataDirectoryParsers.ImageLoadConfigDirectory;

        /// <summary>
        /// Access the ImageCor20Header (COM Descriptor/CLI) from the data directory.
        /// </summary>
        public ImageCor20Header? ImageComDescriptor => _dataDirectoryParsers.ImageComDescriptor;

        /// <summary>
        ///     Signing X509 certificate if the binary was signed with
        /// </summary>
        public X509Certificate2? Pkcs7 => Authenticode?.SigningCertificate;

        /// <summary>
        ///     Access the MetaDataHdr from the COM/CLI header.
        /// </summary>
        public MetaDataHdr? MetaDataHdr => _dotNetStructureParsers.MetaDataHdr;

        /// <summary>
        /// Meta Data Stream #String.
        /// </summary>
        public MetaDataStreamString? MetaDataStreamString => _dotNetStructureParsers.MetaDataStreamString;

        /// <summary>
        /// Meta Data Stream #US (User strings).
        /// </summary>
        public MetaDataStreamUs? MetaDataStreamUs => _dotNetStructureParsers.MetaDataStreamUs;

        /// <summary>
        /// Meta Data Stream #GUID.
        /// </summary>
        public MetaDataStreamGuid? MetaDataStreamGuid => _dotNetStructureParsers.MetaDataStreamGuid;

        /// <summary>
        /// Meta Data Stream #Blob as an byte array.
        /// </summary>
        public byte[]? MetaDataStreamBlob => _dotNetStructureParsers.MetaDataStreamBlob;

        /// <summary>
        ///     Access the Meta Data Stream Tables Header from the list of
        ///     Meta Data Streams of the .Net header.
        /// </summary>
        public MetaDataTablesHdr? MetaDataStreamTablesHeader => _dotNetStructureParsers.MetaDataStreamTablesHeader;

        /// <summary>
        ///     The SHA-256 hash sum of the binary.
        /// </summary>
        public string Sha256
            => _sha256 ??= ComputeHash(RawFile, HashAlgorithmName.SHA256, 32);


        /// <summary>
        ///     The SHA-1 hash sum of the binary.
        /// </summary>
        public string Sha1
            => _sha1 ??= ComputeHash(RawFile, HashAlgorithmName.SHA1, 20);

        /// <summary>
        ///     The MD5 of hash sum of the binary.
        /// </summary>
        public string Md5
            => _md5 ??= ComputeHash(RawFile, HashAlgorithmName.MD5, 16);

        /// <summary>
        ///     The Import Hash of the binary if any imports are
        ///     given else null;
        /// </summary>
        public string? ImpHash
            => _impHash ??= new ImportHash(ImportedFunctions)?.ImpHash;

        /// <summary>
        ///     The Version ID of each module
        ///     if the PE is a CLR assembly.
        /// </summary>
        public List<Guid>? ClrModuleVersionIds
            => (_netGuids ??= new NetGuids(this)).ModuleVersionIds;

        /// <summary>
        ///     The COM TypeLib ID of the assembly, if specified,
        ///     and if the PE is a CLR assembly.
        /// </summary>
        public Guid? ClrComTypeLibId
            => (_netGuids ??= new NetGuids(this)).ComTypeLibId;

        /// <summary>
        ///     Returns the file size in bytes.
        /// </summary>
        public long FileSize => RawFile.Length;

        /// <summary>
        ///     Get an object which holds information about
        ///     the Certificate Revocation Lists of the signing
        ///     certificate if any is present.
        /// </summary>
        /// <returns>Certificate Revocation List information or null if binary is not signed.</returns>
        public CrlUrlList? GetCrlUrlList()
        {
            if (Pkcs7 == null)
                return null;

            try
            {
                return new CrlUrlList(Pkcs7);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        ///     Tests if a file is a PE file based on the MZ
        ///     header. It is not checked if the PE file is correct
        ///     in all other parts.
        /// </summary>
        /// <param name="file">Path to a possible PE file.</param>
        /// <returns>True if the MZ header is set.</returns>
        public static bool IsPeFile(string file)
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read);
            return IsPeFile(fs);
        }

        /// <summary>
        ///     Tests if a file is a PE file based on the MZ
        ///     header. It is not checked if the PE file is correct
        ///     in all other parts.
        /// </summary>
        /// <param name="file">Stream of a possible PE file.</param>
        /// <returns>True if the MZ header is set.</returns>
        public static bool IsPeFile(Stream file)
        {
            Span<byte> buffer = stackalloc byte[2];
            file.Seek(0, SeekOrigin.Begin);
            file.Read(buffer);
            return IsPeFile(buffer);
        }

        /// <summary>
        ///     Tests if a file is a PE file based on the MZ
        ///     header. It is not checked if the PE file is correct
        ///     in all other parts.
        /// </summary>
        /// <param name="file">MMFile of a possible PE file.</param>
        /// <returns>True if the MZ header is set.</returns>
        public static bool IsPeFile(MMFile file)
        {
            if (file.Length < 2)
                return false;

            Span<byte> buffer = file.AsSpan(0, 2);
            return IsPeFile(buffer);
        }

        /// <summary>
        ///     Tests is a buffer is a PE file based on the MZ
        ///     header. It is not checked if the PE file is correct
        ///     in all other parts.
        /// </summary>
        /// <param name="buf">Byte array containing a possible PE file.</param>
        /// <returns>True if the MZ header is set.</returns>
        public static bool IsPeFile(Span<byte> buf)
        {
            if (buf.Length < 2)
                return false;

            return buf[1] == 0x5a && buf[0] == 0x4d; // MZ Header
        }

        private string ComputeHash(IRawFile peFile, HashAlgorithmName hashAlg, int hashLength)
        {
            using var ha = HashAlgorithm.Create(hashAlg.Name);
            Span<byte> hash = stackalloc byte[hashLength];
            ha.TryComputeHash(RawFile.AsSpan(0, RawFile.Length), hash, out int _);

            var sBuilder = new StringBuilder();
            foreach (var t in hash)
                sBuilder.Append(t.ToString("x2"));

            return sBuilder.ToString();
        }

        public void AddSection(string name, int size, ScnCharacteristicsType characteristics)
        {
            if (ImageNtHeaders is null)
                throw new Exception("IMAGE_NT_HEADERS must not be null.");
            if (ImageDosHeader is null)
                throw new Exception("IMAGE_DOS_HEADER must not be null");

            uint getNewSizeOfImage()
            {
                var factor = size / (double)ImageNtHeaders.OptionalHeader.SectionAlignment;
                var additionalSize = (uint) Math.Ceiling(factor) * ImageNtHeaders!.OptionalHeader.SectionAlignment;
                return ImageNtHeaders.OptionalHeader.SizeOfImage + additionalSize;
            }

            uint getNewSecHeaderOffset()
            {
                var sizeOfSection = 0x28;
                var x = (uint)ImageNtHeaders!.FileHeader.SizeOfOptionalHeader + 0x18;
                var startOfSectionHeader = ImageDosHeader.E_lfanew + x;
                return (uint)(startOfSectionHeader + (ImageNtHeaders.FileHeader.NumberOfSections * sizeOfSection));
            }

            uint getNewSecVA()
            {
                var lastSec = ImageSectionHeaders.OrderByDescending(sh => sh.VirtualAddress).First();
                var vaLastSecEnd = lastSec.VirtualAddress + lastSec.VirtualSize;
                var factor = vaLastSecEnd / (double)ImageNtHeaders.OptionalHeader.SectionAlignment;
                return (uint)(Math.Ceiling(factor) * ImageNtHeaders.OptionalHeader.SectionAlignment);
            }



            // Append new section to end of file
            var paNewSec = RawFile.AppendBytes(new Byte[size]);

            // Add new entry in section table
            var newSection = new ImageSectionHeader(RawFile, getNewSecHeaderOffset(), ImageNtHeaders.OptionalHeader.ImageBase)
            {
                Name = name,
                VirtualSize = (uint)size,
                VirtualAddress = getNewSecVA(),
                SizeOfRawData = (uint)size,
                PointerToRawData = (uint)paNewSec,
                PointerToRelocations = 0,
                PointerToLinenumbers = 0,
                NumberOfRelocations = 0,
                NumberOfLinenumbers = 0,
                Characteristics = characteristics
            };

            // Increase number of sections
            ImageNtHeaders.FileHeader.NumberOfSections = (ushort)(ImageNtHeaders.FileHeader.NumberOfSections + 1);

            // Adjust image size by image alignment
            ImageNtHeaders.OptionalHeader.SizeOfImage = getNewSizeOfImage();

            // Reparse section headers
            _nativeStructureParsers.ReparseSectionHeaders();
        }

       

        public void AddImports(List<AdditionalImport> additionalImports)
        {
            if (ImageNtHeaders is null)
                throw new Exception();


            // Throw exception if one of the module to import already exists
            if(ImportedFunctions.Select(i => i.DLL).Distinct().Intersect(additionalImports.Select(i => i.Module)).Any())
            {
                throw new ArgumentException("Module already imported. Currently only imports from new modules are allowed.");
            }

            var sizeOfImpDesc = 0x14;
            var sizeOfThunkData = Is32Bit ? 4 : 8;
            var importRva = ImageNtHeaders.OptionalHeader.DataDirectory[(int)DataDirectoryType.Import].VirtualAddress;
            var importSize = ImageNtHeaders.OptionalHeader.DataDirectory[(int)DataDirectoryType.Import].Size;

            ImageSectionHeader getImportSection()
                => ImageSectionHeaders.First(sh => sh.VirtualAddress + sh.VirtualSize >= importRva);


            var impSection = getImportSection();

            int estimateAdditionalNeededSpace()
                => additionalImports.Select(ai => ai.Functions).Count() * 64;

            var additionalSpace = estimateAdditionalNeededSpace();

            // First copy the current import section to a new section with additional space
            // for the new import
            AddSection(".addImp", (int)(impSection!.SizeOfRawData + additionalSpace), impSection.Characteristics);
            var newImpSec = ImageSectionHeaders.First(sh => sh.Name == ".addImp");
            var oldImpSecBytes = RawFile.AsSpan(impSection.PointerToRawData, impSection.SizeOfRawData);
            RawFile.WriteBytes(newImpSec.PointerToRawData, oldImpSecBytes);
            var paAdditionalSpace = newImpSec.PointerToRawData + oldImpSecBytes.Length;

            // Set the import data directory to the new import section and adjust the size
            ImageNtHeaders.OptionalHeader.DataDirectory[(int)DataDirectoryType.Import].VirtualAddress = importRva - impSection.VirtualAddress + newImpSec.VirtualAddress;
            ImageNtHeaders.OptionalHeader.DataDirectory[(int)DataDirectoryType.Import].Size = (uint)(impSection.SizeOfRawData + additionalSpace);
            var newImportRva = ImageNtHeaders.OptionalHeader.DataDirectory[(int)DataDirectoryType.Import].VirtualAddress;


            uint AddModName(ref uint offset, string module)
            {
                var tmp = Encoding.ASCII.GetBytes(module);
                var mName = new byte[tmp.Length + 1];
                Array.Copy(tmp, mName, tmp.Length);

                var paName = offset;
                RawFile.WriteBytes(offset, mName);

                offset = (uint)(offset + mName.Length);
                return paName;
            }

            List<uint> AddImpByNames(ref uint offset, List<string> funcs)
            {
                var adrList = new List<uint>();
                foreach(var f in funcs)
                {
                    var ibn = new ImageImportByName(RawFile, offset)
                    {
                        Hint = 0,
                        Name = f
                    };

                    adrList.Add(offset);

                    offset += (uint) ibn.Name.Length + 2;
                }

                // Add zero DWORD to end array
                RawFile.WriteUInt(offset + 1, 0);
                offset += 5;

                return adrList;
            }

            uint AddThunkDatas(ref uint offset, List<uint> adrList)
            {
                var paThunkStart = offset;

                foreach(var adr in adrList)
                {
                    new ImageThunkData(RawFile, offset, Is64Bit)
                    {
                        AddressOfData = adr.OffsetToRva(ImageSectionHeaders!)
                    };

                    offset += (uint) sizeOfThunkData;
                }

                // End array with empty thunk data
                new ImageThunkData(RawFile, offset, Is64Bit)
                {
                    AddressOfData = 0
                };

                offset += (uint) sizeOfThunkData;

                return paThunkStart;
            }

            var paIdesc = newImportRva.RvaToOffset(ImageSectionHeaders!) + ImageImportDescriptors!.Length * sizeOfImpDesc;
            var tmpOffset = (uint) paAdditionalSpace;
            foreach(var ai in additionalImports)
            {
                var paName = AddModName(ref tmpOffset, ai.Module);
                var funcAdrs = AddImpByNames(ref tmpOffset, ai.Functions);
                var thunkAdrs = AddThunkDatas(ref tmpOffset, funcAdrs);

                new ImageImportDescriptor(RawFile, paIdesc)
                {
                    Name = paName.OffsetToRva(ImageSectionHeaders!),
                    OriginalFirstThunk = thunkAdrs.OffsetToRva(ImageSectionHeaders!),
                    FirstThunk = thunkAdrs.OffsetToRva(ImageSectionHeaders!),
                    ForwarderChain = 0,
                    TimeDateStamp = 0
                };
                paIdesc += (uint) sizeOfImpDesc;
            }

            // End with zero filled idesc
            new ImageImportDescriptor(RawFile, paIdesc)
            {
                Name = 0,
                OriginalFirstThunk = 0,
                FirstThunk = 0,
                ForwarderChain = 0,
                TimeDateStamp = 0
            };


            /* Add additional imports to new import section
                - For each module to import from, add an IMPORT_DESCRIPTOR
                    - Let OriginalThunk and FirstThunk point to array of RVAs (THUNK_DATA) which point to a hint and the function name (IMPORT_BY_NAME)
                        - THUNK_DATA array must be zero terminated with 0x0000_0000
                        - Function name strings are zero terminated 0x00
                - Add zero filled IMPORT_DESCRIPTOR to end of import table
            */


            // Reparse imports
            _dataDirectoryParsers.ReparseImportDescriptors(ImageSectionHeaders!);
            _dataDirectoryParsers.ReparseImportedFunctions();
        }
    }
}