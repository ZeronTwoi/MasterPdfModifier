# Third-party components

PDF Modifier is built on the following open-source components. Each remains under its own licence.

| Component | Used for | Licence |
| --- | --- | --- |
| [PDFium](https://pdfium.googlesource.com/pdfium/) (binaries from [pdfium-binaries](https://github.com/bblanchon/pdfium-binaries)) | Rendering, text extraction, page and content editing | BSD-3-Clause / Apache-2.0 |
| [PDFsharp](https://github.com/empira/PDFsharp) | Metadata, encryption, removing unused objects | MIT |
| [Tesseract OCR](https://github.com/tesseract-ocr/tesseract) and [Leptonica](http://www.leptonica.org/) | Text recognition | Apache-2.0 / BSD-2-Clause |
| [Tesseract .NET wrapper](https://github.com/charlesw/tesseract) | Loading Tesseract on Windows | Apache-2.0 |
| [tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast) (downloaded on request, not shipped) | Language data for text recognition | Apache-2.0 |
| [SkiaSharp](https://github.com/mono/SkiaSharp) / [Skia](https://skia.org/) | Image decoding, encoding and drawing | MIT / BSD-3-Clause |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | User interface | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | View-model helpers | MIT |

Fonts are not shipped. New text uses fonts installed on the computer; the glyphs that are used get embedded in the
document, as the fonts' licences generally allow for documents.
