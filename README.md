<p align="center">
  <img src="PdfModifier/Assets/app.png" width="96" alt="PDF Modifier icon">
</p>

<h1 align="center">PDF Modifier</h1>

<p align="center">
  A free, open-source desktop app for editing, organising, cleaning up and compressing PDF files.<br>
  It works on scanned pages as well as on typed ones. For Windows and Linux.
</p>

<p align="center">
  <img alt="Licence: MIT" src="https://img.shields.io/badge/licence-MIT-4A5BE6">
  <img alt="Platforms: Windows and Linux" src="https://img.shields.io/badge/platform-Windows%20%7C%20Linux-4A5BE6">
  <img alt="Built with .NET 10" src="https://img.shields.io/badge/.NET-10-4A5BE6">
  <img alt="Interface in 8 languages" src="https://img.shields.io/badge/languages-8-4A5BE6">
</p>

<p align="center">
  <img src="docs/images/cut-sheet.png" width="920" alt="The PDF Modifier window: the tools on the left, the page in the middle with four pages found on one scanned sheet, the settings of the tool on the right">
</p>

## What it can do

22 tools in four groups, all in one window:

| Group | Tools |
| --- | --- |
| **Edit** | Change the text and images of a page, add text boxes and pictures, erase · add a watermark · remove watermarks · page numbers · flatten · metadata |
| **Pages** | Merge · split · cut a sheet that shows several pages into pages of their own · rotate · delete · reorder · insert (blank pages, other PDFs, images) · extract (to PDF or PNG/JPEG/WebP) · scale · crop |
| **Optimise** | Compress one document or a whole folder · improve the clarity of scans · make scans searchable (OCR) · automate a chain of steps |
| **Review and protect** | Compare two PDFs · protect with a password |

## What sets it apart

- **Scans are not second-class.** Most PDFs people actually have are pictures of pages. Text is found in them by
  text recognition and can be changed; watermarks are removed whether they lie over the page or are part of the
  scanned picture; pages that mix a scan with typed text have both kinds offered side by side.
- **You see what a tool will do before it does it.** Watermarks, page numbers, rotation, scaling, compression,
  clean-up and watermark removal are shown on the page while you change their settings, marked as a preview.
  Nothing changes until you press Apply, and everything can be undone.
- **It says when it is not sure.** Where the app decides something by itself (where one page ends on a sheet, what a
  blurry line says), a slider sets how readily it decides, and what it doubts is marked red so you can check,
  correct or leave it out.
- **Your files stay on your computer.** No account, no upload, no tracking. The only thing ever fetched from the
  internet is language data for text recognition, from the official Tesseract repository, and only when you ask
  for it.
- **Eight interface languages**: English, Español, Deutsch, Русский, Français, 日本語, Português and Türkçe. Light
  or dark follows the setting of your desktop.

## A closer look

<table>
  <tr>
    <td width="50%" valign="top">
      <img src="docs/images/edit-scan.png" alt="A scanned page with every line of text found and outlined"><br>
      <b>Edit text, also in scans.</b> Blue is text of the document, orange is text found in the page image.
      Click a line, type, apply.
    </td>
    <td width="50%" valign="top">
      <img src="docs/images/text-box.png" alt="A text box on a page, turned by thirty degrees, with handles"><br>
      <b>Add text and images that stay objects.</b> Move, resize, turn and restyle them at any time, also after
      the file was saved and opened again.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="docs/images/remove-watermark.png" alt="A scanned page shown without its watermark, marked as a preview"><br>
      <b>Remove watermarks with a preview.</b> Here the mark is part of the scan; the list on the left still shows
      the page as it is.
    </td>
    <td width="50%" valign="top">
      <img src="docs/images/improve-clarity.png" alt="A blurry scan brought into focus, marked as a preview"><br>
      <b>Clean up poor scans.</b> Say what is wrong with the pages (blurry, faint, a photograph with shadows) and
      adjust the steps with the result on view.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="docs/images/doubt-in-red.png" alt="A blurry scan with the lines that were hard to read outlined in red"><br>
      <b>Doubts are marked red.</b> Lines that text recognition more guessed at than read can be read again, more
      carefully, one area at a time.
    </td>
    <td width="50%" valign="top">
      <img src="docs/images/dark-theme.png" alt="The window in its dark colours, showing a watermark preview"><br>
      <b>Light and dark.</b> The same window on a dark desktop.
    </td>
  </tr>
</table>

Every tool is described in the **[guide](docs/GUIDE.md)**, together with the keyboard and mouse shortcuts and the
limits worth knowing.

## Get it

PDF Modifier is at version 0.1.0. There is no installer or ready-made download yet: for now it is built from the
source, which takes the [.NET 10 SDK](https://dotnet.microsoft.com/download) and three lines:

```
git clone https://github.com/ZeronTwoi/MasterPdfModifier.git
cd MasterPdfModifier
dotnet run --project PdfModifier
```

A build that runs on a computer without .NET, to keep or to pass on:

```
dotnet publish PdfModifier -c Release -r win-x64 --self-contained -o publish/win-x64
dotnet publish PdfModifier -c Release -r linux-x64 --self-contained -o publish/linux-x64
```

| | Windows | Linux |
| --- | --- | --- |
| **Start it** | Run `PdfModifier.exe`. It carries its own name and icon; pin it to the task bar or the Start menu from its right-click menu. | Run `sh install.sh` in the unpacked folder. That adds PDF Modifier to the application menu with its icon and to "Open with" for PDF files, without root. `sh uninstall.sh` takes it out again. |
| **Text recognition** | Built in. It needs the Microsoft Visual C++ runtime, which most computers already have. | Install Tesseract from your distribution, for example `sudo apt install tesseract-ocr`. Language packs installed that way are used directly. |

Windows is where the app has been used most. The Linux build is made and packaged the same way but has been tried
far less; reports of what works and what does not are welcome.

## Using it

Open a PDF, or drop pictures into the window to start a document from them. Pick a tool on the left, set it up on
the right, look at the preview on the page, press **Apply**. **Ctrl + Z** takes a step back, **Save** writes the
file. Type a word into the box above the tools to find one by its name or by what it does.

The [guide](docs/GUIDE.md) has the rest.

## For developers

```
dotnet build
dotnet test
```

- `PdfModifier.Core` – all PDF work, no user interface. Every operation takes a document as bytes and returns a new
  one.
- `PdfModifier` – the desktop app (Avalonia).
- `PdfModifier.Tests` – unit tests for the core, and tests that drive the real window with mouse and keyboard
  without needing a screen.
- `packaging/linux` – the application menu entry for Linux and the scripts that install and remove it.
- `docs` – the guide and the pictures on this page.

The test suite generates its own documents. Three optional environment variables:

- `PDFMODIFIER_TESSDATA` – a folder with `*.traineddata` files. Recognition tests run when the data is there and are
  skipped otherwise. The app honours the same variable, for example for a portable install.
- `PDFMODIFIER_SCREENSHOTS` – a folder to receive pictures of the window in each tested state. The pictures on this
  page were made that way.
- `PDFMODIFIER_SAMPLES` – a folder to receive sample files for trying things by hand: a text document, scans, sheets
  with several pages, a page of paragraphs, a blurry scan.

A tool that fails tells the user so in a message box. Without a screen nobody can close it, so the window tests only
drive what succeeds; `dotnet test --blame-hang --blame-hang-timeout 200s` ends a run that hangs all the same.

## Feedback

This is an early version. If something does not work, or a tool does less than you need, please open an issue and
say what kind of PDF it was (typed, scanned, or both) and what you expected to happen.

## Licence

PDF Modifier is released under the [MIT licence](LICENSE): free to use, change and pass on, also commercially, as
long as the licence text stays with it.

## Built on

PDF Modifier stands on the work of these open-source projects. None of them is a paid or restricted product; each
remains under its own licence, which asks for the credit given here.

| Project | What it does here | Licence |
| --- | --- | --- |
| [PDFium](https://pdfium.googlesource.com/pdfium/), in the builds of [pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) | Showing pages, reading their text, editing pages and what is on them | BSD-3-Clause / Apache-2.0 |
| [PDFsharp](https://github.com/empira/PDFsharp) | Metadata, password protection, removing unused objects | MIT |
| [Tesseract OCR](https://github.com/tesseract-ocr/tesseract) and [Leptonica](http://www.leptonica.org/) | Text recognition | Apache-2.0 / BSD-2-Clause |
| [Tesseract .NET wrapper](https://github.com/charlesw/tesseract) | Loading Tesseract on Windows | Apache-2.0 |
| [tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast) (downloaded on request, not included) | Language data for text recognition | Apache-2.0 |
| [SkiaSharp](https://github.com/mono/SkiaSharp) and [Skia](https://skia.org/) | Reading, writing and drawing images | MIT / BSD-3-Clause |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | The user interface | MIT |
| [HarfBuzz](https://github.com/harfbuzz/harfbuzz) | Shaping the text of the interface | MIT |
| [ANGLE](https://chromium.googlesource.com/angle/angle) | Drawing the interface on Windows | BSD-3-Clause |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | Connecting the interface to the logic behind it | MIT |
| [.NET](https://github.com/dotnet/runtime) | The runtime the app runs on, included in the self-contained builds | MIT |

The full list, with the smaller parts these bring along, is in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); it
travels with every build. Fonts are not included: new text uses the fonts installed on the computer.

PDF Modifier is an independent project. It is not affiliated with or endorsed by any of the projects and companies
named here; their names are used only to say what the app is built on.
