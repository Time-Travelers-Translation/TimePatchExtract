using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kontract.Extensions;
using Kontract.Interfaces.FileSystem;
using Kontract.Interfaces.Managers;
using Kontract.Interfaces.Providers;
using Kontract.Models.Archive;
using Kontract.Models.Context;
using Kore.Factories;
using Kore.Managers;
using plugin_criware.Archives;
using plugin_nintendo.Archives;
using VCDiff.Decoders;
using VCDiff.Includes;

namespace TimePatchExtract
{
    class Program
    {
        private const string Welcome_ =
            """
            ##################################################
            # This is the Time Travelers Patch Extractor.    #
            # This tool extracts a patch file containing     #
            # delta-diffed files to an output folder to      #
            # allow easy access to the changed files.        #
            ##################################################
            """;

        private static async Task Main(string[] args)
        {
            // Print welcome text
            Console.WriteLine(Welcome_);

            // Get path arguments
            GetPathArguments(args, out var gamePath, out var patchPath, out var outputPath);

            // Apply patch
            string outputFolder = await ApplyPatch(gamePath, patchPath, outputPath);

            // Finish up
            if (outputFolder != null)
            {
                Console.WriteLine();
                Console.WriteLine($"The extracted files can be found in \"{Path.GetFullPath(outputFolder)}\".");
            }

            if (args.Length < 3)
            {
                Console.WriteLine();
                Console.WriteLine("Press any key to close this application.");
                Console.ReadKey();
            }
        }

        private static void GetPathArguments(string[] args, out string gamePath, out string patchPath, out string outputPath)
        {
            // Get cia or 3ds game path
            Console.WriteLine("Enter the file path to Time Travelers (.3ds or .cia):");
            Console.Write("> ");

            gamePath = args.Length > 0 ? args[0] : Console.ReadLine();
            gamePath = gamePath?.Trim('"').Trim() ?? string.Empty;

            if (args.Length > 0)
                Console.WriteLine(args[0]);
            Console.WriteLine();

            // Get patch file
            Console.WriteLine("Enter the file path to the patch file (.pat):");
            Console.Write("> ");

            patchPath = args.Length > 1 ? args[1] : Console.ReadLine();
            patchPath = patchPath?.Trim('"').Trim() ?? string.Empty;

            if (args.Length > 1)
                Console.WriteLine(args[1]);
            Console.WriteLine();

            // Get output path
            Console.WriteLine("Enter any directory, in which the patched files will be extracted to:");
            Console.Write("> ");

            outputPath = args.Length > 2 ? args[2] : Console.ReadLine();
            outputPath = outputPath?.Trim('"').Trim() ?? string.Empty;

            if (args.Length > 2)
                Console.WriteLine(args[2]);
            Console.WriteLine();
        }
        
        private static async Task<string> ApplyPatch(string gamePath, string patchPath, string outputPath)
        {
            // Try to open game
            Console.Write("Opening game... ");

            var partitions = await LoadGamePartitions(gamePath);
            if (partitions == null)
                return null;

            // Create output folder
            Directory.CreateDirectory(outputPath);

            // Try to open and load GameData.cxi
            IArchiveFileInfo gameDataFile = partitions.FirstOrDefault(x => x.FilePath == "/GameData.cxi");
            if (gameDataFile == null)
            {
                Console.WriteLine($"Could not find GameData.cxi in \"{gamePath}\".");
                return null;
            }

            Stream gameDataFileStream = await gameDataFile.GetFileData();

            if (!TryLoadGameFiles(gameDataFileStream, gamePath, out IList<IArchiveFileInfo> gameFiles))
                return null;

            // Try to open and load tt1_ctr.cpk
            IArchiveFileInfo cpkArchiveFile = gameFiles.FirstOrDefault(x => x.FilePath == "/RomFs/tt1_ctr.cpk");
            if (cpkArchiveFile == null)
            {
                Console.WriteLine($"Could not find tt1_ctr.cpk in \"{gamePath}\".");
                return null;
            }

            Stream cpkArchiveFileStream = await cpkArchiveFile.GetFileData();

            if (!TryLoadCpkFiles(cpkArchiveFileStream, gamePath, out _, out IList<IArchiveFileInfo> cpkFiles))
                return null;

            Console.WriteLine("Done");

            // Try to open patch file
            Console.Write("Opening patch file... ");

            if (!TryLoadPatch(patchPath, out PatchFile patchFile))
                return null;

            Console.WriteLine("Done");

            // Extract patched files
            Console.Write($"Extract patched files to {outputPath}... ");

            var cpkOutputPath = Path.Combine(outputPath, "tt1_ctr");
            
            foreach (IArchiveFileInfo cpkFile in cpkFiles)
            {
                if (!patchFile.HasPatch(cpkFile.FilePath.FullName))
                {
                    // Ignore file in cpk, if no patch exists
                    continue;
                }

                // Otherwise apply VCDiff patch
                var source = await cpkFile.GetFileData();
                var delta = patchFile.GetPatch(cpkFile.FilePath.FullName);
                var output = new MemoryStream();

                var coder = new VcDecoder(source, delta, output);
                var result = coder.Decode(out _);

                delta.Close();

                if (result != VCDiffResult.SUCCESS)
                {
                    Console.WriteLine($"An error occurred applying the patch to \"{cpkFile}\" ({result}).");
                    output.Close();

                    continue;
                }

                // Extract patched file to output folder
                var cpkFileOutputPath = Path.Combine(cpkOutputPath, cpkFile.FilePath.ToRelative().FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(cpkFileOutputPath)!);

                await using var patchedFileOutputStream = File.Create(cpkFileOutputPath);

                output.Position = 0;
                await output.CopyToAsync(patchedFileOutputStream);
            }

            Console.WriteLine("Done");

            // Apply code.bin patches
            Console.Write("Apply patches to code.bin... ");

            if (patchFile.HasPatch(".code"))
            {
                IArchiveFileInfo codeFile = gameFiles.FirstOrDefault(x => x.FilePath == "/ExeFs/.code");
                if (codeFile != null)
                {
                    await using Stream codeFileStream = await codeFile.GetFileData();

                    var delta = patchFile.GetPatch(".code");
                    var output = new MemoryStream();

                    var coder = new VcDecoder(codeFileStream, delta, output);
                    var result = coder.Decode(out _);

                    delta.Close();
                    codeFileStream.Close();

                    if (result == VCDiffResult.SUCCESS)
                    {
                        // Save .code to output
                        var codeOutput = File.OpenWrite(Path.Combine(outputPath, "code.bin"));

                        output.Position = 0;
                        await output.CopyToAsync(codeOutput);

                        output.Close();
                        codeOutput.Close();
                    }
                    else
                    {
                        Console.WriteLine("An error occurred applying the patch to \".code\".");
                        output.Close();
                    }
                }
            }

            Console.WriteLine("Done");

            // Apply exheader.bin patches
            Console.Write("Apply patches to exheader.bin... ");

            if (patchFile.HasPatch("exheader.bin"))
            {
                IArchiveFileInfo exHeaderFile = gameFiles.FirstOrDefault(x => x.FilePath == "/ExHeader.bin");
                if (exHeaderFile != null)
                {
                    await using Stream exHeaderStream = await exHeaderFile.GetFileData();

                    var delta = patchFile.GetPatch("exheader.bin");
                    var output = new MemoryStream();

                    var coder = new VcDecoder(exHeaderStream, delta, output);
                    var result = coder.Decode(out _);

                    delta.Close();
                    exHeaderStream.Close();

                    if (result == VCDiffResult.SUCCESS)
                    {
                        // Save exheader.bin to output
                        var codeOutput = File.OpenWrite(Path.Combine(outputPath, "exheader.bin"));

                        output.Position = 0;
                        await output.CopyToAsync(codeOutput);

                        output.Close();
                        codeOutput.Close();
                    }
                    else
                    {
                        Console.WriteLine("An error occurred applying the patch to \"exheader.bin\".");
                        output.Close();
                    }
                }
            }

            Console.WriteLine("Done");

            return outputPath;
        }

        #region Patch File

        private static bool TryLoadPatch(string filePath, out PatchFile patch)
        {
            patch = null;

            if (!File.Exists(filePath))
            {
                Console.WriteLine($"Could not find patch file \"{filePath}\".");
                return false;
            }

            try
            {
                patch = PatchFile.Open(filePath);
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Could not load patch file \"{filePath}\". Error: {e.Message}");
                return false;
            }
        }

        #endregion

        #region Cpk Data

        private static bool TryLoadCpkFiles(Stream cpkData, string gamePath, out Cpk cpk, out IList<IArchiveFileInfo> files)
        {
            cpk = null;
            files = null;

            try
            {
                cpk = new Cpk();
                files = cpk.Load(cpkData);

                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Could not load tt1_ctr.cpk from \"{gamePath}\". Error: {e.Message}");
                return false;
            }
        }

        #endregion

        #region Game Data

        private static bool TryLoadGameFiles(Stream gameData, string gamePath, out IList<IArchiveFileInfo> files)
        {
            files = null;

            try
            {
                var ncch = new NCCH();
                files = ncch.Load(gameData);

                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Could not load GameData.cxi from \"{gamePath}\". Error: {e.Message}");
                Console.WriteLine();
                Console.WriteLine("Make sure you use a decrypted .3ds or .cia!");

                return false;
            }
        }

        #endregion

        #region Game Card

        static async Task<IList<IArchiveFileInfo>> LoadGamePartitions(string gamePath)
        {
            // Check if file exists
            if (!File.Exists(gamePath))
            {
                Console.WriteLine($"The file \"{gamePath}\" does not exist.");
                return null;
            }

            // Check if the file can be opened as readable
            FileStream fileStream;
            try
            {
                fileStream = File.OpenRead(gamePath);
            }
            catch (Exception)
            {
                Console.WriteLine($"The file \"{gamePath}\" can not be opened. Is it open in another program?");
                return null;
            }

            bool isNcsd = await IsNcsd(gamePath);

            if (!TryLoadGamePartitions(fileStream, isNcsd, out IList<IArchiveFileInfo> files))
                return null;

            return files;
        }

        private static async Task<bool> IsNcsd(string gamePath)
        {
            IStreamManager streamManager = new StreamManager();

            using IFileSystem fileSystem = FileSystemFactory.CreatePhysicalFileSystem(streamManager);
            gamePath = (string)fileSystem.ConvertPathFromInternal(gamePath);

            ITemporaryStreamProvider temporaryStreamProvider = streamManager.CreateTemporaryStreamProvider();

            var ncsdPlugin = new NcsdPlugin();
            var identifyContext = new IdentifyContext(temporaryStreamProvider);

            bool isNcsd = await ncsdPlugin.IdentifyAsync(fileSystem, gamePath, identifyContext);

            streamManager.ReleaseAll();

            return isNcsd;
        }

        private static bool TryLoadGamePartitions(FileStream fileStream, bool isNcsd, out IList<IArchiveFileInfo> files)
        {
            files = null;

            try
            {
                if (isNcsd)
                {
                    var ncsd = new NCSD();
                    files = ncsd.Load(fileStream);

                    return true;
                }

                var cia = new CIA();
                files = cia.Load(fileStream);

                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Could not load file \"{fileStream.Name}\". Error: {e.Message}");
                Console.WriteLine();
                Console.WriteLine("Possible reasons could be that the file is not a .3ds or .cia, or is not decrypted.");

                return false;
            }
        }

        #endregion
    }
}
