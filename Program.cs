using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
#if TESSERACT_NATIVE
using Tesseract;
#endif
using System.Diagnostics;

enum DocumentType
{
    SalarySlip,
    Passport,
    DrivingLicence,
    Rental,
    BankStatement,
    AtoStatement,
    Unknown
}

sealed class PlannedMove(string sourcePath, string destinationPath, string reason, string confidence)
{
    public string SourcePath { get; } = sourcePath;
    public string DestinationPath { get; set; } = destinationPath;
    public string Reason { get; set; } = reason;
    public string Confidence { get; } = confidence;
    public string SourceHash { get; set; } = "";
}

sealed record AtoStatementDetails(string? DocumentType, string? FinancialYear);

sealed class ProcessingRunJournal
{
    public string RunId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public List<ProcessingRunAction> Actions { get; set; } = [];
}

sealed class ProcessingRunAction
{
    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public string Reason { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string? Note { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? UndoneAt { get; set; }
}

class Program
{
    const long MaxFileSizeBytes = 100L * 1024 * 1024;
    const string RunDataDirectoryName = ".loan-processing";
    static readonly List<string> ProcessingReportEntries = new();
    static readonly List<PlannedMove> PlannedMoves = new();
    static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };
    static bool IsPlanning;

    static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--process-folder")
        {
            if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] != "--apply"))
            {
                Console.Error.WriteLine("Usage: --process-folder <folder-path> [--apply]");
                return;
            }

            var folderPath = args[1].Trim('"', ' ');
            if (!Directory.Exists(folderPath))
            {
                Console.Error.WriteLine("Error: Invalid or non-existent directory path.");
                return;
            }

            await ProcessFolderAsync(folderPath, args.Length == 3);
            return;
        }

        if (args.Length > 0 && args[0] == "--undo-last-run")
        {
            if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] != "--apply"))
            {
                Console.Error.WriteLine("Usage: --undo-last-run <folder-path> [--apply]");
                return;
            }

            var folderPath = args[1].Trim('"', ' ');
            if (!Directory.Exists(folderPath))
            {
                Console.Error.WriteLine("Error: Invalid or non-existent directory path.");
                return;
            }

            UndoLastRun(folderPath, args.Length == 3);
            return;
        }

        if (args.Length > 0 && args.Contains("--rescue-needs-review"))
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("Usage: --rescue-needs-review <folder-path>");
                return;
            }

            var folderPath = Path.Combine(args[1].Trim('"', ' '), "Needs Review");
            if (!Directory.Exists(folderPath))
            {
                Console.WriteLine("No Needs Review folder found.");
                return;
            }

            await ProcessFolderAsync(folderPath, confirmInConsole: true);
            return;
        }

        while (true)
        {
            Console.Write("Enter the folder path to organize: ");
            string? folderPath = Console.ReadLine()?.Trim('"', ' ');

            if (string.IsNullOrWhiteSpace(folderPath))
            {
                Console.WriteLine("Error: Folder path cannot be empty. Please enter a valid folder path.");
                continue;
            }

            if (!Directory.Exists(folderPath))
            {
                Console.WriteLine("Error: Invalid or non-existent directory path. Please try again.");
                continue;
            }

            await ProcessFolderAsync(folderPath, confirmInConsole: true);

            Console.Write("Process another folder? (y/N): ");
            var again = Console.ReadLine();
            if (!string.Equals(again, "y", StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    static async Task ProcessFolderAsync(
        string directoryPath,
        bool applyChanges = false,
        bool confirmInConsole = false)
    {
        directoryPath = Path.GetFullPath(directoryPath);
        ProcessingReportEntries.Clear();
        PlannedMoves.Clear();
        IsPlanning = true;
        ProcessingReportEntries.Add($"SCAN: Folder processed: {directoryPath}");

        var supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".jpg", ".jpeg", ".png", ".txt"
        };

        var files = EnumerateInputFiles(directoryPath, supportedExtensions);

        if (files.Count == 0)
        {
            IsPlanning = false;
            ProcessingReportEntries.Add("SKIP: No supported files found in the directory.");
            Console.WriteLine("No supported PDF, JPEG/JPG, PNG, or TXT files found in the directory.");
            return;
        }

        Console.WriteLine($"Found {files.Count} supported file(s) to inspect.\n");

        foreach (var filePath in files)
        {
            try
            {
                string fileName = Path.GetFileName(filePath);
                Console.WriteLine($"--------------------------------------------------");
                Console.WriteLine($"Inspecting: {Path.GetRelativePath(directoryPath, filePath)}");
                ProcessingReportEntries.Add($"SCAN: {Path.GetRelativePath(directoryPath, filePath)}");

                if (new FileInfo(filePath).Length > MaxFileSizeBytes)
                {
                    Console.WriteLine("  Review required: file exceeds the 100 MB processing limit; original left in place.");
                    ProcessingReportEntries.Add($"REVIEW: {Path.GetRelativePath(directoryPath, filePath)} -> file exceeds 100 MB; original left in place");
                    continue;
                }

                string extractedText = await ExtractTextAsync(filePath);
                if (string.IsNullOrWhiteSpace(extractedText))
                {
                    DocumentType fallbackType = ClassifyDocument(string.Empty, fileName);
                    if (fallbackType == DocumentType.AtoStatement)
                    {
                        ProcessAtoStatement(directoryPath, filePath, string.Empty);
                        continue;
                    }

                    if (fallbackType == DocumentType.Passport || fallbackType == DocumentType.DrivingLicence)
                    {
                        Console.WriteLine($"  OCR could not read text from image, but identified as {fallbackType} from file name.");
                        ProcessingReportEntries.Add($"REVIEW: {fileName} -> Identity doc flagged by filename ({fallbackType})");
                        ProcessIdDocument(directoryPath, filePath, string.Empty, fallbackType);
                        continue;
                    }

                    if (LooksLikeIdentityDocument(string.Empty, fileName))
                    {
                        Console.WriteLine("  Review required: OCR did not produce enough text, but the scan shows identity-document markers.");
                        ProcessingReportEntries.Add($"REVIEW: {fileName} -> low-confidence identity document");
                        MoveToNeedsReview(directoryPath, filePath, "Low-confidence identity document");
                        continue;
                    }

                    if (IsImageFile(filePath) && !IsReadableImage(filePath))
                    {
                        Console.WriteLine("  Review required: unreadable or corrupt image file.");
                        ProcessingReportEntries.Add($"REVIEW: {fileName} -> unreadable or corrupt image file");
                        MoveToNeedsReview(directoryPath, filePath, "Unreadable image file");
                        continue;
                    }

                    Console.WriteLine("  Review required: no readable text; file may be encrypted, corrupt, or unsupported. Original left in place.");
                    ProcessingReportEntries.Add($"REVIEW: {Path.GetRelativePath(directoryPath, filePath)} -> no readable text; may be encrypted, corrupt, or unsupported; original left in place");
                    continue;
                }

                DocumentType docType = ClassifyDocument(extractedText, fileName);

                switch (docType)
                {
                    case DocumentType.AtoStatement:
                        ProcessingReportEntries.Add($"CLASSIFY: {fileName} -> {docType}");
                        ProcessAtoStatement(directoryPath, filePath, extractedText);
                        break;

                    case DocumentType.Passport:
                    case DocumentType.DrivingLicence:
                        ProcessingReportEntries.Add($"CLASSIFY: {fileName} -> {docType}");
                        ProcessIdDocument(directoryPath, filePath, extractedText, docType);
                        break;

                    case DocumentType.Rental:
                        ProcessingReportEntries.Add($"CLASSIFY: {fileName} -> {docType}");
                        ProcessRentalDocument(directoryPath, filePath, extractedText);
                        break;

                    case DocumentType.SalarySlip:
                        ProcessingReportEntries.Add($"CLASSIFY: {fileName} -> {docType}");
                        ProcessSalarySlip(directoryPath, filePath, extractedText);
                        break;

                    case DocumentType.BankStatement:
                        ProcessingReportEntries.Add($"CLASSIFY: {fileName} -> {docType}");
                        ProcessBankStatement(directoryPath, filePath, extractedText);
                        break;

                    default:
                        if (LooksLikeIdentityDocument(extractedText, fileName))
                        {
                            Console.WriteLine("  Review required: low-confidence OCR indicates an identity document but insufficient detail was extracted for auto-naming.");
                            ProcessingReportEntries.Add($"REVIEW: {fileName} -> low-confidence identity document");
                            MoveToNeedsReview(directoryPath, filePath, "Low-confidence identity document");
                        }
                        else
                        {
                            var (start, end) = ExtractPeriodDates(extractedText, Path.GetFileNameWithoutExtension(filePath));
                            if (start != null && end != null)
                            {
                                ProcessingReportEntries.Add($"CLASSIFY: {fileName} -> SalarySlip");
                                ProcessSalarySlip(directoryPath, filePath, extractedText);
                            }
                            else
                            {
                                Console.WriteLine("  Skipped: Could not determine document type (not recognized as Salary Slip, Passport, Driving Licence, or Bank Statement).");
                                ProcessingReportEntries.Add($"SKIP: {fileName} -> type not recognized");
                            }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                string currentFileName = Path.GetFileName(filePath);
                ProcessingReportEntries.Add($"REVIEW: {Path.GetRelativePath(directoryPath, filePath)} -> could not read file ({ex.GetType().Name}); it may be encrypted, corrupt, or unsupported; original left in place");
                Console.WriteLine($"  Review required: could not read file ({ex.GetType().Name}); original left in place.\n");
            }
        }

        IsPlanning = false;
        ResolvePlannedDestinationConflicts(directoryPath);
        ShowPlan(directoryPath);

        if (confirmInConsole && PlannedMoves.Count > 0)
        {
            Console.Write("\nApply all proposed moves and renames? The originals will be moved (y/N): ");
            applyChanges = string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
        }

        if (applyChanges)
        {
            var journalPath = CreateRunJournal(directoryPath);
            ApplyPlannedMoves(directoryPath, journalPath);
            WriteProcessingReport(directoryPath);
            Console.WriteLine($"\nUndo this run with: --undo-last-run \"{directoryPath}\"");
        }
        else
        {
            Console.WriteLine("\nPreview only; no files were moved or renamed. Run again and confirm to apply.");
        }
    }

    static void WriteProcessingReport(string directoryPath)
    {
        var summary = new List<string>
        {
            "Loan Processing Utility Report",
            $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"Folder: {directoryPath}",
            "",
            $"Total files scanned: {ProcessingReportEntries.Count(x => x.StartsWith("SCAN:"))}",
            $"Moved successfully: {ProcessingReportEntries.Count(x => x.StartsWith("MOVE:"))}",
            $"Items flagged for review: {ProcessingReportEntries.Count(x => x.StartsWith("REVIEW:"))}",
            $"Skipped: {ProcessingReportEntries.Count(x => x.StartsWith("SKIP:"))}",
            "",
            "Details:"
        };

        foreach (var entry in ProcessingReportEntries)
        {
            summary.Add(entry);
        }

        string reportPath = Path.Combine(directoryPath, $"Processing-Report-{DateTime.Now:yyyyMMddHHmmssfff}.txt");
        File.WriteAllLines(reportPath, summary);
        Console.WriteLine($"\nProcessing report saved to: {reportPath}");
    }

    static List<string> EnumerateInputFiles(string rootDirectory, HashSet<string> supportedExtensions)
    {
        var files = new List<string>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootDirectory);

        while (pendingDirectories.Count > 0)
        {
            var currentDirectory = pendingDirectories.Pop();
            try
            {
                files.AddRange(Directory.EnumerateFiles(currentDirectory)
                    .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                    .Where(path => supportedExtensions.Contains(Path.GetExtension(path)))
                    .Where(path => !Path.GetFileName(path).StartsWith("Processing-Report-", StringComparison.OrdinalIgnoreCase)));

                foreach (var childDirectory in Directory.EnumerateDirectories(currentDirectory))
                {
                    var attributes = File.GetAttributes(childDirectory);
                    if ((attributes & FileAttributes.ReparsePoint) != 0 || IsGeneratedOutputDirectory(childDirectory))
                        continue;
                    pendingDirectories.Push(childDirectory);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                var relativePath = Path.GetRelativePath(rootDirectory, currentDirectory);
                var reason = $"cannot access folder ({exception.GetType().Name}); contents left untouched";
                ProcessingReportEntries.Add($"REVIEW: {relativePath} -> {reason}");
                Console.WriteLine($"Review required: {relativePath} {reason}.");
            }
        }

        return files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static bool IsGeneratedOutputDirectory(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals(RunDataDirectoryName, StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Needs Review", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Duplicate Files", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Salary Slip", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Bank Statements", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Rental Documents", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("ATO Statements", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("-ID-Documents", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("-Expired-Documents", StringComparison.OrdinalIgnoreCase);
    }

    static void ShowPlan(string directoryPath)
    {
        Console.WriteLine($"\nPreview: {PlannedMoves.Count} proposed file move(s).");
        foreach (var move in PlannedMoves)
        {
            Console.WriteLine($"  {Path.GetRelativePath(directoryPath, move.SourcePath)}");
            Console.WriteLine($"    -> {Path.GetRelativePath(directoryPath, move.DestinationPath)}");
            Console.WriteLine($"       Confidence: {move.Confidence}");
            Console.WriteLine($"       {move.Reason}");
        }

        foreach (var entry in ProcessingReportEntries.Where(entry =>
                     entry.StartsWith("REVIEW:", StringComparison.Ordinal) ||
                     entry.StartsWith("SKIP:", StringComparison.Ordinal)))
        {
            Console.WriteLine($"  {entry}");
        }
    }

    static void ResolvePlannedDestinationConflicts(string rootDirectory)
    {
        var reservedDestinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var move in PlannedMoves)
        {
            move.SourceHash = ComputeFileHash(move.SourcePath);
            var desiredPath = move.DestinationPath;
            var collisionPath = desiredPath;
            var suffix = 2;
            while (File.Exists(collisionPath) || Directory.Exists(collisionPath) || reservedDestinations.ContainsKey(collisionPath))
            {
                if (Directory.Exists(collisionPath))
                {
                    var occupiedDirectory = Path.GetDirectoryName(desiredPath)!;
                    var occupiedFileBase = Path.GetFileNameWithoutExtension(desiredPath);
                    collisionPath = Path.Combine(occupiedDirectory, $"{occupiedFileBase}-{suffix++}{Path.GetExtension(desiredPath)}");
                    move.Reason += " A directory occupied the proposed name; a unique filename preserves both.";
                    continue;
                }

                var existingHash = reservedDestinations.TryGetValue(collisionPath, out var reservedHash)
                    ? reservedHash
                    : ComputeFileHash(collisionPath);
                if (string.Equals(existingHash, move.SourceHash, StringComparison.Ordinal))
                {
                    var duplicateDirectory = Path.Combine(rootDirectory, "Duplicate Files");
                    var duplicateBase = $"{Path.GetFileNameWithoutExtension(desiredPath)}-EXACT-DUPLICATE-{move.SourceHash[..12]}";
                    collisionPath = GetUniqueAvailablePath(Path.Combine(duplicateDirectory, duplicateBase + Path.GetExtension(desiredPath)), reservedDestinations);
                    move.Reason += " Exact duplicate content confirmed by SHA-256; preserved in Duplicate Files.";
                    break;
                }

                var directory = Path.GetDirectoryName(desiredPath)!;
                var fileBase = Path.GetFileNameWithoutExtension(desiredPath);
                collisionPath = Path.Combine(directory, $"{fileBase}-{suffix++}{Path.GetExtension(desiredPath)}");
                move.Reason += " Existing destination had different content; both files are preserved with unique names.";
            }

            move.DestinationPath = collisionPath;
            reservedDestinations[collisionPath] = move.SourceHash;
        }
    }

    static string GetUniqueAvailablePath(string path, IReadOnlyDictionary<string, string> reservedDestinations)
    {
        var candidate = path;
        var suffix = 2;
        while (File.Exists(candidate) || Directory.Exists(candidate) || reservedDestinations.ContainsKey(candidate))
        {
            candidate = Path.Combine(
                Path.GetDirectoryName(path)!,
                $"{Path.GetFileNameWithoutExtension(path)}-{suffix++}{Path.GetExtension(path)}");
        }

        return candidate;
    }

    static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    static string CreateRunJournal(string rootDirectory)
    {
        var runDirectory = Path.Combine(rootDirectory, RunDataDirectoryName, "Runs");
        Directory.CreateDirectory(runDirectory);
        var journalPath = Path.Combine(runDirectory, $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.json");
        var journal = new ProcessingRunJournal
        {
            RunId = Path.GetFileNameWithoutExtension(journalPath),
            CreatedAt = DateTimeOffset.UtcNow,
            Actions = PlannedMoves.Select(move => new ProcessingRunAction
            {
                SourcePath = move.SourcePath,
                DestinationPath = move.DestinationPath,
                Reason = move.Reason,
                SourceHash = move.SourceHash
            }).ToList()
        };
        SaveRunJournal(journalPath, journal);
        return journalPath;
    }

    static void ApplyPlannedMoves(string rootDirectory, string journalPath)
    {
        var journal = LoadRunJournal(journalPath);
        for (var index = 0; index < PlannedMoves.Count; index++)
        {
            var move = PlannedMoves[index];
            var action = journal.Actions[index];
            if (!File.Exists(move.SourcePath))
            {
                action.Status = "Failed";
                action.Note = "Source file no longer exists; no change made.";
                ProcessingReportEntries.Add($"ERROR: {Path.GetRelativePath(rootDirectory, move.SourcePath)} -> source file no longer exists");
                SaveRunJournal(journalPath, journal);
                continue;
            }

            if (File.Exists(move.DestinationPath) || Directory.Exists(move.DestinationPath))
            {
                action.Status = "Failed";
                action.Note = "Destination became occupied after preview; source was left in place.";
                ProcessingReportEntries.Add($"ERROR: {Path.GetRelativePath(rootDirectory, move.SourcePath)} -> destination became occupied after preview");
                SaveRunJournal(journalPath, journal);
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(move.DestinationPath)!);
                File.Move(move.SourcePath, move.DestinationPath);
                action.Status = "Moved";
                action.CompletedAt = DateTimeOffset.UtcNow;
                ProcessingReportEntries.Add($"MOVE: {Path.GetRelativePath(rootDirectory, move.SourcePath)} -> {Path.GetRelativePath(rootDirectory, move.DestinationPath)}");
                Console.WriteLine($"Moved: {Path.GetRelativePath(rootDirectory, move.SourcePath)} -> {Path.GetRelativePath(rootDirectory, move.DestinationPath)}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                action.Status = "Failed";
                action.Note = $"Move failed ({exception.GetType().Name}); source was not intentionally deleted.";
                ProcessingReportEntries.Add($"ERROR: {Path.GetRelativePath(rootDirectory, move.SourcePath)} -> move failed ({exception.GetType().Name})");
                Console.WriteLine($"Move failed for {Path.GetRelativePath(rootDirectory, move.SourcePath)} ({exception.GetType().Name}).");
            }

            SaveRunJournal(journalPath, journal);
        }
    }

    static void UndoLastRun(string rootDirectory, bool applyChanges = false)
    {
        rootDirectory = Path.GetFullPath(rootDirectory);
        var runsDirectory = Path.Combine(rootDirectory, RunDataDirectoryName, "Runs");
        if (!Directory.Exists(runsDirectory))
        {
            Console.WriteLine("No processing run is available to undo.");
            return;
        }

        var latestRun = Directory.EnumerateFiles(runsDirectory, "*.json")
            .Select(path => new { Path = path, Journal = LoadRunJournal(path) })
            .OrderByDescending(run => run.Journal.CreatedAt)
            .FirstOrDefault();
        if (latestRun is null || !latestRun.Journal.Actions.Any(action => action.Status == "Moved"))
        {
            Console.WriteLine("No completed file moves are available to undo.");
            return;
        }

        if (latestRun.Journal.Actions.Any(action =>
                !IsPathWithinRoot(rootDirectory, action.SourcePath) ||
                !IsPathWithinRoot(rootDirectory, action.DestinationPath)))
            throw new InvalidDataException("The latest processing journal contains a path outside the selected folder; undo was stopped.");

        var completedActions = latestRun.Journal.Actions.Count(action => action.Status == "Moved");
        Console.WriteLine($"Undo preview: {completedActions} move(s) from run {latestRun.Journal.RunId}.");
        foreach (var action in latestRun.Journal.Actions.Where(action => action.Status == "Moved"))
        {
            Console.WriteLine($"  {Path.GetRelativePath(rootDirectory, action.DestinationPath)}");
            Console.WriteLine($"    -> {Path.GetRelativePath(rootDirectory, action.SourcePath)}");
        }

        if (!applyChanges)
        {
            Console.Write("Restore these files to their original paths? (y/N): ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Undo cancelled; no files were changed.");
                return;
            }
        }

        foreach (var action in latestRun.Journal.Actions.Where(action => action.Status == "Moved").Reverse())
        {
            if (!File.Exists(action.DestinationPath) ||
                File.Exists(action.SourcePath) ||
                !string.Equals(ComputeFileHash(action.DestinationPath), action.SourceHash, StringComparison.Ordinal))
            {
                action.Note = "Undo blocked because the destination is missing, has changed, or the original path is occupied.";
                Console.WriteLine($"Could not restore {Path.GetRelativePath(rootDirectory, action.SourcePath)}; a path conflict was detected.");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(action.SourcePath)!);
            File.Move(action.DestinationPath, action.SourcePath);
            action.Status = "Undone";
            action.UndoneAt = DateTimeOffset.UtcNow;
            SaveRunJournal(latestRun.Path, latestRun.Journal);
            try
            {
                RemoveEmptyParentDirectories(rootDirectory, Path.GetDirectoryName(action.DestinationPath)!);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"Could not remove empty destination folders ({exception.GetType().Name}).");
            }

            Console.WriteLine($"Restored: {Path.GetRelativePath(rootDirectory, action.SourcePath)}");
        }
    }

    static void RemoveEmptyParentDirectories(string rootDirectory, string directoryPath)
    {
        var root = Path.GetFullPath(rootDirectory);
        var directory = Path.GetFullPath(directoryPath);
        while (!string.Equals(directory, root, OperatingSystem.IsWindows()
                   ? StringComparison.OrdinalIgnoreCase
                   : StringComparison.Ordinal) &&
               IsPathWithinRoot(root, directory))
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any())
                return;

            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory)!;
        }
    }

    static ProcessingRunJournal LoadRunJournal(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<ProcessingRunJournal>(stream, ManifestJsonOptions)
            ?? throw new InvalidDataException($"Processing journal '{path}' is empty.");
    }

    static void SaveRunJournal(string path, ProcessingRunJournal journal)
    {
        var temporaryPath = path + ".tmp";
        using (var stream = File.Create(temporaryPath))
            JsonSerializer.Serialize(stream, journal, ManifestJsonOptions);
        File.Move(temporaryPath, path, true);
    }

    static bool IsPathWithinRoot(string rootDirectory, string path)
    {
        var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(root, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
    }

    static void PlanMove(string sourcePath, string destinationPath, string reason, string confidence = "Suggested")
    {
        if (!IsPlanning)
        {
            MoveFileWithDuplicateCheck(sourcePath, destinationPath, Path.GetDirectoryName(sourcePath)!, reason);
            return;
        }

        PlannedMoves.Add(new PlannedMove(sourcePath, destinationPath, reason, confidence));
        var reportType = confidence == "Low" ? "REVIEW" : "PLAN";
        ProcessingReportEntries.Add($"{reportType}: {Path.GetFileName(sourcePath)} -> {reason}");
    }

    #region ID Document Processing

    static void ProcessIdDocument(string directoryPath, string filePath, string text, DocumentType docType)
    {
        string extension = Path.GetExtension(filePath);
        string docTypeName = docType == DocumentType.Passport ? "Passport" : "DrivingLicence";

        string? personName = ExtractPersonName(text, Path.GetFileNameWithoutExtension(filePath));
        if (!IsLikelyValidPersonName(personName))
        {
            Console.WriteLine($"  Review required: {docTypeName} could not be assigned a valid person name.");
            MoveToNeedsReview(directoryPath, filePath, $"{docTypeName} - invalid name");
            return;
        }

        string safeName = ToSafeFileName(personName!);
        string personFolder = safeName + "-ID-Documents";

        DateTime? expiryDate = ExtractExpiryDate(text);
        if (expiryDate == null)
        {
            Console.WriteLine($"  Review required: Detected as {docTypeName}, but could not identify Expiry Date.");
            MoveToNeedsReview(directoryPath, filePath, $"{docTypeName} - expiry missing");
            return;
        }

        DateTime today = DateTime.Today;
        string expiryStr = expiryDate.Value.ToString("ddMMyyyy");
        string displayExpiry = expiryDate.Value.ToString("dd-MM-yyyy");
        bool isExpired = expiryDate.Value.Date <= today;

        if (isExpired)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [EXPIRED] {docTypeName} for {safeName} expired on {displayExpiry} (<= Today: {today:dd-MM-yyyy})!");
            Console.ResetColor();

            string expiredDir = Path.Combine(directoryPath, $"{safeName}-Expired-Documents");
            string destFileName = $"{safeName}-{docTypeName}-EXPIRED-{expiryStr}{extension}";
            string destinationPath = Path.Combine(expiredDir, destFileName);

            MoveFileWithDuplicateCheck(filePath, destinationPath, directoryPath, $"{safeName} (Expired {docTypeName})");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  [VALID] {docTypeName} for {safeName} is VALID until {displayExpiry} (> Today: {today:dd-MM-yyyy}).");
            Console.ResetColor();

            string targetDir = Path.Combine(directoryPath, personFolder);

            string destFileName = $"{safeName}-{docTypeName}-ValidTill-{expiryStr}{extension}";
            string destinationPath = Path.Combine(targetDir, destFileName);

            MoveFileWithDuplicateCheck(filePath, destinationPath, directoryPath, $"{safeName} ({docTypeName})");
        }
    }

    #endregion

    static void ProcessAtoStatement(string directoryPath, string filePath, string extractedText)
    {
        var details = ExtractAtoStatementDetails(extractedText, Path.GetFileName(filePath));
        var extension = Path.GetExtension(filePath);
        var targetDirectory = Path.Combine(directoryPath, "ATO Statements");
        string destinationName;
        string reason;

        if (details.DocumentType is not null && details.FinancialYear is not null)
        {
            destinationName = $"ATO-{details.DocumentType}-FY{details.FinancialYear}{extension}";
            reason = $"ATO indicators matched; document type and financial year detected ({details.DocumentType}, FY{details.FinancialYear}). Review this suggestion before applying.";
        }
        else
        {
            var sourceHash = ComputeFileHash(filePath);
            destinationName = $"ATO-Statement-Review-{sourceHash[..12]}{extension}";
            reason = "ATO indicators matched, but document type or financial year was unclear; generic ATO name proposed for human review.";
        }

        var confidence = details.DocumentType is not null && details.FinancialYear is not null
            ? "Suggested"
            : "Low";
        PlanMove(filePath, Path.Combine(targetDirectory, destinationName), reason, confidence);
    }

    #region Salary Slip Processing

    static void ProcessSalarySlip(string directoryPath, string filePath, string extractedText)
    {
        string? employeeName = ExtractSalarySlipEmployeeName(extractedText);
        if (!IsLikelyValidPersonName(employeeName))
        {
            Console.WriteLine("  Review required: Salary slip could not be assigned a valid employee name.");
            MoveToNeedsReview(directoryPath, filePath, "Salary Slip - invalid employee name");
            return;
        }

        var (startDate, endDate) = ExtractPeriodDates(extractedText, Path.GetFileNameWithoutExtension(filePath));
        if (startDate == null || endDate == null)
        {
            Console.WriteLine("  Review required: Salary slip missing a valid pay period.");
            MoveToNeedsReview(directoryPath, filePath, "Salary Slip - invalid pay period");
            return;
        }

        string? employer = ExtractEmployer(extractedText);
        string startStr = startDate.Value.ToString("ddMMyyyy");
        string endStr = endDate.Value.ToString("ddMMyyyy");
        string safeName = ToSafeFileName(employeeName!);

        string targetDir = Path.Combine(directoryPath, "Salary Slip");

        string extension = Path.GetExtension(filePath);
        string baseFileName = $"{safeName}-SalarySlip-Period-{startStr}-{endStr}";
        string destinationPath = Path.Combine(targetDir, $"{baseFileName}{extension}");

        MoveFileWithDuplicateCheck(filePath, destinationPath, directoryPath, $"{safeName} (Period {startStr}-{endStr}, {employer ?? "Employer"})");
    }

    #endregion

    static string? ExtractSalarySlipEmployeeName(string text)
    {
        var match = Regex.Match(
            text,
            @"(?:Employee\s*Name|Payee|Member\s*Name|Customer\s*Name|(?<!\w)Name)\s*[:\-]\s*([A-Z][A-Za-z'\-\.]+(?:\s+[A-Z][A-Za-z'\-\.]+){1,3})(?![A-Za-z'\-\.])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success)
        {
            var candidate = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(match.Groups[1].Value.ToLowerInvariant());
            if (IsLikelyValidPersonName(candidate))
                return candidate;
        }

        const string commaSeparatedName = @"(?<last>[\p{Lu}][\p{L}'’.-]+),\s*(?<first>[\p{Lu}][\p{Ll}]+(?=[\p{Lu}]|\s|$)|[\p{Lu}]{2,}|[\p{Lu}][\p{Ll}]+(?:[-'][\p{Lu}]?[\p{Ll}]+)*)";
        bool hasEmployeeNumberLabel = Regex.IsMatch(
            text,
            @"Emp(?:loyee)?\s*No\.?|\bEmployee\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!hasEmployeeNumberLabel)
            return null;

        var employeeNameMatches = Regex.Matches(
            text,
            $@"(?<!\d)\d{{4,}}\s*{commaSeparatedName}",
            RegexOptions.CultureInvariant);
        return employeeNameMatches
            .Select(BuildSalarySlipName)
            .Where(candidate => candidate is not null)
            .OrderBy(candidate => candidate!.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Length)
            .FirstOrDefault();
    }

    static string? BuildSalarySlipName(Match match)
    {
        string candidate = $"{match.Groups["first"].Value} {match.Groups["last"].Value}";
        candidate = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(candidate.ToLowerInvariant());
        return IsLikelyValidPersonName(candidate) ? candidate : null;
    }

    #region Rental Documents

    static void ProcessRentalDocument(string directoryPath, string filePath, string extractedText)
    {
        string extension = Path.GetExtension(filePath);
        string fileStem = Path.GetFileNameWithoutExtension(filePath);

        string? tenantName = ExtractPersonName(extractedText, fileStem);
        string safeBase;
        if (!string.IsNullOrWhiteSpace(tenantName) && IsLikelyValidPersonName(tenantName))
        {
            safeBase = ToSafeFileName(tenantName!);
        }
        else
        {
            safeBase = Regex.Replace(fileStem, "[^A-Za-z0-9\\-\\s]", "");
            safeBase = ToSafeFileName(safeBase);
            if (string.IsNullOrWhiteSpace(safeBase)) safeBase = "RentalDocument";
        }

        string targetDir = Path.Combine(directoryPath, "Rental Documents");

        string destFileName = $"{safeBase}-RentalDocument{extension}";
        string destinationPath = Path.Combine(targetDir, destFileName);

        MoveFileWithDuplicateCheck(filePath, destinationPath, directoryPath, $"Rental ({safeBase})");
    }

    #endregion

    #region Bank Statement Processing

    static void ProcessBankStatement(string directoryPath, string filePath, string extractedText)
    {
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        string extension = Path.GetExtension(filePath);

        string bankName = ExtractBankName(extractedText, fileName) ?? "Bank";
        string holderName = ExtractAccountHolderName(extractedText, fileName) ?? "AccountHolder";
        if (!IsLikelyValidPersonName(holderName))
        {
            Console.WriteLine("  Review required: Bank statement could not be assigned a valid account holder name.");
            MoveToNeedsReview(directoryPath, filePath, "Bank Statement - invalid account holder name");
            return;
        }

        string safeBank = Regex.Replace(bankName, @"[^A-Za-z0-9\-]", "");
        string safeHolder = ToSafeFileName(holderName);

        string targetDir = Path.Combine(directoryPath, "Bank Statements");

        string baseFileName = $"{safeBank}_{safeHolder}_BankStatement";
        string destinationPath = Path.Combine(targetDir, $"{baseFileName}{extension}");

        PlanMove(filePath, destinationPath, "Bank statement type and account holder detected; account numbers are intentionally omitted from filenames.");
    }

    #endregion

    static bool IsLikelyValidPersonName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        string cleaned = Regex.Replace(value, @"[\r\n\t_]+", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) return false;

        string[] genericNames = { "unknown", "employee", "employer", "account", "holder", "bank", "fund", "trust", "salary", "payslip", "passport", "driver", "driving", "licence", "license", "document", "scan", "id", "new", "south", "wales", "nsw", "road", "street", "avenue", "drive", "hill", "line", "court", "place", "unit", "of", "re", "aus" };
        string[] addressWords = { "road", "rd", "street", "st", "avenue", "ave", "drive", "dr", "court", "ct", "place", "pl", "lane", "ln", "unit", "nsw", "south", "wales", "hill", "line" };

        var tokens = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2 || tokens.Length > 4) return false;

        foreach (var token in tokens)
        {
            string lower = token.ToLowerInvariant();
            if (lower.Length <= 1) return false;
            if (genericNames.Contains(lower)) return false;
            if (addressWords.Contains(lower)) return false;
            if (char.IsDigit(token[0])) return false;
        }

        if (Regex.IsMatch(cleaned, @"\d")) return false;
        if (cleaned.Contains(" of ", StringComparison.OrdinalIgnoreCase) || cleaned.Contains(" re ", StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    static string ToSafeFileName(string input)
    {
        string safe = Regex.Replace(input, @"[^A-Za-z0-9\-\s]", "");
        safe = Regex.Replace(safe, @"\s+", " ").Trim();
        return safe.Replace(' ', '-');
    }

    static void MoveToNeedsReview(string directoryPath, string sourceFilePath, string reason)
    {
        string reviewDir = Path.Combine(directoryPath, "Needs Review");

        string extension = Path.GetExtension(sourceFilePath);
        string sourceHash = ComputeFileHash(sourceFilePath);
        string destinationPath = Path.Combine(reviewDir, $"Review-{sourceHash[..12]}{extension}");
        PlanMove(sourceFilePath, destinationPath, $"Human review required: {reason}", "Low");
    }

    static void MoveFileWithDuplicateCheck(string sourcePath, string destinationPath, string rootDirectory, string logContext)
    {
        if (IsPlanning)
        {
            PlanMove(sourcePath, destinationPath, logContext);
            return;
        }

        try
        {
            var destDir = Path.GetDirectoryName(destinationPath) ?? rootDirectory;
            Directory.CreateDirectory(destDir);

            if (!File.Exists(destinationPath))
            {
                File.Move(sourcePath, destinationPath);
                ProcessingReportEntries.Add($"MOVE: {Path.GetFileName(sourcePath)} -> {Path.GetRelativePath(rootDirectory, destinationPath)}");
                Console.WriteLine($"  Moved: {Path.GetFileName(sourcePath)} -> {Path.GetRelativePath(rootDirectory, destinationPath)}");
                return;
            }

            var sourceHash = ComputeFileHash(sourcePath);
            var destinationHash = ComputeFileHash(destinationPath);
            var fileBase = Path.GetFileNameWithoutExtension(destinationPath);
            var extension = Path.GetExtension(destinationPath);
            var duplicateDirectory = string.Equals(sourceHash, destinationHash, StringComparison.Ordinal)
                ? Path.Combine(rootDirectory, "Duplicate Files")
                : destDir;
            var suffix = string.Equals(sourceHash, destinationHash, StringComparison.Ordinal)
                ? $"-EXACT-DUPLICATE-{sourceHash[..12]}"
                : "-2";
            var uniquePath = Path.Combine(duplicateDirectory, $"{fileBase}{suffix}{extension}");
            var counter = 2;
            while (File.Exists(uniquePath))
                uniquePath = Path.Combine(duplicateDirectory, $"{fileBase}{suffix}-{counter++}{extension}");
            Directory.CreateDirectory(duplicateDirectory);
            File.Move(sourcePath, uniquePath);
            var note = string.Equals(sourceHash, destinationHash, StringComparison.Ordinal)
                ? "exact duplicate content"
                : "different content; name collision";
            ProcessingReportEntries.Add($"MOVE: {Path.GetFileName(sourcePath)} -> {Path.GetRelativePath(rootDirectory, uniquePath)} ({note})");
            Console.WriteLine($"  Preserved destination conflict ({note}): {Path.GetRelativePath(rootDirectory, uniquePath)}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ProcessingReportEntries.Add($"ERROR: {Path.GetFileName(sourcePath)} -> move failed ({exception.GetType().Name})");
            Console.WriteLine($"  Error moving file '{Path.GetFileName(sourcePath)}' ({exception.GetType().Name}).");
        }
    }

    static async Task<string> ExtractTextAsync(string filePath)
    {
        string extension = Path.GetExtension(filePath).ToLowerInvariant();

        if (extension == ".pdf")
        {
            using var document = PdfDocument.Open(filePath);
            var pages = document.GetPages().ToList();
            var textParts = new List<string>();

            foreach (var page in pages)
            {
                var pageText = page.Text;
                if (!string.IsNullOrWhiteSpace(pageText))
                {
                    textParts.Add(pageText);
                }
            }

            var combined = string.Join(Environment.NewLine, textParts);

            if (!string.IsNullOrWhiteSpace(combined))
            {
                return combined;
            }

            try
            {
                var imgBytes = TryRenderPdfFirstPageToPng(filePath);
                if (imgBytes != null && imgBytes.Length > 0)
                {
                    var ocr = await RunOcrOnImageBytesAsync(imgBytes);
                    if (!string.IsNullOrWhiteSpace(ocr)) return ocr;
                }
                else
                {
                    Console.WriteLine("    [OCR Fallback]: Could not render PDF first page to image (ImageMagick not available).\n    Install ImageMagick or provide a PNG to enable OCR fallback.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    [OCR Fallback Error]: {ex.GetType().Name}. Document content was not logged.");
            }

            return combined;
        }

        if (extension == ".jpg" || extension == ".jpeg" || extension == ".png")
        {
            try
            {
                byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
                if (fileBytes.Length == 0)
                {
                    throw new InvalidOperationException("Image file is empty.");
                }

#if TESSERACT_NATIVE
                using var img = Pix.LoadFromMemory(fileBytes);
                if (img is null)
                {
                    throw new InvalidOperationException("Image file could not be decoded.");
                }
#else
                if (!HasSupportedImageSignature(fileBytes))
                    throw new InvalidOperationException("Image file does not have a valid JPEG or PNG signature.");
#endif

                return await RunOcrOnImageBytesAsync(fileBytes);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    [Image Decode Error]: {ex.GetType().Name}. Document content was not logged.");
                return string.Empty;
            }
        }

        if (extension == ".txt")
        {
            return await File.ReadAllTextAsync(filePath);
        }

        return string.Empty;
    }

    static async Task<string> RunOcrOnFileAsync(string filePath)
    {
        try
        {
            byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
            if (fileBytes.Length == 0)
            {
                return string.Empty;
            }

            return await RunOcrOnImageBytesAsync(fileBytes);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    [OCR File Error]: {ex.GetType().Name}. Document content was not logged.");
            return string.Empty;
        }
    }

    static byte[]? TryRenderPdfFirstPageToPng(string pdfPath)
    {
        string[] candidateRoots = OperatingSystem.IsWindows()
            ? new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86)
            }
            : Array.Empty<string>();

        var imageMagickCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in candidateRoots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            try
            {
                if (Directory.Exists(root))
                {
                    foreach (var dir in Directory.EnumerateDirectories(root, "*ImageMagick*", SearchOption.AllDirectories))
                    {
                        imageMagickCandidates.Add(dir);
                    }
                }
            }
            catch { }

            if (Directory.Exists(root))
            {
                imageMagickCandidates.Add(Path.Combine(root, "ImageMagick"));
                imageMagickCandidates.Add(Path.Combine(root, "ImageMagick-7.1.2-Q16-HDRI"));
                imageMagickCandidates.Add(Path.Combine(root, "Magick"));
            }
        }

        foreach (var magickDir in imageMagickCandidates.Where(Directory.Exists))
        {
            foreach (var candidate in new[]
            {
                Path.Combine(magickDir, OperatingSystem.IsWindows() ? "magick.exe" : "magick"),
                Path.Combine(magickDir, OperatingSystem.IsWindows() ? "convert.exe" : "convert")
            })
            {
                if (File.Exists(candidate))
                {
                    try
                    {
                        var tempPng = Path.Combine(Path.GetTempPath(), $"_pdfpage_{Guid.NewGuid()}.png");
                        var args = $"-density 300 \"{pdfPath}[0]\" -quality 100 \"{tempPng}\"";
                        var psi = new ProcessStartInfo
                        {
                            FileName = candidate,
                            Arguments = args,
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            RedirectStandardOutput = false,
                            RedirectStandardError = true
                        };

                        using var proc = Process.Start(psi);
                        if (proc == null) continue;

                        proc.WaitForExit(30_000);
                        if (proc.ExitCode == 0 && File.Exists(tempPng))
                        {
                            var bytes = File.ReadAllBytes(tempPng);
                            try { File.Delete(tempPng); } catch { }
                            return bytes;
                        }

                        try { _ = proc.StandardError.ReadToEnd(); } catch { }
                        try { if (File.Exists(tempPng)) File.Delete(tempPng); } catch { }
                    }
                    catch { }
                }
            }
        }

        string[] commands = { "magick", "convert" };
        foreach (var cmd in commands)
        {
            var executablePath = File.Exists(cmd) ? cmd : FindExecutable(cmd);

            if (executablePath == null) continue;

            try
            {
                var tempPng = Path.Combine(Path.GetTempPath(), $"_pdfpage_{Guid.NewGuid()}.png");
                var args = $"-density 300 \"{pdfPath}[0]\" -quality 100 \"{tempPng}\"";
                var psi = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) continue;

                proc.WaitForExit(30_000);
                if (proc.ExitCode == 0 && File.Exists(tempPng))
                {
                    var bytes = File.ReadAllBytes(tempPng);
                    try { File.Delete(tempPng); } catch { }
                    return bytes;
                }

                try { _ = proc.StandardError.ReadToEnd(); } catch { }
                try { if (File.Exists(tempPng)) File.Delete(tempPng); } catch { }
            }
            catch { }
        }

        return null;
    }

    static Task<string> RunOcrOnImageBytesAsync(byte[] imageBytes)
    {
        var tessDataPath = FindTessDataPath();
        if (tessDataPath is null)
        {
            Console.WriteLine("    [OCR Notice]: tessdata not found. Install Tesseract or place eng.traineddata in a tessdata folder (project, TESSDATA_PREFIX, or standard Tesseract install path).");
            return Task.FromResult(string.Empty);
        }

#if TESSERACT_NATIVE
        try
        {
            using var engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default);
            using var img = Pix.LoadFromMemory(imageBytes);
            using var page = engine.Process(img);
            var resultText = page.GetText() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(resultText))
                Console.WriteLine("    [OCR Notice]: Tesseract OCR returned no text.");
            return Task.FromResult(resultText);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    [OCR Image Error Details]: {ex.GetType().Name}. Document content was not logged.");
            return Task.FromResult(string.Empty);
        }
#else
        return RunOcrWithCommandLineAsync(imageBytes, tessDataPath);
#endif
    }

#if !TESSERACT_NATIVE
    static async Task<string> RunOcrWithCommandLineAsync(byte[] imageBytes, string tessDataPath)
    {
        string? imagePath = null;
        try
        {
            var tesseractPath = FindExecutable("tesseract");
            if (tesseractPath is null)
            {
                Console.WriteLine("    [OCR Notice]: Tesseract is not installed or is not available on PATH. Install it with 'brew install tesseract'.");
                return string.Empty;
            }

            imagePath = Path.Combine(Path.GetTempPath(), $"_ocr_{Guid.NewGuid():N}.img");
            await File.WriteAllBytesAsync(imagePath, imageBytes);
            var startInfo = new ProcessStartInfo(tesseractPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(imagePath);
            startInfo.ArgumentList.Add("stdout");
            startInfo.ArgumentList.Add("-l");
            startInfo.ArgumentList.Add("eng");
            startInfo.ArgumentList.Add("--tessdata-dir");
            startInfo.ArgumentList.Add(tessDataPath);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Tesseract could not be started.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var errorOutput = await errorTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Tesseract exited with code {process.ExitCode}: {errorOutput}");

            var resultText = await outputTask;
            if (string.IsNullOrWhiteSpace(resultText))
                Console.WriteLine("    [OCR Notice]: Tesseract OCR returned no text.");
            return resultText;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    [OCR Image Error Details]: {ex.GetType().Name}. Document content was not logged.");
            return string.Empty;
        }
        finally
        {
            if (imagePath is not null && File.Exists(imagePath))
                File.Delete(imagePath);
        }
    }
#endif

    static string? FindTessDataPath()
    {
        var tessDataPaths = new List<string>();
        var tessDataEnv = Environment.GetEnvironmentVariable("TESSDATA_PREFIX");
        if (!string.IsNullOrWhiteSpace(tessDataEnv))
            tessDataPaths.Add(tessDataEnv);

        tessDataPaths.Add(Path.Combine(AppContext.BaseDirectory, "tessdata"));
        tessDataPaths.Add(Path.Combine(Directory.GetCurrentDirectory(), "tessdata"));

        if (OperatingSystem.IsWindows())
        {
            tessDataPaths.AddRange(new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tessdata"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tesseract-OCR", "tessdata"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tesseract-OCR", "tessdata"),
                Path.Combine("C:\\", "tessdata"),
                Path.Combine("C:\\", "ProgramData", "Tesseract-OCR", "tessdata")
            });
        }

        return tessDataPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(path => Directory.Exists(path) && File.Exists(Path.Combine(path, "eng.traineddata")));
    }

    static string? FindExecutable(string command)
    {
        var suffixes = OperatingSystem.IsWindows() ? new[] { ".exe", "" } : new[] { "" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var suffix in suffixes)
            {
                var candidate = Path.Combine(directory, command + suffix);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    static DocumentType ClassifyDocument(string text, string fileName)
    {
        string combined = (text + " " + fileName).ToLowerInvariant();
        string fileStem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();

        if (IsAtoStatement(combined, fileStem))
        {
            return DocumentType.AtoStatement;
        }

        if (combined.Contains("passport") || combined.Contains("p<") ||
            Regex.IsMatch(fileStem, @"(^|[^a-z])pp([^a-z]|$)") ||
            fileStem.Contains("passport") ||
            (combined.Contains("date of birth") && (combined.Contains("nationality") || combined.Contains("date of issue") || combined.Contains("date of expiry") || combined.Contains("australia") || combined.Contains("passport"))))
        {
            return DocumentType.Passport;
        }

        if ((combined.Contains("date of birth") || combined.Contains("date of issue") || combined.Contains("date of expiry")) &&
            (combined.Contains("nationality") || combined.Contains("aus") || combined.Contains("australia") || combined.Contains("mrz") || combined.Contains("passport")))
        {
            return DocumentType.Passport;
        }

        if (combined.Contains("driver licence") || combined.Contains("driving licence") ||
            combined.Contains("driver license") || combined.Contains("driving license") ||
            combined.Contains("licence no") || combined.Contains("license no") ||
            combined.Contains("driver's license") || combined.Contains("driver's licence") ||
            Regex.IsMatch(fileStem, @"(^|[^a-z])dl([^a-z]|$)") ||
            fileStem.Contains("driverlicence") || fileStem.Contains("driverlicense") || fileStem.Contains("drivinglicence") || fileStem.Contains("drivinglicense"))
        {
            return DocumentType.DrivingLicence;
        }

        if (combined.Contains("tenancy") || combined.Contains("tenancy history") || combined.Contains("lease") || combined.Contains("rental") || combined.Contains("renters") || combined.Contains("tenancy ledger") || combined.Contains("rent") || combined.Contains("property managed") || combined.Contains("rental agreement") || combined.Contains("landlord"))
        {
            return DocumentType.Rental;
        }

        if (combined.Contains("salary") || combined.Contains("payslip") || combined.Contains("pay advice") ||
            combined.Contains("period end") || combined.Contains("pay period") || combined.Contains("pay date") ||
            combined.Contains("net pay") || combined.Contains("superannuation") || combined.Contains("taxable income"))
        {
            return DocumentType.SalarySlip;
        }

        if (combined.Contains("bank statement") || combined.Contains("statement of account") ||
            combined.Contains("account statement") || combined.Contains("financial statement") ||
            (combined.Contains("statement") && (combined.Contains("bsb") || combined.Contains("account number") || combined.Contains("acc no") || combined.Contains("opening balance") || combined.Contains("closing balance") || combined.Contains("transaction"))))
        {
            return DocumentType.BankStatement;
        }

        // rental handled earlier

        return DocumentType.Unknown;
    }

    static bool IsAtoStatement(string combined, string fileStem)
    {
        bool hasAtoAgency = Regex.IsMatch(combined, @"\b(australian taxation office|taxation office)\b") ||
                            Regex.IsMatch(fileStem, @"(^|[^a-z])ato([^a-z]|$)");
        bool hasAtoDocumentType = Regex.IsMatch(
            combined,
            @"\b(statement|income|assessment|tax return|payment summary|taxation)\b",
            RegexOptions.IgnoreCase);
        bool explicitAssessment = Regex.IsMatch(combined, @"\bnotice of assessment\b") &&
                                  Regex.IsMatch(combined, @"\b(australia|ato|taxation)\b");
        bool myGovIncomeStatement = Regex.IsMatch(combined, @"\bmygov\b") &&
                                    Regex.IsMatch(combined, @"\bincome statement\b");

        return (hasAtoAgency && hasAtoDocumentType) || explicitAssessment || myGovIncomeStatement;
    }

    static AtoStatementDetails ExtractAtoStatementDetails(string text, string fileName)
    {
        var combined = $"{text}\n{fileName}";
        string? documentType = null;
        if (Regex.IsMatch(combined, @"\bnotice of assessment\b", RegexOptions.IgnoreCase))
            documentType = "Notice-of-Assessment";
        else if (Regex.IsMatch(combined, @"\b(income statement|payment summary)\b", RegexOptions.IgnoreCase))
            documentType = "Income-Statement";
        else if (Regex.IsMatch(combined, @"\btax return\b", RegexOptions.IgnoreCase))
            documentType = "Tax-Return";
        else if (Regex.IsMatch(combined, @"\baccount statement\b", RegexOptions.IgnoreCase))
            documentType = "Account-Statement";

        string? financialYear = ExtractFinancialYear(combined);
        return new AtoStatementDetails(documentType, financialYear);
    }

    static string? ExtractFinancialYear(string text)
    {
        var match = Regex.Match(
            text,
            @"\b(?:FY|financial year|income year)\s*(20\d{2})\s*[-/]\s*(\d{2}|20\d{2})\b",
            RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var startYear = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var endText = match.Groups[2].Value;
            var endYear = endText.Length == 2
                ? (startYear / 100 * 100) + int.Parse(endText, CultureInfo.InvariantCulture)
                : int.Parse(endText, CultureInfo.InvariantCulture);
            if (endYear == startYear + 1 && startYear >= 2000 && endYear <= DateTime.Today.Year + 1)
                return $"{startYear}-{endYear}";
        }

        match = Regex.Match(
            text,
            @"\b(?:financial year|income year)(?:\s+ended|\s+ending)?\s+(?:30\s+June\s+)?(20\d{2})\b",
            RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var endYear = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var startYear = endYear - 1;
            if (startYear >= 2000 && endYear <= DateTime.Today.Year + 1)
                return $"{startYear}-{endYear}";
        }

        return null;
    }

    static string? ExtractBankName(string text, string fallbackFileName)
    {
        string combined = text + " " + fallbackFileName;

        string[] knownBanks =
        {
            "National Australia Bank", "NAB",
            "Commonwealth Bank", "CBA", "CommBank",
            "Westpac", "ANZ", "St.George", "St George", "Bank of Queensland", "BOQ", "Bendigo Bank", "Macquarie Bank", "Macquarie", "ING", "Suncorp",
            "Chase", "Bank of America", "Wells Fargo", "Citibank", "Barclays", "HSBC", "Lloyds", "NatWest", "Standard Chartered"
        };

        foreach (var bank in knownBanks)
        {
            if (Regex.IsMatch(combined, $@"\b{Regex.Escape(bank)}\b", RegexOptions.IgnoreCase))
            {
                if (bank.Equals("National Australia Bank", StringComparison.OrdinalIgnoreCase)) return "NAB";
                if (bank.Equals("Commonwealth Bank", StringComparison.OrdinalIgnoreCase) || bank.Equals("CommBank", StringComparison.OrdinalIgnoreCase)) return "CBA";
                if (bank.Equals("St.George", StringComparison.OrdinalIgnoreCase) || bank.Equals("St George", StringComparison.OrdinalIgnoreCase)) return "StGeorge";
                return Regex.Replace(bank, @"\s+", "");
            }
        }

        var bankMatch = Regex.Match(text, @"\b([A-Za-z]+(?:\s+[A-Za-z]+)?\s+Bank)\b", RegexOptions.IgnoreCase);
        if (bankMatch.Success)
        {
            return Regex.Replace(bankMatch.Groups[1].Value.Trim(), @"\s+", "");
        }

        return null;
    }

    static string? ExtractAccountHolderName(string text, string fallbackFileName)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            var match = Regex.Match(text, @"(?:Account\s*Name|Account\s*Holder|Account\s*Title|Account\s*Name\:)\s*[:\-]?\s*([A-Za-z][A-Za-z\s\.&,\-]{2,})", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var name = match.Groups[1].Value.Trim();
                return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name.ToLower());
            }

            match = Regex.Match(text, @"(?:Name|Holder)\s*[:\-]\s*([A-Za-z][A-Za-z\s\.&,\-]{2,})", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var name = match.Groups[1].Value.Trim();
                return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name.ToLower());
            }
        }

        var p = ExtractPersonName(text, fallbackFileName);
        if (!string.IsNullOrWhiteSpace(p)) return p;

        var fnMatch = Regex.Match(fallbackFileName, "^([A-Za-z]+)");
        if (fnMatch.Success) return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(fnMatch.Groups[1].Value.ToLower());

        return null;
    }

    static string? ExtractAccountNumber(string text, string fallbackFileName)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            var match = Regex.Match(text, @"(?:Account\s*Number|Account\s*No\.?|Acc\.?\s*No\.?|Account)\s*[:\-]?\s*([0-9\-\s]{6,20})", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var num = Regex.Replace(match.Groups[1].Value, "[^0-9]", "");
                if (num.Length >= 6) return num;
            }

            match = Regex.Match(text, @"\b([0-9]{6,12})\b");
            if (match.Success) return match.Groups[1].Value;
        }

        var fmatch = Regex.Match(fallbackFileName, @"([0-9]{6,12})");
        if (fmatch.Success) return fmatch.Groups[1].Value;

        return null;
    }

    static string? ExtractPersonName(string text, string fallbackFileName)
    {
        static string NormalizePersonName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var cleaned = Regex.Replace(value, @"[\u0000-\u001F\u007F]+", " ");
            cleaned = cleaned.Replace("_", " ");
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

            if (string.IsNullOrWhiteSpace(cleaned)) return string.Empty;

            string[] noise = { "fund", "trust", "trustee", "bank", "statement", "salary", "payslip", "driver", "driving", "licence", "license", "passport", "doc", "scan", "id", "public", "service", "employee", "employer", "australia", "new", "south", "wales", "nsw", "road", "street", "avenue", "drive", "hill", "line", "court", "place", "unit", "of", "re", "aus" };
            var tokens = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var resultTokens = new List<string>();

            foreach (var token in tokens)
            {
                var upper = token.ToUpperInvariant();
                if (noise.Contains(token.ToLowerInvariant())) continue;
                if (upper.Length <= 1) continue;
                if (upper == "AND" || upper == "THE") continue;
                if (char.IsDigit(token[0])) continue;

                resultTokens.Add(CultureInfo.CurrentCulture.TextInfo.ToTitleCase(token.ToLowerInvariant()));
            }

            return string.Join(" ", resultTokens);
        }

        static bool TryBuildPersonName(string firstToken, string secondToken, out string result)
        {
            result = string.Empty;

            string[] corporate = { "FUND", "LTD", "PTY", "INC", "LLC", "TRUSTEE", "THE", "ABN", "BANK", "SUPERANNUATION", "EMPLOYEE", "EMPLOYER", "AUSTRALIA", "NSW", "NEW", "SOUTH", "WALES", "ROAD", "STREET", "AVENUE", "DRIVE", "HILL", "LINE", "COURT", "PLACE", "UNIT", "OF", "RE", "AUS" };

            var a = NormalizePersonName(firstToken);
            var b = NormalizePersonName(secondToken);
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            if (corporate.Contains(a.ToUpperInvariant()) || corporate.Contains(b.ToUpperInvariant())) return false;

            var candidate = NormalizePersonName($"{a} {b}");
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            if (!IsLikelyValidPersonName(candidate)) return false;

            result = candidate;
            return true;
        }

        var mrzName = ExtractNameFromMrz(text);
        if (!string.IsNullOrWhiteSpace(mrzName) && IsLikelyValidPersonName(mrzName)) return mrzName;

        if (!string.IsNullOrWhiteSpace(text))
        {
            foreach (var pattern in new[]
            {
                @"(?:Employee\s*Name|Name|Payee|Member\s*Name|Customer\s*Name)\s*[:\-]?\s*(?:\d+\s+)?([A-Z][A-Za-z'\-\.]+(?:\s+[A-Z][A-Za-z'\-\.]+){1,3})",
                @"(?:Emp\.?\s*No\.?\s*\d*\s*)?([A-Z][A-Za-z'\-\.]+)\s*,\s*([A-Z][A-Za-z'\-\.]+)",
                @"(?:Employee\s*No\.?\s*\d*\s*[:\-]?\s*)?([A-Z][A-Za-z'\-\.]+)\s+([A-Z][A-Za-z'\-\.]+)",
                @"\b([A-Z][A-Za-z'\-\.]+)\s*,\s*([A-Z][A-Za-z'\-\.]+)\b"
            })
            {
                foreach (Match match in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
                {
                    var candidate = match.Groups[1].Value;
                    if (match.Groups.Count > 2 && !string.IsNullOrWhiteSpace(match.Groups[2].Value))
                    {
                        candidate = $"{match.Groups[2].Value} {match.Groups[1].Value}";
                    }

                    var normalized = NormalizePersonName(candidate);
                    if (IsLikelyValidPersonName(normalized)) return normalized;
                }
            }

            var upperLastFirst = Regex.Match(text, @"\b([A-Z]{2,}),\s*([A-Z]{3,}(?:\s+[A-Z]{2,})*)\b");
            if (upperLastFirst.Success && TryBuildPersonName(upperLastFirst.Groups[2].Value, upperLastFirst.Groups[1].Value, out var upperResult))
            {
                return upperResult;
            }

            var lastFirstMatch = Regex.Match(text, @"\b([A-Za-z]{2,}),\s*([A-Za-z]{3,}(?:\s+[A-Za-z]+)*)\b");
            if (lastFirstMatch.Success)
            {
                if (TryBuildPersonName(lastFirstMatch.Groups[2].Value, lastFirstMatch.Groups[1].Value, out var lastFirstResult))
                {
                    return lastFirstResult;
                }
            }
        }

        var fallbackMatch = Regex.Match(fallbackFileName, @"^([A-Za-z]+)");
        if (fallbackMatch.Success)
        {
            string candidate = NormalizePersonName(fallbackMatch.Groups[1].Value);
            string[] genericPrefixes = new[] { "pay", "advice", "salary", "slip", "payslip", "passport", "license", "licence", "dl", "doc", "scan", "kot", "el" };
            if (!genericPrefixes.Contains(candidate.ToLowerInvariant()) && IsLikelyValidPersonName(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    static DateTime? ExtractExpiryDate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        static bool TryParseMrzCompactDate(string token, out DateTime result)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(token)) return false;
            token = Regex.Replace(token, "[^0-9]", "");
            if (token.Length != 6) return false;

            int yy = int.Parse(token.Substring(0, 2));
            int mm = int.Parse(token.Substring(2, 2));
            int dd = int.Parse(token.Substring(4, 2));
            if (mm < 1 || mm > 12 || dd < 1 || dd > 31) return false;

            int year = yy <= 60 ? 2000 + yy : 1900 + yy;
            try
            {
                result = new DateTime(year, mm, dd);
                return true;
            }
            catch
            {
                return false;
            }
        }

        var passportLines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => Regex.Replace(l.Trim(), @"\s+", ""))
            .ToList();

        var birthDate = (DateTime?)null;
        var candidateExpiryDates = new List<DateTime>();

        for (int i = 0; i < passportLines.Count; i++)
        {
            var line = passportLines[i];
            if (!line.Contains("P<", StringComparison.OrdinalIgnoreCase)) continue;

            if (i + 1 < passportLines.Count)
            {
                var mrzSecondLine = passportLines[i + 1];
                var numericTokens = Regex.Matches(mrzSecondLine, @"\d{6}");
                foreach (Match match in numericTokens)
                {
                    if (TryParseMrzCompactDate(match.Value, out var parsed))
                    {
                        candidateExpiryDates.Add(parsed);
                    }
                }

                var birthMatch = Regex.Match(mrzSecondLine, @"[A-Z0-9<]{5,}[0-9]{6}[0-9A-Z<]{1}[0-9]{6}");
                if (birthMatch.Success)
                {
                    var birthPart = Regex.Matches(birthMatch.Value, @"\d{6}");
                    if (birthPart.Count >= 2)
                    {
                        if (TryParseMrzCompactDate(birthPart[0].Value, out var parsedBirth))
                        {
                            birthDate = parsedBirth;
                        }
                        if (birthPart.Count >= 2 && TryParseMrzCompactDate(birthPart[1].Value, out var parsedExpiry))
                        {
                            candidateExpiryDates.Add(parsedExpiry);
                        }
                    }
                }
            }
        }

        if (birthDate != null)
        {
            var validExpiry = candidateExpiryDates
                .Where(d => d > birthDate.Value.AddYears(1))
                .OrderByDescending(d => d)
                .FirstOrDefault();
            if (validExpiry != default)
            {
                return validExpiry;
            }
        }

        if (candidateExpiryDates.Count > 0)
        {
            return candidateExpiryDates.OrderByDescending(d => d).FirstOrDefault();
        }

        string normalizedText = text;
        normalizedText = normalizedText.Replace("xp1ry", "expiry");
        normalizedText = normalizedText.Replace("xplry", "expiry");
        normalizedText = normalizedText.Replace("exp1ry", "expiry");
        normalizedText = normalizedText.Replace("vaild", "valid");
        normalizedText = normalizedText.Replace("Dote de", "Date de");

        var explicitPatterns = new[]
        {
            @"(?:Date\s*of\s*Expiry|Expiry\s*Date|Expires\s*on|Expires|Valid\s*(?:Until|to)|EXP\.?|[Ee]xpiry|[Ee]xpires|[Ee]xpir[y1])\s*[:\-]?\s*([0-9]{1,2}\s*[-\/\.]?\s*[A-Za-z0-9]+\s*[-\/\.]?\s*[0-9]{2,4})",
            @"(?:Expiry|Expires|Valid\s*(?:Until|to))[^0-9]{0,20}([0-9]{1,2}\s+[A-Za-z]{3,9}\s+[0-9]{2,4})",
            @"(?:Expiry|Expires|Valid\s*(?:Until|to)\s*[:\-]?\s*)([0-9]{1,2}\s*[-/\.]\s*[0-9]{1,2}\s*[-/\.]\s*[0-9]{2,4})"
        };

        foreach (var pattern in explicitPatterns)
        {
            var match = Regex.Match(normalizedText, pattern, RegexOptions.IgnoreCase);
            if (match.Success && TryParseFlexibleDate(match.Groups[1].Value, out var explicitDate))
            {
                return explicitDate;
            }
        }

        var dobMatch = Regex.Match(normalizedText, @"Date\s*of\s*Birth\s*[:\-]?\s*([0-9]{1,2}\s+[A-Za-z]{3,9}\s+[0-9]{2,4})", RegexOptions.IgnoreCase);
        if (dobMatch.Success && TryParseFlexibleDate(dobMatch.Groups[1].Value, out var birthDateFallback))
        {
            var possibleExpiryDates = Regex.Matches(normalizedText, @"\b([0-9]{1,2}\s+[A-Za-z]{3,9}\s+[0-9]{2,4})\b", RegexOptions.IgnoreCase);
            foreach (Match match in possibleExpiryDates)
            {
                if (match.Value.Equals(dobMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) continue;
                if (TryParseFlexibleDate(match.Groups[1].Value, out var candidateDate) && candidateDate > birthDateFallback.AddYears(1))
                {
                    return candidateDate;
                }
            }
        }

        var anyDmy = Regex.Matches(normalizedText, @"\b([0-9]{1,2}\s*[A-Za-z]{3}\s*[0-9]{4})\b", RegexOptions.IgnoreCase);
        if (anyDmy.Count > 0)
        {
            DateTime? latest = null;
            foreach (Match m in anyDmy)
            {
                if (TryParseFlexibleDate(m.Groups[1].Value, out var d))
                {
                    if (latest == null || d > latest) latest = d;
                }
            }
            if (latest != null)
            {
                var dobFallback = Regex.Match(normalizedText, @"Date\s*of\s*Birth\s*[:\-]?\s*([0-9]{1,2}\s+[A-Za-z]{3,9}\s+[0-9]{2,4})", RegexOptions.IgnoreCase);
                if (dobFallback.Success && TryParseFlexibleDate(dobFallback.Groups[1].Value, out var birthDateFallback2) && latest > birthDateFallback2.AddYears(1))
                {
                    return latest;
                }
            }
        }

        return null;
    }

    static string? ExtractNameFromMrz(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        static bool LooksLikeMrzFiller(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            token = Regex.Replace(token, "[^A-Z]", "", RegexOptions.IgnoreCase);
            if (token.Length <= 4) return false;

            if (Regex.IsMatch(token, @"(.)\1{4,}", RegexOptions.IgnoreCase)) return true;

            var groups = token.GroupBy(ch => ch).OrderByDescending(g => g.Count()).ToList();
            if (groups.Count == 0) return false;

            var dominant = groups[0].Count();
            return dominant >= token.Length * 0.7;
        }

        static string CleanMrzField(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            var compact = raw.Replace('<', ' ').Replace('-', ' ');
            compact = Regex.Replace(compact, @"\d+", " ");
            compact = Regex.Replace(compact, @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(compact)) return string.Empty;

            var countryCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AUS", "USA", "GBR", "CAN", "NZL", "IND", "NPL", "PAK", "LKA", "BGD",
                "FRA", "DEU", "ESP", "ITA", "NLD", "BEL", "AUT", "SWE", "NOR", "DNK",
                "FIN", "IRL", "PRT", "CHE", "CZE", "POL", "GRC", "TUR", "RUS", "UKR",
                "JPN", "KOR", "CHN", "HKG", "SGP", "MYS", "IDN", "PHL", "THA", "VNM",
                "ZAF", "EGY", "SAU", "ARE", "QAT", "KWT", "BHR", "OMN", "ISR", "MEX",
                "BRA", "ARG", "CHL", "PER", "COL", "ECU", "URY", "PRY", "BOL", "DOM",
                "CUB", "CRI", "PAN", "GTM", "HND", "NIC", "SLV"
            };

            var compactUpper = compact.ToUpperInvariant();
            if (compactUpper.Length >= 4 && countryCodes.Contains(compactUpper.Substring(0, 3)))
            {
                compact = compactUpper.Substring(3).Trim();
            }

            var tokens = compact.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var filtered = new List<string>();
            foreach (var token in tokens)
            {
                var normalized = token.Trim();
                if (string.IsNullOrWhiteSpace(normalized)) continue;
                if (normalized.Length <= 1) continue;
                if (LooksLikeMrzFiller(normalized)) continue;
                if (normalized.Any(ch => !char.IsLetter(ch))) continue;
                filtered.Add(normalized);
            }

            return string.Join(" ", filtered).Trim();
        }

        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 8) continue;

            var match = Regex.Match(trimmed, @"P<([A-Z0-9<]+)<<([A-Z0-9<]+)", RegexOptions.IgnoreCase);
            if (!match.Success) continue;

            string surnameRaw = match.Groups[1].Value;
            string givenRaw = match.Groups[2].Value;

            string surname = CleanMrzField(surnameRaw);
            string given = CleanMrzField(givenRaw);

            var candidateName = string.Join(" ", new[] { given, surname }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (IsLikelyValidPersonName(candidateName))
            {
                return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(candidateName.ToLowerInvariant());
            }
        }

        var legacy = Regex.Match(text, @"([A-Z<]{2,})<<([A-Z<]{2,})", RegexOptions.IgnoreCase);
        if (legacy.Success)
        {
            string surnameRaw = legacy.Groups[1].Value;
            string givenRaw = legacy.Groups[2].Value;

            string surname = CleanMrzField(surnameRaw);
            string given = CleanMrzField(givenRaw);
            var candidateName = string.Join(" ", new[] { given, surname }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (IsLikelyValidPersonName(candidateName))
            {
                return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(candidateName.ToLowerInvariant());
            }
        }

        return null;
    }

    static (DateTime? StartDate, DateTime? EndDate) ExtractPeriodDates(string text, string fallbackFileName)
    {
        string datePattern = @"(\d{1,2}[-\/][A-Za-z0-9]+[-\/]\d{4})\s*(?:to|-|–|—)\s*(\d{1,2}[-\/][A-Za-z0-9]+[-\/]\d{4})";

        var match = Regex.Match(text, datePattern, RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            match = Regex.Match(fallbackFileName, datePattern, RegexOptions.IgnoreCase);
        }

        if (match.Success)
        {
            string startRaw = match.Groups[1].Value;
            string endRaw = match.Groups[2].Value;

            if (TryParseFlexibleDate(startRaw, out var start) && TryParseFlexibleDate(endRaw, out var end))
            {
                return (start, end);
            }
        }

        var payDateIndex = text.IndexOf("Pay Date", StringComparison.OrdinalIgnoreCase);
        if (payDateIndex >= 0)
        {
            var monthIndex = text.IndexOf("Month", payDateIndex, StringComparison.OrdinalIgnoreCase);
            if (monthIndex >= 0 && monthIndex - payDateIndex <= 120)
            {
                var dateSectionLength = Math.Min(120, text.Length - monthIndex);
                var dateSection = text.Substring(monthIndex, dateSectionLength);
                var dates = Regex.Matches(dateSection, @"\d{1,2}[-\/.][A-Za-z0-9]+[-\/.]\d{4}")
                    .Select(match => match.Value)
                    .Distinct(StringComparer.Ordinal)
                    .Select(value => TryParseFlexibleDate(value, out var parsed) ? parsed : (DateTime?)null)
                    .Where(value => value.HasValue)
                    .Select(value => value!.Value)
                    .Order()
                    .ToArray();

                if (dates.Length == 2)
                    return (dates[0], dates[1]);
            }
        }

        return (null, null);
    }

    static string? ExtractEmployer(string text)
    {
        var match = Regex.Match(text, @"(?:Employer)\s*[:\-]?\s*([A-Za-z0-9\s&,\.\-]+?)(?=\s*(?:\d{2}[\/\-]|Period\s*End|Job\s*Class|Employee|$))", RegexOptions.IgnoreCase);
        if (match.Success && match.Groups[1].Value.Trim().Length > 0)
        {
            return match.Groups[1].Value.Trim();
        }

        var companyMatch = Regex.Match(text, @"\b([A-Za-z0-9\s&]+?\s+(?:PTY\s+LTD|LTD|LIMITED|INC|CORP|LLC))\b", RegexOptions.IgnoreCase);
        if (companyMatch.Success)
        {
            return companyMatch.Groups[1].Value.Trim();
        }

        return null;
    }

    static bool TryParseFlexibleDate(string raw, out DateTime result)
    {
        string cleaned = Regex.Replace(raw.Trim(), @"\s+", " ");
        cleaned = Regex.Replace(cleaned, @"^(\d{1,2})([A-Za-z]{3,9})\s+(\d{4})$", "$1 $2 $3");

        string[] formats =
        {
            "d-M-yyyy", "dd-MM-yyyy", "d-MMM-yyyy", "dd-MMM-yyyy",
            "d/M/yyyy", "dd/MM/yyyy", "d/MMM/yyyy", "dd/MMM/yyyy",
            "d.M.yyyy", "dd.MM.yyyy", "d.MMM.yyyy", "dd.MMM.yyyy",
            "d MMM yyyy", "dd MMM yyyy", "d MMMM yyyy", "dd MMMM yyyy",
            "d-MMM-yy", "dd-MMM-yy",
            "yyyy-MM-dd", "yyyy/MM/dd", "yyyy.MM.dd"
        };

        return DateTime.TryParseExact(cleaned, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out result)
            || DateTime.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    static bool LooksLikeIdentityDocument(string text, string fileName)
    {
        string combined = (text + " " + fileName).ToLowerInvariant();

        bool hasBirthMarker = combined.Contains("dateof birth") || combined.Contains("date of birth") || combined.Contains("dateofbirth") || combined.Contains("dob") ||
            Regex.IsMatch(combined, @"date\s*of\s*birth");
        bool hasBirthDate = Regex.IsMatch(combined, @"\b\d{1,2}\s+(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)\s+\d{4}\b");

        bool hasIssueOrExpiry = combined.Contains("date of issue") || combined.Contains("dateof issue") ||
            combined.Contains("date of expiry") || combined.Contains("dateof expiry") || combined.Contains("expiry") || combined.Contains("expires");
        bool hasNationality = combined.Contains("nationality") || combined.Contains("aus") || combined.Contains("australia");
        bool hasPassportMarkers = combined.Contains("passport") || combined.Contains("mrz") || combined.Contains("p<") || combined.Contains("passport no");
        bool hasLicenceMarkers = combined.Contains("driver licence") || combined.Contains("driving licence") || combined.Contains("license no") || combined.Contains("licence no");

        if (hasBirthMarker && hasBirthDate) return true;
        return (hasBirthMarker && (hasIssueOrExpiry || hasNationality || hasPassportMarkers || hasLicenceMarkers)) || hasPassportMarkers || hasLicenceMarkers;
    }

    static bool IsImageFile(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext == ".jpg" || ext == ".jpeg" || ext == ".png";
    }

    static bool IsReadableImage(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return false;
            byte[] bytes = File.ReadAllBytes(filePath);
            if (bytes.Length == 0) return false;

#if TESSERACT_NATIVE
            using var img = Pix.LoadFromMemory(bytes);
            return img is not null;
#else
            return HasSupportedImageSignature(bytes);
#endif
        }
        catch
        {
            return false;
        }
    }

    static bool HasSupportedImageSignature(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff ||
        bytes.Length >= 8 &&
        bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e && bytes[3] == 0x47 &&
        bytes[4] == 0x0d && bytes[5] == 0x0a && bytes[6] == 0x1a && bytes[7] == 0x0a;
}