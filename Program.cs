using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using Tesseract;
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

class Program
{
    static readonly List<string> ProcessingReportEntries = new();
    static readonly int AutoRescueThreshold = 5;

    static async Task Main(string[] args)
    {
        if (args.Length > 0 && args.Contains("--rescue-needs-review"))
        {
            Console.Write("Enter the folder path to rescue Needs Review from: ");
            string? folderPath = Console.ReadLine()?.Trim('"', ' ');
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                Console.WriteLine("Error: Invalid or non-existent directory path.");
                return;
            }

            await RescueNeedsReviewAsync(folderPath);
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

            await ProcessFolderAsync(folderPath);

            await PromptToRescueNeedsReviewAsync(folderPath);

            Console.Write("Process another folder? (y/N): ");
            var again = Console.ReadLine();
            if (!string.Equals(again, "y", StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    static async Task ProcessFolderAsync(string directoryPath)
    {
        ProcessingReportEntries.Clear();
        ProcessingReportEntries.Add($"SCAN: Folder processed: {directoryPath}");

        var supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".jpg", ".jpeg", ".png", ".txt"
        };

        var files = Directory.GetFiles(directoryPath, "*.*", SearchOption.TopDirectoryOnly)
                             .Where(f => supportedExtensions.Contains(Path.GetExtension(f)))
                             .Where(f => !Path.GetFileName(f).StartsWith("Processing-Report-", StringComparison.OrdinalIgnoreCase))
                             .ToList();

        if (files.Count == 0)
        {
            ProcessingReportEntries.Add("SKIP: No supported files found in the directory.");
            WriteProcessingReport(directoryPath);
            Console.WriteLine("No supported PDF, JPEG/JPG, PNG, or TXT files found in the directory.");
            return;
        }

        Console.WriteLine($"Found {files.Count} file(s) to inspect.\n");

        foreach (var filePath in files)
        {
            try
            {
                string fileName = Path.GetFileName(filePath);
                Console.WriteLine($"--------------------------------------------------");
                Console.WriteLine($"Processing: {fileName}");
                ProcessingReportEntries.Add($"SCAN: {fileName}");

                string extractedText = await ExtractTextAsync(filePath);
                if (string.IsNullOrWhiteSpace(extractedText))
                {
                    DocumentType fallbackType = ClassifyDocument(string.Empty, fileName);
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

                    Console.WriteLine("  Skipped: Unable to extract text or perform OCR.");
                    ProcessingReportEntries.Add($"SKIP: {fileName} -> no readable OCR text");
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
                ProcessingReportEntries.Add($"ERROR: {currentFileName} -> {ex.Message}");
                Console.WriteLine($"  Error processing file: {ex.Message}\n");
            }
        }

        WriteProcessingReport(directoryPath);
        Console.WriteLine("\nCompleted processing all files.");
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
            $"Moved to Needs Review: {ProcessingReportEntries.Count(x => x.StartsWith("REVIEW:"))}",
            $"Skipped: {ProcessingReportEntries.Count(x => x.StartsWith("SKIP:"))}",
            "",
            "Details:"
        };

        foreach (var entry in ProcessingReportEntries)
        {
            summary.Add(entry);
        }

        string reportPath = Path.Combine(directoryPath, $"Processing-Report-{DateTime.Now:yyyyMMddHHmmss}.txt");
        File.WriteAllLines(reportPath, summary);
        Console.WriteLine($"\nProcessing report saved to: {reportPath}");
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
            Directory.CreateDirectory(expiredDir);

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
            Directory.CreateDirectory(targetDir);

            string destFileName = $"{safeName}-{docTypeName}-ValidTill-{expiryStr}{extension}";
            string destinationPath = Path.Combine(targetDir, destFileName);

            MoveFileWithDuplicateCheck(filePath, destinationPath, directoryPath, $"{safeName} ({docTypeName})");
        }
    }

    #endregion

    #region Salary Slip Processing

    static void ProcessSalarySlip(string directoryPath, string filePath, string extractedText)
    {
        string? employeeName = ExtractPersonName(extractedText, Path.GetFileNameWithoutExtension(filePath));
        Console.WriteLine($"  [DEBUG] ExtractPersonName result: '{employeeName ?? "<null>"}'");
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
        Directory.CreateDirectory(targetDir);

        string extension = Path.GetExtension(filePath);
        string baseFileName = $"{safeName}-SalarySlip-Period-{startStr}-{endStr}";
        string destinationPath = Path.Combine(targetDir, $"{baseFileName}{extension}");

        MoveFileWithDuplicateCheck(filePath, destinationPath, directoryPath, $"{safeName} (Period {startStr}-{endStr}, {employer ?? "Employer"})");
    }

    #endregion

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
        Directory.CreateDirectory(targetDir);

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
        string accountNumber = ExtractAccountNumber(extractedText, fileName) ?? "UnknownAccount";

        if (!IsLikelyValidPersonName(holderName))
        {
            Console.WriteLine("  Review required: Bank statement could not be assigned a valid account holder name.");
            MoveToNeedsReview(directoryPath, filePath, "Bank Statement - invalid account holder name");
            return;
        }

        if (string.IsNullOrWhiteSpace(accountNumber) || !Regex.IsMatch(accountNumber, @"^\d{6,20}$"))
        {
            Console.WriteLine("  Review required: Bank statement account number is missing or invalid.");
            MoveToNeedsReview(directoryPath, filePath, "Bank Statement - invalid account number");
            return;
        }

        string safeBank = Regex.Replace(bankName, @"[^A-Za-z0-9\-]", "");
        string safeHolder = ToSafeFileName(holderName);
        string safeAccount = Regex.Replace(accountNumber, @"[^0-9]", "");

        string targetDir = Path.Combine(directoryPath, "Bank Statements");
        Directory.CreateDirectory(targetDir);

        string baseFileName = $"{safeBank}_{safeHolder}_{safeAccount}";
        string destinationPath = Path.Combine(targetDir, $"{baseFileName}{extension}");

        MoveFileWithDuplicateCheck(filePath, destinationPath, directoryPath, $"Bank Statement ({safeBank}, {safeHolder}, {safeAccount})");
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
        Directory.CreateDirectory(reviewDir);

        string extension = Path.GetExtension(sourceFilePath);
        string fileName = Path.GetFileNameWithoutExtension(sourceFilePath);
        string safeBase = ToSafeFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeBase)) safeBase = "NeedsReview";

        string destinationPath = Path.Combine(reviewDir, $"{safeBase}-{DateTime.Now:yyyyMMddHHmmss}{extension}");
        ProcessingReportEntries.Add($"REVIEW: {Path.GetFileName(sourceFilePath)} -> {destinationPath} ({reason})");
        MoveFileWithDuplicateCheck(sourceFilePath, destinationPath, directoryPath, reason);
    }

    static void MoveFileWithDuplicateCheck(string sourcePath, string destinationPath, string rootDirectory, string logContext)
    {
        try
        {
            var destDir = Path.GetDirectoryName(destinationPath) ?? rootDirectory;
            Directory.CreateDirectory(destDir);

            if (!File.Exists(destinationPath))
            {
                File.Move(sourcePath, destinationPath);
                ProcessingReportEntries.Add($"MOVE: {Path.GetFileName(sourcePath)} -> {destinationPath}");
                Console.WriteLine($"  Moved: {Path.GetFileName(sourcePath)} -> {destinationPath}");
                return;
            }

            string dupDir = Path.Combine(rootDirectory, "Duplicate Files");
            Directory.CreateDirectory(dupDir);
            string newName = Path.GetFileNameWithoutExtension(destinationPath) + $"-DUP-{DateTime.Now:yyyyMMddHHmmss}" + Path.GetExtension(destinationPath);
            string dupPath = Path.Combine(dupDir, newName);
            File.Move(sourcePath, dupPath);
            ProcessingReportEntries.Add($"MOVE: {Path.GetFileName(sourcePath)} -> {dupPath} (duplicate) ");
            Console.WriteLine($"  Duplicate detected ({logContext}). Moved to: {dupPath}");
        }
        catch (Exception ex)
        {
            ProcessingReportEntries.Add($"ERROR: {Path.GetFileName(sourcePath)} -> {ex.Message}");
            Console.WriteLine($"  Error moving file '{sourcePath}': {ex.Message}");
        }
    }

    static async Task RescueNeedsReviewAsync(string directoryPath)
    {
        Console.WriteLine($"Rescuing Needs Review files under: {directoryPath}");
        var reviewDir = Path.Combine(directoryPath, "Needs Review");
        if (!Directory.Exists(reviewDir))
        {
            Console.WriteLine("No Needs Review folder found.");
            return;
        }

        var files = Directory.GetFiles(reviewDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => new[] { ".pdf", ".jpg", ".jpeg", ".png", ".txt" }.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (files.Count == 0)
        {
            Console.WriteLine("No files in Needs Review.");
            return;
        }

        foreach (var filePath in files)
        {
            try
            {
                Console.WriteLine($"Inspecting: {Path.GetFileName(filePath)}");
                string text = await ExtractTextAsync(filePath);
                var docType = ClassifyDocument(text, Path.GetFileName(filePath));

                // If classification strongly indicates Rental/Tenancy, prefer that over ID rescue
                if (docType == DocumentType.Rental)
                {
                    var fileStem = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
                    bool strongRental = Regex.IsMatch(text ?? string.Empty, @"\b(tenancy|tenancy history|tenancy ledger|lease|rental|renters|tenancy agreement|rental agreement|rent)\b", RegexOptions.IgnoreCase)
                        || Regex.IsMatch(fileStem, @"\b(tenancy|tenant|ledger|rental|rent)\b", RegexOptions.IgnoreCase);

                    if (strongRental)
                    {
                        ProcessingReportEntries.Add($"RESCUE-CLASSIFY: {Path.GetFileName(filePath)} -> Rental (strong)");
                        ProcessRentalDocument(directoryPath, filePath, text);
                        continue;
                    }
                    else
                    {
                        ProcessingReportEntries.Add($"RESCUE-CLASSIFY: {Path.GetFileName(filePath)} -> Rental (weak) - skipping rental rescue");
                    }
                }

                bool looksLikeDl = docType == DocumentType.DrivingLicence ||
                    Regex.IsMatch(text ?? string.Empty, "licen|driver|licence|license|lic\\b|expiry|card number", RegexOptions.IgnoreCase);

                if (looksLikeDl)
                {
                    string? personName = ExtractPersonName(text, Path.GetFileNameWithoutExtension(filePath));
                    string safeName = !string.IsNullOrWhiteSpace(personName) && IsLikelyValidPersonName(personName) ? ToSafeFileName(personName!) : ToSafeFileName(Path.GetFileNameWithoutExtension(filePath));
                    if (string.IsNullOrWhiteSpace(safeName)) safeName = "Unknown";

                    string targetDir = Path.Combine(directoryPath, safeName + "-ID-Documents");
                    Directory.CreateDirectory(targetDir);

                    string destFileName = Path.GetFileName(filePath);
                    string destinationPath = Path.Combine(targetDir, destFileName);

                    MoveFileWithDuplicateCheck(filePath, destinationPath, directoryPath, $"Rescued DrivingLicence ({safeName})");
                }
                else
                {
                    Console.WriteLine($"  Skipping (not a driving licence): {Path.GetFileName(filePath)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Error inspecting {Path.GetFileName(filePath)}: {ex.Message}");
            }
        }

        Console.WriteLine("Rescue complete.");
    }

    static async Task PromptToRescueNeedsReviewAsync(string directoryPath)
    {
        var reviewDir = Path.Combine(directoryPath, "Needs Review");
        if (!Directory.Exists(reviewDir)) return;

        var files = Directory.GetFiles(reviewDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => new[] { ".pdf", ".jpg", ".jpeg", ".png", ".txt" }.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (files.Count == 0) return;

        if (files.Count <= AutoRescueThreshold)
        {
            Console.WriteLine();
            Console.WriteLine($"Found {files.Count} file(s) in 'Needs Review' (<= {AutoRescueThreshold}) - auto reprocessing now.");
            await RescueNeedsReviewAsync(directoryPath);
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"Found {files.Count} file(s) in 'Needs Review'.");
        Console.WriteLine("Options: [1] Reprocess Needs Review now  [2] Open Needs Review folder  [3] Skip");

        while (true)
        {
            Console.Write("Choose [1/2/3] (default 3): ");
            var choice = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(choice) || choice == "3") break;
            if (choice == "1")
            {
                await RescueNeedsReviewAsync(directoryPath);
                break;
            }
            if (choice == "2")
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "explorer",
                        Arguments = "\"" + reviewDir + "\"",
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                }
                catch
                {
                    Console.WriteLine($"Could not open folder: {reviewDir}");
                }

                Console.WriteLine("Opened Needs Review folder. Choose again when ready.");
                continue;
            }

            Console.WriteLine("Invalid choice. Enter 1, 2, or 3.");
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
            try
            {
                var firstPageText = pages.Count > 0 ? pages[0].Text : string.Empty;
                Console.WriteLine("    [DEBUG] Embedded text (first page):");
                Console.WriteLine(string.IsNullOrWhiteSpace(firstPageText) ? "    <empty>" : firstPageText);
            }
            catch { }

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
                    Console.WriteLine("    [DEBUG] OCR text (first page):");
                    Console.WriteLine(string.IsNullOrWhiteSpace(ocr) ? "    <empty>" : ocr);
                    if (!string.IsNullOrWhiteSpace(ocr)) return ocr;
                }
                else
                {
                    Console.WriteLine("    [OCR Fallback]: Could not render PDF first page to image (ImageMagick not available).\n    Install ImageMagick or provide a PNG to enable OCR fallback.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    [OCR Fallback Error]: {ex.GetType().Name}: {ex.Message}");
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

                using var img = Pix.LoadFromMemory(fileBytes);
                if (img == null)
                {
                    throw new InvalidOperationException("Image file could not be decoded.");
                }

                return await RunOcrOnImageBytesAsync(fileBytes);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    [Image Decode Error]: {ex.Message}");
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
            Console.WriteLine($"    [OCR File Error]: {ex.Message}");
            return string.Empty;
        }
    }

    static byte[]? TryRenderPdfFirstPageToPng(string pdfPath)
    {
        string[] candidateRoots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86)
        };

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
                Path.Combine(magickDir, "magick.exe"),
                Path.Combine(magickDir, "convert.exe")
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

                        try { var err = proc.StandardError.ReadToEnd(); if (!string.IsNullOrWhiteSpace(err)) Console.WriteLine($"    [ImageMagick Error]: {err}"); } catch { }
                        try { if (File.Exists(tempPng)) File.Delete(tempPng); } catch { }
                    }
                    catch { }
                }
            }
        }

        string[] commands = { "magick", "convert" };
        foreach (var cmd in commands)
        {
            string? executablePath = null;
            try
            {
                if (File.Exists(cmd)) executablePath = cmd;
                else if (Environment.GetEnvironmentVariable("PATH") is string pathVar)
                {
                    var matches = pathVar.Split(Path.PathSeparator)
                        .Select(p => Path.Combine(p, cmd + ".exe"))
                        .Where(File.Exists)
                        .ToList();
                    executablePath = matches.FirstOrDefault();
                }
            }
            catch { }

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

                try { var err = proc.StandardError.ReadToEnd(); if (!string.IsNullOrWhiteSpace(err)) Console.WriteLine($"    [ImageMagick Error]: {err}"); } catch { }
                try { if (File.Exists(tempPng)) File.Delete(tempPng); } catch { }
            }
            catch { }
        }

        return null;
    }

    static Task<string> RunOcrOnImageBytesAsync(byte[] imageBytes)
    {
        try
        {
            var tessDataPaths = new List<string>();

            var tessDataEnv = Environment.GetEnvironmentVariable("TESSDATA_PREFIX");
            if (!string.IsNullOrWhiteSpace(tessDataEnv)) tessDataPaths.Add(tessDataEnv);

            string appBase = AppDomain.CurrentDomain.BaseDirectory;
            tessDataPaths.Add(Path.Combine(appBase, "tessdata"));
            tessDataPaths.Add(Path.Combine(AppContext.BaseDirectory, "tessdata"));
            tessDataPaths.Add(Path.Combine(Directory.GetCurrentDirectory(), "tessdata"));

            var standardPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tessdata"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tesseract-OCR", "tessdata"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tesseract-OCR", "tessdata"),
                Path.Combine("C:\\", "tessdata"),
                Path.Combine("C:\\", "ProgramData", "Tesseract-OCR", "tessdata")
            };

            foreach (var p in standardPaths)
            {
                if (!string.IsNullOrWhiteSpace(p)) tessDataPaths.Add(p);
            }

            string? tessDataPath = tessDataPaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(Directory.Exists);

            if (string.IsNullOrWhiteSpace(tessDataPath))
            {
                Console.WriteLine("    [OCR Notice]: tessdata not found. Install Tesseract or place eng.traineddata in a tessdata folder (project, TESSDATA_PREFIX, or standard Tesseract install path).");
                return Task.FromResult(string.Empty);
            }

            using var engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default);
            using var img = Pix.LoadFromMemory(imageBytes);
            using var page = engine.Process(img);
            var resultText = page.GetText();

            if (string.IsNullOrWhiteSpace(resultText))
            {
                Console.WriteLine("    [OCR Notice]: Tesseract OCR returned no text.");
            }

            return Task.FromResult(resultText ?? string.Empty);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    [OCR Image Error Details]: {ex.GetType().Name}: {ex.Message}");
            return Task.FromResult(string.Empty);
        }
    }

    static DocumentType ClassifyDocument(string text, string fileName)
    {
        string combined = (text + " " + fileName).ToLowerInvariant();
        string fileStem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();

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

        if (combined.Contains("ato statement"))
        {
            return DocumentType.AtoStatement;
        }

        // rental handled earlier

        return DocumentType.Unknown;
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

            using var img = Pix.LoadFromMemory(bytes);
            return img != null;
        }
        catch
        {
            return false;
        }
    }
}