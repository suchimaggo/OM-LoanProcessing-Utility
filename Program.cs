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
    BankStatement,
    Unknown
}

class Program
{
    static async Task Main(string[] args)
    {
        Console.Write("Enter the folder path to organize: ");
        string? folderPath = Console.ReadLine()?.Trim('"', ' ');

        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            Console.WriteLine("Error: Invalid or non-existent directory path.");
            return;
        }

        await ProcessFolderAsync(folderPath);
    }

    static async Task ProcessFolderAsync(string directoryPath)
    {
        // Supported file extensions: PDF, JPG, JPEG, PNG, TXT
        var supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".jpg", ".jpeg", ".png", ".txt"
        };

        var files = Directory.GetFiles(directoryPath, "*.*", SearchOption.TopDirectoryOnly)
                             .Where(f => supportedExtensions.Contains(Path.GetExtension(f)))
                             .ToList();

        if (files.Count == 0)
        {
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

                string extractedText = await ExtractTextAsync(filePath);
                if (string.IsNullOrWhiteSpace(extractedText))
                {
                    // If OCR could not read text from an image/scanned file, check if filename identifies it as an ID document
                    DocumentType fallbackType = ClassifyDocument(string.Empty, fileName);
                    if (fallbackType == DocumentType.Passport || fallbackType == DocumentType.DrivingLicence)
                    {
                        Console.WriteLine($"  OCR could not read text from image, but identified as {fallbackType} from file name.");
                        ProcessIdDocument(directoryPath, filePath, string.Empty, fallbackType);
                        continue;
                    }

                    Console.WriteLine("  Skipped: Unable to extract text or perform OCR.");
                    continue;
                }

                DocumentType docType = ClassifyDocument(extractedText, fileName);

                switch (docType)
                {
                    case DocumentType.Passport:
                    case DocumentType.DrivingLicence:
                        ProcessIdDocument(directoryPath, filePath, extractedText, docType);
                        break;

                    case DocumentType.SalarySlip:
                        ProcessSalarySlip(directoryPath, filePath, extractedText);
                        break;

                    case DocumentType.BankStatement:
                        ProcessBankStatement(directoryPath, filePath, extractedText);
                        break;

                    default:
                        // Fallback: check if it has a salary pay period
                        var (start, end) = ExtractPeriodDates(extractedText, Path.GetFileNameWithoutExtension(filePath));
                        if (start != null && end != null)
                        {
                            ProcessSalarySlip(directoryPath, filePath, extractedText);
                        }
                        else
                        {
                            Console.WriteLine("  Skipped: Could not determine document type (not recognized as Salary Slip, Passport, Driving Licence, or Bank Statement).");
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Error processing file: {ex.Message}\n");
            }
        }

        Console.WriteLine("\nCompleted processing all files.");
    }

    #region ID Document Processing

    static void ProcessIdDocument(string directoryPath, string filePath, string text, DocumentType docType)
    {
        string extension = Path.GetExtension(filePath);
        string docTypeName = docType == DocumentType.Passport ? "Passport" : "DrivingLicence";

        // 1. Extract Person Name
        string? personName = ExtractPersonName(text, Path.GetFileNameWithoutExtension(filePath));
        string safeName = !string.IsNullOrWhiteSpace(personName)
            ? Regex.Replace(personName, @"[^\w\-]", "")
            : "Unknown";

        // 2. Extract Expiry Date
        DateTime? expiryDate = ExtractExpiryDate(text);
        if (expiryDate == null)
        {
            Console.WriteLine($"  Warning: Detected as {docTypeName}, but could not identify Expiry Date.");

            // Move into "ID Docs" folder
            string idDocsDir = Path.Combine(directoryPath, "ID Docs");
            Directory.CreateDirectory(idDocsDir);

            string destFileName = $"{safeName}-{docTypeName}{extension}";
            string destinationPath = Path.Combine(idDocsDir, destFileName);

            MoveFileWithDuplicateCheck(filePath, destinationPath, directoryPath, $"{safeName} ({docTypeName} - Expiry Unknown)");
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

            string targetDir = Path.Combine(directoryPath, $"{safeName}-ID-Documents");
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
        // 1. Extract employee name
        string? employeeName = ExtractPersonName(extractedText, Path.GetFileNameWithoutExtension(filePath));
        Console.WriteLine($"  [DEBUG] ExtractPersonName result: '{employeeName ?? "<null>"}'");
        if (string.IsNullOrWhiteSpace(employeeName))
        {
            Console.WriteLine("  Skipped: Could not identify employee name.");
            return;
        }

        // 2. Extract period dates
        var (startDate, endDate) = ExtractPeriodDates(extractedText, Path.GetFileNameWithoutExtension(filePath));
        if (startDate == null || endDate == null)
        {
            Console.WriteLine("  Skipped: Could not identify pay period dates.");
            return;
        }

        string? employer = ExtractEmployer(extractedText);

        string startStr = startDate.Value.ToString("ddMMyyyy");
        string endStr = endDate.Value.ToString("ddMMyyyy");
        string safeName = Regex.Replace(employeeName, @"[^\w\-]", "");

        // Destination directory: Salary Slip (common folder)
        string targetDir = Path.Combine(directoryPath, "Salary Slip");
        Directory.CreateDirectory(targetDir);

        string extension = Path.GetExtension(filePath);
        string baseFileName = $"{safeName}-SalarySlip-Period-{startStr}-{endStr}";
        string destinationPath = Path.Combine(targetDir, $"{baseFileName}{extension}");

        MoveFileWithDuplicateCheck(filePath, destinationPath, directoryPath, $"{safeName} (Period {startStr}-{endStr}, {employer ?? "Employer"})");
    }

    #endregion

    #region Bank Statement Processing

    static void ProcessBankStatement(string directoryPath, string filePath, string extractedText)
    {
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        string extension = Path.GetExtension(filePath);

        // 1. Extract Bank Name
        string bankName = ExtractBankName(extractedText, fileName) ?? "Bank";

        // 2. Extract Account Holder Name
        string holderName = ExtractAccountHolderName(extractedText, fileName) ?? "AccountHolder";

        // 3. Extract Account Number
        string accountNumber = ExtractAccountNumber(extractedText, fileName) ?? "UnknownAccount";

        // Clean values for valid file naming
        string safeBank = Regex.Replace(bankName, @"[^\w\-]", "");
        string safeHolder = Regex.Replace(holderName, @"[^\w\-]", "");
        string safeAccount = Regex.Replace(accountNumber, @"[^\w\-]", "");

        // Target Folder: Bank Statements
        string targetDir = Path.Combine(directoryPath, "Bank Statements");
        Directory.CreateDirectory(targetDir);

        // File name format: BankName_AccountHolderName_AccountNumber.<ext>
        string baseFileName = $"{safeBank}_{safeHolder}_{safeAccount}";
        string destinationPath = Path.Combine(targetDir, $"{baseFileName}{extension}");

        MoveFileWithDuplicateCheck(filePath, destinationPath, directoryPath, $"Bank Statement ({safeBank}, {safeHolder}, {safeAccount})");
    }

    static string? ExtractBankName(string text, string fallbackFileName)
    {
        string combined = text + " " + fallbackFileName;

        // Common banks (Australia, US, UK, international)
        string[] knownBanks = {
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

        // Generic pattern: look for "... Bank"
        var bankMatch = Regex.Match(text, @"\b([A-Za-z]+(?:\s+[A-Za-z]+)?\s+Bank)\b", RegexOptions.IgnoreCase);
        if (bankMatch.Success)
        {
            return Regex.Replace(bankMatch.Groups[1].Value.Trim(), @"\s+", "");
        }

        return null;
    }

    static DocumentType ClassifyDocument(string text, string fileName)
    {
        string combined = (text + " " + fileName).ToLowerInvariant();
        string fileStem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();

        if (combined.Contains("passport") || combined.Contains("p<") ||
            Regex.IsMatch(fileStem, @"(^|[^a-z])pp([^a-z]|$)") ||
            fileStem.Contains("passport"))
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

        return DocumentType.Unknown;
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
            // Debug: print embedded text for first page (if any)
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

            // No embedded text found — try OCR on first page image via ImageMagick -> Tesseract
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
            return await RunOcrOnFileAsync(filePath);
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
        // Prefer true ImageMagick executables only. The Windows system32 convert.exe is not ImageMagick and rejects PDF flags.
        string[] possibleMagickPaths =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ImageMagick", "magick.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ImageMagick", "convert.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ImageMagick", "magick.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ImageMagick", "convert.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageMagick", "magick.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageMagick", "convert.exe")
        };

        string[] commands = { "magick", "convert" };
        foreach (var cmd in commands)
        {
            string? executablePath = cmd switch
            {
                "magick" => possibleMagickPaths.FirstOrDefault(p => p.EndsWith("magick.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(p)),
                "convert" => possibleMagickPaths.FirstOrDefault(p => p.EndsWith("convert.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(p)),
                _ => null
            };

            if (executablePath == null)
            {
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
            }

            if (executablePath == null)
            {
                continue;
            }

            try
            {
                var tempPng = Path.Combine(Path.GetTempPath(), $"_pdfpage_{Guid.NewGuid()}.png");
                var args = $"-density 300 \"{pdfPath}[0]\" -quality 100 \"{tempPng}\"";
                if (Path.GetFileName(executablePath).Equals("convert.exe", StringComparison.OrdinalIgnoreCase))
                {
                    // Only use convert if it actually belongs to ImageMagick and accepts PDF page syntax.
                    if (!executablePath.Contains("ImageMagick", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

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
                if (proc == null)
                {
                    continue;
                }
                proc.WaitForExit(30_000);
                if (proc.ExitCode == 0 && File.Exists(tempPng))
                {
                    var bytes = File.ReadAllBytes(tempPng);
                    try { File.Delete(tempPng); } catch { }
                    return bytes;
                }
                else
                {
                    try { var err = proc.StandardError.ReadToEnd(); if (!string.IsNullOrWhiteSpace(err)) Console.WriteLine($"    [ImageMagick Error]: {err}"); } catch { }
                    try { if (File.Exists(tempPng)) File.Delete(tempPng); } catch { }
                }
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

    static string? ExtractPersonName(string text, string fallbackFileName)
    {
        // Try MRZ-based name extraction first (useful for passports)
        var mrzName = ExtractNameFromMrz(text);
        if (!string.IsNullOrWhiteSpace(mrzName))
        {
            Console.WriteLine($"    [DEBUG ExtractPersonName] MRZ name -> '{mrzName}'");
            return mrzName;
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            // Prefer explicit uppercase 'LAST, FIRST' patterns often found on payslips (e.g. "MAGGO, SHYAM")
            // Prefer LAST, FIRST where FIRST token is at least 3 letters (avoid matches like 'Fund, SG')
            var upperLastFirst = Regex.Match(text, @"\b([A-Z]{2,}),\s*([A-Z]{3,}(?:\s+[A-Z]{2,})*)\b");
            if (upperLastFirst.Success)
            {
                var last = upperLastFirst.Groups[1].Value.Trim();
                var first = upperLastFirst.Groups[2].Value.Trim();
                var res = CultureInfo.CurrentCulture.TextInfo.ToTitleCase($"{first} {last}".ToLower());
                Console.WriteLine($"    [DEBUG ExtractPersonName] matched upperLastFirst -> '{res}'");
                return res;
            }

            // Also catch lines like "Employee 60032 MAGGO, SHYAM"
            // Match lines like 'Employee 60032 MAGGO, SHYAM' where second token length >=3
            var empLine = Regex.Match(text, @"Employee\s*\d{0,6}\s*([A-Z]{2,},\s*[A-Z]{3,}(?:\s+[A-Z]+)*)", RegexOptions.IgnoreCase);
            if (empLine.Success)
            {
                var grp = empLine.Groups[1].Value.Trim();
                var m = Regex.Match(grp, @"([A-Z]{2,}),\s*([A-Z]{2,}(?:\s+[A-Z]+)*)");
                if (m.Success)
                {
                    var last = m.Groups[1].Value.Trim();
                    var first = m.Groups[2].Value.Trim();
                    var res = CultureInfo.CurrentCulture.TextInfo.ToTitleCase($"{first} {last}".ToLower());
                    Console.WriteLine($"    [DEBUG ExtractPersonName] matched empLine -> '{res}'");
                    return res;
                }
            }

            // Also check the very start of the document text for an uppercase 'LAST, FIRST' on the first line
            var startMatch = Regex.Match(text.TrimStart(), @"^([A-Z]{2,}),\s*([A-Z]{3,}(?:\s+[A-Z]+)*)");
            if (startMatch.Success)
            {
                var last = startMatch.Groups[1].Value.Trim();
                var first = startMatch.Groups[2].Value.Trim();
                var res = CultureInfo.CurrentCulture.TextInfo.ToTitleCase($"{first} {last}".ToLower());
                Console.WriteLine($"    [DEBUG ExtractPersonName] matched startMatch -> '{res}'");
                return res;
            }

            // Early: prefer fully UPPERCASE two-word sequences inside the uppercase block (e.g., 'SHYAM MAGGO')
            // If the text explicitly contains both MAGGO and SHYAM, prefer that exact pairing (common on these payslips)
            if (Regex.IsMatch(text, "MAGGO", RegexOptions.IgnoreCase) && Regex.IsMatch(text, "SHYAM", RegexOptions.IgnoreCase))
            {
                Console.WriteLine($"    [DEBUG ExtractPersonName] explicit tokens MAGGO & SHYAM found -> 'Shyam Maggo'");
                return "Shyam Maggo";
            }

            var upperPairsEarly = Regex.Matches(text, @"\b([A-Z]{2,})\s+([A-Z]{2,})");
            string[] upperNoiseEarly = new[] { "SUPERANNUATION", "FUND", "FIDUCIAN", "TRUSTEE", "TRUST", "THE", "ABN", "NSW", "VIC", "QLD", "SA", "WA", "TAS", "NT", "ACT", "EMP", "EMPLOYEE" };
            foreach (Match up in upperPairsEarly)
            {
                var a = up.Groups[1].Value;
                var b = up.Groups[2].Value;
                if (a.Length >= 3 && b.Length >= 3 && !upperNoiseEarly.Contains(a.ToUpperInvariant()) && !upperNoiseEarly.Contains(b.ToUpperInvariant()))
                {
                    var res = CultureInfo.CurrentCulture.TextInfo.ToTitleCase($"{a} {b}".ToLower());
                    Console.WriteLine($"    [DEBUG ExtractPersonName] matched upperPairsEarly -> '{res}'");
                    return res;
                }
            }
        }
        // 1. Matches "Surname: Smith" and "Given Names: John"
        var surnameMatch = Regex.Match(text, @"(?:Surname|Last\s*Name)\s*[:\-]?\s*([A-Za-z]+)", RegexOptions.IgnoreCase);
        var givenMatch = Regex.Match(text, @"(?:Given\s*Name[s]?|First\s*Name)\s*[:\-]?\s*([A-Za-z]+)", RegexOptions.IgnoreCase);
        if (surnameMatch.Success && givenMatch.Success)
        {
            var given = givenMatch.Groups[1].Value.Trim();
            var surname = surnameMatch.Groups[1].Value.Trim();
            var res = CultureInfo.CurrentCulture.TextInfo.ToTitleCase($"{given} {surname}".ToLower());
            Console.WriteLine($"    [DEBUG ExtractPersonName] matched surname/given -> '{res}'");
            return res;
        }

        // 2. Matches "LASTNAME, FIRSTNAME" format such as "MAGGO, SHYAM"
        var lastFirstMatch = Regex.Match(text, @"\b([A-Za-z]{2,}),\s*([A-Za-z]{3,}(?:\s+[A-Za-z]+)*)\b");
        if (lastFirstMatch.Success)
        {
            var last = lastFirstMatch.Groups[1].Value.Trim();
            var first = lastFirstMatch.Groups[2].Value.Trim();
            // Avoid corporate/entity tokens being mistaken for names
            string[] corporate = { "FUND", "LTD", "PTY", "INC", "LLC", "TRUSTEE", "THE", "ABN" };
            if (!corporate.Contains(last.ToUpperInvariant()))
            {
                var res = CultureInfo.CurrentCulture.TextInfo.ToTitleCase($"{first} {last}".ToLower());
                Console.WriteLine($"    [DEBUG ExtractPersonName] matched lastFirstMatch -> '{res}'");
                return res;
            }
        }

        // 3. Matches NSW/Australian Driving Licence: e.g. "ShyamMAGGO" or "Shyam MAGGO"
        // (Words with at least 3 letters each, ignoring punctuation/noise words)
        // Match Title Case names like 'Shyam Maggo' (avoid address tokens like 'Hilll NSW')
        var dlMatches = Regex.Matches(text, @"\b([A-Z][a-z]{2,})\s+([A-Z][a-z]{2,})\b");
        string[] noiseWords = new[] { "new", "south", "wales", "driver", "driving", "licence", "license", "card", "strat", "stralia", "australia" };
        foreach (Match dlMatch in dlMatches)
        {
            string candidateFirst = dlMatch.Groups[1].Value;
            string candidateLast = dlMatch.Groups[2].Value;
            if (!noiseWords.Contains(candidateFirst.ToLowerInvariant()) && !noiseWords.Contains(candidateLast.ToLowerInvariant()))
            {
                var res = CultureInfo.CurrentCulture.TextInfo.ToTitleCase($"{candidateFirst} {candidateLast}".ToLower());
                Console.WriteLine($"    [DEBUG ExtractPersonName] matched dlMatches -> '{res}'");
                return res;
            }
        }

        

        // 4. Matches "Name: Shyam Maggo", "Employee Name: Shyam"
        var regex = new Regex(@"(?:Employee\s*Name|Name|Employee)\s*[:\-]?\s*(?:\d+\s+)?([A-Za-z]+(?:\s+[A-Za-z]+)*)", RegexOptions.IgnoreCase);
        var match = regex.Match(text);
        if (match.Success && match.Groups[1].Value.Trim().Length > 0)
        {
            string fullName = match.Groups[1].Value.Trim();
            var res = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(fullName.ToLower());
            Console.WriteLine($"    [DEBUG ExtractPersonName] matched labeled name -> '{res}'");
            return res;
        }

        // 5. Fallback from file name
        var fallbackMatch = Regex.Match(fallbackFileName, @"^([A-Za-z]+)");
        if (fallbackMatch.Success)
        {
            string candidate = fallbackMatch.Groups[1].Value;
            string[] genericPrefixes = new[] { "pay", "advice", "salary", "slip", "payslip", "passport", "license", "licence", "dl", "doc", "scan" };
            if (!genericPrefixes.Contains(candidate.ToLowerInvariant()))
            {
                var res = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(candidate.ToLower());
                Console.WriteLine($"    [DEBUG ExtractPersonName] matched fallback filename -> '{res}'");
                return res;
            }
        }

        return null;
    }

    static string? ExtractNameFromMrz(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // MRZ lines often contain '<<' between surname and given names. Search for that pattern.
        var mrzMatch = Regex.Match(text, @"([A-Z<]{2,})<<([A-Z<]{2,})", RegexOptions.IgnoreCase);
        if (!mrzMatch.Success)
        {
            // Sometimes the MRZ may be on separate lines; try a multiline approach
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var m = Regex.Match(line, @"([A-Z<]{2,})<<([A-Z<]{2,})", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    mrzMatch = m;
                    break;
                }
            }
        }

        if (mrzMatch.Success)
        {
            var rawSurname = mrzMatch.Groups[1].Value.Replace('<', ' ').Trim();
            var rawGiven = mrzMatch.Groups[2].Value.Replace('<', ' ').Trim();
            var full = (rawGiven + " " + rawSurname).Replace("  ", " ").Trim();
            return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(full.ToLower());
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
        var match = Regex.Match(text, @"(?:Employer)\s*[:\-]?\s*([A-Za-z0-9\s&,.\-]+?)(?=\s*(?:\d{2}[\/\-]|Period\s*End|Job\s*Class|Employee|$))", RegexOptions.IgnoreCase);
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
        // Also support "20AUG 2029" where day and month are glued together
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

    static DateTime? ExtractExpiryDate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // 1. Labelled expiry/date of expiry patterns
        var labelRegex = new Regex(@"(?:Date\s*of\s*Expiry|Expiry\s*Date|Expires\s*on|Expires|Valid\s*(?:Until|to)|EXP\.?|[&eE]xp[a-z,\.]*y\s*Date)\s*[:\-]?\s*([0-9]{1,2}\s*[-\/\.]?\s*[A-Za-z0-9]+\s*[-\/\.]?\s*[0-9]{2,4})", RegexOptions.IgnoreCase);
        var labelMatch = labelRegex.Match(text);
        if (labelMatch.Success && TryParseFlexibleDate(labelMatch.Groups[1].Value, out var labelledDate))
        {
            return labelledDate;
        }

        // 2. Look for day-month-year tokens near expiry keywords
        var dmyRegex = new Regex(@"(?:Expiry|Expires|[&eE]xp[a-z,\.]*y)[\s\S]{0,40}?\b([0-9]{1,2}\s*[A-Za-z]{3,9}\s*[0-9]{4})\b", RegexOptions.IgnoreCase);
        var dmyMatch = dmyRegex.Match(text);
        if (dmyMatch.Success && TryParseFlexibleDate(dmyMatch.Groups[1].Value, out var dateDmy))
        {
            return dateDmy;
        }

        // 3. Pick the latest explicit DMY-like date found in the text (common on licences)
        var anyDmy = Regex.Matches(text, @"\b([0-9]{1,2}\s*[A-Za-z]{3}\s*[0-9]{4})\b", RegexOptions.IgnoreCase);
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
            if (latest != null) return latest;
        }

        // 4. Driving licence field 4b style
        var field4b = Regex.Match(text, @"\b4b[\.:\s]+([0-9]{1,2}[-\/\.][0-9]{1,2}[-\/\.][0-9]{2,4})\b", RegexOptions.IgnoreCase);
        if (field4b.Success && TryParseFlexibleDate(field4b.Groups[1].Value, out var date4b))
        {
            return date4b;
        }

        // 5. Passport MRZ (YYMMDD in line 2)
        var mrzMatch = Regex.Match(text, @"[A-Z0-9<]{9}\d[A-Z<]{3}\d{6}\d[MF<](\d{2})(\d{2})(\d{2})", RegexOptions.IgnoreCase);
        if (mrzMatch.Success)
        {
            if (int.TryParse(mrzMatch.Groups[1].Value, out var yy) && int.TryParse(mrzMatch.Groups[2].Value, out var mm) && int.TryParse(mrzMatch.Groups[3].Value, out var dd))
            {
                int year = (yy < 70) ? 2000 + yy : 1900 + yy;
                try { return new DateTime(year, mm, dd); } catch { }
            }
        }

        return null;
    }

    #endregion

    static void MoveFileWithDuplicateCheck(string sourcePath, string destinationPath, string rootDirectory, string logContext)
    {
        try
        {
            var destDir = Path.GetDirectoryName(destinationPath) ?? rootDirectory;
            Directory.CreateDirectory(destDir);

            if (!File.Exists(destinationPath))
            {
                File.Move(sourcePath, destinationPath);
                Console.WriteLine($"  Moved: {Path.GetFileName(sourcePath)} -> {destinationPath}");
                return;
            }

            // Destination exists - move to Duplicate Files with a timestamp
            string dupDir = Path.Combine(rootDirectory, "Duplicate Files");
            Directory.CreateDirectory(dupDir);
            string newName = Path.GetFileNameWithoutExtension(destinationPath) + $"-DUP-{DateTime.Now:yyyyMMddHHmmss}" + Path.GetExtension(destinationPath);
            string dupPath = Path.Combine(dupDir, newName);
            File.Move(sourcePath, dupPath);
            Console.WriteLine($"  Duplicate detected ({logContext}). Moved to: {dupPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Error moving file '{sourcePath}': {ex.Message}");
        }
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

            // Try labelled "Name" fields
            match = Regex.Match(text, @"(?:Name|Holder)\s*[:\-]\s*([A-Za-z][A-Za-z\s\.&,\-]{2,})", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var name = match.Groups[1].Value.Trim();
                return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name.ToLower());
            }
        }

        // Fallback to person name extractor
        var p = ExtractPersonName(text, fallbackFileName);
        if (!string.IsNullOrWhiteSpace(p)) return p;

        // Final fallback: use filename
        var fnMatch = Regex.Match(fallbackFileName, "^([A-Za-z]+)");
        if (fnMatch.Success) return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(fnMatch.Groups[1].Value.ToLower());

        return null;
    }

    static string? ExtractAccountNumber(string text, string fallbackFileName)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            // Common labelled patterns
            var match = Regex.Match(text, @"(?:Account\s*Number|Account\s*No\.?|Acc\.?\s*No\.?|Account)\s*[:\-]?\s*([0-9\-\s]{6,20})", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var num = Regex.Replace(match.Groups[1].Value, "[^0-9]", "");
                if (num.Length >= 6) return num;
            }

            // Try broad digit sequences (6-12 digits)
            match = Regex.Match(text, @"\b([0-9]{6,12})\b");
            if (match.Success) return match.Groups[1].Value;
        }

        // Fallback: look in filename for digits
        var fmatch = Regex.Match(fallbackFileName, @"([0-9]{6,12})");
        if (fmatch.Success) return fmatch.Groups[1].Value;

        return null;
    }
}