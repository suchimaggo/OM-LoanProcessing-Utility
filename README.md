# LoanProcessing-Utility

Small console utility to classify and organise loan-related documents (ID, salary slips, bank statements, rental documents).

## Prerequisites
- .NET 9+ SDK (for build/run)
- Tesseract OCR (for image/PDF OCR)
- ImageMagick and/or Ghostscript (optional, enables PDF->PNG fallback for OCR)
- Git (optional, for cloning)

Recommended installers (Windows): use winget or installers from vendor websites.

Example (winget):
```powershell
winget install Microsoft.DotNet.SDK.9
winget install Git.Git
winget install tesseract.tesseract
winget install ImageMagick.ImageMagick
winget install Artifex.Ghostscript
```

## Prepare Tesseract
Ensure `eng.traineddata` exists in a `tessdata` folder. Either install Tesseract normally or create a `tessdata` folder inside the project or set `TESSDATA_PREFIX`:

```powershell
setx TESSDATA_PREFIX "C:\Program Files\Tesseract-OCR\tessdata"
# Restart terminal/VS Code after setx
```

Alternatively, drop `eng.traineddata` at `C:\tessdata` or `./tessdata` in the project root.

## Build
```powershell
cd C:\LoanProcessing-Utility
dotnet build LoanProcessing-Utility.csproj -c Release
```

## Run (interactive)
```powershell
dotnet run --project LoanProcessing-Utility.csproj -c Release
# Enter folder path when prompted.
```

## Run (non-interactive)
Process a specific folder (piped path):
```powershell
echo "C:\Path\To\Folder" | dotnet run --project LoanProcessing-Utility.csproj -c Release
```

## Rescue Needs Review
```powershell
echo "C:\Path\To\Folder" | dotnet run --project LoanProcessing-Utility.csproj -c Release -- --rescue-needs-review
```

## Publish single-file executable (optional)
```powershell
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -o .\publish
```

## Troubleshooting
- No OCR output: ensure `tessdata/eng.traineddata` exists and `TESSDATA_PREFIX` is set.
- PDF fallback not working: install ImageMagick (magick) or Ghostscript (gswin64c) and ensure they are on PATH.
- Image decode failures: check image file integrity by opening in an image viewer.
- Why moved to `Needs Review`: check the generated `Processing-Report-*.txt` file in the processed folder — it contains per-file debug and reasons.

## Notes
- The tool is conservative: ambiguous files are routed to `Needs Review` to avoid bad auto-renames.
- There is an interactive prompt after processing to reprocess `Needs Review` files; small counts (<=5) are auto-rescued.

If you want a custom setup script or packaging (MSI/EXE), tell me the target environment and I will create it.
# Loan Processing Utility

A small .NET console app that organizes documents in a selected folder by type.

It can recognize and sort:
- Salary slips
- Passports
- Driving licences
- Bank statements

It also includes OCR fallback for scanned PDFs and images when embedded text is unavailable.

## Requirements
- .NET 9 SDK
- Tesseract OCR language data (`eng.traineddata`) in a `tessdata` folder
- ImageMagick installed for PDF-to-image OCR fallback when needed

## Run

From the project folder:

```bash
dotnet run --project LoanProcessing-Utility.csproj -c Release
```

Then enter the folder path to organize.

## Notes
- The app moves recognized files into folders such as `Salary Slip`, `ID Docs`, and `Bank Statements`.
- Duplicate files are placed in a `Duplicate Files` folder.
- Expired IDs are moved to a folder named like `Name-Expired-Documents`.
