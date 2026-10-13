# Third-party components

Master PDF Modifier is built on the following open-source components. Each remains under its own licence; the licence
texts are at the links. None of them is a paid or restricted product.

## What the app works with

| Component | Used for | Licence |
| --- | --- | --- |
| [PDFium](https://pdfium.googlesource.com/pdfium/) (binaries from [pdfium-binaries](https://github.com/bblanchon/pdfium-binaries)) | Rendering, text extraction, page and content editing | BSD-3-Clause / Apache-2.0 |
| [PDFsharp](https://github.com/empira/PDFsharp) | Metadata, encryption, removing unused objects | MIT |
| [Tesseract OCR](https://github.com/tesseract-ocr/tesseract) and [Leptonica](http://www.leptonica.org/) | Text recognition | Apache-2.0 / BSD-2-Clause |
| [Tesseract .NET wrapper](https://github.com/charlesw/tesseract) | Loading Tesseract on Windows | Apache-2.0 |
| [SkiaSharp](https://github.com/mono/SkiaSharp) / [Skia](https://skia.org/) | Image decoding, encoding and drawing | MIT / BSD-3-Clause |

## What the window is made of

| Component | Used for | Licence |
| --- | --- | --- |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | User interface | MIT |
| [HarfBuzz](https://github.com/harfbuzz/harfbuzz) (through HarfBuzzSharp, part of [SkiaSharp](https://github.com/mono/SkiaSharp)) | Shaping the text of the interface | MIT |
| [ANGLE](https://chromium.googlesource.com/angle/angle) (comes with Avalonia on Windows) | Drawing the interface with the graphics card | BSD-3-Clause |
| [MicroCom](https://github.com/kekekeks/MicroCom) (comes with Avalonia) | Talking to Windows | MIT |
| [Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus) (comes with Avalonia) | Talking to the Linux desktop | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | View-model helpers | MIT |

## What it runs on

| Component | Used for | Licence |
| --- | --- | --- |
| [.NET](https://github.com/dotnet/runtime), with its Microsoft.Extensions and System.Security libraries | The runtime; it is inside the self-contained builds | MIT |

## What the app may fetch, after asking

These are not part of the app and are not shipped with it. They go from their owners to the user's computer, under
their owners' terms, when text recognition needs them and the user has said yes.

| Component | Used for | Terms |
| --- | --- | --- |
| [Microsoft Visual C++ runtime](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist) (Windows, downloaded from Microsoft) | What the built-in text recognition is built on | Microsoft's licence terms for the redistributable |
| [Tesseract OCR](https://github.com/tesseract-ocr/tesseract) (Linux, installed from the distribution) | Text recognition | Apache-2.0 |
| [tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast) (downloaded from the Tesseract project) | Language data for text recognition | Apache-2.0 |

Fonts are not shipped. New text uses fonts installed on the computer; the glyphs that are used get embedded in the
document, as the fonts' licences generally allow for documents.

Master PDF Modifier is an independent project. It is not affiliated with or endorsed by any of the projects and companies
named here.
