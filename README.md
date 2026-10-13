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
